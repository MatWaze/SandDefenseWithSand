using System;
using UnityEngine;
using Digger.Modules.Core.Sources;
using Digger.Modules.Runtime.Sources;
using SandDefenseWithSand.Clay;

namespace SandDefenseWithSand.Digger
{
    public class DiggerController : MonoBehaviour
    {
        // Leave time for the scoop and its blend back to the grip.
        private const float HeldDigInterval = 0.9f;

        [SerializeField]
        private CustomBrush _customBrush;
        [SerializeField] private FigurineStart _figurineStart;

        [SerializeField, Min(1)]
        private int _maxProgress = 4;

        private DiggerMasterRuntime _digger;
        private SandInShovelVisual[] _sandVisuals;
        private int _progress;
        private float _nextHeldDigTime;

        public bool IsModifying { get; private set; }

        public bool IsShovelFull => _progress >= Mathf.Max(1, _maxProgress);

        public ModificationParameters parameters;

        public event EventHandler OnModificationCompleted;
        public event EventHandler OnSandAdded;

        private void Awake()
        {
            _sandVisuals = GetComponentsInChildren<SandInShovelVisual>(true);
            UpdateSandVisuals();
        }

        // Preserves the original Dig API for any other callers.
        public void Dig(Vector3 pos)
        {
            TryDig(pos);
        }

        internal bool TryDigWhileHeld(Vector3 position)
        {
            float now = Time.time;
            if (now < _nextHeldDigTime || !TryDig(position))
                return false;

            _nextHeldDigTime = now + HeldDigInterval;
            return true;
        }

        internal bool CanDigGround(Vector3 position)
        {
            if (_figurineStart == null)
                return true;

            float radius = Mathf.Abs(parameters.Size.x);
            Vector2 halfSize = new Vector2(radius, radius);
            if (parameters.Brush == BrushType.RoundedCube)
                halfSize.y = Mathf.Abs(parameters.Size.z);
            else if (parameters.Brush == BrushType.Custom)
            {
                CustomBrush brush = _customBrush != null ? _customBrush : parameters.CustomBrush;
                DiggerSystem diggerSystem = FindAnyObjectByType<DiggerSystem>();
                if (brush != null && diggerSystem != null)
                {
                    Vector3 voxelSize = diggerSystem.HeightmapScale;
                    // Treat the full voxel span as reach to cover off-center meshes.
                    halfSize = new Vector2(brush.InputSizeVox.x * radius * voxelSize.x,
                        brush.InputSizeVox.z * Mathf.Abs(parameters.Size.z) * voxelSize.z);
                }
            }

            return !_figurineStart.OverlapsProtectedGround(position, halfSize);
        }

        // Returns false when the runtime is not ready or another modification is active.
        public bool TryDig(Vector3 pos)
        {
            if (IsModifying || IsShovelFull || !CanDigGround(pos))
                return false;

            if (_digger == null)
                _digger = FindAnyObjectByType<DiggerMasterRuntime>();
            if (_digger == null)
                return false;

            parameters.Position = pos;
            parameters.Callback = OnModificationComplete;
            if (_customBrush != null)
                parameters.CustomBrush = _customBrush;
            IsModifying = true;

            try
            {
                if (_digger.ModifyAsyncBuffured(parameters))
                    return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }

            IsModifying = false;
            return false;
        }

        private void OnModificationComplete(ModificationResult result)
        {
            if (!IsModifying)
                return;

            IsModifying = false;
            bool collected = result.TotalModifiedVoxels > 0;
            if (collected)
            {
                Debug.Log("Increasing progress");
                _progress++;
                UpdateSandVisuals();
            }
            if (_figurineStart != null && result.TotalModifiedVoxels > 0)
                _figurineStart.InvalidateGroundBoundary();
            OnModificationCompleted?.Invoke(this, EventArgs.Empty);

            if (collected && IsShovelFull)
                OnSandAdded?.Invoke(this, EventArgs.Empty);
        }

        internal void ResetProgress()
        {
            _progress = 0;
            UpdateSandVisuals();
        }

        internal void FillShovel()
        {
            if (IsShovelFull)
                return;

            _progress = Mathf.Max(1, _maxProgress);
            UpdateSandVisuals();
            OnSandAdded?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateSandVisuals()
        {
            foreach (SandInShovelVisual visual in _sandVisuals)
                if (visual != null)
                    visual.SetFull(IsShovelFull);
        }
    }
}
