using UnityEngine;

namespace TerrainGrassSystem
{
    // Density, tile layout, and LOD distances. Shared between terrains so a
    // project can dial in performance once.
    [CreateAssetMenu(menuName = "TerrainGrassSystem/Grass/Terrain Settings", fileName = "GrassTerrainSettings")]
    public class GrassTerrainSettings : ScriptableObject
    {
        [Header("Tile Layout")]
        [Tooltip("Tile size in world meters. Smaller tiles = finer culling but more CPU tile checks and descriptors.")]
        [Min(2f)] public float TileSize = 10f;

        [Tooltip("Number of blades per axis inside one tile. Total blades per tile = N*N. Grid is jittered so it does not look like a lattice.")]
        [Range(4, 256)] public int BladesPerTileAxis = 135;

        [Header("Culling")]
        [Tooltip("Tiles farther than this from the camera are not generated at all.")]
        [Min(2f)] public float TileCullDistance = 130f;

        [Tooltip("Per-blade max distance. Blades beyond this are dropped on the GPU.")]
        [Min(2f)] public float MaxBladeDistance = 150f;

        [Tooltip("Extra perspective-frustum angle generated outside the visible screen. Prevents side bald strips when the camera turns quickly or is finalized after grass culling. 0 = exact frustum.")]
        [Range(0f, 20f)] public float FrustumPaddingDegrees = 8f;

        [Tooltip("Rejects blades hidden behind opaque camera depth before they are appended to the draw buffers.")]
        public bool EnableDepthOcclusion = false;

        [Tooltip("World-space safety bias in meters. Higher values keep more grass near depth edges and reduce popping.")]
        [Min(0f)] public float DepthOcclusionBias = 0.25f;

        [Tooltip("Scales the blade bounding radius used by the depth test. Lower values cull more aggressively.")]
        [Range(0f, 2f)] public float DepthOcclusionRadiusScale = 0.8f;

        [Tooltip("Extra cross samples around the blade center in screen pixels. 0 = center only, 1-4 = more conservative edge handling.")]
        [Range(0, 4)] public int DepthOcclusionSampleRadiusPixels = 2;

        [Header("LOD")]
        [Tooltip("Blades closer than this are rendered with the high LOD mesh.")]
        [Min(0f)] public float HighLodDistance = 4.1f;

        [Tooltip("Width of the stochastic transition around the LOD boundary and the fade near max distance. Bigger = softer transitions.")]
        [Min(0.1f)] public float LodBlendBand = 8f;

        [Header("Distance Thinning")]
        [Tooltip("Distance from the camera at which grass starts to thin out. Blades closer than this are never thinned (high LOD is never touched either).")]
        [Min(0f)] public float ThinningStartDistance = 30f;

        [Tooltip("How aggressively grass thins toward the max blade distance. 0 = no thinning, 1 = maximum (almost all distant low-LOD blades dropped).")]
        [Range(0f, 1f)] public float ThinningStrength = 1f;

        [Header("Clumping noise")]
        [Tooltip("World-space scale for the clumping noise texture. Smaller = larger clumps.")]
        public float ClumpScale = 0f;

        [Header("Buffer Capacity")]
        [Tooltip("Maximum blades the high-LOD buffer can hold across all tiles in a frame.")]
        [Min(1024)] public int MaxHighLodBlades = 600_000;
        [Tooltip("Maximum blades the low-LOD buffer can hold across all tiles in a frame.")]
        [Min(1024)] public int MaxLowLodBlades  = 800_000;

        public float ComputeMaxDistanceSq()
        {
            return MaxBladeDistance * MaxBladeDistance;
        }
    }
}
