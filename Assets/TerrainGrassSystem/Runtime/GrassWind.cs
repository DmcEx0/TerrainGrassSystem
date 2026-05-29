using UnityEngine;

namespace TerrainGrassSystem
{
    // Owns the wind state pushed to all grass shaders. Drop one of these
    // anywhere in the scene; GrassRenderer instances find it by Object.FindFirstObjectByType.
    [ExecuteAlways]
    [AddComponentMenu("TerrainGrassSystem/Grass/Grass Wind")]
    public class GrassWind : MonoBehaviour
    {
        static readonly int s_WindNoiseId      = Shader.PropertyToID("_GrassWindNoise");
        static readonly int s_WindParamsId     = Shader.PropertyToID("_GrassWindParams");
        static readonly int s_WindDirectionId  = Shader.PropertyToID("_GrassWindDirection");

        [Header("Noise")]
        [Tooltip("A seamless Perlin noise texture. R is used for the primary gust, G for a secondary octave.")]
        public Texture2D WindNoise;

        [Header("Strength")]
        [Min(0f)] public float Strength = 0.15f;

        [Header("Sampling")]
        [Tooltip("World-space frequency of the noise sampling. Smaller = larger gust patterns.")]
        public float Frequency    = 0.06f;
        [Tooltip("Scroll speed of the primary noise octave.")]
        public float ScrollSpeed  = 0.6f;
        [Tooltip("Scroll speed of the secondary octave; mismatch with primary keeps the wind looking organic.")]
        public float GustSpeed    = 0.9f;

        [Header("Direction")]
        [Range(0f, 360f)] public float DirectionDegrees = 45f;

        // Last values pushed this frame. GrassTerrain reads these to bind the
        // same wind to the compute generation pass (compute samples wind once
        // per blade). Globals alone can't be relied on across the compute
        // boundary, so we hand the values over explicitly.
        public Texture2D ActiveNoise => WindNoise;
        public Vector4 ActiveParams { get; private set; }
        public Vector4 ActiveDirection { get; private set; }

        void OnEnable() { PushToShader(); }

        void Update() { PushToShader(); }

        // Allow editor previews without a play loop.
        void OnValidate() { PushToShader(); }

        void PushToShader()
        {
            if (WindNoise != null)
            {
                Shader.SetGlobalTexture(s_WindNoiseId, WindNoise);
            }
            ActiveParams = new Vector4(Strength, Frequency, ScrollSpeed, GustSpeed);
            Shader.SetGlobalVector(s_WindParamsId, ActiveParams);

            float rad = DirectionDegrees * Mathf.Deg2Rad;
            float t;
#if UNITY_EDITOR
            t = Application.isPlaying ? Time.time : (float)UnityEditor.EditorApplication.timeSinceStartup;
#else
            t = Time.time;
#endif
            ActiveDirection = new Vector4(Mathf.Cos(rad), 0f, Mathf.Sin(rad), t);
            Shader.SetGlobalVector(s_WindDirectionId, ActiveDirection);
        }
    }
}
