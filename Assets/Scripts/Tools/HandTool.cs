using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

namespace SandDefenseWithSand.Tools
{
    /// <summary>
    /// The single hand controller used by both hands and every kind of tool.
    /// It handles grabbing/releasing once, then delegates behaviour to the Tool
    /// component found on the grabbed ItemGrabbable.
    /// Put one HandTool on Arms_Rig for the left hand and one for the right hand.
    /// </summary>
    public sealed class HandTool : MonoBehaviour
    {
        public enum HandSide
        {
            Left,
            Right
        }

        [Header("Hand Setup")]
        [SerializeField] private HandSide _handSide = HandSide.Left;
        [Tooltip("Assign the grip transform for this hand.")]
        [FormerlySerializedAs("_toolGripPoint")]
        [FormerlySerializedAs("_shovelGripPoint")]
        [SerializeField] private Transform _toolGripPoint;

        [Header("Grab Interaction")]
        [SerializeField, Min(0f)] private float _grabRange = 0.2f;
        [SerializeField] private InputActionReference _grabAction;
        [SerializeField] private InputActionReference _mouseHoldAction;
        [SerializeField] private LayerMask _grabbableLayer;

        [Header("Tool Actions")]
        [Tooltip("Used by ShovelTool to add sand. Usually V.")]
        [FormerlySerializedAs("_addSandAction")]
        [SerializeField] private InputActionReference _addClayAction;
        [Tooltip("Shared remove action. On a shovel it removes a whole positive ClayObject; on a carver it creates a negative-blend cut. Usually C.")]
        [FormerlySerializedAs("_removeSandAction")]
        [SerializeField] private InputActionReference _removeClayAction;

        [Header("Terrain Raycast (ShovelTool only)")]
        [SerializeField] private LayerMask _terrainLayer;
        [SerializeField, Min(0.01f)] private float _maxDigDistance = 5f;
        [SerializeField] private float _carveOffset;

        private static readonly HashSet<ItemGrabbable> HeldItems = new HashSet<ItemGrabbable>();
        private static readonly Dictionary<InputAction, int> GrabActionHandledFrame =
            new Dictionary<InputAction, int>();

        private ItemGrabbable _heldItem;
        private Tool _heldTool;
        private Rigidbody _heldRigidbody;
        private Transform _originalParent;
        private bool _originalIsKinematic;
        private Transform _toolHead;
        private Animator _handAnimator;
        public bool IsMouseHoldActionPressed => IsActionPressed(_mouseHoldAction);

        public InputActionReference AddClayAction => _addClayAction;
        public InputActionReference RemoveClayAction => _removeClayAction;

        private void Awake()
        {
            _handAnimator = GetComponent<Animator>();
            if (_handAnimator == null)
            {
                Debug.LogError(
                    $"{nameof(HandTool)} ({_handSide}) on '{name}' needs an Animator on the same Arms_Rig GameObject.",
                    this);
            }
        }

        private void Update()
        {
            if (IsGrabComboPressedThisFrame())
            {
                // Both hands may reference the same InputAction. Let only one HandTool
                // process that action in this frame, otherwise the second hand could
                // immediately re-grab an item released by the first hand. Different
                // left/right mouse actions remain independent.
                InputAction mouseAction = _mouseHoldAction != null ? _mouseHoldAction.action : null;
                if (mouseAction != null)
                {
                    if (GrabActionHandledFrame.TryGetValue(mouseAction, out int handledFrame) &&
                        handledFrame == Time.frameCount)
                    {
                        return;
                    }

                    GrabActionHandledFrame[mouseAction] = Time.frameCount;
                }

                // If a held tool is busy, do not fall through and grab another item.
                if (_heldTool != null)
                {
                    if (_heldTool.CanRelease)
                        ReleaseHeldItem(playAnimation: true);
                }
                else if (TryFindNearestTool(out ItemGrabbable item))
                {
                    GrabItem(item);
                }

                return;
            }

            if (_heldTool != null)
                _heldTool.OnHeldUpdate(this);
        }

        private void OnDisable()
        {
            if (_heldTool != null)
                ReleaseHeldItem(playAnimation: false);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSharedState()
        {
            HeldItems.Clear();
            GrabActionHandledFrame.Clear();
        }

        private bool IsGrabComboPressedThisFrame()
        {
            if (!IsActionPressed(_grabAction) || !IsActionPressed(_mouseHoldAction))
                return false;

            return WasPressedThisFrame(_grabAction) || WasPressedThisFrame(_mouseHoldAction);
        }

        private bool TryFindNearestTool(out ItemGrabbable selectedItem)
        {
            selectedItem = null;
            if (_toolGripPoint == null || _grabRange <= 0f || _grabbableLayer.value == 0)
                return false;

            Collider[] colliders = Physics.OverlapSphere(
                _toolGripPoint.position,
                _grabRange,
                _grabbableLayer,
                QueryTriggerInteraction.Ignore);

            float bestDistanceSqr = float.PositiveInfinity;
            foreach (Collider candidateCollider in colliders)
            {
                if (candidateCollider == null)
                    continue;

                ItemGrabbable candidate = candidateCollider.GetComponentInParent<ItemGrabbable>();
                if (candidate == null || HeldItems.Contains(candidate))
                    continue;

                // A grabbable object without a Tool component is not a hand tool.
                if (candidate.AttachedTool == null)
                    continue;

                Vector3 closestPoint = candidateCollider.ClosestPoint(_toolGripPoint.position);
                float distanceSqr = (closestPoint - _toolGripPoint.position).sqrMagnitude;
                if (distanceSqr < bestDistanceSqr)
                {
                    bestDistanceSqr = distanceSqr;
                    selectedItem = candidate;
                }
            }

            return selectedItem != null;
        }

        private void GrabItem(ItemGrabbable item)
        {
            if (item == null || HeldItems.Contains(item))
                return;

            Tool tool = item.AttachedTool;
            if (tool == null)
                return;

            if (_toolGripPoint == null)
            {
                Debug.LogWarning($"{nameof(HandTool)} ({_handSide}): assign this hand's Tool Grip Point.", this);
                return;
            }

            Vector3 gripOffset = tool is CarverTool ? Vector3.left * _carveOffset : Vector3.zero;
            if (!item.TryMount(_toolGripPoint, gripOffset, tool is ShovelTool, out _originalParent))
                return;

            _heldItem = item;
            _heldTool = tool;
            HeldItems.Add(item);

            _heldRigidbody = item.FindOwnedRigidbody();
            if (_heldRigidbody != null)
            {
                _originalIsKinematic = _heldRigidbody.isKinematic;
                _heldRigidbody.isKinematic = true;
            }

            _toolHead = tool.ResolveToolTip(item.transform);

            try
            {
                _heldTool.OnPickedUp(this, item);
                SetGrabAnimation(true);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, item);
                ReleaseHeldItem(playAnimation: false, drop: false);
            }
        }

        private void ReleaseHeldItem(bool playAnimation, bool drop = true)
        {
            ItemGrabbable releasedItem = _heldItem;
            Tool releasedTool = _heldTool;
            if (releasedItem == null || releasedTool == null)
                return;

            try
            {
                releasedTool.OnPutDown(this, releasedItem);
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception, releasedItem);
            }

            HeldItems.Remove(releasedItem);
            releasedItem.Unmount(_originalParent);

            if (_heldRigidbody != null)
            {
                if (drop)
                    ItemGrabbable.EnableDropPhysics(_heldRigidbody);
                else
                    _heldRigidbody.isKinematic = _originalIsKinematic;
            }

            if (playAnimation)
                SetGrabAnimation(false);

            _heldRigidbody = null;
            _heldItem = null;
            _heldTool = null;
            _toolHead = null;
            _originalParent = null;
        }

        public bool TryGetToolRay(out Ray ray)
        {
            ray = default;
            if (_toolHead == null)
                return false;

            ray = new Ray(_toolHead.position, _toolHead.forward);
            return true;
        }

        public bool TryRaycastTerrain(out RaycastHit hit)
        {
            hit = default;
            if (!TryGetToolRay(out Ray ray))
                return false;

            return Physics.Raycast(
                ray,
                out hit,
                _maxDigDistance,
                _terrainLayer,
                QueryTriggerInteraction.Ignore);
        }

        public bool IsActionPressed(InputActionReference actionReference)
        {
            return actionReference != null && actionReference.action != null &&
                   actionReference.action.IsPressed();
        }

        public bool WasPressedThisFrame(InputActionReference actionReference)
        {
            return actionReference != null && actionReference.action != null &&
                   actionReference.action.WasPressedThisFrame();
        }

        public void SetDigAnimation(bool isDigging)
        {
            if (_handAnimator == null)
                return;

            string trigger = _handSide == HandSide.Right
                ? (isDigging ? "GrabToDigRight" : "DigToGrabRight")
                : (isDigging ? "GrabToDigLeft" : "DigToGrabLeft");

            _handAnimator.ResetTrigger(trigger);
            _handAnimator.SetTrigger(trigger);
        }

        private void SetGrabAnimation(bool isGrab)
        {
            if (_handAnimator == null)
                return;

            string trigger = _handSide == HandSide.Right
                ? (isGrab ? "IdleToGrabRight" : "GrabToIdleRight")
                : (isGrab ? "IdleToGrab" : "GrabToIdle");

            _handAnimator.ResetTrigger(trigger);
            _handAnimator.SetTrigger(trigger);
        }
    }
}
