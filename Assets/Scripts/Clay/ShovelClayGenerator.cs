using System.Collections.Generic;
using Clayxels;
using UnityEngine;
using SandDefenseWithSand.Hands;

namespace SandDefenseWithSand.Clay
{
    /// <summary>
    /// Shovel-specific figurine operations. The hit BoxCollider is treated as a
    /// placement grid on each face. The shovel cube is snapped in collider-relative
    /// increments, so it can overlap the target face by N, N-1, ... 1 grid rows.
    /// Terrain modification remains in DiggerController.
    /// </summary>
    public sealed class ShovelClayGenerator : FigurineGeneratorBase
    {
        private const float GroundContactTolerance = 0.0005f;
        private static readonly Color32 PlacementColor = new Color32(255, 231, 139, 255);

        [Header("Positive Clay Placement")]
        [Tooltip("Number of grid divisions along each axis of a BoxCollider face. Use 1 to center placement on the hit face.")]
        [SerializeField, Min(1)] private int _faceGridDivisions = 3;

        [Tooltip("Tiny overlap into the collider face, in world units, so the new positive ClayObject connects to the figurine.")]
        [SerializeField, Min(0f)] private float _surfaceInset = 0.049f;

        [Tooltip("Reject effectively identical cube placements, as a fraction of the new cube's largest world-space dimension.")]
        [SerializeField, Range(0.01f, 0.5f)] private float _duplicatePositionTolerance = 0.25f;

        [SerializeField] private Vector3 _positiveColliderSize = new Vector3(0.9f, 0.8f, 0.9f);
        [SerializeField] private Vector3 _clayCubeSize = new Vector3(0.115f, 0.13f, 0.249f);

        [Header("Terrain Preview (Digging itself stays in DiggerController)")]
        [SerializeField] private GameObject _terrainOutline;


        private readonly ClayEditPreview _editPreview = new ClayEditPreview();
        private readonly List<Collider> _removalColliders = new List<Collider>();
        private readonly List<Terrain> _activeTerrains = new List<Terrain>();
        private int _groundLayers;

        protected override void Awake()
        {
            base.Awake();
            _groundLayers = LayerMask.GetMask("Terrain");
            if (_figurineOutline != null)
            {
                foreach (Collider collider in _figurineOutline.GetComponentsInChildren<Collider>(true))
                    collider.enabled = false;
                _editPreview.ConfigurePlacementPreview(_figurineOutline, PlacementColor);
            }
            ResetFigurineOutline();
        }

        protected override void OnDestroy()
        {
            _editPreview.Dispose();
            base.OnDestroy();
        }

        protected override void OnDisable()
        {
            ResetEditPreviews();
            base.OnDisable();
        }

        public void ResetEditPreviews()
        {
            _editPreview.Hide();
            ResetFigurineOutline();
        }

        public void DrawTerrainOutline(Vector3 position)
        {
            if (_terrainOutline == null)
                return;

            _terrainOutline.transform.position = position;
            if (!_terrainOutline.activeSelf)
                _terrainOutline.SetActive(true);
        }

        public void ResetTerrainOutline()
        {
            if (_terrainOutline != null && _terrainOutline.activeSelf)
                _terrainOutline.SetActive(false);
        }

        internal RaycastHit? ShowAim(Ray ray, bool hasSand, RaycastHit? terrainHit, bool groundBlocked, out bool canDig)
        {
            AimResult result = QueryFigurineAim(ray, out RaycastHit hit, out float distance,
                terrainHit.GetValueOrDefault().distance);
            bool anchorTarget = hasSand && result == AimResult.Miss &&
                TryAimAtPlacementAnchor(ray, distance, out hit);
            if (anchorTarget)
            {
                distance = hit.distance;
                result = AimResult.Hit;
            }
            if (terrainHit.HasValue && terrainHit.Value.distance < distance)
                result = AimResult.Miss;
            ClayObject clayObject = null;
            bool hasClayTarget = result == AimResult.Hit && (anchorTarget || TryGetRemovalTarget(hit, out clayObject));
            bool canScoop = hasClayTarget && !hasSand && clayObject != null && clayObject.name == _clayName;
            bool groundTarget = result == AimResult.Miss && terrainHit.HasValue;
            canDig = groundTarget && !groundBlocked;
            if (canDig)
                DrawTerrainOutline(terrainHit.Value.point);
            Vector3 placement = default;
            bool canPlace = hasClayTarget && hasSand && TryGetAddPosition(hit, out placement);
            Vector3 markerPosition = groundTarget ? terrainHit.Value.point : ray.GetPoint(distance);
            ClayEditPreview.AimState aimState = canScoop || canPlace || canDig
                ? ClayEditPreview.AimState.Ready
                : result == AimResult.Miss ? ClayEditPreview.AimState.Miss : ClayEditPreview.AimState.Blocked;
            if (groundTarget && groundBlocked)
                aimState = ClayEditPreview.AimState.ProtectedGround;

            if (canPlace)
                ShowPlacementOutline(placement, hit, clayObject);
            else
                ResetFigurineOutline();

            if (_editPreview.Begin())
            {
                if (canScoop)
                    _editPreview.AddRemoval(GetRemovalBounds(clayObject, hit.collider));
                _editPreview.AddAim(ray.origin, ray.GetPoint(MaxFigurineOutlineDistance), markerPosition, aimState);
                if (canPlace)
                {
                    if (_figurineOutline != null)
                        _editPreview.AddPlacementBounds(_figurineOutline.transform.localToWorldMatrix, PlacementColor);
                    _editPreview.AddPlacementMarker(placement, PlacementColor);
                }
                _editPreview.Draw();
            }

            return hasClayTarget ? hit : (RaycastHit?)null;
        }

        private void ShowPlacementOutline(Vector3 position, RaycastHit hit, ClayObject clayObject)
        {
            if (_figurineOutline == null)
                return;

            Transform container = _targetFigurine.transform;
            Transform outline = _figurineOutline.transform;
            // Match the parent, world rotation and local scale of the new ClayObject.
            if (outline.parent != container)
                outline.SetParent(container, false);
            outline.rotation = Quaternion.identity;
            Matrix4x4 contactBounds = clayObject != null && clayObject.mode == ClayObject.ClayObjectMode.single && clayObject.primitiveType == 0
                ? clayObject.transform.localToWorldMatrix
                : hit.collider is BoxCollider box
                    ? box.transform.localToWorldMatrix * Matrix4x4.TRS(box.center, Quaternion.identity, box.size)
                    : GetRemovalBounds(clayObject, hit.collider);
            float feather = Mathf.Clamp(GetNewCubeHalfExtentAlong(hit.normal) * 0.16f, 0.004f, 0.02f);
            _editPreview.SetPlacementContact(contactBounds, feather);
            ShowFigurineOutline(position, _clayCubeSize);
        }

        public bool AddSandToFigurine(RaycastHit surfaceHit)
        {
            if (!TryGetAddPosition(surfaceHit, out Vector3 addPosition))
                return false;

            return CreateBoxClayObject(_clayName, addPosition, _clayCubeSize, _clayBlend, 40f, _positiveColliderSize) != null;
        }

        private bool TryGetAddPosition(RaycastHit surfaceHit, out Vector3 addPosition)
        {
            if (!TryGetGridPlacement(surfaceHit, out addPosition) || _targetFigurine.isFrozen())
                return false;

            // The placed cube has an identity world rotation and inherits scale
            // from the container. Use its full dimensions for placement checks.
            Transform container = _targetFigurine.transform;
            Matrix4x4 cube = container.localToWorldMatrix *
                Matrix4x4.TRS(Vector3.zero, Quaternion.Inverse(container.rotation), _clayCubeSize);
            if (!TryFitPlacementToGround(surfaceHit, cube, ref addPosition))
                return false;

            float worldCubeSize = GetWorldCubeSizeForDuplicateCheck();
            float duplicateDistance = worldCubeSize * _duplicatePositionTolerance;
            float duplicateDistanceSqr = duplicateDistance * duplicateDistance;

            
            // _targetFigurine.GetComponentsInChildren(true, _existingClay);
            foreach (ClayObject clayObject in _targetFigurine.GetClayObjects())
            {
                if (!IsPositiveClayObject(clayObject))
                    continue;

                if ((clayObject.transform.position - addPosition).sqrMagnitude < duplicateDistanceSqr)
                    return false;
            }

            return HasPlacementClearance(addPosition, cube);
        }

        private bool HasPlacementClearance(Vector3 position, Matrix4x4 cube)
        {
            // Cover the visible cube and any collider that extends past it.
            Matrix4x4 solid = cube * Matrix4x4.Scale(Vector3.Max(Vector3.one, _positiveColliderSize));
            Bounds bounds = new Bounds(position, GetWorldHalfSize(solid) * 2f);
            foreach (HandPosition hand in HandPosition.ActiveHands)
            {
                if (hand.TryGetFigurineSweepArm(out ArmCapsuleCollision.Pose arm, out float radius, out _) &&
                    arm.Overlaps(bounds, radius + hand.CollisionSkin))
                    return false;
                HandContactGeometry fingers = hand.ContactGeometry;
                for (int i = 0; fingers != null && i < fingers.Count; i++)
                {
                    fingers.GetCapsule(i, out Vector3 a, out Vector3 b, out float r);
                    var capsule = new ArmCapsuleCollision.Pose { start = a, end = b };
                    if (capsule.Overlaps(bounds, r)) return false;
                }
                if (hand.HeldShovel != null && hand.HeldShovel.OverlapsPlacement(position, solid))
                    return false;
            }

            return true;
        }

        private static Vector3 GetWorldHalfSize(Matrix4x4 cube)
        {
            return new Vector3(
                Mathf.Abs(cube.m00) + Mathf.Abs(cube.m01) + Mathf.Abs(cube.m02),
                Mathf.Abs(cube.m10) + Mathf.Abs(cube.m11) + Mathf.Abs(cube.m12),
                Mathf.Abs(cube.m20) + Mathf.Abs(cube.m21) + Mathf.Abs(cube.m22)) * 0.5f;
        }

        private bool TryFitPlacementToGround(RaycastHit surfaceHit, Matrix4x4 cube, ref Vector3 position)
        {
            if (_groundLayers == 0)
                return true;

            Vector3 halfSize = GetWorldHalfSize(cube);

            // Start above the ground so a cube already buried under the surface is
            // also rejected. The cast uses the current terrain and Digger colliders.
            float startY = position.y + halfSize.y + 1f;
            Terrain.GetActiveTerrains(_activeTerrains);
            foreach (Terrain terrain in _activeTerrains)
            {
                if (terrain.terrainData == null || (_groundLayers & (1 << terrain.gameObject.layer)) == 0)
                    continue;

                Vector3 corner = terrain.GetPosition();
                Vector3 size = terrain.terrainData.size;
                if (position.x + halfSize.x >= corner.x && position.x - halfSize.x <= corner.x + size.x &&
                    position.z + halfSize.z >= corner.z && position.z - halfSize.z <= corner.z + size.z)
                    startY = Mathf.Max(startY, corner.y + size.y + halfSize.y + 1f);
            }

            Vector3 start = new Vector3(position.x, startY, position.z);
            if (!Physics.BoxCast(start, halfSize, Vector3.down, out RaycastHit groundHit, Quaternion.identity,
                    startY - position.y - GroundContactTolerance, _groundLayers, QueryTriggerInteraction.Ignore))
                return true;

            // An upward-facing placement can give up some of its inset at ground
            // level, provided the cube still overlaps the clay it is attaching to.
            Vector3 normal = surfaceHit.normal;
            if (normal.y <= 0.5f)
                return false;

            Vector3 grounded = position;
            grounded.y = startY - groundHit.distance + GroundContactTolerance;
            float halfExtentAlongFace = (Mathf.Abs(Vector3.Dot(normal, cube.GetColumn(0))) +
                Mathf.Abs(Vector3.Dot(normal, cube.GetColumn(1))) +
                Mathf.Abs(Vector3.Dot(normal, cube.GetColumn(2)))) * 0.5f;
            if (Vector3.Dot(grounded - surfaceHit.point, normal) >= halfExtentAlongFace)
                return false;

            position = grounded;
            return true;
        }

        private Matrix4x4 GetRemovalBounds(ClayObject clayObject, Collider hit)
        {
            if (clayObject.mode == ClayObject.ClayObjectMode.single && clayObject.primitiveType == 0)
                return clayObject.transform.localToWorldMatrix;

            Bounds bounds = hit.bounds;
            clayObject.GetComponentsInChildren(false, _removalColliders);
            foreach (Collider collider in _removalColliders)
                if (collider.enabled && !collider.isTrigger)
                    bounds.Encapsulate(collider.bounds);

            return Matrix4x4.TRS(bounds.center, Quaternion.identity, bounds.size);
        }

        /// <summary>
        /// Calculates the same position for both preview and Add. The hit BoxCollider
        /// face is divided into N segments. The complete hit-face range is remapped to
        /// the complete placement-offset range, so the edge can always select the
        /// one-row-overlap position, even when N is 4, 5, etc.
        /// </summary>
        private bool TryGetGridPlacement(RaycastHit hit, out Vector3 worldPlacement)
        {
            worldPlacement = default;

            if (_targetFigurine == null || hit.collider == null)
                return false;

            BoxCollider box = hit.collider as BoxCollider;
            if (box == null)
                box = hit.collider.GetComponent<BoxCollider>();
            if (box == null || !box.enabled || !box.gameObject.activeInHierarchy)
                return false;

            int divisions = Mathf.Max(1, _faceGridDivisions);
            Transform boxTransform = box.transform;
            Vector3 localHit = boxTransform.InverseTransformPoint(hit.point) - box.center;

            // Convert the world-space face normal into the collider's local normal space.
            Vector3 localNormal = boxTransform.localToWorldMatrix.transpose
                .MultiplyVector(hit.normal).normalized;
            if (localNormal.sqrMagnitude < 0.0001f)
                return false;

            int normalAxis = GetDominantAxis(localNormal);
            float faceSign = localNormal[normalAxis] >= 0f ? 1f : -1f;

            // Lock the point to the exact collider face. The other two coordinates
            // decide how far the new cube is shifted across the face.
            localHit[normalAxis] = faceSign * box.size[normalAxis] * 0.5f;

            for (int axis = 0; axis < 3; axis++)
            {
                if (axis == normalAxis)
                    continue;

                float faceHalfSize = box.size[axis] * 0.5f;
                float gridStep = box.size[axis] / divisions;
                if (faceHalfSize <= 0.00001f || gridStep <= 0.00001f)
                    return false;

                // Normalize the ray hit over the entire collider face: -1 at one edge,
                // 0 in the middle, +1 at the opposite edge.
                float hitAcrossFace = Mathf.Clamp(localHit[axis] / faceHalfSize, -1f, 1f);

                // A one-row overlap requires the maximum centre shift of (N - 1) grid
                // steps. Scaling the full hit range to that offset makes this reachable
                // at the face edge for every division count.
                float maxOffset = gridStep * (divisions - 1);
                float desiredOffset = hitAcrossFace * maxOffset;
                float snappedOffset = Mathf.Round(desiredOffset / gridStep) * gridStep;

                localHit[axis] = Mathf.Clamp(snappedOffset, -maxOffset, maxOffset);
            }

            Vector3 facePoint = boxTransform.TransformPoint(box.center + localHit);
            Vector3 worldNormal = hit.normal.sqrMagnitude > 0.0001f
                ? hit.normal.normalized
                : Vector3.up;

            float halfExtentAlongNormal = GetNewCubeHalfExtentAlong(worldNormal);
            worldPlacement = facePoint + worldNormal * Mathf.Max(0f, halfExtentAlongNormal - _surfaceInset);
            return true;
        }

        private float GetNewCubeHalfExtentAlong(Vector3 worldNormal)
        {
            // The ClayObject is not rotated by this script. Its local axes follow the
            // target container, so project its scaled half-size onto the hit normal.
            Vector3 scale = _targetFigurine != null
                ? _targetFigurine.transform.lossyScale
                : Vector3.one;

            Vector3 axisX = _targetFigurine != null
                ? _targetFigurine.transform.right
                : Vector3.right;
            Vector3 axisY = _targetFigurine != null
                ? _targetFigurine.transform.up
                : Vector3.up;
            Vector3 axisZ = _targetFigurine != null
                ? _targetFigurine.transform.forward
                : Vector3.forward;

            float extent = Mathf.Abs(Vector3.Dot(worldNormal, axisX)) * Mathf.Abs(_clayCubeSize.x * scale.x);
            extent += Mathf.Abs(Vector3.Dot(worldNormal, axisY)) * Mathf.Abs(_clayCubeSize.y * scale.y);
            extent += Mathf.Abs(Vector3.Dot(worldNormal, axisZ)) * Mathf.Abs(_clayCubeSize.z * scale.z);
            return extent * 0.5f;
        }

        private float GetWorldCubeSizeForDuplicateCheck()
        {
            if (_targetFigurine == null)
                return Mathf.Max(_clayCubeSize.x, _clayCubeSize.y, _clayCubeSize.z);

            Vector3 scale = _targetFigurine.transform.lossyScale;
            return Mathf.Max(
                Mathf.Abs(_clayCubeSize.x * scale.x),
                Mathf.Abs(_clayCubeSize.y * scale.y),
                Mathf.Abs(_clayCubeSize.z * scale.z));
        }

        private static int GetDominantAxis(Vector3 vector)
        {
            Vector3 absolute = new Vector3(Mathf.Abs(vector.x), Mathf.Abs(vector.y), Mathf.Abs(vector.z));
            if (absolute.x >= absolute.y && absolute.x >= absolute.z)
                return 0;
            return absolute.y >= absolute.z ? 1 : 2;
        }

        /// <summary>
        /// Removes the entire positive ClayObject that owns the hit collider. Negative
        /// blend carving is handled separately by CarverTool.
        /// </summary>
        public bool RemoveWholeClayObject(RaycastHit surfaceHit)
        {
            if (!TryGetRemovalTarget(surfaceHit, out ClayObject clayObject))
                return false;

            if (clayObject.name != _clayName)
                return false;

            GameObject objectToRemove = clayObject.gameObject;

            objectToRemove.SetActive(false);
            _targetFigurine.scheduleClayObjectsScan();
            Destroy(objectToRemove);
            MarkFigurineCollidersDirty();
            Physics.SyncTransforms();

            return true;
        }

        private bool TryGetRemovalTarget(RaycastHit surfaceHit, out ClayObject clayObject)
        {
            clayObject = null;
            if (IsSculptingLocked || _targetFigurine == null || _targetFigurine.isFrozen() || surfaceHit.collider == null)
                return false;

            clayObject = surfaceHit.collider.GetComponentInParent<ClayObject>();
            return IsClayObjectInTarget(clayObject) && IsPositiveClayObject(clayObject) && clayObject.gameObject.activeInHierarchy;
        }
    }
}
