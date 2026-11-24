using UnityEngine;

namespace SandDefenseWithSand.Hands
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    [DefaultExecutionOrder(150)] // Read the hands after their collision and IK updates.
    public sealed class HandCameraTilt : MonoBehaviour
    {
        [SerializeField] private float _maxPitch = 7f;
        [SerializeField] private float _smoothTime = .16f;

        private Quaternion _restRotation;
        private float _pitch;
        private float _pitchVelocity;

        private void OnEnable()
        {
            _restRotation = transform.localRotation;
            _pitch = 0f;
            _pitchVelocity = 0f;
        }

        private void LateUpdate()
        {
            float digWeight = 0f;
            bool handMoved = false;
            foreach (HandPosition hand in HandPosition.ActiveHands)
            {
                if (!hand.TryGetDigViewWeight(out float weight)) continue;
                handMoved = true;
                digWeight = Mathf.Max(digWeight, weight);
            }

            if (!handMoved)
            {
                _pitchVelocity = 0f;
                return;
            }

            float targetPitch = Mathf.SmoothStep(0f, _maxPitch, digWeight);
            _pitch = Mathf.SmoothDamp(_pitch, targetPitch, ref _pitchVelocity, _smoothTime);
            transform.localRotation = _restRotation * Quaternion.AngleAxis(_pitch, Vector3.right);
        }

        internal Quaternion AimFromView
        {
            get
            {
                // Rotate camera rays back to the authored view. This also works
                // with a parent because the pitch is applied around the camera's X axis.
                Quaternion view = transform.rotation;
                return view * Quaternion.AngleAxis(-_pitch, Vector3.right) * Quaternion.Inverse(view);
            }
        }

        private void OnDisable()
        {
            transform.localRotation = _restRotation;
        }
    }
}
