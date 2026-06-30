using UnityEngine;

public class TestInteraction : MonoBehaviour
{
    [SerializeField] private CapsuleCollider _collider;
    
    void Update()
    {
        Shader.SetGlobalVector("_PlayerPosition", transform.position + Vector3.up * _collider.radius);
    }
}
