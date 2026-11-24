using System;
using System.Collections.Generic;
using Clayxels;
using UnityEngine;

namespace SandDefenseWithSand.Clay
{
    /// <summary>
    /// Shared figurine targeting, box-operand queries, ClayObject creation and preview
    /// handling for tools that edit a specific ClayContainer. No interface is needed:
    /// shovel and carver generators inherit these common operations directly.
    /// </summary>
    public abstract class FigurineGeneratorBase : MonoBehaviour
    {
        private const float RayPointTolerance = 0.00001f;

        internal enum AimResult { Miss, Blocked, TooClose, Hit }

        protected struct BoxOperand
        {
            public ClayObject ClayObject;
            public BoxCollider Collider;
            public bool IsNegative;
        }

        [Header("Target Figurine")]
        [Tooltip("The Clayxels sculpture this tool edits. Assign the same container to the shovel and carver if they edit the same figure.")]
        [SerializeField] protected ClayContainer _targetFigurine;

        [Tooltip("Layer used by the target figure's ClayObjects. Usually Sand Figurine.")]
        [SerializeField] protected LayerMask _figurineLayer;

        [Header("Figurine Preview")]
        [Tooltip("Preview object for this tool. Use the same cube outline and scale for shovel and carver placement.")]
        [SerializeField] protected GameObject _figurineOutline;

        [Header("Surface Query")]
        [SerializeField, Min(0.01f)] private float _raycastMaxDistance = 5f;
        [SerializeField, Min(0f)] private float _surfaceProbeDistance = 0.002f;
        [Tooltip("Small tolerance used to ignore old positive-box faces that lie inside a negative cutter.")]
        [SerializeField, Min(0f)] private float _raycastCarveExclusionDistance = 0.002f;

        [SerializeField] private float _minFigurineOutlineDistance = 0.11f;
        [SerializeField] private float _maxFigurineOutlineDistance = 0.29f;

        [SerializeField]
        protected float _clayBlend;

        [SerializeField]
        protected string _clayName;

        private static readonly HashSet<FigurineGeneratorBase> ActiveGenerators = new HashSet<FigurineGeneratorBase>();

        private readonly List<BoxOperand> _boxOperands = new List<BoxOperand>(64);
        private Collider[] _rayOriginOverlaps = new Collider[8];
        private ClayxelsHopper _hopper;
        private bool _boxOperandCacheDirty = true;
        private int _figurineLayerIndex = -1;


        public ClayContainer TargetFigurine => _targetFigurine;

        protected bool TryAimAtPlacementAnchor(Ray ray, float maxDistance, out RaycastHit hit)
        {
            hit = default;
            BoxCollider anchor = _hopper != null ? _hopper.PlacementAnchor : null;
            return anchor != null && anchor.enabled && !IsSculptingLocked &&
                anchor.Raycast(ray, out hit, maxDistance) && hit.distance >= _minFigurineOutlineDistance;
        }

        protected bool IsSculptingLocked => _hopper != null && _hopper.IsSculptingLocked;
        protected float MaxFigurineOutlineDistance => Mathf.Max(0f, _maxFigurineOutlineDistance);

        protected virtual void Awake()
        {
            ResolveFigurineLayer();

            if (_targetFigurine == null)
            {
                Debug.LogError($"{GetType().Name} on '{name}' needs a Target Figurine (ClayContainer) reference.", this);
                return;
            }

            SetTargetFigurine(_targetFigurine);
        }

        internal void SetTargetFigurine(ClayContainer figurine)
        {
            ResetFigurineOutline();
            _targetFigurine = figurine;
            _hopper = figurine.GetComponent<ClayxelsHopper>();
            if (!IsSculptingLocked)
                figurine.setInteractive(true);
            RefreshFigurineColliderCache();
        }

        protected virtual void OnEnable()
        {
            ActiveGenerators.Add(this);
            _boxOperandCacheDirty = true;
        }

        protected virtual void OnDisable()
        {
            ActiveGenerators.Remove(this);
            ResetFigurineOutline();
        }

        protected virtual void OnDestroy()
        {
            // The preview is normally a separate scene object or tool child; only hide
            // it here. Do not destroy an Inspector-assigned preview object.
            ResetFigurineOutline();
        }

        /// <summary>Rebuilds cached BoxCollider references after adding/removing ClayObjects.</summary>
        public void RefreshFigurineColliderCache()
        {
            _boxOperands.Clear();

            if (_targetFigurine == null)
            {
                _boxOperandCacheDirty = false;
                return;
            }

            ClayObject[] clayObjects = _targetFigurine.GetComponentsInChildren<ClayObject>(true);
            foreach (ClayObject clayObject in clayObjects)
            {
                if (clayObject == null || !IsInsideTargetFigurine(clayObject))
                    continue;

                bool isNegative = clayObject.blend < -0.00001f;
                BoxCollider[] colliders = clayObject.GetComponentsInChildren<BoxCollider>(true);
                foreach (BoxCollider box in colliders)
                {
                    if (box == null)
                        continue;

                    _boxOperands.Add(new BoxOperand
                    {
                        ClayObject = clayObject,
                        Collider = box,
                        IsNegative = isNegative
                    });
                }
            }

            _boxOperandCacheDirty = false;
        }

        /// <summary>
        /// Finds the first exposed positive collider face along the tool ray.
        /// The outline range is applied after determining what blocks the ray.
        /// </summary>
        public bool TryRaycastVisibleFigurine(Ray ray, out RaycastHit visibleHit)
        {
            if (QueryFigurineAim(ray, out visibleHit, out _) == AimResult.Hit)
                return true;

            visibleHit = default;
            return false;
        }

        internal AimResult QueryFigurineAim(Ray ray, out RaycastHit visibleHit, out float distance,
            float terrainDistance = 0f)
        {
            visibleHit = default;
            float maxDistance = Mathf.Max(0f, Mathf.Min(_raycastMaxDistance, _maxFigurineOutlineDistance));
            distance = maxDistance;
            if (terrainDistance > 0f && !CanQueryFigurine())
            {
                ResolveFigurineLayer();
                if (Physics.CheckSphere(ray.origin, RayPointTolerance, _figurineLayer, QueryTriggerInteraction.Ignore))
                {
                    distance = 0f;
                    return AimResult.Blocked;
                }
                if (Physics.Raycast(ray, out RaycastHit blocker, terrainDistance, _figurineLayer, QueryTriggerInteraction.Ignore))
                {
                    distance = blocker.distance;
                    return AimResult.Blocked;
                }
                return AimResult.Miss;
            }
            if (!CanQueryFigurine() || maxDistance <= 0f)
                return AimResult.Miss;

            EnsureBoxOperandCache();
            float firstSolidDistance = FirstSolidDistanceFromOrigin(ray, maxDistance);
            if (firstSolidDistance <= 0f)
            {
                distance = 0f;
                return AimResult.TooClose;
            }

            RaycastHit[] hits = Physics.RaycastAll(
                ray,
                maxDistance,
                _figurineLayer,
                QueryTriggerInteraction.Ignore);

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (RaycastHit hit in hits)
            {
                if (hit.distance >= firstSolidDistance)
                    break;

                if (hit.collider == null)
                    continue;

                ClayObject hitPiece = hit.collider.GetComponentInParent<ClayObject>();
                // if (hitPiece != null && !IsPositiveClayObject(hitPiece))
                //     continue;

                if (hitPiece != null && !IsInsideTargetFigurine(hitPiece))
                {
                    distance = hit.distance;
                    return AimResult.Blocked;
                }

                if (hit.collider is BoxCollider box && IsNearNegativeCutter(hit.point, RayPointTolerance))
                {
                    // A cutter can erase the entry face yet leave solid clay farther
                    // along this same box. Only an open cut exposes a piece behind it.
                    firstSolidDistance = Mathf.Min(firstSolidDistance,
                        FirstSolidBoxDistance(box, ray, hit.distance, maxDistance));
                    continue;
                }

                distance = hit.distance;
                if (hit.distance < _minFigurineOutlineDistance)
                    return AimResult.TooClose;
                if (IsNearNegativeCutter(hit.point, _raycastCarveExclusionDistance))
                    return AimResult.Blocked;

                visibleHit = hit;
                return AimResult.Hit;
            }

            if (firstSolidDistance <= maxDistance)
            {
                distance = firstSolidDistance;
                return AimResult.Blocked;
            }

            return AimResult.Miss;
        }

        private float FirstSolidDistanceFromOrigin(Ray ray, float maxDistance)
        {
            // Unity omits ray hits on boxes containing the origin. Query that
            // point separately so an embedded shovel cannot aim through clay.
            int count;
            while (true)
            {
                count = Physics.OverlapSphereNonAlloc(ray.origin, RayPointTolerance, _rayOriginOverlaps,
                    _figurineLayer, QueryTriggerInteraction.Ignore);
                if (count < _rayOriginOverlaps.Length)
                    break;

                Array.Resize(ref _rayOriginOverlaps, _rayOriginOverlaps.Length * 2);
            }

            float firstSolidDistance = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                if (!(_rayOriginOverlaps[i] is BoxCollider box) || !ContainsRayPoint(box, ray.origin))
                    continue;

                ClayObject piece = box.GetComponentInParent<ClayObject>();
                if (piece != null && !IsPositiveClayObject(piece))
                    continue;

                if (IsInsideTargetFigurine(piece))
                {
                    firstSolidDistance = Mathf.Min(firstSolidDistance, FirstSolidBoxDistance(box, ray, 0f, maxDistance));
                }
                else if (!TryGetBoxExitDistance(box, ray, out float exit) || exit > RayPointTolerance)
                {
                    return 0f;
                }
            }

            return firstSolidDistance;
        }

        private float FirstSolidBoxDistance(BoxCollider box, Ray ray, float entry, float maxDistance)
        {
            if (!TryGetBoxExitDistance(box, ray, out float exit))
                return entry;

            float end = Mathf.Min(exit, maxDistance);
            float distance = entry;
            while (distance < end)
            {
                Vector3 point = ray.GetPoint(distance);
                float cutEnd = distance;
                foreach (BoxOperand operand in _boxOperands)
                {
                    BoxCollider cutter = operand.Collider;
                    if (!operand.IsNegative || cutter == null || !cutter.enabled ||
                        !cutter.gameObject.activeInHierarchy || !ContainsRayPoint(cutter, point))
                        continue;

                    if (TryGetBoxExitDistance(cutter, ray, out float cutterExit))
                        cutEnd = Mathf.Max(cutEnd, cutterExit);
                }

                if (cutEnd <= distance + RayPointTolerance)
                    return distance;

                distance = cutEnd;
            }

            return float.PositiveInfinity;
        }

        private static bool ContainsRayPoint(BoxCollider box, Vector3 point)
        {
            return (box.ClosestPoint(point) - point).sqrMagnitude <= RayPointTolerance * RayPointTolerance;
        }

        private static bool TryGetBoxExitDistance(BoxCollider box, Ray ray, out float exit)
        {
            Bounds bounds = box.bounds;
            // Start the reverse ray beyond the entire bounds so its entry is the
            // original ray's exit face, even when the original origin is inside.
            float outside = Vector3.Dot(bounds.center - ray.origin, ray.direction) + bounds.extents.magnitude + 0.001f;
            if (outside > 0f && box.Raycast(new Ray(ray.GetPoint(outside), -ray.direction), out RaycastHit hit, outside))
            {
                exit = outside - hit.distance;
                return true;
            }

            exit = 0f;
            return false;
        }

        /// <summary>
        /// Finds the nearest exposed face of a positive ClayObject BoxCollider to a
        /// tool-tip position. A candidate is rejected if the tip or candidate surface
        /// is inside/too close to an existing negative cutter.
        /// </summary>
        public bool TryFindPositiveBoxSurface(
            Vector3 toolPosition,
            float maxDistance,
            Vector3 preferredNormal,
            float carvedAreaClearance,
            out Vector3 surfacePoint,
            out Vector3 surfaceNormal)
        {
            surfacePoint = default;
            surfaceNormal = default;

            if (!CanQueryFigurine() || maxDistance < 0f)
                return false;

            EnsureBoxOperandCache();

            float clearance = Mathf.Max(0f, carvedAreaClearance);
            if (IsNearNegativeCutter(toolPosition, clearance))
                return false;

            Vector3 preferred = preferredNormal.sqrMagnitude > 0.0001f
                ? preferredNormal.normalized
                : Vector3.zero;

            float bestDistance = maxDistance;
            bool found = false;

            foreach (BoxOperand operand in _boxOperands)
            {
                BoxCollider box = operand.Collider;
                if (operand.IsNegative || box == null || !box.enabled || !box.gameObject.activeInHierarchy)
                    continue;

                if (!IsInsideTargetFigurine(operand.ClayObject) || !IsInFigurineLayer(box.gameObject.layer))
                    continue;

                if (!TryGetNearestBoxSurface(toolPosition, box, out Vector3 candidatePoint, out Vector3 candidateNormal))
                    continue;

                float distance = Vector3.Distance(toolPosition, candidatePoint);
                if (distance > bestDistance)
                    continue;

                if (preferred != Vector3.zero && Vector3.Dot(candidateNormal, preferred) < 0.05f)
                    continue;

                if (IsNearNegativeCutter(candidatePoint, clearance))
                    continue;

                float probe = Mathf.Max(0.0001f, _surfaceProbeDistance);
                Vector3 outsideProbe = candidatePoint + candidateNormal * probe;
                if (IsOccludedByAnotherPositiveBox(outsideProbe, box, probe))
                    continue;

                bestDistance = distance;
                surfacePoint = candidatePoint;
                surfaceNormal = candidateNormal;
                found = true;
            }

            return found;
        }

        /// <summary>Shows the assigned preview at a pose without changing its scale.</summary>
        protected void ShowFigurineOutline(Vector3 position)
        {
            if (_figurineOutline == null)
                return;

            _figurineOutline.transform.position = position;
            if (!_figurineOutline.activeSelf)
                _figurineOutline.SetActive(true);
        }

        /// <summary>Shows the assigned preview with a requested local scale.</summary>
        protected void ShowFigurineOutline(Vector3 position, Vector3 localScale)
        {
            if (_figurineOutline == null)
                return;

            _figurineOutline.transform.position = position;
            _figurineOutline.transform.localScale = localScale;
            if (!_figurineOutline.activeSelf)
                _figurineOutline.SetActive(true);
        }

        public float GetWorldCubeSize()
        {
            float containerScale = _targetFigurine != null
                ? Mathf.Abs(_targetFigurine.transform.lossyScale.x)
                : 1f;
            float outlineWidth = _figurineOutline != null
                ? Mathf.Abs(_figurineOutline.transform.localScale.x)
                : 0f;
            float size = outlineWidth * containerScale;
            return size > 0.0001f ? size : 1f;
        }

        /// <summary>
        /// Shared placement calculation for shovel additions and carver subtractions.
        /// The preview and the created ClayObject must use this exact same position.
        /// </summary>
        protected Vector3 GetSurfacePlacementPosition(RaycastHit surfaceHit, float offsetFraction)
        {
            Vector3 normal = surfaceHit.normal.sqrMagnitude > 0.0001f
                ? surfaceHit.normal.normalized
                : Vector3.up;

            return surfaceHit.point + normal * (GetWorldCubeSize() * offsetFraction);
        }

        public void ResetFigurineOutline()
        {
            if (_figurineOutline != null && _figurineOutline.activeSelf)
                _figurineOutline.SetActive(false);
        }

        protected Vector3 GetFigurineOutlineLocalScale()
        {
            return _figurineOutline != null ? _figurineOutline.transform.localScale : Vector3.one;
        }

        protected float GetFigurineOutlineWorldWidth()
        {
            // Keep this aligned with the size used by AddSandToFigurine and the carver.
            return GetWorldCubeSize();
        }

        /// <summary>Creates a box ClayObject on the assigned target figurine.</summary>
        protected ClayObject CreateBoxClayObject(
            string objectName,
            Vector3 worldPosition,
            Vector3 localScale,
            float blend,
            float round,
            Vector3 colliderSize)
        {
            if (_targetFigurine == null || IsSculptingLocked)
                return null;

            ClayObject clayObject = _targetFigurine.addClayObject();
            if (clayObject == null)
                return null;

            clayObject.name = objectName;

            BoxCollider box = clayObject.GetComponent<BoxCollider>();
            if (box == null)
                box = clayObject.gameObject.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = colliderSize;
            box.enabled = true;

            clayObject.setPrimitiveType(0);
            if (Mathf.Abs(blend) > 0.00001f)
                clayObject.setBlend(blend);

            clayObject.gameObject.layer = GetFigurineLayerIndex();
            clayObject.setAttribute("round", round);
            clayObject.transform.localScale = localScale;
            clayObject.transform.position = worldPosition;

            clayObject.forceUpdate();
            MarkFigurineCollidersDirty();
            Physics.SyncTransforms();
    
            return clayObject;
        }

        /// <summary>Marks the cache dirty after an external ClayObject change.</summary>
        public void MarkFigurineCollidersDirty()
        {
            // Multiple tools may edit the same ClayContainer. Invalidate every generator
            // cache targeting that container, not just the generator that made the edit.
            foreach (FigurineGeneratorBase generator in ActiveGenerators)
            {
                if (generator != null && generator._targetFigurine == _targetFigurine)
                    generator._boxOperandCacheDirty = true;
            }
        }

        protected bool IsClayObjectInTarget(ClayObject clayObject)
        {
            return clayObject != null && IsInsideTargetFigurine(clayObject);
        }

        protected bool IsPositiveClayObject(ClayObject clayObject)
        {
            return clayObject != null && clayObject.blend >= -0.00001f;
        }

        protected bool IsPointInsideOrNearNegativeCutter(Vector3 point, float clearance)
        {
            EnsureBoxOperandCache();
            return IsNearNegativeCutter(point, Mathf.Max(0f, clearance));
        }

        protected static Quaternion MakeSurfaceRotation(Vector3 surfaceNormal, Vector3 preferredUp)
        {
            Vector3 normal = surfaceNormal.sqrMagnitude > 0.0001f
                ? surfaceNormal.normalized
                : Vector3.up;

            Vector3 up = Vector3.ProjectOnPlane(preferredUp, normal);
            if (up.sqrMagnitude < 0.0001f)
                up = Vector3.ProjectOnPlane(Vector3.up, normal);
            if (up.sqrMagnitude < 0.0001f)
                up = Vector3.ProjectOnPlane(Vector3.right, normal);

            return Quaternion.LookRotation(normal, up.normalized);
        }

        protected float GetContainerUniformScale()
        {
            if (_targetFigurine == null)
                return 1f;

            float scale = Mathf.Abs(_targetFigurine.transform.lossyScale.x);
            return scale > 0.0001f ? scale : 1f;
        }

        private bool CanQueryFigurine()
        {
            if (_targetFigurine == null || IsSculptingLocked)
                return false;

            ResolveFigurineLayer();
            return _figurineLayer.value != 0;
        }

        private void ResolveFigurineLayer()
        {
            if (_figurineLayer.value != 0)
            {
                _figurineLayerIndex = FirstLayerInMask(_figurineLayer);
                return;
            }

            _figurineLayerIndex = LayerMask.NameToLayer("Sand Figurine");
            if (_figurineLayerIndex >= 0)
                _figurineLayer = 1 << _figurineLayerIndex;
        }

        private int GetFigurineLayerIndex()
        {
            if (_figurineLayerIndex >= 0)
                return _figurineLayerIndex;

            ResolveFigurineLayer();
            if (_figurineLayerIndex >= 0)
                return _figurineLayerIndex;

            if (_targetFigurine != null)
            {
                ClayObject existing = _targetFigurine.GetComponentInChildren<ClayObject>(true);
                if (existing != null)
                    return existing.gameObject.layer;
            }

            return 0;
        }

        private static int FirstLayerInMask(LayerMask mask)
        {
            for (int i = 0; i < 32; i++)
            {
                if ((mask.value & (1 << i)) != 0)
                    return i;
            }

            return -1;
        }

        private bool IsInFigurineLayer(int layer)
        {
            return (_figurineLayer.value & (1 << layer)) != 0;
        }

        private bool IsInsideTargetFigurine(ClayObject clayObject)
        {
            return clayObject != null && _targetFigurine != null &&
                   clayObject.transform.IsChildOf(_targetFigurine.transform);
        }

        private void EnsureBoxOperandCache()
        {
            if (_boxOperandCacheDirty)
                RefreshFigurineColliderCache();
        }

        private bool IsNearNegativeCutter(Vector3 point, float clearance)
        {
            float clearanceSqr = clearance * clearance;

            foreach (BoxOperand operand in _boxOperands)
            {
                BoxCollider box = operand.Collider;
                if (!operand.IsNegative || box == null || !box.enabled || !box.gameObject.activeInHierarchy)
                    continue;

                Vector3 closest = box.ClosestPoint(point);
                if ((closest - point).sqrMagnitude <= clearanceSqr)
                    return true;
            }

            return false;
        }

        private bool IsOccludedByAnotherPositiveBox(Vector3 probePoint, BoxCollider ignoredBox, float tolerance)
        {
            float toleranceSqr = tolerance * tolerance;

            foreach (BoxOperand operand in _boxOperands)
            {
                BoxCollider box = operand.Collider;
                if (operand.IsNegative || box == null || box == ignoredBox ||
                    !box.enabled || !box.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Vector3 closest = box.ClosestPoint(probePoint);
                if ((closest - probePoint).sqrMagnitude <= toleranceSqr)
                    return true;
            }

            return false;
        }

        private static bool TryGetNearestBoxSurface(
            Vector3 worldPosition,
            BoxCollider box,
            out Vector3 worldSurfacePoint,
            out Vector3 worldSurfaceNormal)
        {
            worldSurfacePoint = default;
            worldSurfaceNormal = default;
            if (box == null)
                return false;

            Transform boxTransform = box.transform;
            Vector3 localPoint = boxTransform.InverseTransformPoint(worldPosition) - box.center;
            Vector3 half = box.size * 0.5f;

            if (half.x <= 0f || half.y <= 0f || half.z <= 0f)
                return false;

            bool inside = Mathf.Abs(localPoint.x) <= half.x &&
                          Mathf.Abs(localPoint.y) <= half.y &&
                          Mathf.Abs(localPoint.z) <= half.z;

            Vector3 surfaceLocal;
            Vector3 normalLocal;

            if (!inside)
            {
                surfaceLocal = new Vector3(
                    Mathf.Clamp(localPoint.x, -half.x, half.x),
                    Mathf.Clamp(localPoint.y, -half.y, half.y),
                    Mathf.Clamp(localPoint.z, -half.z, half.z));

                Vector3 toPosition = worldPosition - boxTransform.TransformPoint(box.center + surfaceLocal);
                if (toPosition.sqrMagnitude > 0.0000001f)
                {
                    worldSurfacePoint = boxTransform.TransformPoint(box.center + surfaceLocal);
                    worldSurfaceNormal = toPosition.normalized;
                    return true;
                }

                normalLocal = GetNearestFaceNormal(localPoint, half, boxTransform);
            }
            else
            {
                normalLocal = GetNearestFaceNormal(localPoint, half, boxTransform);
                surfaceLocal = localPoint;
                if (normalLocal.x > 0.5f) surfaceLocal.x = half.x;
                else if (normalLocal.x < -0.5f) surfaceLocal.x = -half.x;
                else if (normalLocal.y > 0.5f) surfaceLocal.y = half.y;
                else if (normalLocal.y < -0.5f) surfaceLocal.y = -half.y;
                else if (normalLocal.z > 0.5f) surfaceLocal.z = half.z;
                else surfaceLocal.z = -half.z;
            }

            worldSurfacePoint = boxTransform.TransformPoint(box.center + surfaceLocal);
            Matrix4x4 normalMatrix = boxTransform.localToWorldMatrix.inverse.transpose;
            worldSurfaceNormal = normalMatrix.MultiplyVector(normalLocal).normalized;
            return worldSurfaceNormal.sqrMagnitude > 0.0001f;
        }

        private static Vector3 GetNearestFaceNormal(Vector3 localPoint, Vector3 half, Transform boxTransform)
        {
            Vector3 scale = boxTransform.lossyScale;
            float dx = Mathf.Max(0f, half.x - Mathf.Abs(localPoint.x)) * Mathf.Abs(scale.x);
            float dy = Mathf.Max(0f, half.y - Mathf.Abs(localPoint.y)) * Mathf.Abs(scale.y);
            float dz = Mathf.Max(0f, half.z - Mathf.Abs(localPoint.z)) * Mathf.Abs(scale.z);

            if (dx <= dy && dx <= dz)
                return new Vector3(localPoint.x >= 0f ? 1f : -1f, 0f, 0f);
            if (dy <= dz)
                return new Vector3(0f, localPoint.y >= 0f ? 1f : -1f, 0f);
            return new Vector3(0f, 0f, localPoint.z >= 0f ? 1f : -1f);
        }
    }
}
