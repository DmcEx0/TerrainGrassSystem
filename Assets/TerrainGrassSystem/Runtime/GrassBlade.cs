using System.Runtime.InteropServices;
using UnityEngine;

namespace TerrainGrassSystem
{
    // Mirror of the GrassBlade struct in GrassCommon.hlsl. Keep the field order
    // and packing identical — the GPU reads this from a StructuredBuffer.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GrassBlade
    {
        public Vector3 Position;
        public float   TiltAngle;

        public Vector3 Facing;
        public float   Height;

        public Vector3 Color;
        public float   Width;

        public float   Bend;
        public float   LodBlend;
        public uint    InteractionPacked; // two half floats: normalized interaction push in world XZ
        public uint    TypeIndex;

        public Vector3 NormalUp;   // per-blade up axis (blended terrain normal vs world up by GrassType.SlopeFollow)
        public float   WindBase;   // per-blade wind sway magnitude (v-independent), computed in CSGenerate

        public const int SizeBytes = 80;
    }

    // Mirror of GrassTypeParams in GrassCommon.hlsl.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct GrassTypeParamsGpu
    {
        public float BaseHeight;          public float HeightVariance;
        public float BaseWidth;           public float WidthVariance;
        public float BaseTilt;            public float TiltVariance;
        public float Bend;                public float DensityMultiplier;
        public Vector3 BaseColor;         public float FacingVariance;
        public Vector3 TipColor;          public float HeightRandomness;
        public float FacingRandomness;    public float FoldHeight;
        public float SlopeFollow;         public float SmallBladeHeight;
        public float WidthHeightCoupling; public float MaxBladesPerCell;
        public float WindHeightFalloff;   public float _pad2;

        public const int SizeBytes = 96;
    }
}
