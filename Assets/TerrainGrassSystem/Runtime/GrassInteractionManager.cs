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
        const int MaxSources = 8;

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

        void LateUpdate()
        {
            int count = 0;
            foreach (var src in s_Sources)
            {
                if (count >= MaxSources) break;
                if (src == null || !src.isActiveAndEnabled) continue;
                Vector3 p = src.transform.position;
                _buffer[count++] = new Vector4(p.x, p.y, p.z, src.Radius);
            }
            
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
