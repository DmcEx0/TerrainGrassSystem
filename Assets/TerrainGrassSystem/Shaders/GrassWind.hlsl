#ifndef ABYSSRISING_GRASS_WIND_INCLUDED
#define ABYSSRISING_GRASS_WIND_INCLUDED

// Wind is sampled ONCE PER BLADE in the compute pass (GrassCompute.compute,
// ComputeWindBases) and the resulting v-independent magnitude is stored on
// GrassBlade.windBase. The render shaders only need the global wind direction
// to turn that scalar back into a horizontal sway vector — there is no texture
// fetch in the vertex stage anymore.
CBUFFER_START(GrassWindCB)
    float4 _GrassWindParams;       // x=strength, y=frequency, z=scrollSpeed, w=gustSpeed (kept for layout; consumed in compute)
    float4 _GrassWindDirection;    // xz = primary direction (unit), w = time
CBUFFER_END

// Horizontal sway in world space. windBase is the per-blade magnitude from the
// compute pass; v is the blade height parameter (0..1) so the tip displaces
// more than the root.
float3 ApplyGrassWind(float windBase, float v)
{
    float2 dir = _GrassWindDirection.xz;
    return float3(dir.x, 0.0, dir.y) * (windBase * v * v);
}

// --- ShaderGraph Custom Function entry point --------------------------------
//
// Use this as a second Custom Function node in the graph alongside
// GrassBlade_Vertex_float. Wire:
//
//   Inputs:
//     WindBase (Float) - from the WindBase output of GrassBlade_Vertex /
//                        GrassBlade_VertexBillboard. This is the per-blade
//                        magnitude baked in the compute pass.
//     V        (Float) - blade height parameter 0..1. Use a Split node on
//                        Position(OS) and take the G channel (= PositionOS.y).
//
//   Outputs:
//     WindOffset (Vector3) - world-space horizontal displacement. ADD this to
//                            PositionWS from GrassBlade_Vertex and feed the sum
//                            into the Vertex Position block.
//
// To disable wind entirely: do not add this node; wire PositionWS directly.
void GrassWind_Apply_float(float WindBase, float V, out float3 WindOffset)
{
    WindOffset = ApplyGrassWind(WindBase, V);
}

#endif // ABYSSRISING_GRASS_WIND_INCLUDED
