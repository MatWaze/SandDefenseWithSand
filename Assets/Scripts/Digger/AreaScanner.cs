using System.Collections.Generic;
using System.Linq;
using Digger.Modules.Core.Sources;
using Unity.VisualScripting;
using UnityEngine;

namespace SandDefenseWithSand.Digger
{
    public class AreaScanner : MonoBehaviour
    {
        [SerializeField]
        private Vector3 center;

        [SerializeField]
        private Vector3 size;

        public IEnumerable<Collider> ScanArea()
        {
            Vector3 worldCenter = transform.position + center;

            Collider[] hitColliders = Physics.OverlapBox(worldCenter, size * 0.5f, transform.rotation);

            return hitColliders.Where(c => c.gameObject.GetComponent<Chunk>() != null);
        }

        public GameObject DuplicateChunkGroup(string groupName)
        {
            GameObject groupRoot = new GameObject(groupName);
            groupRoot.transform.position = transform.position;
            groupRoot.transform.rotation = transform.rotation;

            var detectedColliders = ScanArea();
            Debug.Log(detectedColliders.Count());

            foreach (Collider col in detectedColliders)
            {
                GameObject originalChunkObj = col.gameObject;

                GameObject copiedChunkObj = Instantiate(originalChunkObj, groupRoot.transform);

                copiedChunkObj.transform.position = originalChunkObj.transform.position;
                copiedChunkObj.transform.rotation = originalChunkObj.transform.rotation;
            }

            return groupRoot;
        }

        // Draw the scan area in the Scene view for easy debugging
        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.matrix = Matrix4x4.TRS(transform.TransformPoint(center), transform.rotation, size);
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }
    }
}
