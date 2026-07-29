#ifndef GRASS_INTERACTION_INCLUDED
#define GRASS_INTERACTION_INCLUDED

#define GRASS_MAX_INTERACTION_SOURCES 8

// Recorded by GrassInteractionManager through the grass RenderGraph draw pass
// (or pushed directly for the editor-only Scene View preview).
// xyz = world-space centre of the source, w = radius of influence.
float4 _GrassInteractionSources[GRASS_MAX_INTERACTION_SOURCES];
int    _GrassInteractionCount;

// Runtime blade rendering uses the interaction displacement precomputed once
// per blade by GrassCompute.compute. Two signed half floats store world XZ.
float3 ApplyGrassInteractionPacked(uint packedOffset, float v, float maxPush)
{
    float2 push = float2(
        f16tof32(packedOffset & 0xFFFFu),
        f16tof32(packedOffset >> 16));
    return float3(push.x, 0.0, push.y) * (maxPush * v * v);
}

// Returns a horizontal world-space push offset.
// The result is scaled by v² so the root stays fixed and the tip bends the most.
// maxPush — maximum displacement (metres) when a blade is right at the source centre.
float3 ApplyGrassInteraction(float3 bladeRoot, float v, float maxPush)
{
    float3 offset = float3(0.0, 0.0, 0.0);
    [loop]
    for (int i = 0; i < _GrassInteractionCount; i++)
    {
        float4 src   = _GrassInteractionSources[i];
        float3 delta = bladeRoot - src.xyz;
        delta.y      = 0.0;             // project to the XZ plane — horizontal push only
        float  rad   = src.w;
        float  distSq = dot(delta, delta);
        if (distSq < rad * rad && distSq > 1e-6)
        {
            float dist = sqrt(distSq);
            float t  = 1.0 - saturate(dist / rad);
            offset  += delta * rsqrt(distSq) * (t * t) * maxPush;
        }
    }
    return offset * (v * v);            // v² : root anchored, tip displaced most
}

// --- ShaderGraph Custom Function entry point --------------------------------
//
// Add this as a third node in the graph after GrassBlade_Vertex and
// GrassWind_Apply. Wire:
//
//   Inputs:
//     BladeRootWS       (Vector3) - from the BladeRootWS output of
//                                   GrassBlade_Vertex / GrassBlade_VertexBillboard.
//     V                 (Float)   - blade height 0..1. Use Split(Position OS).G —
//                                   the same node you already use for GrassWind_Apply.
//     MaxPush           (Float)   - exposed Float property, e.g. 0.5 m.
//
//   Outputs:
//     InteractionOffset (Vector3) - add to (PositionWS + WindOffset) before
//                                   driving the Vertex Position block:
//
//       PositionWS ──┐
//                    ├─ Add ──┐
//       WindOffset ──┘        ├─ Add ──► Vertex Position
//   InteractionOffset ────────┘
//
// To disable interaction: remove this node; leave the inner Add as-is.
void GrassInteraction_Apply_float(float3 BladeRootWS, float V, float MaxPush,
                                  out float3 InteractionOffset)
{
    InteractionOffset = ApplyGrassInteraction(BladeRootWS, V, MaxPush);
}

#endif // GRASS_INTERACTION_INCLUDED
