using UnityEngine;
using UnityEngine.InputSystem;
using SandDefenseWithSand.Clay;
using SandDefenseWithSand.Tools;

namespace SandDefenseWithSand.Hands
{
    /// <summary>
    /// A carving tool. It shares grabbing with HandShovel but has no DiggerController
    /// or shovel-fill dependency. It only carves within range of exposed positive clay.
    /// </summary>
    public sealed class HandCarver : HandToolBase
    {
        [Header("Carving Input")]
        [SerializeField] private InputActionReference _carveAction;

        [Header("Surface Contact")]
        [Tooltip("Maximum distance from the carver tip to an exposed positive BoxCollider surface.")]
        [SerializeField, Min(0.001f)] private float _surfaceDistance = 0.08f;

        [Tooltip("Extra distance around previous negative cutters where another carve is rejected. This prevents carving from an already-carved region.")]
        [SerializeField, Min(0f)] private float _carvedAreaClearance = 0.02f;

        [Header("Cutter Shape")]
        [Tooltip("Width and height of the shallow rectangular cutter, in world units.")]
        [SerializeField, Min(0.001f)] private float _cutWidth = 0.12f;

        [Tooltip("Cutter thickness along the surface normal, in world units. Most of this thickness is placed inside the positive clay.")]
        [SerializeField, Min(0.001f)] private float _cutDepth = 0.025f;

        [Tooltip("Small overlap past the detected outside surface to avoid an uncut seam.")]
        [SerializeField, Min(0f)] private float _surfaceOverlap = 0.001f;

        private CarverClayGenerator _carverClayGenerator;

        protected override void OnToolGrabbed(ItemGrabbable item)
        {
            _carverClayGenerator = item.GetComponent<CarverClayGenerator>();
            if (_carverClayGenerator == null)
            {
                Debug.LogWarning(
                    $"HandCarver ({_handSide}): '{item.name}' needs a CarverClayGenerator with its Target Figurine assigned.",
                    item);
            }
        }

        protected override void OnToolReleased(ItemGrabbable item)
        {
            _carverClayGenerator?.ResetFigurineOutline();
            _carverClayGenerator = null;
        }

        protected override void OnHeldToolUpdate()
        {
            if (_carverClayGenerator == null || _toolHead == null || !IsMouseHoldActionPressed ||
                !TryGetToolRay(out _))
            {
                _carverClayGenerator?.ResetFigurineOutline();
                return;
            }

            // The preferred normal faces back toward the carver tip. Negative cutters
            // are ignored as contact surfaces, and their volumes plus a clearance band
            // are excluded by CarverClayGenerator.
            // if (!_carverClayGenerator.TryGetCarveSurface(
            //         _toolHead.position,
            //         _surfaceDistance,
            //         -_toolHead.forward,
            //         _carvedAreaClearance,
            //         out Vector3 surfacePoint,
            //         out Vector3 surfaceNormal))
            // {
            //     _carverClayGenerator.ResetFigurineOutline();
            //     return;
            // }

            // _carverClayGenerator.DrawCarveOutline(
            //     surfacePoint,
            //     surfaceNormal);

            // // One cutter per press; holding the key does not create a stream of cutters.
            // if (WasPressedThisFrame(_carveAction))
            // {
            //     _carverClayGenerator.AddNegativeClayAtSurface(
            //         surfacePoint,
            //         surfaceNormal,
            //         _cutWidth,
            //         _cutDepth,
            //         _surfaceOverlap,
            //         _carvedAreaClearance);
            // }
        }

        protected override void OnNoToolHeldUpdate()
        {
            _carverClayGenerator?.ResetFigurineOutline();
        }
    }
}
