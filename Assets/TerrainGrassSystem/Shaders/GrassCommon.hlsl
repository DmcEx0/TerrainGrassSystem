#ifndef ABYSSRISING_GRASS_COMMON_INCLUDED
#define ABYSSRISING_GRASS_COMMON_INCLUDED

// Per-blade payload produced by the compute shader and consumed by the render shader.
// Keep layout identical to GrassBlade struct in C# (StructLayout, Pack=4). 80 bytes.
struct GrassBlade
{
    float3 position;   // world-space root
    float  tiltAngle;  // forward tilt in radians (rotation around the blade right axis)

    float3 facing;     // unit vector roughly in XZ plane; blade width axis is perpendicular to it
    float  height;     // total blade height in meters

    float3 color;      // tinted base color (root)
    float  width;      // base width in meters

    float  bend;       // signed Bezier control-point offset along facing
    float  lodBlend;   // 1 = freshly born at this LOD, 0 = about to fade out
    uint   hash;       // per-blade random hash
    uint   typeIndex;  // flag bits. Bit 0 = folded (short blade implemented as
                       // one long blade folded at v=0.5 so a single mesh
                       // instance renders as two short blades sharing a root).
                       // Remaining bits reserved.

    float3 normalUp;   // per-blade up axis (blended terrain normal vs world up by GrassType.slopeFollow)
    float  windBase;   // per-blade wind sway magnitude (v-independent), computed in CSGenerate
};

// Grass type description shared between CPU and GPU.
// Layout MUST match GrassTypeParamsGpu in GrassBlade.cs (96 bytes / 6 vec4s).
struct GrassTypeParams
{
    float baseHeight;          float heightVariance;
    float baseWidth;           float widthVariance;
    float baseTilt;            float tiltVariance;
    float bend;                float densityMultiplier;
    float3 baseColor;          float facingVariance;       // radians; per-blade jitter added to the dir signal
    float3 tipColor;           float heightRandomness;     // meters; per-blade jitter added to noise-driven blade height
    float facingRandomness;    float foldHeight;           // 0..1; blends per-blade facing | meters; blades shorter than this render as folded V (0 = disabled)
    float slopeFollow;         float smallBladeHeight;     // 0..1; slope-follow blend | meters; non-folded blades shorter than this route to LowLOD (0 = disabled)
    float widthHeightCoupling; float maxBladesPerCell;     // 0..1; per-blade width = baseWidth * lerp(1, bh/baseHeight, coupling) | clamped max blades per tuft cell
    float windHeightFalloff;   float _pad2;                // 0..1; how strongly wind sway scales down for short blades (0 = uniform, 1 = short blades nearly still)
};

// ---- Hashing ----------------------------------------------------------------

uint WangHash(uint seed)
{
    seed = (seed ^ 61u) ^ (seed >> 16u);
    seed *= 9u;
    seed = seed ^ (seed >> 4u);
    seed *= 0x27d4eb2du;
    seed = seed ^ (seed >> 15u);
    return seed;
}

uint Hash2(uint2 v) { return WangHash(v.x * 1973u + v.y * 9277u); }

float HashToFloat(uint h) { return frac(float(h) * (1.0 / 4294967296.0)); }

float2 HashToFloat2(uint h)
{
    return float2(HashToFloat(h), HashToFloat(WangHash(h ^ 0x9E3779B9u)));
}

// ---- Folded-blade vertex transform -----------------------------------------
//
// A "folded" blade renders one long blade mesh as two short blades sharing the
// same root: the mesh is bent at v=0.5 so that vertex becomes the new root and
// both v=0 and v=1 become tips of two sub-blades that fan out left/right. This
// halves draw work for short-grass areas — one mesh instance, two visible
// blades — while keeping the rest of the shader code unchanged.
//
// Call once at the top of every vertex stage. After this:
//   - v parameter is "0 at this sub-blade's root, 1 at its tip" so all
//     downstream code (Bezier, twist ramp, AO, UV, wind) just works.
//   - facing is rotated for this sub-blade so the two halves spread into a V.
//
// Non-folded blades pass through unchanged.
#define FOLDED_SPREAD_ANGLE 0.35  // ~20 deg per side, total spread ~40 deg

// Rotates `facing` around `up` by `angle` (Rodrigues). `up` must be unit.
float3 RotateAroundUp(float3 facing, float3 up, float angle)
{
    float ca = cos(angle);
    float sa = sin(angle);
    return facing * ca + cross(up, facing) * sa + up * dot(up, facing) * (1.0 - ca);
}

void ApplyBladeFold(inout float v, inout float3 facing, float3 up, GrassBlade blade)
{
    if ((blade.typeIndex & 1u) == 0u) return;

    float sideSign = (v < 0.5) ? -1.0 : 1.0;
    v = abs(v * 2.0 - 1.0);

    facing = RotateAroundUp(facing, up, sideSign * FOLDED_SPREAD_ANGLE);
}

// ---- Frustum culling --------------------------------------------------------

// Six planes in (nx, ny, nz, d) form. plane.xyz . point + plane.w >= 0 means inside.
float4 _GrassFrustumPlanes[6];

bool SphereInFrustum(float3 center, float radius)
{
    [unroll] for (int i = 0; i < 6; ++i)
    {
        float d = dot(_GrassFrustumPlanes[i].xyz, center) + _GrassFrustumPlanes[i].w;
        if (d < -radius) return false;
    }
    return true;
}

#endif // ABYSSRISING_GRASS_COMMON_INCLUDED
