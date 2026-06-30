#ifndef GRASS_INTERACTION_INCLUDED
#define GRASS_INTERACTION_INCLUDED

#define GRASS_MAX_INTERACTION_SOURCES 8

// Set every frame by GrassInteractionManager.cs via Shader.SetGlobalVectorArray.
// xyz = world-space centre of the source, w = radius of influence.
float4 _GrassInteractionSources[GRASS_MAX_INTERACTION_SOURCES];
int    _GrassInteractionCount;

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
        float  dist  = length(delta);
        float  rad   = src.w;
        if (dist < rad && dist > 0.001)
        {
            float t  = 1.0 - saturate(dist / rad);
            offset  += normalize(delta) * (t * t) * maxPush;
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
