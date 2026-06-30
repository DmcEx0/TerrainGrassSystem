using UnityEngine;

namespace TerrainGrassSystem
{
    [AddComponentMenu("TerrainGrassSystem/Grass/Grass Interaction Source")]
    public class GrassInteractionSource : MonoBehaviour
    {
        [Tooltip("Radius of influence in world units. Grass inside this sphere is pushed away.")]
        [Min(0f)] public float Radius = 0.5f;

        void OnEnable()  => GrassInteractionManager.Register(this);
        void OnDisable() => GrassInteractionManager.Unregister(this);
    }
}
