using Clayxels;
using UnityEngine;

namespace SandDefenseWithSand.Clay
{
    /// <summary>
    /// Negative-clay version of ShovelClayGenerator.AddSandToFigurine.
    /// It uses the same visible-positive-surface hit, placement offset, cube dimensions,
    /// and collider dimensions. Negative ClayObjects are never valid hit surfaces and
    /// a new cutter cannot be centred inside an existing negative cutter.
    /// </summary>
    public sealed class CarverClayGenerator : FigurineGeneratorBase
    {
        [Header("Negative Clay")]
        [Tooltip("100 creates a full-strength negative ClayObject. Lower values make a weaker subtraction.")]
        [SerializeField, Range(-100f, -0.001f)] private float _negativeBlend = -1f;

        [Tooltip("Must match ShovelClayGenerator's Add Cube Surface Offset.")]
        [SerializeField, Range(0.1f, 0.5f)] private float _addCubeSurfaceOffset = 0.38f;

        [Tooltip("Must match ShovelClayGenerator's Positive Collider Size.")]
        [SerializeField] private Vector3 _cutterColliderSize = new (0.75f, 0.75f, 0.75f);

        private static readonly Vector3 CutterLocalScale = new (0.05f, 0.05f, 0.2f);
        private const float CutterRound = 0f;

        public void DrawCarveOutline(RaycastHit surfaceHit)
        {
            if (_figurineOutline == null)
                return;

            ShowFigurineOutline(GetSurfacePlacementPosition(surfaceHit, _addCubeSurfaceOffset));
        }

        public bool AddNegativeClayAtSurface(RaycastHit surfaceHit)
        {
            if (_targetFigurine == null || _figurineOutline == null || surfaceHit.collider == null)
                return false;

            // Never carve from a negative ClayObject or from a collider outside the
            // configured target figurine. TryRaycastVisibleFigurine already enforces
            // this, but keep the operation safe if called from another script.
            ClayObject hitPiece = surfaceHit.collider.GetComponentInParent<ClayObject>();
            if (!IsClayObjectInTarget(hitPiece) || !IsPositiveClayObject(hitPiece))
                return false;

            Vector3 cutterPosition = GetSurfacePlacementPosition(surfaceHit, _addCubeSurfaceOffset);

            // A negative cutter is not itself a valid surface. Also reject a new cutter
            // whose centre would land inside an existing negative cutter. This prevents
            // stacking a second negative layer over a previous cut while still allowing
            // new cuts on untouched positive surfaces.
            if (IsPointInsideOrNearNegativeCutter(surfaceHit.point, 0f) ||
                IsPointInsideOrNearNegativeCutter(cutterPosition, 0f))
            {
                return false;
            }

            ClayObject cutter = CreateBoxClayObject(
                "carver_negative_cube",
                cutterPosition,
                CutterLocalScale,
                _negativeBlend,
                CutterRound,
                _cutterColliderSize);

            return cutter != null;
        }
    }
}
