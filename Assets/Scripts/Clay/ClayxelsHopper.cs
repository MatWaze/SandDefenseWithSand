using System.Collections;
using System.Collections.Generic;
using Clayxels;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SandDefenseWithSand.Clay
{
    /// <summary>
    /// Moves a reanimated Clayxels sculpture with box colliders or convex hulls.
    /// </summary>
    [DefaultExecutionOrder(100)] // Read the accepted IK targets after HandPosition.LateUpdate.
    public class ClayxelsHopper : MonoBehaviour
    {
        private enum CollisionMode { ClayObjectBoxes, GeneratedMeshColliders, MeshSnapshotWithBoxColliders }

        private const float WakeLookAngle = 25f;
        private const float WakeHopTimeScale = 1.3f;
        private const float WakeTurnTime = 0.45f;
        private const float WakeLookPause = 0.18f;

        [SerializeField] private BoxCollider _placementAnchor;
        internal BoxCollider PlacementAnchor => _placementAnchor;

        [Header("Reanimation")]
        [SerializeField] private InputActionReference _reanimateFigurine;
        [Tooltip("Set before reanimating. Clay Object Boxes keeps Clayxels rendering and the original boxes. Mesh Snapshot With Box Colliders swaps only the renderer. Generated Mesh Colliders replaces the boxes with convex hulls.")]
        [SerializeField] private CollisionMode _collisionMode = CollisionMode.ClayObjectBoxes;

        [Header("Enemy detection")]
        [SerializeField] private LayerMask _enemyLayer;
        [SerializeField, Min(0.1f)] private float _detectionRadius = 8f;
        [SerializeField, Min(0.05f)] private float _targetSearchInterval = 0.25f;
        [SerializeField, Min(0f)] private float _stoppingDistance = 1.2f;

        [Header("Hopping")]
        [SerializeField, Min(0f)] private float _chaseSpeed = 1.5f;
        [SerializeField, Min(0f)] private float _hopHeight = 0.25f;
        [SerializeField, Min(0.1f)] private float _hopsPerSecond = 2f;
        [SerializeField, Min(0f)] private float _turnSpeed = 8f;

        [Header("Look at last moved hand")]
        [Tooltip("Assign the transform moved by the left-hand IK/position controller.")]
        [SerializeField] private Transform _leftHand;
        [Tooltip("Assign the transform moved by the right-hand IK/position controller.")]
        [SerializeField] private Transform _rightHand;
        [Tooltip("Minimum hand movement in meters per 1/60 second before the sculpture responds.")]
        [SerializeField, Min(0f)] private float _handMovementThreshold = 0.001f;
        [Tooltip("How quickly the sculpture turns to face the selected hand.")]
        [SerializeField, Min(0.01f)] private float _handLookTurnSpeed = 8f;
        [Tooltip("Maximum turn away from the starting heading while idly looking at a hand.")]
        [SerializeField, Range(0f, 89f)] private float _idleHandLookLimit = 35f;
        [Tooltip("Yaw correction for the sculpture's front when facing a hand or enemy. Use 180 when the front points along local -Z.")]
        [SerializeField, Range(-180f, 180f)] private float _handLookYawOffset = 180f;

        [Header("Movement preparation")]
        [Tooltip("ClayObject behaviours paused per frame before movement starts.")]
        [SerializeField, Min(1)] private int _clayObjectsPerFrame = 32;
        [Tooltip("Maximum number of frames to wait for a pending Clayxels sculpt update before preparing movement.")]
        [SerializeField, Min(1)] private int _maxClayxelsWaitFrames = 120;

        [Header("Mesh snapshot")]
        [Tooltip("Number of snapshot vertices uploaded to the Unity Mesh per frame. Smaller values spread upload work across more frames.")]
        [SerializeField, Min(256)] private int _meshUploadVerticesPerFrame = 8192;
        [Tooltip("Use the snapshot renderer in Generated Mesh Colliders mode. Mesh Snapshot With Box Colliders always uses it.")]
        [SerializeField] private bool _showMeshSnapshot = true;

        [Header("Convex decomposition collider")]
        [Tooltip("Maximum number of convex hulls. Lower values are faster and produce simpler collision geometry.")]
        [SerializeField, Range(1, 128)] private int _maxConvexHulls = 8;
        [Tooltip("VHACD voxel resolution. 10000 is the package's LowResolution setting; higher values can take longer.")]
        [SerializeField, Range(10000, 6400000)] private int _convexColliderResolution = 10000;

        [SerializeField] private bool _logPreparationTimings = true;

        private ClayContainer _clayContainer;
        private bool _isPreparing;
        private ClayxelsMovementPreparation _movementPreparation;
        private readonly ClayxelsHandSweep _handSweep = new ClayxelsHandSweep();
        private readonly ClayxelsHandLook _handLook = new ClayxelsHandLook();

        private Vector3 _groundPosition;
        private Vector3 _supportOffset;
        private float _hopOffset;
        private float _motionTime;
        private bool _isWaking;
        private float _wakeElapsed;
        private Quaternion _wakeStartRotation;
        private float _nextTargetSearchTime;
        private Transform _currentTarget;
        private LayerMask _effectiveEnemyLayer;
        private bool _isReanimated;
        private int _spawnFrame;
        private readonly List<EnemyController> _enemyTargets = new List<EnemyController>(32);

        internal bool IsSculptingLocked => _isPreparing || _isReanimated;
        internal bool HasReanimatedBoxes => _isReanimated && _movementPreparation?.HasGeneratedColliders != true;
        internal event System.Action<ClayxelsHopper> Reanimated;

        private void Awake()
        {
            _clayContainer = GetComponent<ClayContainer>();
            _effectiveEnemyLayer = _enemyLayer;
            _groundPosition = transform.position;
            _spawnFrame = Time.frameCount;
        }

        private void Update()
        {
            _motionTime += Time.deltaTime;

            if (Time.frameCount != _spawnFrame && !_isReanimated && !_isPreparing && _reanimateFigurine != null &&
                _reanimateFigurine.action != null && _reanimateFigurine.action.WasPressedThisFrame())
            {
                Reanimate();
            }

            if (!_isReanimated)
                return;

            if (Time.time >= _nextTargetSearchTime)
            {
                var nearestEnemy = FindNearestEnemy();

                if (nearestEnemy)
                {
                    _currentTarget = FindNearestEnemy().transform;
                    _nextTargetSearchTime = Time.time + _targetSearchInterval;
                }
            }

            if (_currentTarget != null)
            {
                if (_isWaking)
                {
                    _motionTime = HopDuration * WakeHopProgress;
                    _isWaking = false;
                }
                UpdateChasing();
            }
            else
            {
                _hopOffset = 0f;
            }
        }

        private void LateUpdate()
        {
            if (_isReanimated)
                ApplyMovementPose();
        }

        /// <summary>Activates enemy detection and hopping after the asynchronous Clayxels preparation finishes.</summary>
        public void Reanimate()
        {
            if (_isPreparing || _isReanimated)
                return;

            if (_placementAnchor != null)
            {
                if (GetComponentInChildren<ClayObject>() == null) return;
                _placementAnchor.enabled = false;
            }
            _isPreparing = true;
            StartCoroutine(PrepareThenActivate());
        }

        private IEnumerator PrepareThenActivate()
        {
            CollisionMode mode = _collisionMode;
            if (_clayContainer != null)
            {
                _movementPreparation = new ClayxelsMovementPreparation(_clayContainer, new ClayxelsMovementPreparation.Settings
                {
                    ClayObjectsPerFrame = _clayObjectsPerFrame,
                    MaxWaitFrames = _maxClayxelsWaitFrames,
                    MeshUploadVerticesPerFrame = _meshUploadVerticesPerFrame,
                    MaxConvexHulls = _maxConvexHulls,
                    ConvexColliderResolution = _convexColliderResolution,
                    LogTimings = _logPreparationTimings
                });
                yield return _movementPreparation.Prepare(
                    mode != CollisionMode.ClayObjectBoxes, mode == CollisionMode.GeneratedMeshColliders);

                if (!_movementPreparation.IsReady)
                {
                    _movementPreparation.Dispose();
                    _movementPreparation = null;
                    if (_placementAnchor != null) _placementAnchor.enabled = true;
                    _isPreparing = false;
                    yield break;
                }
            }

            _groundPosition = GetSupportPoint();
            _supportOffset = Quaternion.Inverse(transform.rotation) * (_groundPosition - transform.position);
            _hopOffset = 0f;
            _movementPreparation?.Activate(mode == CollisionMode.MeshSnapshotWithBoxColliders || _showMeshSnapshot);
            _handLook.Reset(_leftHand, _rightHand);
            RefreshEnemyCache();
    
            // _nextTargetSearchTime = Time.time + _targetSearchInterval;

            _isWaking = _currentTarget == null;
            _wakeElapsed = 0f;
            _wakeStartRotation = transform.rotation;
            _isReanimated = true;
            _isPreparing = false;
            if (HasReanimatedBoxes)
            {
                _handSweep.Initialize(transform, _supportOffset);
                ClayxelsHandSweep.NotifyPoseChanged();
            }
            Reanimated?.Invoke(this);
        }

        /// <summary>
        /// Rebuilds the cached enemy list. Call this after spawning or changing the layer
        /// of enemies at runtime. Existing enemies do not need colliders.
        /// </summary>
        public void RefreshEnemyCache()
        {
            _enemyTargets.Clear();
            if (_effectiveEnemyLayer.value == 0)
                return;

            EnemyController[] allTransforms = FindObjectsByType<EnemyController>();
            var uniqueTargets = new HashSet<EnemyController>();

            foreach (var enemy in allTransforms)
            {
                if (uniqueTargets.Add(enemy))
                    _enemyTargets.Add(enemy);
            }
        }

        private void UpdateChasing()
        {
            Vector3 toTarget = _currentTarget.position - _groundPosition;
            Vector3 planarDirection = Vector3.ProjectOnPlane(toTarget, Vector3.up);
            float distance = planarDirection.magnitude;

            if (distance > Mathf.Max(0.001f, _stoppingDistance))
            {
                float distanceToMove = Mathf.Min(_chaseSpeed * Time.deltaTime, distance - _stoppingDistance);
                _groundPosition += planarDirection / distance * distanceToMove;
            }
            else
            {
                // foreach (var clayObject in _clayContainer.GetSortedClayObjects())
                // {
                // }
            }

            float hopPhase = _motionTime / HopDuration * Mathf.PI;
            _hopOffset = Mathf.Abs(Mathf.Sin(hopPhase)) * _hopHeight;
        }

        private void ApplyMovementPose()
        {
            bool wasWaking = _isWaking;
            bool lookingAtHand = false;
            Quaternion rotation = wasWaking ? UpdateWakeUpPose() : GetTrackingRotation(out lookingAtHand);
            Vector3 support = _groundPosition + Vector3.up * _hopOffset;
            Vector3 position = support - rotation * _supportOffset;
            Quaternion turn = Quaternion.Inverse(transform.rotation) * rotation;
            float turnSize = new Vector3(turn.x, turn.y, turn.z).sqrMagnitude;
            if ((wasWaking && !_isWaking) || (transform.position - position).sqrMagnitude > 0.00000001f ||
                turnSize > 0.00000001f)
            {
                if (HasReanimatedBoxes)
                {
                    _handSweep.LimitMotion(ref support, ref rotation, lookingAtHand);
                    position = support - rotation * _supportOffset;
                    _groundPosition.x = support.x;
                    _groundPosition.z = support.z;
                    if (position == transform.position && rotation.Equals(transform.rotation))
                        return;
                }
                transform.SetPositionAndRotation(position, rotation);
                if (HasReanimatedBoxes)
                    ClayxelsHandSweep.NotifyPoseChanged();
            }
        }

        private float HopDuration => 1f / Mathf.Max(0.1f, _hopsPerSecond);
        private float WakeHopDuration => HopDuration * WakeHopTimeScale;
        private float WakeHopProgress => Mathf.SmoothStep(0f, 1f, _wakeElapsed / WakeHopDuration);

        private Quaternion UpdateWakeUpPose()
        {
            _handLook.Reset(_leftHand, _rightHand);
            _wakeElapsed += Time.deltaTime;
            float lookTime = _wakeElapsed - WakeHopDuration;
            if (lookTime < 0f)
            {
                _hopOffset = _hopHeight * Mathf.Sin(Mathf.PI * WakeHopProgress);
                return _wakeStartRotation;
            }

            _hopOffset = 0f;
            const float leftEnd = WakeTurnTime + WakeLookPause;
            const float rightTurnTime = 2f * WakeTurnTime;
            const float rightEnd = leftEnd + rightTurnTime + WakeLookPause;
            float yaw;
            if (lookTime < leftEnd)
                yaw = Mathf.SmoothStep(0f, -WakeLookAngle, lookTime / WakeTurnTime);
            else if (lookTime < rightEnd)
                yaw = Mathf.SmoothStep(-WakeLookAngle, WakeLookAngle, (lookTime - leftEnd) / rightTurnTime);
            else if (lookTime < rightEnd + WakeTurnTime)
                yaw = Mathf.SmoothStep(WakeLookAngle, 0f, (lookTime - rightEnd) / WakeTurnTime);
            else
            {
                _isWaking = false;
                return _wakeStartRotation;
            }

            return Quaternion.AngleAxis(yaw, Vector3.up) * _wakeStartRotation;
        }

        private Quaternion GetTrackingRotation(out bool lookingAtHand)
        {
            lookingAtHand = false;
            Vector3 direction;
            float turnSpeed;
            if (_currentTarget != null)
            {
                _handLook.Reset(_leftHand, _rightHand);
                direction = _currentTarget.position - _groundPosition;
                turnSpeed = _turnSpeed;
            }
            else if (_handLook.TryGetFocus(_leftHand, _rightHand, _handMovementThreshold, out Vector3 focus))
            {
                lookingAtHand = true;
                direction = focus - _groundPosition;
                turnSpeed = _handLookTurnSpeed;
            }
            else
            {
                return transform.rotation;
            }

            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
                return transform.rotation;

            Quaternion desired = Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(0f, _handLookYawOffset, 0f);
            if (lookingAtHand)
            {
                Vector3 startingForward = Vector3.ProjectOnPlane(_wakeStartRotation * Vector3.forward, Vector3.up);
                float yaw = Vector3.SignedAngle(startingForward, desired * Vector3.forward, Vector3.up);
                desired = Quaternion.AngleAxis(Mathf.Clamp(yaw, -_idleHandLookLimit, _idleHandLookLimit), Vector3.up) * _wakeStartRotation;
            }
            return Quaternion.Slerp(transform.rotation, desired, 1f - Mathf.Exp(-turnSpeed * Time.deltaTime));
        }

        private Vector3 GetSupportPoint()
        {
            Bounds? lowestBox = null;
            foreach (BoxCollider box in GetComponentsInChildren<BoxCollider>())
            {
                if (!box.enabled || box.isTrigger)
                    continue;

                ClayObject piece = box.GetComponentInParent<ClayObject>();
                if (piece == null || piece.blend < 0f)
                    continue;

                Bounds bounds = box.bounds;
                if (!lowestBox.HasValue || bounds.min.y < lowestBox.Value.min.y)
                    lowestBox = bounds;
            }

            MeshRenderer snapshotRenderer = _movementPreparation?.SnapshotRenderer;
            if (!lowestBox.HasValue && snapshotRenderer != null)
                lowestBox = snapshotRenderer.bounds;
            if (!lowestBox.HasValue)
                return transform.position;

            Vector3 foot = lowestBox.Value.center;
            foot.y = lowestBox.Value.min.y;
            return foot;
        }

        private EnemyController FindNearestEnemy()
        {
            EnemyController nearestEnemy = null;
            float nearestDistanceSquared = float.PositiveInfinity;
            float radiusSquared = _detectionRadius * _detectionRadius;

            // No FindObjectsByType, HashSet, or per-search allocations here.
            for (int i = _enemyTargets.Count - 1; i >= 0; i--)
            {
                EnemyController enemy = _enemyTargets[i];
                if (enemy == null)
                {
                    _enemyTargets.RemoveAt(i);
                    continue;
                }

                if (!enemy.gameObject.activeInHierarchy)
                    continue;

                float distanceSquared = GetPlanarDistanceSquared(_groundPosition, enemy.transform.position);
                if (distanceSquared > radiusSquared || distanceSquared >= nearestDistanceSquared)
                    continue;

                nearestDistanceSquared = distanceSquared;
                nearestEnemy = enemy;
            }

            return nearestEnemy;
        }

        private Transform FindEnemyInParents(Transform startingTransform)
        {
            Transform result = null;
            for (Transform current = startingTransform; current != null; current = current.parent)
            {
                int layerBit = 1 << current.gameObject.layer;
                if ((_effectiveEnemyLayer.value & layerBit) != 0)
                    result = current;
            }
            return result;
        }

        private void OnDestroy()
        {
            _movementPreparation?.Dispose();
        }

        private static float GetPlanarDistanceSquared(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            delta.y = 0f;
            return delta.sqrMagnitude;
        }
    }
}
