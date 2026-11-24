using UnityEngine;
using SandDefenseWithSand.Hands;

namespace SandDefenseWithSand.Tools
{
    /// <summary>
    /// Marker for items that can be picked up. A usable hand tool must also contain
    /// one concrete Tool component, such as ShovelTool or CarverTool.
    /// </summary>
    [DisallowMultipleComponent]
    public class ItemGrabbable : MonoBehaviour
    {
        [Tooltip("Optional explicit Tool component. If empty, the first Tool component on this object or its children is used.")]
        [SerializeField] private Tool _tool;
        private HandPosition _holdingHand;

        public Tool AttachedTool
        {
            get
            {
                ResolveTool();
                return _tool;
            }
        }

        private void Awake()
        {
            ResolveTool();
        }

        internal bool TryMount(Transform grip, Vector3 localOffset, bool isShovel, out Transform originalParent)
        {
            originalParent = transform.parent;
            transform.SetParent(grip, false);
            transform.SetLocalPositionAndRotation(localOffset, Quaternion.identity);
            if (isShovel)
            {
                _holdingHand = HandPosition.FindHandForGrip(grip);
                _holdingHand?.TrackHeldShovel(transform);
            }
            return true;
        }

        internal void Unmount(Transform originalParent)
        {
            if (_holdingHand != null)
                _holdingHand.ReleaseShovel();
            _holdingHand = null;
            transform.SetParent(originalParent, true);
        }

        internal Rigidbody FindOwnedRigidbody()
        {
            Rigidbody body = GetComponent<Rigidbody>();
            return body != null ? body : GetComponentInChildren<Rigidbody>(true);
        }

        internal static void EnableDropPhysics(Rigidbody body)
        {
            body.isKinematic = false;
            body.useGravity = true;
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.WakeUp();
        }

        private void ResolveTool()
        {
            if (_tool != null)
                return;

            _tool = GetComponent<Tool>();
            if (_tool == null)
                _tool = GetComponentInChildren<Tool>(true);
        }
    }
}
