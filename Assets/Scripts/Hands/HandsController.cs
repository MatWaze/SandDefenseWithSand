using UnityEngine;
using UnityEngine.InputSystem;

namespace SandDefenseWithSand.Hands
{
    public class HandsController : MonoBehaviour
    {
        public enum Side { Left, Right }

        [SerializeField] private Side side;

        [SerializeField] private Transform leftWrist;
        [SerializeField] private Transform leftArmTarget;
        [SerializeField] private Transform leftShoulder;
        [SerializeField] private Transform leftForearm;
        [SerializeField] private Transform body;

        [SerializeField] private float sensitivity = 0.01f;
        [SerializeField] private float maxReach = 0.5f;

        [Header("LEFT arm limits (body local space)")]
        [SerializeField] private float minX = -0.5f;
        [SerializeField] private float maxX = 0f;
        [SerializeField] private float minZ = -0.5f;
        [SerializeField] private float maxZ = 0.5f;
        [SerializeField] private float minY = -0.3f;
        [SerializeField] private float maxY = 0.6f;

        [Header("RIGHT arm limits (body local space)")]
        [SerializeField] private float rMinX = 0f;
        [SerializeField] private float rMaxX = 0.5f;
        [SerializeField] private float rMinZ = -0.5f;
        [SerializeField] private float rMaxZ = 0.5f;
        [SerializeField] private float rMinY = -0.3f;
        [SerializeField] private float rMaxY = 0.6f;

        private Vector3 offset;
        private float armLength;
        private bool initialized; // false until the player first grabs control

        private void Start()
        {
            armLength = Vector3.Distance(leftShoulder.position, leftForearm.position)
                + Vector3.Distance(leftForearm.position, leftWrist.position);
        }

        private void Update()
        {
            if (Mouse.current == null) return;

            bool isPressed = (side == Side.Left)
                ? Mouse.current.leftButton.isPressed
                : Mouse.current.rightButton.isPressed;

            // Do NOTHING until the player first presses the button.
            // The target stays exactly where it was placed in the scene.
            if (!isPressed && !initialized) return;

            // First time the button is pressed: capture the offset
            // from wherever the target currently is (the scene position)
            if (isPressed && !initialized)
            {
                offset = leftArmTarget.position - leftWrist.position;
                initialized = true;
            }

            if (isPressed)
            {
                Vector2 delta = Mouse.current.delta.ReadValue();

                // Mirror horizontal input for the right arm
                float dirX = (side == Side.Left) ? 1f : -1f;

                offset.x += delta.x * sensitivity * dirX;
                offset.z += delta.x * sensitivity;
                offset.y += delta.y * sensitivity;
                offset.x += delta.y * sensitivity * dirX;
            }

            Vector3 local = body.InverseTransformDirection(offset);

            if (side == Side.Left)
            {
                local.x = Mathf.Clamp(local.x, minX, maxX);
                local.z = Mathf.Clamp(local.z, minZ, maxZ);
                local.y = Mathf.Clamp(local.y, minY, maxY);
            }
            else
            {
                local.x = Mathf.Clamp(local.x, rMinX, rMaxX);
                local.z = Mathf.Clamp(local.z, rMinZ, rMaxZ);
                local.y = Mathf.Clamp(local.y, rMinY, rMaxY);
            }

            local = Vector3.ClampMagnitude(local, maxReach);
            offset = body.TransformDirection(local);

            Vector3 shoulderPos = leftShoulder.position;
            Vector3 targetPos = leftWrist.position + offset;

            Vector3 fromShoulder = targetPos - shoulderPos;
            float maxDist = armLength * 0.85f;

            if (fromShoulder.magnitude > maxDist)
            {
                targetPos = shoulderPos + fromShoulder.normalized * maxDist;
            }

            leftArmTarget.position = targetPos;
        }
    }
}
