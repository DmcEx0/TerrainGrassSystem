#ifndef ABYSSRISING_GRASS_BLADE_GRAPH_INCLUDED
#define ABYSSRISING_GRASS_BLADE_GRAPH_INCLUDED

// Custom Function entry points for a URP Lit ShaderGraph that replaces
// GrassBlade.shader. The graph wires the outputs of GrassBlade_Vertex_float
// into the Vertex stage (Position, Normal in Object space — via a World->Object
// transform node) and BaseColor (in Fragment stage) of the Lit Master Stack.
//
// Buffer + globals are declared here, so just including this file via the
// Custom Function node's "File" mode makes _GrassBlades visible to the graph.

#include "GrassCommon.hlsl"
#include "GrassWind.hlsl"

StructuredBuffer<GrassBlade> _GrassBlades;

// --- Bezier helpers ---------------------------------------------------------
//
// P0 = root, P1 = (0, h/2, bend), P2 tilts forward by tiltAngle.
float3 _GrassBladeBezier(float t, float h, float tilt, float bend)
{
    float3 p0 = float3(0, 0, 0);
    float3 p1 = float3(0, h * 0.5, bend);
    float3 p2 = float3(0, cos(tilt) * h, sin(tilt) * h);
    float u = 1.0 - t;
    return u * u * p0 + 2.0 * u * t * p1 + t * t * p2;
}

float3 _GrassBladeBezierTangent(float t, float h, float tilt, float bend)
{
    float3 p0 = float3(0, 0, 0);
    float3 p1 = float3(0, h * 0.5, bend);
    float3 p2 = float3(0, cos(tilt) * h, sin(tilt) * h);
    return normalize(2.0 * (1.0 - t) * (p1 - p0) + 2.0 * t * (p2 - p1));
}

// --- Vertex Custom Function -------------------------------------------------
//
// Inputs:
//   PositionOS    - vertex position of the unit blade mesh. Format from
//                   GrassBladeMesh: x = side in [-1, +1] (0 at tip),
//                                   y = segment v in [0, 1].
//   InstanceID    - SV_InstanceID forwarded via the graph's Instance ID node.
//   CamFacingT    - edge-on dot threshold (0..1). Higher = only the most
//                   perpendicular blades twist.
//   CamFacingMaxA - maximum twist at the tip, in degrees (5..10 is sensible).
//   CurvedNAmt    - curved-normal amount (0..1).
//   AOStrength    - bottom-of-blade AO (0..1).
//   TipBoost      - multiplier applied at the blade tip.
//
// Camera-facing model: when a blade is close to edge-on to the camera, its
// cross-sections twist around the Bezier tangent of the curve. The amount
// ramps linearly with v — root unchanged, tip rotated by the clamped max
// angle. The blade as a whole keeps its pose; only the surface twists.
//
// Outputs (WORLD SPACE):
//   PositionWS   - drive Vertex Position directly (model matrix is identity for
//                  Graphics.RenderMeshIndirect; no Transform node needed).
//   NormalWS     - drive Vertex Normal directly.
//   ColorBase    - blade.color * AO * tipBoost (premixed). Use this OR
//                  GrassBlade_Masks_float for manual blending.
//   UV           - (side*0.5+0.5, v).
void GrassBlade_Vertex_float(
    float3 PositionOS,
    float  InstanceID,
    float  CamFacingT,
    float  CamFacingMaxA,
    float  CurvedNAmt,
    float  AOStrength,
    float  TipBoost,
    out float3 PositionWS,
    out float3 NormalWS,
    out float3 ColorBase,
    out float2 UV)
{
    uint id = (uint)InstanceID;
    GrassBlade blade = _GrassBlades[id];

    float side = PositionOS.x;
    float v    = PositionOS.y;

    // Local frame --------------------------------------------------------
    // Per-blade up axis from compute: blended terrain normal vs world up by
    // GrassType.slopeFollow. Re-orthogonalize facing to lie in the plane
    // perpendicular to up.
    float3 up     = normalize(blade.normalUp);
    float3 facing = normalize(blade.facing - up * dot(blade.facing, up));

    // Folded blades remap v and rotate facing so one mesh draws as two short
    // sub-blades. No-op for normal blades.
    ApplyBladeFold(v, facing, up, blade);

    float3 right  = normalize(cross(up, facing));

    // Camera-facing setup ------------------------------------------------
    float3 cameraWS  = _WorldSpaceCameraPos.xyz;
    float3 toCamera  = normalize(cameraWS - blade.position);
    float3 toCamFlat = normalize(toCamera - up * dot(toCamera, up) + float3(1e-6, 0, 0));

    float  edgeOn   = abs(dot(right, toCamFlat));               // 1 = edge-on
    float  trigger  = smoothstep(CamFacingT, 0.98, edgeOn);

    float3 crFT     = cross(facing, toCamFlat);
    float  yawFull  = atan2(dot(crFT, up), dot(facing, toCamFlat));
    float  maxYaw   = radians(CamFacingMaxA);
    float  yawTotal = clamp(yawFull, -maxYaw, maxYaw) * trigger;

    // LOD blend height + Bezier tangent at this vertex (twist axis) ------
    float  h      = blade.height * blade.lodBlend;
    float3 tLocal = _GrassBladeBezierTangent(v, h, blade.tiltAngle, blade.bend);
    float3 axis   = normalize(up * tLocal.y + facing * tLocal.z);

    // Per-vertex twist amount ramps linearly with v ----------------------
    float vertexYaw = yawTotal * v;
    float cYaw = cos(vertexYaw);
    float sYaw = sin(vertexYaw);
    float dotAR = dot(axis, right);
    float dotAF = dot(axis, facing);
    float3 rRight  = right  * cYaw + cross(axis, right)  * sYaw + axis * dotAR * (1.0 - cYaw);
    float3 rFacing = facing * cYaw + cross(axis, facing) * sYaw + axis * dotAF * (1.0 - cYaw);

    // Geometry -----------------------------------------------------------
    float  wAtV       = blade.width * (1.0 - v * 0.75);
    float3 curve      = _GrassBladeBezier(v, h, blade.tiltAngle, blade.bend);
    float  sideOffset = (side * 0.5) * wAtV;
    float3 localPos   = rFacing * curve.z + up * curve.y + rRight * sideOffset;
    float3 worldPos   = blade.position + localPos
                      + ApplyGrassWind(blade.windBase, v);

    // Curved fake normal -------------------------------------------------
    float3 tangentWS    = normalize(up * tLocal.y + rFacing * tLocal.z);
    float3 bladeNormal  = normalize(cross(rRight, tangentWS));
    float3 curved       = normalize(lerp(bladeNormal, rRight * sign(side + 1e-4), CurvedNAmt * abs(side)));

    // SSAO writes its source from the DepthNormals pre-pass. The curved fake
    // normal swings ±rRight across the blade width, so neighbouring screen
    // pixels see normals that point in opposite directions. URP's SSAO reads
    // that as a hard occlusion edge and stamps black blotches — worst at the
    // viewport corners, where its sample kernel is asymmetric. In those
    // passes only, write the blade's stable up axis so SSAO sees a smooth
    // surface. ForwardLit / GBuffer keep the curved normal for shading.
    #if defined(SHADERPASS) && \
        ((SHADERPASS == SHADERPASS_DEPTHNORMALSONLY) || \
         (SHADERPASS == SHADERPASS_DEPTHNORMALS))
        float3 outNormalWS = up;
    #else
        float3 outNormalWS = curved;
    #endif

    // Color (premixed convenience output) --------------------------------
    float  aoFactor = lerp(1.0 - AOStrength, 1.0, v);
    float3 color    = blade.color * aoFactor * lerp(1.0, TipBoost, v);

    PositionWS = worldPos;
    NormalWS   = outNormalWS;
    ColorBase  = color;
    UV         = float2(side * 0.5 + 0.5, v);
}

// --- Billboard Vertex Custom Function ---------------------------------------
//
// Drop-in replacement for GrassBlade_Vertex_float used in GrassBladeLowLOD.
// The single-triangle LowLOD mesh is too coarse for the per-cross-section twist
// used by HighLOD: the tip vertex (side=0) has no width contribution and for
// upright blades curve.z≈0, so the Rodrigues twist has no effect on actual
// vertex positions. The correct approach for a single triangle is to set
// right = cross(up, toCamFlat) — the width axis is always perpendicular to the
// camera, guaranteeing the face normal points toward it. The blade's own facing
// is kept for the tilt/lean geometry so each stalk retains its natural pose.
// Same parameter signature as GrassBlade_Vertex_float (CamFacingT and
// CamFacingMaxA are accepted but unused).
void GrassBlade_VertexBillboard_float(
    float3 PositionOS,
    float  InstanceID,
    float  CamFacingT,
    float  CamFacingMaxA,
    float  CurvedNAmt,
    float  AOStrength,
    float  TipBoost,
    out float3 PositionWS,
    out float3 NormalWS,
    out float3 ColorBase,
    out float2 UV)
{
    uint id = (uint)InstanceID;
    GrassBlade blade = _GrassBlades[id];

    float side = PositionOS.x;
    float v    = PositionOS.y;

    float3 up     = normalize(blade.normalUp);
    float3 facing = normalize(blade.facing - up * dot(blade.facing, up));
    ApplyBladeFold(v, facing, up, blade);

    // Billboard: drive the width axis from the camera direction so the triangle
    // face always points at the viewer. The tilt/lean still uses the blade's own
    // facing so stalks keep their original natural pose.
    float3 cameraWS  = _WorldSpaceCameraPos.xyz;
    float3 toCamera  = normalize(cameraWS - blade.position);
    float3 toCamFlat = normalize(toCamera - up * dot(toCamera, up) + float3(1e-6, 0, 0));
    float3 right     = normalize(cross(toCamFlat, up));

    float  h        = blade.height * blade.lodBlend;
    float  wAtV     = blade.width * (1.0 - v * 0.75);
    float3 curve    = _GrassBladeBezier(v, h, blade.tiltAngle, blade.bend);
    float3 localPos = facing * curve.z + up * curve.y + right * (side * 0.5 * wAtV);
    float3 worldPos = blade.position + localPos + ApplyGrassWind(blade.windBase, v);

    float3 tLocal      = _GrassBladeBezierTangent(v, h, blade.tiltAngle, blade.bend);
    float3 tangentWS   = normalize(up * tLocal.y + facing * tLocal.z);
    float3 bladeNormal = normalize(cross(right, tangentWS));
    float3 curved      = normalize(lerp(bladeNormal, right * sign(side + 1e-4), CurvedNAmt * abs(side)));

    #if defined(SHADERPASS) && \
        ((SHADERPASS == SHADERPASS_DEPTHNORMALSONLY) || \
         (SHADERPASS == SHADERPASS_DEPTHNORMALS))
        float3 outNormalWS = up;
    #else
        float3 outNormalWS = curved;
    #endif

    float  aoFactor = lerp(1.0 - AOStrength, 1.0, v);
    float3 color    = blade.color * aoFactor * lerp(1.0, TipBoost, v);

    PositionWS = worldPos;
    NormalWS   = outNormalWS;
    ColorBase  = color;
    UV         = float2(side * 0.5 + 0.5, v);
}

// --- Masks Custom Function --------------------------------------------------
//
// Separate from GrassBlade_Vertex_float so the existing graph stays intact.
// Add a NEW Custom Function node and wire:
//
//   Inputs:
//     V          (Float) - PositionOS.y. Use a Split node on Position(OS),
//                          take G channel.
//     AOStrength (Float) - exposed property, 0..1.
//
//   Outputs:
//     AO         (Float) - 0..1 multiplier; multiply into your final color to
//                          darken the blade root by AOStrength.
//                          AO = lerp(1 - AOStrength, 1, V)
//                          (V=0 root → 1 - AOStrength; V=1 tip → 1).
//     TipMask    (Float) - 0..1 raw tip mask. 0 at the root, 1 at the tip.
//                          Pipe through a Power or Smoothstep node if you need
//                          a sharper tip falloff (e.g. Power(TipMask, 3) keeps
//                          the highlight on the top third only).
void GrassBlade_Masks_float(
    float V,
    float AOStrength,
    out float AO,
    out float TipMask)
{
    AO      = lerp(1.0 - AOStrength, 1.0, V);
    TipMask = V;
}

#endif // ABYSSRISING_GRASS_BLADE_GRAPH_INCLUDED
