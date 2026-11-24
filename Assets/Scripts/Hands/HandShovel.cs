using System;
using UnityEngine;
using UnityEngine.InputSystem;
using SandDefenseWithSand.Clay;
using SandDefenseWithSand.Digger;
using SandDefenseWithSand.Tools;

namespace SandDefenseWithSand.Hands
{
    public partial class HandShovel : HandToolBase
    {
        [Header("Shovel Interaction")]
        [SerializeField] private InputActionReference _addSandAction;
        [SerializeField] private InputActionReference _removeSandAction;

        [Header("Terrain Raycast")]
        [SerializeField] private LayerMask _terrainLayer;
        [SerializeField, Min(0.01f)] private float _maxDigDistance = 5f;

        private DiggerController _diggerController;
        private ShovelClayGenerator _shovelClayGenerator;
        private volatile bool _digCompletionPending;
        private bool _isDigAnimationActive;

        protected override bool CanReleaseHeldItem =>
            _diggerController == null || !_diggerController.IsModifying;

        protected override void OnToolGrabbed(ItemGrabbable item)
        {
            _shovelClayGenerator = item.GetComponent<ShovelClayGenerator>();
            if (_shovelClayGenerator == null)
            {
                Debug.LogWarning(
                    $"HandShovel ({_handSide}): '{item.name}' needs a ShovelClayGenerator with its Target Figurine assigned.",
                    item);
            }

            _diggerController = item.GetComponent<DiggerController>();
            if (_diggerController == null)
            {
                Debug.LogWarning(
                    $"HandShovel ({_handSide}): '{item.name}' needs a DiggerController component.",
                    item);
            }
            else
            {
                _diggerController.OnModificationCompleted -= HandleDigCompleted;
                _diggerController.OnModificationCompleted += HandleDigCompleted;
            }
        }

        protected override void OnToolReleased(ItemGrabbable item)
        {
            ResetOutlines();

            if (_diggerController != null)
                _diggerController.OnModificationCompleted -= HandleDigCompleted;

            _diggerController = null;
            _shovelClayGenerator = null;
            _digCompletionPending = false;
            _isDigAnimationActive = false;
        }

        protected override void OnHeldToolUpdate()
        {
            ProcessDigCompletion();
            ProcessOwnedShovelFrame();
        }

        protected override void OnNoToolHeldUpdate()
        {
            ResetOutlines();
        }

        private void ProcessOwnedShovelFrame()
        {
            ResetOutlines();

            if (!TryGetToolRay(out Ray ray) || (_diggerController != null && _diggerController.IsModifying) ||
                !IsMouseHoldActionPressed)
                return;

            bool hasSand = _diggerController != null && _diggerController.IsShovelFull;
            RaycastHit? terrainHit = RaycastDigSand(ray, out bool groundBlocked);
            bool canDig = terrainHit.HasValue && !groundBlocked;
            RaycastHit? figurineHit = _shovelClayGenerator != null
                ? _shovelClayGenerator.ShowAim(ray, hasSand, terrainHit, groundBlocked, out canDig)
                : null;

            if (WasPressedThisFrame(_addSandAction))
            {
                if (hasSand && figurineHit.HasValue && _shovelClayGenerator.AddSandToFigurine(figurineHit.Value))
                {
                    _diggerController.ResetProgress();
                    _shovelClayGenerator.ResetEditPreviews();
                }
            }
            else if (WasPressedThisFrame(_removeSandAction))
            {
                if (_diggerController != null && !hasSand && figurineHit.HasValue &&
                    _shovelClayGenerator.RemoveWholeClayObject(figurineHit.Value))
                {
                    _diggerController.FillShovel();
                    ResetOutlines();
                }
            }
            else if (canDig && terrainHit.HasValue && _diggerController.TryDigWhileHeld(terrainHit.Value.point))
            {
                SetDigAnimation(true);
            }
        }

        private void ResetOutlines()
        {
            if (_shovelClayGenerator == null)
                return;

            _shovelClayGenerator.ResetEditPreviews();
            _shovelClayGenerator.ResetTerrainOutline();
        }

        private void SetDigAnimation(bool isDigging)
        {
            if (_isDigAnimationActive == isDigging)
                return;

            _isDigAnimationActive = isDigging;
            if (_handAnimator == null)
                return;

            string trigger = _handSide == HandSide.Right
                ? (isDigging ? "GrabToDigRight" : "DigToGrabRight")
                : (isDigging ? "GrabToDigLeft" : "DigToGrabLeft");

            _handAnimator.ResetTrigger(trigger);
            _handAnimator.SetTrigger(trigger);
        }

        private void HandleDigCompleted(object sender, EventArgs e)
        {
            // The Digger callback can run outside Update. Queue Animator changes here.
            _digCompletionPending = true;
        }

        private void ProcessDigCompletion()
        {
            if (!_digCompletionPending || _itemGrabbable == null)
                return;

            _digCompletionPending = false;
            SetDigAnimation(false);
        }
    }
}
