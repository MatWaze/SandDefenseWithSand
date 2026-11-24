using UnityEngine;
using SandDefenseWithSand.Hands;

namespace SandDefenseWithSand.Clay
{
    /// <summary>Tracks the most recent deliberate hand motion for a figurine's gaze.</summary>
    internal sealed class ClayxelsHandLook
    {
        private Transform _trackedLeftHand;
        private Transform _trackedRightHand;
        private Transform _lastMovedHand;
        private Vector3 _lastLookPosition;
        private Vector3 _previousLeftHandPosition;
        private Vector3 _previousRightHandPosition;

        public void Reset(Transform leftHand, Transform rightHand)
        {
            _lastMovedHand = null;
            _trackedLeftHand = leftHand;
            _trackedRightHand = rightHand;

            if (_trackedLeftHand != null)
                _previousLeftHandPosition = _trackedLeftHand.position;

            if (_trackedRightHand != null)
                _previousRightHandPosition = _trackedRightHand.position;
        }

        public bool TryGetFocus(Transform leftHand, Transform rightHand, float movementThreshold, out Vector3 focus)
        {
            if (_lastMovedHand != null && (!_lastMovedHand.gameObject.activeInHierarchy ||
                (_lastMovedHand != leftHand && _lastMovedHand != rightHand)))
            {
                _lastMovedHand = null;
            }

            float leftMovement = SampleHandMovement(leftHand, ref _trackedLeftHand, ref _previousLeftHandPosition,
                out int leftDragStart);
            float rightMovement = SampleHandMovement(rightHand, ref _trackedRightHand, ref _previousRightHandPosition,
                out int rightDragStart);

            // Both controls read the same pointer. Contact changes how far each arm
            // can travel, so movement magnitude cannot choose between held hands.
            if (leftDragStart >= 0 || rightDragStart >= 0)
            {
                bool preferLeft = leftDragStart > rightDragStart ||
                    (leftDragStart == rightDragStart && _lastMovedHand != rightHand);
                if (preferLeft) rightMovement = 0f;
                else leftMovement = 0f;
            }

            float deadZone = movementThreshold * 60f * Time.deltaTime;
            if (Mathf.Max(leftMovement, rightMovement) > deadZone * deadZone)
            {
                if (leftMovement > rightMovement)
                    _lastMovedHand = leftHand;
                else if (rightMovement > leftMovement || _lastMovedHand == null)
                    _lastMovedHand = rightHand;
                _lastLookPosition = _lastMovedHand.position;
            }

            focus = _lastLookPosition;
            return _lastMovedHand != null;
        }

        private static float SampleHandMovement(Transform hand, ref Transform trackedHand, ref Vector3 previousPosition,
            out int dragStart)
        {
            dragStart = -1;
            if (hand == null || !hand.gameObject.activeInHierarchy)
            {
                trackedHand = null;
                return 0f;
            }

            Vector3 position = hand.position;
            float movement = trackedHand == hand ? (position - previousPosition).sqrMagnitude : 0f;
            previousPosition = position;
            trackedHand = hand;
            foreach (HandPosition controller in HandPosition.ActiveHands)
            {
                if (!controller.ControlsHand(hand)) continue;
                dragStart = controller.DragStartFrame;
                return controller.WasPushedByFigurine ? 0f : movement;
            }
            return movement;
        }
    }
}
