using UnityEngine;

namespace TerrainGrassSystem
{
    // Density, tile layout, and LOD distances. Shared between terrains so a
    // project can dial in performance once.
    [CreateAssetMenu(menuName = "TerrainGrassSystem/Grass/Terrain Settings", fileName = "GrassTerrainSettings")]
    public class GrassTerrainSettings : ScriptableObject
    {
        [Header("Tile Layout")]
        [Tooltip("Tile size in world meters. Smaller tiles = finer culling but more dispatches per frame.")]
        [Min(2f)] public float TileSize = 16f;

        [Tooltip("Number of blades per axis inside one tile. Total blades per tile = N*N. Grid is jittered so it does not look like a lattice.")]
        [Range(4, 256)] public int BladesPerTileAxis = 96;

        [Header("Culling")]
        [Tooltip("Tiles farther than this from the camera are not generated at all.")]
        [Min(2f)] public float TileCullDistance = 80f;

        [Tooltip("Per-blade max distance. Blades beyond this are dropped on the GPU.")]
        [Min(2f)] public float MaxBladeDistance = 60f;

        [Header("LOD")]
        [Tooltip("Blades closer than this are rendered with the high LOD mesh.")]
        [Min(0f)] public float HighLodDistance = 18f;

        [Tooltip("Width of the cross-fade band around lod transitions and the max distance. Bigger = softer, more overdraw.")]
        [Min(0.1f)] public float LodBlendBand = 6f;

        [Header("Distance Thinning")]
        [Tooltip("Distance from the camera at which grass starts to thin out. Blades closer than this are never thinned (high LOD is never touched either).")]
        [Min(0f)] public float ThinningStartDistance = 30f;

        [Tooltip("How aggressively grass thins toward the max blade distance. 0 = no thinning, 1 = maximum (almost all distant low-LOD blades dropped).")]
        [Range(0f, 1f)] public float ThinningStrength = 0f;

        [Header("Clumping noise")]
        [Tooltip("World-space scale for the clumping noise texture. Smaller = larger clumps.")]
        public float ClumpScale = 0.05f;

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
