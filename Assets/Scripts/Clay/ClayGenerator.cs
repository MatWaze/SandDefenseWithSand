using System;
using Clayxels;
using UnityEngine;

namespace SandDefenseWithSand.Clay
{
    public class ClayGenerator : MonoBehaviour
    {
        [SerializeField] private ClayContainer _clayContainer;
        [SerializeField] private GameObject _addCubeOutline;
        [SerializeField] private GameObject _digCubeOutline;

        [Header("Figurine surface placement")]
        [Tooltip("How far the new cube's centre is placed out from the detected surface, as a fraction of the outline's world-space width. The default slightly overlaps the existing collider so the pieces join.")]
        [SerializeField, Range(0.1f, 0.5f)] private float _addCubeSurfaceOffset = 0.38f;
        [Tooltip("Reject a new piece if its centre would be this close to an existing ClayObject centre, as a fraction of the outline's world-space width. This prevents repeated clicks from stacking pieces at the same point.")]
        [SerializeField, Range(0.01f, 0.5f)] private float _duplicatePositionTolerance = 0.25f;
        [SerializeField, Min(0f)] private float _minFigurineOutlineDistance = 0.5f;
        [SerializeField, Min(0f)] private float _maxFigurineOutlineDistance = 1.5f;

        private const float ColliderInsideEpsilonSqr = 0.0000000001f;
        private int _figurineLayerIndex = -1;

        private void Start()
        {
            if (_clayContainer == null)
            {
                Debug.LogError($"{nameof(ClayGenerator)} on '{name}' needs a ClayContainer reference.", this);
                return;
            }

            _clayContainer.setInteractive(true);

            _figurineLayerIndex = LayerMask.NameToLayer("Sand Figurine");
            if (_figurineLayerIndex < 0)
            {
                Debug.LogError("Create a Unity layer named 'Sand Figurine'. Clay figurine raycasts use this layer.", this);
            }

            if (_addCubeOutline != null)
                _addCubeOutline.SetActive(false);
            if (_digCubeOutline != null)
                _digCubeOutline.SetActive(false);
        }

        public void DrawCubeOnTerrain(Vector3 position)
        {
            if (_digCubeOutline == null)
                return;

            _digCubeOutline.transform.position = position;
            _digCubeOutline.SetActive(true);
        }

        // Kept for callers that only have a position. New shovel code should use the
        // RaycastHit overload so the outline can be offset along the surface normal.
        public void DrawCubeOnFigurine(Vector3 position)
        {
            if (_addCubeOutline == null)
                return;

            _addCubeOutline.transform.position = position;
            _addCubeOutline.SetActive(true);
        }

        public void DrawCubeOnFigurine(RaycastHit surfaceHit)
        {
            if (_addCubeOutline == null)
                return;

            _addCubeOutline.transform.position = GetAddPosition(surfaceHit);
            _addCubeOutline.SetActive(true);
        }

        public void ResetOutlineTerrain()
        {
            // Terrain previews use the dig outline.
            if (_digCubeOutline != null)
                _digCubeOutline.SetActive(false);
        }

        public void ResetOutlineFigurine()
        {
            // Figurine previews use the add outline.
            if (_addCubeOutline != null)
                _addCubeOutline.SetActive(false);
        }

       public bool TryRaycastVisibleFigurine(
            Ray ray,
            LayerMask figurineLayer,
            out RaycastHit visibleHit)
        {
            visibleHit = default;

            if (_clayContainer == null || figurineLayer.value == 0)
                return false;

            Collider[] pieceColliders =
                _clayContainer.GetComponentsInChildren<Collider>(true);

            if (pieceColliders.Length == 0)
                return false;

            // Cast a regular ray and get the closest hit.
            bool didHit = Physics.Raycast(
                ray,
                out RaycastHit hit,
                _maxFigurineOutlineDistance,
                figurineLayer,
                QueryTriggerInteraction.Ignore
            );

            if (!didHit || hit.collider == null)
                return false;

            // Reject hits outside the allowed distance window.
            if (hit.distance < _minFigurineOutlineDistance ||
                hit.distance > _maxFigurineOutlineDistance)
            {
                return false;
            }

            ClayObject hitPiece =
                hit.collider.GetComponentInParent<ClayObject>();

            if (hitPiece == null ||
                !hitPiece.transform.IsChildOf(_clayContainer.transform))
            {
                return false;
            }

            float worldCubeSize = GetWorldCubeSize();
            float probeDistance = Mathf.Max(0.001f, worldCubeSize * 0.01f);

            // Probe just outside the hit surface to detect hidden faces.
            Vector3 outsideProbe =
                hit.point + hit.normal.normalized * probeDistance;

            foreach (Collider other in pieceColliders)
            {
                if (other == null ||
                    other == hit.collider ||
                    !other.enabled ||
                    !other.gameObject.activeInHierarchy)
                {
                    continue;
                }

                ClayObject otherPiece =
                    other.GetComponentInParent<ClayObject>();

                if (otherPiece == null ||
                    !otherPiece.transform.IsChildOf(_clayContainer.transform))
                {
                    continue;
                }

                if ((figurineLayer.value & (1 << other.gameObject.layer)) == 0)
                    continue;

                Vector3 closestPoint = other.ClosestPoint(outsideProbe);

                if ((closestPoint - outsideProbe).sqrMagnitude
                    <= ColliderInsideEpsilonSqr)
                {
                    // The closest hit is buried, so reject it.
                    return false;
                }
            }

            visibleHit = hit;
            return true;
        }

        /// <summary>
        /// Compatibility overload for callers that only have a position.
        /// The shovel should pass a full RaycastHit so placement follows the surface normal.
        /// </summary>
        public void AddSandToFigurine(Vector3 point)
        {
            AddSandAtPosition(point);
        }

        public void AddSandToFigurine(RaycastHit surfaceHit)
        {
            AddSandAtPosition(GetAddPosition(surfaceHit));
        }

        public void RemoveSandFromFigurine(RaycastHit surfaceHit)
        {
            RemoveSandAtPosition(surfaceHit);
        }

        private void AddSandAtPosition(Vector3 addPosition)
        {
            if (_clayContainer == null || _addCubeOutline == null)
                return;

            float worldCubeSize = GetWorldCubeSize();
            float duplicateDistance = worldCubeSize * _duplicatePositionTolerance;
            float duplicateDistanceSqr = duplicateDistance * duplicateDistance;

            // Guard against repeated input frames while the physics/Clayxels surfaces
            // are refreshing. A valid next placement is normally about 0.8 cube widths
            // from the previous centre, so this only blocks effectively identical adds.
            ClayObject[] existingPieces = _clayContainer.GetComponentsInChildren<ClayObject>(true);
            foreach (ClayObject existing in existingPieces)
            {
                if (existing == null)
                    continue;

                if ((existing.transform.position - addPosition).sqrMagnitude < duplicateDistanceSqr)
                    return;
            }

            ClayObject clayObject = _clayContainer.addClayObject();
            if (clayObject == null)
                return;

            BoxCollider box = clayObject.GetComponent<BoxCollider>();
            if (box == null)
                box = clayObject.gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(0.8f, 0.8f, 0.8f);

            clayObject.setPrimitiveType(0);
            // clayObject.setAttribute("round", 33f);

            clayObject.gameObject.layer = _figurineLayerIndex;

            float localScale = _addCubeOutline.transform.localScale.x;
            clayObject.transform.localScale = Vector3.one * localScale;
            clayObject.transform.position = addPosition;
            clayObject.forceUpdate();

            // Make the new outer collider available to the next raycast immediately,
            // instead of allowing multiple adds to use the stale previous surface.
            Physics.SyncTransforms();
        }

        private void RemoveSandAtPosition(RaycastHit removePosition)
        {
            ClayObject clayObject = removePosition.collider.GetComponentInParent<ClayObject>();

            if (clayObject == null)
                return;
            Debug.Log(removePosition.collider.gameObject.name);

            GameObject objectToRemove = clayObject.gameObject;

            objectToRemove.SetActive(false);

            _clayContainer.scheduleClayObjectsScan();

            Destroy(objectToRemove);

            Physics.SyncTransforms();
        }

        private Vector3 GetAddPosition(RaycastHit surfaceHit)
        {
            Vector3 normal = surfaceHit.normal.sqrMagnitude > 0.0001f
                ? surfaceHit.normal.normalized
                : Vector3.up;

            return surfaceHit.point + normal * (GetWorldCubeSize() * _addCubeSurfaceOffset);
        }

        private float GetWorldCubeSize()
        {
            if (_addCubeOutline == null)
                return 1f;

            // AddSandToFigurine assigns the outline's local X scale to each new
            // ClayObject. Include the container's scale to get that piece's world size.
            float containerScale = _clayContainer != null
                ? Mathf.Abs(_clayContainer.transform.lossyScale.x)
                : 1f;
            float size = Mathf.Abs(_addCubeOutline.transform.localScale.x) * containerScale;
            return size > 0.0001f ? size : 1f;
        }
    }
}
