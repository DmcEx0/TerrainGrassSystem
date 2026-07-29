using UnityEngine;

namespace TerrainGrassSystem
{
    [AddComponentMenu("TerrainGrassSystem/Grass/Grass Interaction Source")]
    public class GrassInteractionSource : MonoBehaviour
    {
        [Tooltip("Radius of influence in world units. Grass inside this sphere is pushed away.")]
        [Min(0f)] public float Radius = 0.5f;

        [Header("Depth Occlusion")]
        [Tooltip("Prevents this object (normally the player) from being treated as a grass depth-occlusion blocker. This keeps grass behind a third-person character from disappearing.")]
        public bool ExcludeFromDepthOcclusion = true;

        [Tooltip("Local-space centre of the conservative sphere used to protect grass from depth occlusion.")]
        public Vector3 DepthOcclusionCenter = Vector3.up;

        [Tooltip("Radius of the conservative depth-occlusion sphere. Set to 0 to reuse the interaction Radius.")]
        [Min(0f)] public float DepthOcclusionRadius = 1f;

        void OnEnable()  => GrassInteractionManager.Register(this);
        void OnDisable() => GrassInteractionManager.Unregister(this);

        internal Vector4 GetDepthOcclusionSphere()
        {
            Vector3 centre = transform.TransformPoint(DepthOcclusionCenter);
            Vector3 scale = transform.lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            float radius = (DepthOcclusionRadius > 0f ? DepthOcclusionRadius : Radius) * maxScale;
            return new Vector4(centre.x, centre.y, centre.z, radius);
        }
    }
}
