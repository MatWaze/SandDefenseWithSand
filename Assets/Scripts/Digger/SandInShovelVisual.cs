using UnityEngine;

namespace SandDefenseWithSand.Digger
{
    public class SandInShovelVisual : MonoBehaviour
    {
        [SerializeField]
        private GameObject _sandInShovel;

        internal void SetFull(bool full)
        {
            if (_sandInShovel != null)
                _sandInShovel.SetActive(full);
        }
    }
}
