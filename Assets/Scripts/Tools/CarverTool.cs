using UnityEngine;
using SandDefenseWithSand.Clay;

namespace SandDefenseWithSand.Tools
{
    /// <summary>
    /// Carver-specific behaviour. It uses the same figurine raycast and placement
    /// preview as ShovelTool; the only difference is that C creates a negative
    /// ClayObject instead of adding positive clay or removing a whole piece.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CarverTool : Tool
    {
        private CarverClayGenerator _clayGenerator;

        public override void OnPickedUp(HandTool hand, ItemGrabbable item)
        {
            _clayGenerator = GetOnItem<CarverClayGenerator>(item);
            if (_clayGenerator == null)
            {
                Debug.LogWarning(
                    $"{nameof(CarverTool)} '{item.name}' needs a CarverClayGenerator with Target Figurine assigned.",
                    item);
            }
        }

        public override void OnPutDown(HandTool hand, ItemGrabbable item)
        {
            _clayGenerator?.ResetFigurineOutline();
            _clayGenerator = null;
        }

        public override void OnHeldUpdate(HandTool hand)
        {
            // Same outline lifecycle as ShovelTool: hide it first, then show it only
            // when the held tool ray is currently over a valid visible surface.
            _clayGenerator?.ResetFigurineOutline();

            if (_clayGenerator == null || !hand.IsMouseHoldActionPressed ||
                !hand.TryGetToolRay(out Ray ray))
            {
                return;
            }

            if (!_clayGenerator.TryRaycastVisibleFigurine(ray, out RaycastHit hit))
                return;

            _clayGenerator.DrawCarveOutline(hit);

            // C is the shared Remove Clay action. The grabbed Tool component decides
            // what it means: a shovel removes a whole positive piece; a carver cuts it.
            if (hand.WasPressedThisFrame(hand.RemoveClayAction))
                _clayGenerator.AddNegativeClayAtSurface(hit);
        }
    }
}
