using UnityEngine;

namespace SandDefenseWithSand.Tools
{
    /// <summary>
    /// Base behaviour attached to a grabbable tool prefab.
    /// HandTool owns grabbing and delegates tool-specific behaviour to this component.
    /// Add exactly one concrete Tool-derived component (ShovelTool or CarverTool) to
    /// each ItemGrabbable prefab.
    /// </summary>
    public abstract class Tool : MonoBehaviour
    {
        [Header("Tool Contact")]
        [Tooltip("Optional explicit contact transform. If empty, HandTool searches for a named child such as CarverTip, ShovelHead, Head, Tip, Blade or Scoop.")]
        [SerializeField] private Transform _toolTip;

        /// <summary>Whether this tool can currently be released from the hand.</summary>
        public virtual bool CanRelease => true;

        /// <summary>Resolved contact point used for the tool ray and proximity checks.</summary>
        public Transform ResolveToolTip(Transform itemRoot)
        {
            if (_toolTip != null)
                return _toolTip;

            if (itemRoot == null)
                return transform;

            Transform[] descendants = itemRoot.GetComponentsInChildren<Transform>(true);
            string[] preferredNames =
            {
                "carvertip", "tooltip", "shovelhead", "head", "tip", "blade", "scoop"
            };

            foreach (string preferredName in preferredNames)
            {
                foreach (Transform candidate in descendants)
                {
                    if (candidate == null || candidate == itemRoot)
                        continue;

                    if (NormalizeName(candidate.name) == preferredName)
                        return candidate;
                }
            }

            return transform != null ? transform : itemRoot;
        }

        /// <summary>Called after the item has been parented to the hand grip.</summary>
        public virtual void OnPickedUp(HandTool hand, ItemGrabbable item) { }

        /// <summary>Called before the item is detached from the hand.</summary>
        public virtual void OnPutDown(HandTool hand, ItemGrabbable item) { }

        /// <summary>Called once per frame while this tool is held.</summary>
        public abstract void OnHeldUpdate(HandTool hand);

        protected static T GetOnItem<T>(ItemGrabbable item) where T : Component
        {
            if (item == null)
                return null;

            T component = item.GetComponent<T>();
            return component != null ? component : item.GetComponentInChildren<T>(true);
        }

        private static string NormalizeName(string value)
        {
            return value.Replace(" ", string.Empty)
                        .Replace("_", string.Empty)
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
        }
    }
}
