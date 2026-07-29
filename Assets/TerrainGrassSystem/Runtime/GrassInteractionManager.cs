using System.Collections.Generic;
using UnityEngine;

namespace TerrainGrassSystem
{
    /// <summary>
    /// Place one instance anywhere in the scene. It collects all active
    /// GrassInteractionSource components each frame and pushes their positions
    /// and radii to the global shader properties read by GrassInteraction.hlsl.
    /// Up to 8 sources are supported simultaneously.
    /// </summary>
    [AddComponentMenu("TerrainGrassSystem/Grass/Grass Interaction Manager")]
    public class GrassInteractionManager : MonoBehaviour
    {
        [SerializeField] private float _interactionStr;
        internal const int MaxSources = 8;

        static readonly List<GrassInteractionSource> s_Sources = new();
        static readonly int PropSources = Shader.PropertyToID("_GrassInteractionSources");
        static readonly int PropCount   = Shader.PropertyToID("_GrassInteractionCount");
        
        static readonly int PropInteractionStr  = Shader.PropertyToID("_InteractionStr");

        readonly Vector4[] _buffer = new Vector4[MaxSources];

        public static void Register(GrassInteractionSource src)
        {
            if (!s_Sources.Contains(src))
                s_Sources.Add(src);
        }

        public static void Unregister(GrassInteractionSource src) =>
            s_Sources.Remove(src);

        internal static int FillDepthOcclusionExclusions(Vector4[] destination)
        {
            if (destination == null) return 0;

            int count = 0;
            for (int i = 0; i < s_Sources.Count && count < destination.Length; ++i)
            {
                var src = s_Sources[i];
                if (src == null || !src.isActiveAndEnabled || !src.ExcludeFromDepthOcclusion) continue;

                Vector4 sphere = src.GetDepthOcclusionSphere();
                if (sphere.w <= 0f) continue;
                destination[count++] = sphere;
            }

            return count;
        }

        internal static int FillInteractionSources(Vector4[] destination)
        {
            if (destination == null) return 0;

            int count = 0;
            for (int i = 0; i < s_Sources.Count && count < destination.Length; ++i)
            {
                var src = s_Sources[i];
                if (src == null || !src.isActiveAndEnabled || src.Radius <= 0f) continue;

                Vector3 p = src.transform.position;
                Vector3 scale = src.transform.lossyScale;
                float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                destination[count++] = new Vector4(p.x, p.y, p.z, src.Radius * maxScale);
            }

            return count;
        }

        void LateUpdate()
        {
            int count = FillInteractionSources(_buffer);
            
            Shader.SetGlobalVectorArray(PropSources, _buffer);
            Shader.SetGlobalFloat(PropInteractionStr, _interactionStr);
            Shader.SetGlobalInt(PropCount, count);
        }

        void OnDestroy()
        {
            Shader.SetGlobalInt(PropCount, 0);
        }
    }
}
