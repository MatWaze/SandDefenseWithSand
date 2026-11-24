using System;
using UnityEngine;
using SandDefenseWithSand.Clay;
using SandDefenseWithSand.Digger;

namespace SandDefenseWithSand.Tools
{
    /// <summary>
    /// Shovel-specific behaviour. The hand controller does not know about Digger or
    /// Clayxels; this component handles terrain digging and transferring clay to and
    /// from the figurine.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShovelTool : Tool
    {
        private DiggerController _diggerController;
        private ShovelClayGenerator _clayGenerator;
        private HandTool _owningHand;
        private volatile bool _digCompletionPending;
        private bool _isDigAnimationActive;

        private void Awake()
        {
            var item = GetComponentInParent<ItemGrabbable>();
            if (item == null) return;
            Rigidbody body = item.FindOwnedRigidbody();
            if (body == null) return;
            var contact = Resources.Load<PhysicsMaterial>("ShovelContact");
            foreach (Collider hull in body.GetComponentsInChildren<Collider>(true))
            {
                if (hull.attachedRigidbody == body && !hull.isTrigger && hull.sharedMaterial == null)
                    hull.sharedMaterial = contact;
            }
        }

        public override bool CanRelease => _diggerController == null || !_diggerController.IsModifying;

        public override void OnPickedUp(HandTool hand, ItemGrabbable item)
        {
            _owningHand = hand;
            _clayGenerator = GetOnItem<ShovelClayGenerator>(item);
            if (_clayGenerator == null)
            {
                Debug.LogWarning(
                    $"{nameof(ShovelTool)} '{item.name}' needs a ShovelClayGenerator with Target Figurine assigned.",
                    item);
            }

            _diggerController = GetOnItem<DiggerController>(item);
            if (_diggerController == null)
            {
                Debug.LogWarning($"{nameof(ShovelTool)} '{item.name}' needs a DiggerController.", item);
            }
            else
            {
                _diggerController.OnModificationCompleted -= HandleDigCompleted;
                _diggerController.OnModificationCompleted += HandleDigCompleted;
            }
        }

        public override void OnPutDown(HandTool hand, ItemGrabbable item)
        {
            ResetOutlines();

            if (_diggerController != null)
                _diggerController.OnModificationCompleted -= HandleDigCompleted;

            if (_owningHand != null && _isDigAnimationActive)
                SetDigAnimation(_owningHand, false);

            _diggerController = null;
            _clayGenerator = null;
            _owningHand = null;
            _digCompletionPending = false;
            _isDigAnimationActive = false;
        }

        public override void OnHeldUpdate(HandTool hand)
        {
            ProcessDigCompletion(hand);
            ResetOutlines();

            if (!hand.TryGetToolRay(out Ray ray) || (_diggerController != null && _diggerController.IsModifying) ||
                !hand.IsMouseHoldActionPressed)
                return;

            bool hasSand = _diggerController != null && _diggerController.IsShovelFull;
            RaycastHit? terrainHit = RaycastDig(hand, out bool groundBlocked);
            bool canDig = terrainHit.HasValue && !groundBlocked;
            RaycastHit? figurineHit = _clayGenerator != null
                ? _clayGenerator.ShowAim(ray, hasSand, terrainHit, groundBlocked, out canDig)
                : null;

            if (hand.WasPressedThisFrame(hand.AddClayAction))
            {
                if (hasSand && figurineHit.HasValue && _clayGenerator.AddSandToFigurine(figurineHit.Value))
                {
                    _diggerController.ResetProgress();
                    _clayGenerator.ResetEditPreviews();
                }
            }
            else if (hand.WasPressedThisFrame(hand.RemoveClayAction))
            {
                if (_diggerController != null && !hasSand && figurineHit.HasValue &&
                    _clayGenerator.RemoveWholeClayObject(figurineHit.Value))
                {
                    _diggerController.FillShovel();
                    ResetOutlines();
                }
            }
            else if (canDig && terrainHit.HasValue && _diggerController.TryDigWhileHeld(terrainHit.Value.point))
            {
                SetDigAnimation(hand, true);
            }
        }

        private RaycastHit? RaycastDig(HandTool hand, out bool groundBlocked)
        {
            groundBlocked = false;
            if (_diggerController == null || _diggerController.IsShovelFull || !hand.TryRaycastTerrain(out RaycastHit hit))
            {
                return null;
            }

            groundBlocked = !_diggerController.CanDigGround(hit.point);
            return hit;
        }

        private void ResetOutlines()
        {
            _clayGenerator?.ResetEditPreviews();
            _clayGenerator?.ResetTerrainOutline();
        }

        private void HandleDigCompleted(object sender, EventArgs e)
        {
            // Queue Animator access for HandTool.Update rather than touching it here.
            _digCompletionPending = true;
        }

        private void ProcessDigCompletion(HandTool hand)
        {
            if (!_digCompletionPending)
                return;

            _digCompletionPending = false;
            SetDigAnimation(hand, false);
        }

        private void SetDigAnimation(HandTool hand, bool isDigging)
        {
            if (_isDigAnimationActive == isDigging)
                return;

            _isDigAnimationActive = isDigging;
            hand.SetDigAnimation(isDigging);
        }
    }
}
