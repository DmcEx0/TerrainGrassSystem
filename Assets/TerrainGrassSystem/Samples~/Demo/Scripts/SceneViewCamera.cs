using UnityEngine;

namespace TerrainGrassSystem.Demo
{
    /// <summary>
    /// Replicates Unity Scene-view camera controls at runtime.
    ///
    /// Controls:
    ///   Right-mouse held  — look around (yaw / pitch)
    ///   WASD / QE         — fly forward / left / back / right / down / up
    ///   Shift             — move faster (x3)
    ///   Scroll wheel      — dolly forward / backward
    ///   Middle-mouse drag — pan
    ///   Alt + Left-mouse  — orbit around pivot (last focus point)
    ///   F                 — focus on the object under the cursor (sets pivot)
    /// </summary>
    [AddComponentMenu("TerrainGrassSystem/Scene View Camera")]
    public class SceneViewCamera : MonoBehaviour
    {
        [Header("Look")]
        [SerializeField] float lookSensitivity = 2.5f;

        [Header("Fly")]
        [SerializeField] float moveSpeed      = 10f;
        [SerializeField] float fastMultiplier = 3f;
        [SerializeField] float scrollSpeed    = 5f;

        [Header("Pan")]
        [SerializeField] float panSensitivity = 0.3f;

        [Header("Orbit")]
        [SerializeField] float orbitSensitivity = 2.5f;

        // -- internal state -------------------------------------------------

        float   _yaw;
        float   _pitch;
        Vector3 _pivot;         // orbit / focus point in world space
        bool    _hasPivot;

        void Start()
        {
            var e = transform.eulerAngles;
            _yaw   = e.y;
            _pitch = e.x;
        }

        void Update()
        {
            bool rightMouse  = Input.GetMouseButton(1);
            bool middleMouse = Input.GetMouseButton(2);
            bool altHeld     = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool leftMouse   = Input.GetMouseButton(0);

            // -- Right-mouse fly mode ------------------------------------------
            if (rightMouse)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Look();
                Fly();
            }
            else
            {
                Cursor.lockState = CursorLockMode.None;
            }

            // -- Scroll wheel dolly (always active) ----------------------------
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f)
                transform.Translate(Vector3.forward * scroll * scrollSpeed * 10f, Space.Self);

            // -- Middle-mouse pan ----------------------------------------------
            if (middleMouse && !altHeld)
                Pan();

            // -- Alt + left-mouse orbit ----------------------------------------
            if (altHeld && leftMouse)
            {
                if (!_hasPivot)
                    SetPivotInFront();
                Orbit();
            }

            // -- F — focus -----------------------------------------------------
            if (Input.GetKeyDown(KeyCode.F))
                SetPivotInFront();
        }

        // -----------------------------------------------------------------------

        void Look()
        {
            float dx = Input.GetAxis("Mouse X") * lookSensitivity;
            float dy = Input.GetAxis("Mouse Y") * lookSensitivity;

            _yaw   += dx;
            _pitch  = Mathf.Clamp(_pitch - dy, -89f, 89f);

            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        void Fly()
        {
            float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? fastMultiplier : 1f);

            Vector3 dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir += Vector3.forward;
            if (Input.GetKey(KeyCode.S)) dir += Vector3.back;
            if (Input.GetKey(KeyCode.A)) dir += Vector3.left;
            if (Input.GetKey(KeyCode.D)) dir += Vector3.right;
            if (Input.GetKey(KeyCode.E)) dir += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) dir += Vector3.down;

            transform.Translate(dir * (speed * Time.deltaTime), Space.Self);
        }

        void Pan()
        {
            float dx = Input.GetAxis("Mouse X") * panSensitivity;
            float dy = Input.GetAxis("Mouse Y") * panSensitivity;
            transform.Translate(new Vector3(-dx, -dy, 0f), Space.Self);
        }

        void Orbit()
        {
            float dx = Input.GetAxis("Mouse X") * orbitSensitivity;
            float dy = Input.GetAxis("Mouse Y") * orbitSensitivity;

            _yaw   += dx;
            _pitch  = Mathf.Clamp(_pitch - dy, -89f, 89f);

            float dist = Vector3.Distance(transform.position, _pivot);
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            transform.position = _pivot - transform.forward * dist;
        }

        void SetPivotInFront()
        {
            // Raycast; fall back to a fixed distance if nothing is hit.
            if (Physics.Raycast(transform.position, transform.forward, out RaycastHit hit, 1000f))
                _pivot = hit.point;
            else
                _pivot = transform.position + transform.forward * 10f;

            _hasPivot = true;
        }
    }
}
