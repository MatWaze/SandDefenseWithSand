using UnityEngine;

namespace SandDefenseWithSand.Hands
{
    // Shovel-specific aiming and raycasts. The common grab/ungrab lifecycle lives
    // in HandToolBase and is shared with HandCarver.
    public partial class HandShovel
    {
        private RaycastHit? RaycastDigSand(Ray ray, out bool groundBlocked)
        {
            groundBlocked = false;
            if (_diggerController == null || _diggerController.IsShovelFull)
                return null;

            if (Physics.Raycast(
                    ray,
                    out RaycastHit hit,
                    _maxDigDistance,
                    _terrainLayer,
                    QueryTriggerInteraction.Ignore))
            {
                groundBlocked = !_diggerController.CanDigGround(hit.point);
                return hit;
            }

            return null;
        }
    }
}
