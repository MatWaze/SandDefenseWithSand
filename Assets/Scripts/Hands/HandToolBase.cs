using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;
using SandDefenseWithSand.Tools;

namespace SandDefenseWithSand.Hands
{
    public enum HandSide
    {
        Left,
        Right
    }

    public abstract class HandToolBase : MonoBehaviour
    {
        [Header("Hand Setup")]
        [SerializeField] protected HandSide _handSide = HandSide.Left;
        [Tooltip("This hand's grip point. Assign the matching left/right grip transform.")]
        [FormerlySerializedAs("_shovelGripPoint")]
        [SerializeField] protected Transform _toolGripPoint;

        [Header("Grab Interaction")]
        [SerializeField, Min(0f)] protected float _grabRange = 0.2f;
        [SerializeField] protected InputActionReference _grabAction;
        [SerializeField] protected InputActionReference _mouseHoldAction;
        [SerializeField] protected LayerMask _grabbableLayer;

        protected ItemGrabbable _itemGrabbable;
        protected Rigidbody _itemRigidbody;
        protected Transform _toolHead;
        protected Animator _handAnimator;

        private Transform _originalParent;

        // Shared across every HandToolBase-derived component, so a shovel and a carver
        // cannot grab the same ItemGrabbable at the same time.
        private static readonly HashSet<ItemGrabbable> HeldItems = new HashSet<ItemGrabbable>();

        protected bool IsHoldingTool => _itemGrabbable != null;
        protected bool IsMouseHoldActionPressed => IsActionPressed(_mouseHoldAction);
        protected Transform ToolHead => _toolHead;

        /// <summary>Derived tools can prevent release while an asynchronous operation is running.</summary>
        protected virtual bool CanReleaseHeldItem => true;

        protected virtual void Awake()
        {
            _handAnimator = GetComponent<Animator>();
            if (_handAnimator == null)
            {
                Debug.LogError(
                    $"{GetType().Name} on '{name}' needs an Animator on the same Arms_Rig GameObject.",
                    this);
            }
        }

        protected virtual void Update()
        {
            if (IsGrabComboPressedThisFrame())
            {
                // If a derived tool is temporarily unable to release (for example,
                // while DiggerController is modifying terrain), do not fall through
                // and grab a second item while the first one is still parented here.
                if (IsHoldingTool)
                {
                    if (CanReleaseHeldItem)
                        ReleaseHeldItem(true);
                }
                else if (TryFindNearestGrabbable(out ItemGrabbable item))
                {
                    GrabItem(item);
                }

                return;
            }

            if (IsHoldingTool)
                OnHeldToolUpdate();
            else
                OnNoToolHeldUpdate();
        }

        protected virtual void OnDisable()
        {
            if (IsHoldingTool)
                ReleaseHeldItem(playAnimation: false);
        }

        protected virtual void OnHeldToolUpdate() { }
        protected virtual void OnNoToolHeldUpdate() { }

        /// <summary>Called after the item has been parented to the hand grip.</summary>
        protected virtual void OnToolGrabbed(ItemGrabbable item) { }

        /// <summary>Called before the held item is detached and base references are cleared.</summary>
        protected virtual void OnToolReleased(ItemGrabbable item) { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSharedState()
        {
            HeldItems.Clear();
        }

        private bool IsGrabComboPressedThisFrame()
        {
            if (!IsActionPressed(_grabAction) || !IsActionPressed(_mouseHoldAction))
                return false;

            return WasPressedThisFrame(_grabAction) || WasPressedThisFrame(_mouseHoldAction);
        }

        private bool TryFindNearestGrabbable(out ItemGrabbable selectedItem)
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

            if (_toolGripPoint == null)
            {
                Debug.LogWarning(
                    $"{GetType().Name} ({_handSide}): assign this hand's tool grip point in the Inspector.",
                    this);
                return;
            }

            if (!item.TryMount(_toolGripPoint, Vector3.zero, this is HandShovel, out _originalParent))
                return;

            _itemGrabbable = item;
            HeldItems.Add(item);

            _itemRigidbody = item.FindOwnedRigidbody();
            if (_itemRigidbody != null)
                _itemRigidbody.isKinematic = true;

            _toolHead = FindToolHeadTransform(item.transform);

            OnToolGrabbed(item);
            SetGrabAnimation(true);
        }

        private void ReleaseHeldItem(bool playAnimation)
        {
            ItemGrabbable releasedItem = _itemGrabbable;
            if (releasedItem == null)
                return;

            OnToolReleased(releasedItem);
            HeldItems.Remove(releasedItem);

            // Restore the hierarchy that the item had before it was grabbed.
            releasedItem.Unmount(_originalParent);

            if (_itemRigidbody != null)
                ItemGrabbable.EnableDropPhysics(_itemRigidbody);

            if (playAnimation)
                SetGrabAnimation(false);

            _itemRigidbody = null;
            _itemGrabbable = null;
            _toolHead = null;
            _originalParent = null;
        }

        protected void SetGrabAnimation(bool isGrab)
        {
            if (_handAnimator == null)
                return;

            string trigger;
            if (_handSide == HandSide.Right)
            {
                trigger = isGrab ? "IdleToGrabRight" : "GrabToIdleRight";
            }
            else
            {
                trigger = isGrab ? "IdleToGrab" : "GrabToIdle";
            }

            bool triggerExists = false;
            foreach (AnimatorControllerParameter parameter in _handAnimator.parameters)
            {
                if (parameter.name == trigger && parameter.type == AnimatorControllerParameterType.Trigger)
                {
                    triggerExists = true;
                    break;
                }
            }

            if (!triggerExists)
            {
                Debug.LogError(
                    $"{GetType().Name} ({_handSide}): Animator '{_handAnimator.name}' must have a Trigger parameter named '{trigger}'.",
                    _handAnimator);
                return;
            }

            _handAnimator.ResetTrigger(trigger);
            _handAnimator.SetTrigger(trigger);
        }

        protected static bool IsActionPressed(InputActionReference actionReference)
        {
            return actionReference.action.IsPressed();
        }

        protected static bool WasPressedThisFrame(InputActionReference actionReference)
        {
            return actionReference.action.WasPressedThisFrame();
        }

        /// <summary>
        /// Finds a useful contact transform on the grabbed tool. Explicit Tip/Head
        /// children are preferred; the item root is the fallback.
        /// </summary>
        protected virtual Transform FindToolHeadTransform(Transform itemRoot)
        {
            if (itemRoot == null)
                return null;

            Transform[] descendants = itemRoot.GetComponentsInChildren<Transform>(true);
            string preferredName = "head";

            foreach (Transform candidate in descendants)
            {
                if (candidate == itemRoot)
                    continue;

                if (NormalizeName(candidate.name) == preferredName)
                    return candidate;
            }

            return itemRoot;
        }

        private static string NormalizeName(string value)
        {
            return value.Replace(" ", string.Empty)
                        .Replace("_", string.Empty)
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
        }

        protected bool TryGetToolRay(out Ray ray)
        {
            ray = default;
            if (_toolHead == null)
                return false;

            ray = new Ray(_toolHead.position, _toolHead.forward);
            return true;
        }
    }
}
