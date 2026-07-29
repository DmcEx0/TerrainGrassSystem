using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TerrainGrassSystem
{
    /// <summary>
    /// Optional bridge for ShaderGraph materials. The default HLSL grass path
    /// reads registered sources directly during compute generation. When this
    /// component exists, GrassRenderPassFeature records the same source data as
    /// global shader properties for GrassInteraction.hlsl graph nodes.
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
        static GrassInteractionManager s_ActiveManager;

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

        void OnEnable()
        {
            if (s_ActiveManager == null) s_ActiveManager = this;
#if UNITY_EDITOR
            if (!Application.isPlaying) PushShaderGlobalsImmediate();
#endif
        }

        void OnDisable()
        {
            if (s_ActiveManager != this) return;
            s_ActiveManager = null;
            Shader.SetGlobalInt(PropCount, 0);
        }

#if UNITY_EDITOR
        void Update()
        {
            if (!Application.isPlaying && s_ActiveManager == this)
                PushShaderGlobalsImmediate();
        }

        void PushShaderGlobalsImmediate()
        {
            int count = FillInteractionSources(_buffer);
            Shader.SetGlobalVectorArray(PropSources, _buffer);
            Shader.SetGlobalFloat(PropInteractionStr, _interactionStr);
            Shader.SetGlobalInt(PropCount, count);
        }
#endif

        internal static void PushShaderGlobals(RasterCommandBuffer cmd)
        {
            var manager = s_ActiveManager;
            if (manager == null) return;

            int count = FillInteractionSources(manager._buffer);
            cmd.SetGlobalVectorArray(PropSources, manager._buffer);
            cmd.SetGlobalFloat(PropInteractionStr, manager._interactionStr);
            cmd.SetGlobalInt(PropCount, count);
        }
    }
}
