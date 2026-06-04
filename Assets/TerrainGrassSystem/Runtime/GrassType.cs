using UnityEngine;

namespace TerrainGrassSystem
{
    // Art-tweakable parameters for a single grass species. Several of these can
    // be assigned to a GrassTerrain — the grass mask texture selects between
    // them per pixel via its GBA channels.
    [CreateAssetMenu(menuName = "TerrainGrassSystem/Grass/Grass Type", fileName = "GrassType")]
    public class GrassType : ScriptableObject
    {
        [Header("Height")]
        [Min(0f)] public float BaseHeight       = 0.8f;
        [Min(0f)]
        [Tooltip("Noise-driven height variation. Neighbours get similar values — produces visible patches of taller / shorter grass following the clump noise.")]
        public float HeightVariance             = 0.4f;
        [Min(0f)]
        [Tooltip("Per-blade random height jitter, in meters. Independent of the clump noise — every blade gets its own roll, so even within a single patch some blades are taller or shorter than their neighbours.")]
        public float HeightRandomness           = 0.2f;

        [Header("Width")]
        [Min(0f)] public float BaseWidth        = 0.04f;
        [Min(0f)] public float WidthVariance    = 0.02f;
        [Range(0f, 1f)]
        [Tooltip("How much per-blade width scales with per-blade height. 0 = width independent of height (legacy). 1 = width fully scales by (height / BaseHeight), so a blade at half its base height is also half-width. Useful with strong HeightVariance / mask.g painting so short blades read as proportionally thinner rather than stubby.")]
        public float WidthHeightCoupling        = 0.581f;

        [Header("Shape")]
        [Tooltip("Forward tilt at the blade tip, in radians.")]
        public float BaseTilt                   = -0.4f;
        public float TiltVariance               = 0.1f;
        [Range(0f, 1f)]
        [Tooltip("How much the blade aligns with the terrain slope. 0 = blade always grows straight up regardless of terrain. 1 = blade grows perpendicular to the terrain surface. Values in between blend the two.")]
        public float SlopeFollow                = 0f;
        [Tooltip("Per-blade random rotation around the vertical axis, in radians. Added on top of the noise-driven facing direction. 0 = neighbours all face the same way (visible edge-on lines / bald spots when the camera spins). ~0.5 (≈28°) gives good local variation; π (≈3.14) is fully random.")]
        public float FacingVariance             = 3f;
        [Range(0f, 1f)]
        [Tooltip("Blend per-blade facing between the clump-noise direction and a fully random per-blade angle. 0 = blades follow the noise (visible direction clumps). 1 = each blade rolls its own random rotation, noise direction is ignored. Independent of FacingVariance.")]
        public float FacingRandomness           = 0.05f;
        [Tooltip("Bezier mid-control offset (curvature). Positive arches blade forward along its facing axis.")]
        public float Bend                       = 0f;

        [Header("Density")]
        [Range(0f, 4f)] public float DensityMultiplier = 1.0f;
        [Range(1, 16)]
        [Tooltip("Maximum blades per cell when the Clamp brush (mask.b) is fully painted. At B=0 you still get the natural 1-2 baseline; at B=1 the per-cell count ramps toward this max. Raise to give the Clamp brush more visible 'thickening' headroom. NOTE: blade buffers (MaxHighLodBlades / MaxLowLodBlades in GrassTerrainSettings) cap the total per frame — bump them in tandem if you crank this and see grass dropping out.")]
        public int MaxBladesPerCell = 16;

        [Header("Short-blade optimizations")]
        [Min(0f)]
        [Tooltip("Blades shorter than this (meters) are rendered as one mesh folded into a V — a single instance draws as two short sub-blades sharing a root, doubling visible mass in low-height areas for free. Set to 0 to disable folding entirely. Typical: 30-50% of BaseHeight.")]
        public float FoldHeight = 0f;
        [Min(0f)]
        [Tooltip("Non-folded blades shorter than this (meters) are routed to the low-LOD mesh — 1 triangle instead of ~7. Folded blades are exempt (their geometry needs the v=0.5 vertex that low-LOD lacks). Set to 0 to disable. Typical: at or just below FoldHeight so anything short either folds or simplifies — no blade pays full mesh cost for a 10 cm sprout.")]
        public float SmallBladeHeight = 0.1f;

        [Header("Wind")]
        [Range(0f, 1f)]
        [Tooltip("How strongly wind sway scales down for short blades. 0 = every blade sways with the same base magnitude (legacy). 1 = sway scales with (height / BaseHeight)^2, so a blade at half its base height catches ~1/4 the wind and very short sprouts stay almost still. Use to keep noise/wind from visibly jittering low grass and ground cover.")]
        public float WindHeightFalloff          = 0.75f;

        [Header("Color")]
        [ColorUsage(false, false)] public Color BaseColor = new Color(0.18f, 0.35f, 0.10f);
        [ColorUsage(false, false)] public Color TipColor  = new Color(0.55f, 0.78f, 0.30f);

        public GrassTypeParamsGpu ToGpu()
        {
            return new GrassTypeParamsGpu
            {
                BaseHeight          = BaseHeight,
                HeightVariance      = HeightVariance,
                BaseWidth           = BaseWidth,
                WidthVariance       = WidthVariance,
                BaseTilt            = BaseTilt,
                TiltVariance        = TiltVariance,
                Bend                = Bend,
                DensityMultiplier   = DensityMultiplier,
                BaseColor           = new Vector3(BaseColor.r, BaseColor.g, BaseColor.b),
                FacingVariance      = FacingVariance,
                TipColor            = new Vector3(TipColor.r,  TipColor.g,  TipColor.b),
                HeightRandomness    = HeightRandomness,
                FacingRandomness    = FacingRandomness,
                FoldHeight          = FoldHeight,
                SlopeFollow         = SlopeFollow,
                SmallBladeHeight    = SmallBladeHeight,
                WidthHeightCoupling = WidthHeightCoupling,
                MaxBladesPerCell    = MaxBladesPerCell,
                WindHeightFalloff   = WindHeightFalloff,
            };
        }
    }
}
