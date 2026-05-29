Shader "TerrainGrassSystem/Grass/Blade"
{
    Properties
    {
        [HideInInspector] _BaseColorMultiplier ("Base Color Multiplier", Color) = (1,1,1,1)
        _TipBoost            ("Tip Brightness",       Range(0.5, 3.0)) = 1.2
        _CamFacingThreshold  ("Edge-On Trigger (cos)", Range(0.0, 1.0)) = 0.8
        _CamFacingMaxAngle   ("Camera-Facing Max Angle (deg at tip)", Range(0.0, 30.0)) = 8.0
        _CurvedNormalAmount  ("Curved Normal Amount", Range(0.0, 1.0)) = 0.7
        _AOStrength          ("Bottom AO",            Range(0.0, 1.0)) = 0.5
        _SpecularPower       ("Specular Power",       Range(1.0, 256.0)) = 32.0
        _SpecularStrength    ("Specular Strength",    Range(0.0, 1.0)) = 0.15
    }

    SubShader
    {
        Tags
        {
            "RenderType"      = "Opaque"
            "RenderPipeline"  = "UniversalPipeline"
            "Queue"           = "Geometry"
            "IgnoreProjector" = "True"
        }

        Cull Off // grass blade is double-sided
        ZWrite On

        // =====================================================================
        // Forward
        // =====================================================================
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex   GrassVertex
            #pragma fragment GrassFragment
            #pragma target 5.0
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "GrassCommon.hlsl"
            #include "GrassWind.hlsl"

            StructuredBuffer<GrassBlade> _GrassBlades;
            int _GrassLodSegments;   // 3 for high LOD, 1 for low LOD

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColorMultiplier;
                half  _TipBoost;
                half  _CamFacingThreshold;
                half  _CamFacingMaxAngle;
                half  _CurvedNormalAmount;
                half  _AOStrength;
                half  _SpecularPower;
                half  _SpecularStrength;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;  // x = side (-1..+1), y = segment v (0..1), z = unused
                uint   vertexID   : SV_VertexID;
                uint   instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 colorBase   : TEXCOORD2;
                float2 uv          : TEXCOORD3;
                float  fogFactor   : TEXCOORD4;
                float4 shadowCoord : TEXCOORD5;
            };

            // Quadratic Bezier in the blade-local frame.
            // P0 = root, P1 = (0, h/2, bend), P2 tilts forward by tiltAngle.
            float3 BezierBlade(float t, float h, float tilt, float bend)
            {
                float3 p0 = float3(0, 0, 0);
                float3 p1 = float3(0, h * 0.5, bend);
                float3 p2 = float3(0, cos(tilt) * h, sin(tilt) * h);
                float u = 1.0 - t;
                return u * u * p0 + 2.0 * u * t * p1 + t * t * p2;
            }

            // Tangent along the Bezier (used to orient lit normal).
            float3 BezierTangent(float t, float h, float tilt, float bend)
            {
                float3 p0 = float3(0, 0, 0);
                float3 p1 = float3(0, h * 0.5, bend);
                float3 p2 = float3(0, cos(tilt) * h, sin(tilt) * h);
                return normalize(2.0 * (1.0 - t) * (p1 - p0) + 2.0 * t * (p2 - p1));
            }

            Varyings GrassVertex(Attributes IN)
            {
                Varyings OUT = (Varyings)0;

                GrassBlade blade = _GrassBlades[IN.instanceID];

                float side = IN.positionOS.x;       // -1 = left edge, +1 = right edge, 0 = tip
                float v    = IN.positionOS.y;       //  0 = root, 1 = tip

                // Per-blade up axis from compute: blended terrain normal vs
                // world up by GrassType.slopeFollow. Re-orthogonalize facing
                // so it lies in the plane perpendicular to up.
                float3 up     = normalize(blade.normalUp);
                float3 facing = normalize(blade.facing - up * dot(blade.facing, up));

                // Folded blades remap v and rotate facing so one mesh draws
                // as two short sub-blades. No-op for normal blades.
                ApplyBladeFold(v, facing, up, blade);

                float3 right  = normalize(cross(up, facing));

                // Camera-facing setup. Triggers only when the blade is close to
                // EDGE-ON to camera (its width axis points toward the view),
                // clamps the yaw to a small max angle, and twists the blade
                // ALONG ITS BEZIER TANGENT instead of around the world-up axis —
                // so each cross-section rotates a little, the rotation grows
                // smoothly from root (no rotation) to tip (full clamped yaw),
                // and the blade's overall pose stays the same.
                float3 cameraWS  = _WorldSpaceCameraPos.xyz;
                float3 toCamera  = normalize(cameraWS - blade.position);
                float3 toCamFlat = normalize(toCamera - up * dot(toCamera, up) + float3(1e-6, 0, 0));

                float  edgeOn   = abs(dot(right, toCamFlat));           // 1 = edge-on, 0 = face-on
                float  trigger  = smoothstep(_CamFacingThreshold, 0.98, edgeOn);

                float3 crFT     = cross(facing, toCamFlat);
                float  yawFull  = atan2(dot(crFT, up), dot(facing, toCamFlat));
                float  maxYaw   = radians(_CamFacingMaxAngle);
                float  yawTotal = clamp(yawFull, -maxYaw, maxYaw) * trigger;

                // LOD blend height.
                float h = blade.height * blade.lodBlend;

                // Bezier tangent at THIS vertex, in world space — the twist axis.
                float3 tLocal = BezierTangent(v, h, blade.tiltAngle, blade.bend); // (0, ty, tz)
                float3 axis   = normalize(up * tLocal.y + facing * tLocal.z);

                // Per-vertex twist amount: 0 at root, max at tip (= along Bezier).
                float vertexYaw = yawTotal * v;
                float cYaw = cos(vertexYaw);
                float sYaw = sin(vertexYaw);
                // Rodrigues' rotation around `axis`.
                float dotAR = dot(axis, right);
                float dotAF = dot(axis, facing);
                float3 rRight  = right  * cYaw + cross(axis, right)  * sYaw + axis * dotAR * (1.0 - cYaw);
                float3 rFacing = facing * cYaw + cross(axis, facing) * sYaw + axis * dotAF * (1.0 - cYaw);

                // Blade width narrows toward the tip.
                float wAtV = blade.width * (1.0 - v * 0.75);

                // Position on the curve, expanded with the rotated basis so each
                // cross-section "twists" along the curve.
                float3 curve = BezierBlade(v, h, blade.tiltAngle, blade.bend);
                float  sideOffset = (side * 0.5) * wAtV;
                float3 localPos = rFacing * curve.z + up * curve.y + rRight * sideOffset;

                float3 worldPos = blade.position + localPos
                                + ApplyGrassWind(blade.windBase, v);

                // Curved fake normals using the rotated basis and a world-space
                // tangent — keeps lighting consistent with the geometry twist.
                float3 tangentWS    = normalize(up * tLocal.y + rFacing * tLocal.z);
                float3 bladeNormal  = normalize(cross(rRight, tangentWS));
                float3 curvedNormal = normalize(lerp(bladeNormal, rRight * sign(side + 1e-4), _CurvedNormalAmount * abs(side)));

                OUT.positionWS = worldPos;
                OUT.positionCS = TransformWorldToHClip(worldPos);
                OUT.normalWS   = curvedNormal;

                // Root darker than tip; tipBoost emphasizes the top.
                float aoFactor = lerp(1.0 - _AOStrength, 1.0, v);
                OUT.colorBase  = blade.color * aoFactor * lerp(1.0, _TipBoost, v) * _BaseColorMultiplier.rgb;

                OUT.uv = float2(side * 0.5 + 0.5, v);
                OUT.fogFactor   = ComputeFogFactor(OUT.positionCS.z);
                OUT.shadowCoord = TransformWorldToShadowCoord(worldPos);

                return OUT;
            }

            half4 GrassFragment(Varyings IN, FRONT_FACE_TYPE isFrontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(_WorldSpaceCameraPos.xyz - IN.positionWS);

                // Two-sided lighting: flip normal on the back-facing side so both
                // sides receive sensible diffuse.
                N = IS_FRONT_VFACE(isFrontFace, N, -N);

                Light mainLight = GetMainLight(IN.shadowCoord);

                float NdotL = saturate(dot(N, mainLight.direction));
                float3 diffuse = mainLight.color * (NdotL * mainLight.shadowAttenuation);

                // Cheap specular highlight for backlit grass.
                float3 H = normalize(mainLight.direction + V);
                float NdotH = saturate(dot(N, H));
                float spec  = pow(NdotH, _SpecularPower) * _SpecularStrength;

                float3 ambient = SampleSH(N);

                float3 col = IN.colorBase * (diffuse + ambient) + spec * mainLight.color;

                col = MixFog(col, IN.fogFactor);
                return half4(col, 1.0);
            }
            ENDHLSL
        }

        // =====================================================================
        // Shadow caster
        // =====================================================================
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ColorMask 0
            ZWrite On

            HLSLPROGRAM
            #pragma vertex   ShadowVertex
            #pragma fragment ShadowFragment
            #pragma target 5.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "GrassCommon.hlsl"
            #include "GrassWind.hlsl"

            StructuredBuffer<GrassBlade> _GrassBlades;

            // Must mirror the forward pass exactly for SRP Batcher compatibility
            // AND so shadow geometry matches the visible blade after twisting.
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColorMultiplier;
                half  _TipBoost;
                half  _CamFacingThreshold;
                half  _CamFacingMaxAngle;
                half  _CurvedNormalAmount;
                half  _AOStrength;
                half  _SpecularPower;
                half  _SpecularStrength;
            CBUFFER_END

            float3 BezierBlade(float t, float h, float tilt, float bend)
            {
                float3 p0 = float3(0, 0, 0);
                float3 p1 = float3(0, h * 0.5, bend);
                float3 p2 = float3(0, cos(tilt) * h, sin(tilt) * h);
                float u = 1.0 - t;
                return u * u * p0 + 2.0 * u * t * p1 + t * t * p2;
            }

            float3 BezierTangent(float t, float h, float tilt, float bend)
            {
                float3 p0 = float3(0, 0, 0);
                float3 p1 = float3(0, h * 0.5, bend);
                float3 p2 = float3(0, cos(tilt) * h, sin(tilt) * h);
                return normalize(2.0 * (1.0 - t) * (p1 - p0) + 2.0 * t * (p2 - p1));
            }

            struct ShadowAttributes
            {
                float3 positionOS : POSITION;
                uint   instanceID : SV_InstanceID;
            };

            float4 ShadowVertex(ShadowAttributes IN) : SV_POSITION
            {
                GrassBlade blade = _GrassBlades[IN.instanceID];

                float side = IN.positionOS.x;
                float v    = IN.positionOS.y;

                float3 up     = normalize(blade.normalUp);
                float3 facing = normalize(blade.facing - up * dot(blade.facing, up));
                ApplyBladeFold(v, facing, up, blade);

                float3 right  = normalize(cross(up, facing));

                float3 cameraWS  = _WorldSpaceCameraPos.xyz;
                float3 toCamera  = normalize(cameraWS - blade.position);
                float3 toCamFlat = normalize(toCamera - up * dot(toCamera, up) + float3(1e-6, 0, 0));

                float  edgeOn   = abs(dot(right, toCamFlat));
                float  trigger  = smoothstep(_CamFacingThreshold, 0.98, edgeOn);
                float3 crFT     = cross(facing, toCamFlat);
                float  yawFull  = atan2(dot(crFT, up), dot(facing, toCamFlat));
                float  maxYaw   = radians(_CamFacingMaxAngle);
                float  yawTotal = clamp(yawFull, -maxYaw, maxYaw) * trigger;

                float h = blade.height * blade.lodBlend;

                float3 tLocal = BezierTangent(v, h, blade.tiltAngle, blade.bend);
                float3 axis   = normalize(up * tLocal.y + facing * tLocal.z);

                float vertexYaw = yawTotal * v;
                float cYaw = cos(vertexYaw);
                float sYaw = sin(vertexYaw);
                float dotAR = dot(axis, right);
                float dotAF = dot(axis, facing);
                float3 rRight  = right  * cYaw + cross(axis, right)  * sYaw + axis * dotAR * (1.0 - cYaw);
                float3 rFacing = facing * cYaw + cross(axis, facing) * sYaw + axis * dotAF * (1.0 - cYaw);

                float wAtV = blade.width * (1.0 - v * 0.75);
                float3 curve = BezierBlade(v, h, blade.tiltAngle, blade.bend);
                float3 localPos = rFacing * curve.z + up * curve.y + rRight * (side * 0.5 * wAtV);
                float3 worldPos = blade.position + localPos
                                + ApplyGrassWind(blade.windBase, v);

                float3 lightDirWS = _MainLightPosition.xyz;
                float3 normalWS = up;
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(worldPos, normalWS, lightDirWS));

                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return positionCS;
            }

            half4 ShadowFragment() : SV_Target { return 0; }
            ENDHLSL
        }

        // =====================================================================
        // Depth only
        // =====================================================================
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ColorMask 0
            ZWrite On

            HLSLPROGRAM
            #pragma vertex   DepthVertex
            #pragma fragment DepthFragment
            #pragma target 5.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "GrassCommon.hlsl"
            #include "GrassWind.hlsl"

            StructuredBuffer<GrassBlade> _GrassBlades;

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColorMultiplier;
                half  _TipBoost;
                half  _CamFacingThreshold;
                half  _CamFacingMaxAngle;
                half  _CurvedNormalAmount;
                half  _AOStrength;
                half  _SpecularPower;
                half  _SpecularStrength;
            CBUFFER_END

            float3 BezierBlade(float t, float h, float tilt, float bend)
            {
                float3 p0 = float3(0, 0, 0);
                float3 p1 = float3(0, h * 0.5, bend);
                float3 p2 = float3(0, cos(tilt) * h, sin(tilt) * h);
                float u = 1.0 - t;
                return u * u * p0 + 2.0 * u * t * p1 + t * t * p2;
            }

            float3 BezierTangent(float t, float h, float tilt, float bend)
            {
                float3 p0 = float3(0, 0, 0);
                float3 p1 = float3(0, h * 0.5, bend);
                float3 p2 = float3(0, cos(tilt) * h, sin(tilt) * h);
                return normalize(2.0 * (1.0 - t) * (p1 - p0) + 2.0 * t * (p2 - p1));
            }

            struct DepthAttributes
            {
                float3 positionOS : POSITION;
                uint   instanceID : SV_InstanceID;
            };

            float4 DepthVertex(DepthAttributes IN) : SV_POSITION
            {
                GrassBlade blade = _GrassBlades[IN.instanceID];

                float side = IN.positionOS.x;
                float v    = IN.positionOS.y;

                float3 up     = normalize(blade.normalUp);
                float3 facing = normalize(blade.facing - up * dot(blade.facing, up));
                ApplyBladeFold(v, facing, up, blade);

                float3 right  = normalize(cross(up, facing));

                float3 cameraWS  = _WorldSpaceCameraPos.xyz;
                float3 toCamera  = normalize(cameraWS - blade.position);
                float3 toCamFlat = normalize(toCamera - up * dot(toCamera, up) + float3(1e-6, 0, 0));

                float  edgeOn   = abs(dot(right, toCamFlat));
                float  trigger  = smoothstep(_CamFacingThreshold, 0.98, edgeOn);
                float3 crFT     = cross(facing, toCamFlat);
                float  yawFull  = atan2(dot(crFT, up), dot(facing, toCamFlat));
                float  maxYaw   = radians(_CamFacingMaxAngle);
                float  yawTotal = clamp(yawFull, -maxYaw, maxYaw) * trigger;

                float h = blade.height * blade.lodBlend;
                float3 tLocal = BezierTangent(v, h, blade.tiltAngle, blade.bend);
                float3 axis   = normalize(up * tLocal.y + facing * tLocal.z);

                float vertexYaw = yawTotal * v;
                float cYaw = cos(vertexYaw);
                float sYaw = sin(vertexYaw);
                float dotAR = dot(axis, right);
                float dotAF = dot(axis, facing);
                float3 rRight  = right  * cYaw + cross(axis, right)  * sYaw + axis * dotAR * (1.0 - cYaw);
                float3 rFacing = facing * cYaw + cross(axis, facing) * sYaw + axis * dotAF * (1.0 - cYaw);

                float wAtV = blade.width * (1.0 - v * 0.75);
                float3 curve = BezierBlade(v, h, blade.tiltAngle, blade.bend);
                float3 worldPos = blade.position
                    + rFacing * curve.z + up * curve.y + rRight * (side * 0.5 * wAtV)
                    + ApplyGrassWind(blade.windBase, v);

                return TransformWorldToHClip(worldPos);
            }

            half4 DepthFragment() : SV_Target { return 0; }
            ENDHLSL
        }
    }

    FallBack Off
}
