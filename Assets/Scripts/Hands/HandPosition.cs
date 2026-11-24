using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations.Rigging;
using UnityEngine.InputSystem;
using SandDefenseWithSand.Clay;

namespace SandDefenseWithSand.Hands
{
    public class HandPosition : MonoBehaviour
    {
        private const float MaxDragDeltaTime = 1f / 30f;
        private static readonly List<HandPosition> EnabledHands = new List<HandPosition>(2);
        internal static IReadOnlyList<HandPosition> ActiveHands => EnabledHands;

        [SerializeField] protected Transform _handTransform;
        // [SerializeField] protected Transform _wristTransform;
        // [SerializeField] protected Transform _handMiddleTransform;

        [SerializeField] protected float _sensitivity;
        [SerializeField] protected float _minClampX;
        [SerializeField] protected float _maxClampX;
        [SerializeField] protected float _minClampY;
        [SerializeField] protected float _maxClampY;
        [SerializeField] protected float _minClampZ;
        [SerializeField] protected float _maxClampZ;

        [SerializeField] protected InputActionReference _mousePositionAction;
        [SerializeField] protected InputActionReference _mouseHoldAction;
        [SerializeField] protected LayerMask _obstacleLayers = ~0;
        [Tooltip("Optional capsule start marker. Defaults to Forearm Transform. The other end is Hand Transform (the IK target).")]
        [SerializeField] private Transform _collisionStartTransform;
        [Tooltip("World-space radius of the capsule from the forearm endpoint to the palm target.")]
        [SerializeField, Min(0.001f)] protected float _capsuleRadius = 0.03f;

        [Header("Collision Movement")]
        [Tooltip("Gap to keep between the arm capsule and obstacles.")]
        [SerializeField, Min(0.001f)] private float _raycastDistance = 0.005f;

        [Tooltip("Higher values make the hand catch up to the mouse target faster.")]
        [SerializeField, Min(0.1f)] private float _movementSmoothing = 12f;

        [Tooltip("Maximum speed of mouse-driven hand movement in world units per second.")]
        [SerializeField, Min(0.01f)] private float _maxMovementSpeed = 1.5f;

        [Header("Mouse Wheel Depth")]
        [SerializeField] private InputActionReference _mouseScrollAction;

        [Header("Wrist Rotation")]
        [SerializeField] private InputActionReference _wristRotationAction;
        [Tooltip("Forearm bone used for wrist rotation axes and the default arm capsule start.")]
        [SerializeField] private Transform _forearmTransform;
        [Tooltip("Assign hand.L or hand.R (the Tip bone in the Two Bone IK constraint).")]
        [SerializeField] private Transform _wristBoneTransform;
        [SerializeField, Min(0f)] private float _wristRotationSpeed = 90f;

        [Tooltip("W increases wrist flexion; S increases wrist extension. Limits are in degrees.")]
        [SerializeField] private float _minFlexionExtensionAngle = -70f;
        [SerializeField] private float _maxFlexionExtensionAngle = 80f;

        [Tooltip("D increases pronation; A increases supination. Limits are in degrees.")]
        [SerializeField] private float _minPronationSupinationAngle = -80f;
        [SerializeField] private float _maxPronationSupinationAngle = 80f;

        [Header("Anatomical Rotation Axes (Forearm Local Space)")]
        [Tooltip("Axis for wrist flexion/extension. This is commonly perpendicular to the deviation axis. Adjust for your rig's bone orientation; reverse the vector if W causes extension instead of flexion.")]
        [SerializeField] private Vector3 _flexionExtensionAxis = Vector3.right;
        [Tooltip("Axis along the forearm for pronation/supination. Adjust for your rig's bone orientation. Positive angle should be pronation.")]
        [SerializeField] private Vector3 _pronationSupinationAxis = Vector3.up;

        // Initial target-to-tip rotation offset used by Two Bone IK when
        // Maintain Target Offset includes Rotation.
        private Quaternion _targetToWristRotationOffset = Quaternion.identity;
        private Quaternion _idleWristRotation = Quaternion.identity;
        private float _flexionExtensionAngle;
        private float _pronationSupinationAngle;
        // Action clips add a small wrist gesture without replacing the player's
        // wrist angle or moving the collision-tested IK target.
        [SerializeField, HideInInspector] private float _toolFlexionOffset;
        [SerializeField, HideInInspector] private float _toolSupinationOffset;

        [SerializeField, Min(0.01f)]
        private float _depthStep = 0.15f;

        private Vector3 _dragGoal;
        private Vector2 _previousDragPointer;
        private bool _dragAnchored;
        private bool _dragActive;
        private bool _recoveringFromFigurine;
        private bool _pushedByFigurine;
        private bool _waitingForCursorAfterPush;
        private Vector2 _cursorAtFigurinePush;
        private uint _lastFigurinePoseVersion;

        protected Animator _handAnimator;
        protected bool _isInitialized;

        private ArmCapsuleCollision _collision;
        internal HandContactGeometry ContactGeometry { get; private set; }
        private HandContactCollision _handContacts;
        private Func<Vector3, Vector3, float> _limitTranslation;
        private int _fingerPoseFrame = -1;
        private int _sharedRigFrame = -1;
        internal HeldShovelCollision HeldShovel { get; private set; }
        private RigBuilder _rigBuilder;
        private Transform[] _poseTransforms;
        private Vector3[] _posePositions;
        private Quaternion[] _poseRotations;
        private Vector3[] _poseScales;
        private HandPosition[] _handControllers;
        private Func<Vector3, ArmCapsuleCollision.Pose> _evaluatePose;
        private Func<Vector3, Vector3> _limitToReach;
        private Func<Vector3, int, float, float> _clampAxisToReach;
        private Action _prepareRecoveryPose;
        private Vector3 _solvedPosition;
        private Quaternion _solvedRotation;
        private bool _controlledPoseInitialized;
        private bool _mouseMoveRequested;
        private bool _movedVerticallyByDrag;
        private int _dragStartFrame;
        private bool _collisionGeometryInitialized;
        private bool _rigInitialized;
        private Transform _armRoot;
        private float _armReach;
        private TwoBoneIKConstraint _armConstraint;
        private bool _previewArm;
        private Vector3 _previewRoot, _previewHint, _previewBend;
        private float _upperArmLength, _forearmLength;

        public void ApplyOffsetToHand()
        {
            Camera camera = Camera.main;
            if (!_collisionGeometryInitialized ||
                _handTransform == null ||
                _mousePositionAction == null ||
                _mousePositionAction.action == null ||
                camera == null)
            {
                return;
            }

            Vector2 mousePos = _mousePositionAction.action.ReadValue<Vector2>();
            if (!_dragActive)
            {
                _dragGoal = _solvedPosition;
                _previousDragPointer = mousePos;
                _dragActive = true;
            }

            if (_mouseMoveRequested && !_dragAnchored)
            {
                foreach (HandPosition other in _handControllers)
                {
                    if (other == this || !other._mouseMoveRequested ||
                        other._mousePositionAction?.action != _mousePositionAction.action)
                        continue;

                    // Two hands sharing a pointer start from their own positions.
                    AnchorDrag(mousePos);
                    break;
                }
            }

            Vector3 nextGoal = _dragGoal;
            if (_mouseScrollAction?.action != null)
            {
                float scrollY = _mouseScrollAction.action.ReadValue<Vector2>().y;
                if (Mathf.Abs(scrollY) > .01f)
                    nextGoal.z = Mathf.Clamp(nextGoal.z - Mathf.Sign(scrollY) * _depthStep, _minClampZ, _maxClampZ);
            }

            Vector2 pointerDelta = mousePos - _previousDragPointer;
            if (!_dragAnchored || pointerDelta.sqrMagnitude > 0f)
            {
                Quaternion aimFromView = Quaternion.identity;
                if (camera.TryGetComponent(out HandCameraTilt cameraTilt) && cameraTilt.isActiveAndEnabled)
                    aimFromView = cameraTilt.AimFromView;

                // Once anchored, only new pointer travel changes X/Y. Measure that
                // travel at the previous depth so a wheel step changes Z alone.
                // Use the same aiming view for both projections to avoid camera feedback.
                Vector3 cameraPosition = camera.transform.position;
                Vector2 pointer = _dragAnchored
                    ? (Vector2)camera.WorldToScreenPoint(cameraPosition +
                        Quaternion.Inverse(aimFromView) * (_dragGoal - cameraPosition)) + pointerDelta
                    : mousePos;
                float planeZ = _dragAnchored ? _dragGoal.z : nextGoal.z;
                Plane handPlane = new Plane(Vector3.forward, new Vector3(0f, 0f, planeZ));
                Ray viewRay = camera.ScreenPointToRay(pointer);
                Ray ray = new Ray(cameraPosition + aimFromView * (viewRay.origin - cameraPosition),
                    aimFromView * viewRay.direction);
                if (!handPlane.Raycast(ray, out float distance))
                    return;

                Vector3 point = ray.GetPoint(distance);
                nextGoal.x = Mathf.Clamp(point.x, _minClampX, _maxClampX);
                nextGoal.y = Mathf.Clamp(point.y, _minClampY, _maxClampY);
            }

            _dragGoal = nextGoal;
            _previousDragPointer = mousePos;
            Vector3 currentPosition = _solvedPosition;
            if (_dragGoal.Equals(currentPosition))
                return;

            // Keep a long frame from turning the next drag update into a jump.
            float deltaTime = Mathf.Min(Time.deltaTime, MaxDragDeltaTime);
            Vector3 target = ClampToArmReachAtDepth(_dragGoal);
            float blend = 1f - Mathf.Exp(-_movementSmoothing * deltaTime);
            target = Vector3.Lerp(currentPosition, target, blend);
            float maxTravel = _maxMovementSpeed * deltaTime;
            target = Vector3.MoveTowards(currentPosition, target, maxTravel);
            Vector3 inputTarget = Vector3.MoveTowards(currentPosition, Vector3.Lerp(currentPosition, nextGoal, blend), maxTravel);
            _handTransform.position = MoveWithCollisions(currentPosition, target, inputTarget, out int clearAxes);
            if (_collision.WasConstrained)
            {
                // Drop blocked input and let clear directions finish catching up.
                AnchorDrag(mousePos);
                for (int axis = 0; axis < 3; axis++)
                    if ((clearAxes & (1 << axis)) != 0)
                        _dragGoal[axis] = nextGoal[axis];
            }
        }

        private void AnchorDrag(Vector2 pointer)
        {
            _dragGoal = _solvedPosition;
            _previousDragPointer = pointer;
            _dragActive = true;
            _dragAnchored = true;
        }

        private void CaptureControlledPose()
        {
            if (_handTransform == null) return;
            _solvedPosition = _handTransform.position;
            _solvedRotation = _handTransform.rotation;
            _controlledPoseInitialized = true;
        }

        private void InitializeCollisionGeometry()
        {
            if (_handTransform == null) return;
            if (!_controlledPoseInitialized) CaptureControlledPose();
            _handAnimator = GetComponent<Animator>();
            _rigBuilder = GetComponent<RigBuilder>();
            _armRoot = null;
            _armConstraint = null;
            _armReach = 0f;
            foreach (TwoBoneIKConstraint constraint in GetComponentsInChildren<TwoBoneIKConstraint>(true))
            {
                var data = constraint.data;
                if (data.target != _handTransform || data.root == null || data.mid == null || data.tip == null)
                    continue;
                _armRoot = data.root;
                _armConstraint = constraint;
                float upper = Vector3.Distance(data.root.position, data.mid.position);
                float forearm = Vector3.Distance(data.mid.position, data.tip.position);
                _armReach = upper + forearm;
                // Unity stops applying the elbow hint near full extension. Keep
                // enough bend for the collision preview and the evaluated rig to agree.
                float minimumBend = _armReach * Mathf.Sqrt(.001f);
                if (data.hint != null && minimumBend < Mathf.Min(upper, forearm))
                    _armReach = Mathf.Sqrt(upper * upper - minimumBend * minimumBend) +
                        Mathf.Sqrt(forearm * forearm - minimumBend * minimumBend);
                _solvedPosition = ClampToArmReach(_solvedPosition);
                break;
            }
            _handControllers = GetComponents<HandPosition>();
            Transform root = _handAnimator != null ? _handAnimator.transform : transform;
            _poseTransforms = root.GetComponentsInChildren<Transform>(true);
            _posePositions = new Vector3[_poseTransforms.Length];
            _poseRotations = new Quaternion[_poseTransforms.Length];
            _poseScales = new Vector3[_poseTransforms.Length];
            if (_collision == null) _collision = new ArmCapsuleCollision(transform);
            if (_wristBoneTransform != null)
            {
                ContactGeometry = new HandContactGeometry(_wristBoneTransform);
                if (ContactGeometry.Count > 0)
                    _handContacts = new HandContactCollision(ContactGeometry, transform, _obstacleLayers, _collision);
            }
            _limitTranslation = LimitTranslation;
            _evaluatePose = EvaluateCollisionPose;
            _limitToReach = ClampToArmReachAtDepth;
            _clampAxisToReach = ClampAxisToArmReach;
            _prepareRecoveryPose = CaptureEvaluationPose;
            _collisionGeometryInitialized = true;
        }

        private void InitializeRigAfterAnimation()
        {
            _handAnimator = GetComponent<Animator>();
            _rigBuilder = GetComponent<RigBuilder>();
            _handControllers = GetComponents<HandPosition>();

            if (_rigBuilder != null && _rigBuilder.enabled && _handAnimator != null)
            {
                // RigBuilder.OnEnable builds against the edit-time skeleton. The
                // Animator's first sample can change that skeleton before IK runs.
                // Bind once to the animated pose, retaining both authored targets.
                _rigBuilder.Clear();
                _handAnimator.Update(0f);
                RestoreControlledTargets();
                foreach (HandPosition controller in _handControllers)
                {
                    if (controller._handTransform != null && controller._wristBoneTransform != null)
                    {
                        controller._targetToWristRotationOffset =
                            Quaternion.Inverse(controller._handTransform.rotation) * controller._wristBoneTransform.rotation;
                        if (controller._forearmTransform != null)
                            controller._idleWristRotation = Quaternion.Inverse(controller._forearmTransform.rotation) *
                                controller._wristBoneTransform.rotation;
                    }
                }
                _rigBuilder.Build();
            }

            // Both hands share one Animator and graph. Rebuilding it again for the
            // other hand would capture an already constrained pose.
            foreach (HandPosition controller in _handControllers)
                controller._rigInitialized = true;
        }

        private Vector3 GetCapsuleStart(Vector3 handPosition)
        {
            Transform start = _collisionStartTransform != null ? _collisionStartTransform : _forearmTransform;
            return start != null ? start.position : handPosition;
        }

        private void RestoreControlledTargets()
        {
            foreach (HandPosition controller in _handControllers)
            {
                if (!controller.enabled || !controller.gameObject.activeInHierarchy || controller._handTransform == null)
                    continue;
                if (!controller._controlledPoseInitialized) controller.CaptureControlledPose();
                controller._handTransform.SetPositionAndRotation(controller._solvedPosition, controller._solvedRotation);
            }
        }

        private void EvaluateRig()
        {
            if (_rigBuilder != null && _rigBuilder.enabled && _rigBuilder.graph.IsValid())
                _rigBuilder.Evaluate(0f);
            // Both hands share this graph. Sampling it again for one elbow must
            // also restore finger joints already stopped at a surface this frame.
            foreach (HandPosition hand in _handControllers)
                if (hand._fingerPoseFrame == Time.frameCount)
                    hand.ContactGeometry.RestoreAnimation();
        }

        private void CaptureEvaluationPose()
        {
            RestoreControlledTargets();
            EvaluateRig();
            RestoreControlledTargets();
            _handContacts?.Capture(_solvedPosition, _solvedRotation);
            HeldShovel?.Capture(_solvedPosition, _solvedRotation);
            _previewArm = false;
            if (_armConstraint != null)
            {
                var data = _armConstraint.data;
                Rig rig = _armConstraint.GetComponentInParent<Rig>();
                _previewArm = _forearmTransform == data.mid &&
                    (_collisionStartTransform == null || _collisionStartTransform == data.mid) &&
                    data.hint != null && !data.maintainTargetPositionOffset &&
                    _armConstraint.weight >= .9999f && data.targetPositionWeight >= .9999f &&
                    data.hintWeight >= .9999f && (rig == null || rig.weight >= .9999f);
                if (_previewArm)
                {
                    _previewRoot = data.root.position;
                    _previewHint = data.hint.position - _previewRoot;
                    _previewBend = data.mid.position - _previewRoot;
                    _upperArmLength = Vector3.Distance(data.root.position, data.mid.position);
                    _forearmLength = Vector3.Distance(data.mid.position, data.tip.position);
                    return;
                }
            }
            for (int i = 0; i < _poseTransforms.Length; i++)
            {
                Transform bone = _poseTransforms[i];
                if (bone == null) continue;
                _posePositions[i] = bone.localPosition;
                _poseRotations[i] = bone.localRotation;
                _poseScales[i] = bone.localScale;
            }
        }

        private ArmCapsuleCollision.Pose EvaluateCollisionPose(Vector3 position)
        {
            if (_previewArm)
            {
                // Full-weight two-bone IK with a full-weight hint has a geometric
                // solution. Trial collision checks must not write into the live rig.
                Vector3 delta = position - _previewRoot;
                float distance = Mathf.Max(delta.magnitude, .00001f);
                Vector3 axis = delta / distance;
                float along = (_upperArmLength * _upperArmLength - _forearmLength * _forearmLength +
                    distance * distance) / (2f * distance);
                Vector3 bend = Vector3.ProjectOnPlane(_previewHint, axis);
                if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(_previewBend, axis);
                float height = Mathf.Sqrt(Mathf.Max(0f, _upperArmLength * _upperArmLength - along * along));
                return new ArmCapsuleCollision.Pose {
                    start = _previewRoot + axis * along + bend.normalized * height,
                    end = position
                };
            }
            // Start every trial from the same animated pose. Repeated IK evaluations
            // must not accumulate rotations or modify another hand's accepted target.
            for (int i = 0; i < _poseTransforms.Length; i++)
            {
                Transform bone = _poseTransforms[i];
                if (bone == null) continue;
                bone.GetLocalPositionAndRotation(out Vector3 positionBefore, out Quaternion rotationBefore);
                if (!positionBefore.Equals(_posePositions[i]) || !rotationBefore.Equals(_poseRotations[i]))
                    bone.SetLocalPositionAndRotation(_posePositions[i], _poseRotations[i]);
                if (!bone.localScale.Equals(_poseScales[i]))
                    bone.localScale = _poseScales[i];
            }
            RestoreControlledTargets();
            _handTransform.SetPositionAndRotation(position, _solvedRotation);
            EvaluateRig();
            RestoreControlledTargets();
            _handTransform.SetPositionAndRotation(position, _solvedRotation);
            return new ArmCapsuleCollision.Pose { start = GetCapsuleStart(position), end = position };
        }

        private Vector3 MoveWithCollisions(Vector3 currentPosition, Vector3 targetPosition, Vector3 inputTarget, out int clearAxes)
        {
            CaptureEvaluationPose();
            targetPosition = ClampToArmReach(targetPosition);
            _solvedPosition = _collision.Move(currentPosition, targetPosition, inputTarget, _capsuleRadius,
                _raycastDistance, _obstacleLayers, _evaluatePose, _clampAxisToReach, out clearAxes, _limitTranslation);
            if (_previewArm)
            {
                RestoreControlledTargets();
                EvaluateRig();
                RestoreControlledTargets();
            }
            return _solvedPosition;
        }

        private float LimitTranslation(Vector3 from, Vector3 to)
        {
            float fraction = _handContacts?.LimitTranslation(from, to) ?? 1f;
            return HeldShovel == null ? fraction : Mathf.Min(fraction, HeldShovel.LimitTranslation(from, to));
        }

        private Vector3 ClampToArmReach(Vector3 position)
        {
            if (_armRoot == null || _armReach <= 0f) return position;
            // A target outside the arm's reach stays separated from the wrist even
            // at full IK weight with Maintain Target Position Offset disabled.
            float reach = Mathf.Max(.001f, _armReach - .001f);
            return _armRoot.position + Vector3.ClampMagnitude(position - _armRoot.position, reach);
        }

        private float ClampAxisToArmReach(Vector3 from, int axis, float goal)
        {
            if (_armRoot == null || _armReach <= 0f) return goal;

            Vector3 root = _armRoot.position;
            Vector3 perpendicular = from - root;
            perpendicular[axis] = 0f;
            float reach = Mathf.Max(.001f, _armReach - .001f);
            float extent = Mathf.Sqrt(Mathf.Max(0f, reach * reach - perpendicular.sqrMagnitude));
            float min = Mathf.Min(from[axis], root[axis] - extent);
            float max = Mathf.Max(from[axis], root[axis] + extent);
            return Mathf.Clamp(goal, min, max);
        }

        private bool RecoverFromFigurineContact()
        {
            uint version = ClayxelsHandSweep.CollisionPoseVersion;
            if (!_recoveringFromFigurine && version == _lastFigurinePoseVersion)
                return false;

            _lastFigurinePoseVersion = version;
            ClayxelsHandSweep.SyncCollisionPoses();
            Vector3 current = _solvedPosition;
            var pose = new ArmCapsuleCollision.Pose { start = GetCapsuleStart(current), end = current };
            float maxTravel = Mathf.Min(_maxMovementSpeed * Time.deltaTime, .035f);
            _solvedPosition = _collision.RecoverFromFigurine(current, pose, _capsuleRadius, _raycastDistance,
                _obstacleLayers, maxTravel, _prepareRecoveryPose, _evaluatePose, _limitToReach, _limitTranslation);
            RestoreControlledTargets();
            _recoveringFromFigurine = _solvedPosition != current;
            if (_recoveringFromFigurine)
            {
                EvaluateRig();
                RestoreControlledTargets();
                if (_dragActive)
                    AnchorDrag(_previousDragPointer);
            }
            return _recoveringFromFigurine;
        }

        internal bool TryGetFigurineSweepArm(out ArmCapsuleCollision.Pose pose, out float radius, out int layers)
        {
            pose = default;
            radius = Mathf.Max(.001f, _capsuleRadius);
            layers = _obstacleLayers;
            if (!_collisionGeometryInitialized || _handTransform == null || layers == 0)
                return false;

            pose = new ArmCapsuleCollision.Pose { start = GetCapsuleStart(_solvedPosition), end = _solvedPosition };
            return true;
        }

        internal float GetFigurineBoxDepth(ArmCapsuleCollision.Pose pose, float radius, BoxCollider box,
            Vector3 position, Quaternion rotation)
        {
            return _collision.GetBoxDepth(pose, radius, box, position, rotation);
        }

        internal bool ControlsHand(Transform hand) => hand == _handTransform || hand == _wristBoneTransform;

        internal static HandPosition FindHandForGrip(Transform grip)
        {
            foreach (HandPosition hand in EnabledHands)
                if ((hand._wristBoneTransform != null && grip.IsChildOf(hand._wristBoneTransform)) ||
                    (hand._handTransform != null && grip.IsChildOf(hand._handTransform)))
                    return hand;
            return null;
        }

        internal void TrackHeldShovel(Transform item)
        {
            HeldShovel = new HeldShovelCollision(item, transform, _obstacleLayers);
        }

        internal void ReleaseShovel() => HeldShovel = null;

        internal bool WasPushedByFigurine => _pushedByFigurine;
        internal int DragStartFrame => _mouseMoveRequested ? _dragStartFrame : -1;
        internal float CollisionSkin => Mathf.Max(.001f, _raycastDistance);

        internal bool TryGetDigViewWeight(out float weight)
        {
            weight = 0f;
            if (!_movedVerticallyByDrag)
                return false;

            // Start tilting halfway down and reach full pitch near the lowest pose.
            float height = Mathf.InverseLerp(_minClampY, _maxClampY, _solvedPosition.y);
            weight = Mathf.InverseLerp(.5f, .1f, height);
            return true;
        }

        private bool ShouldApplyMouseDrag()
        {
            if (!_mouseMoveRequested)
            {
                _waitingForCursorAfterPush = false;
                return false;
            }
            if (!_pushedByFigurine && !_waitingForCursorAfterPush)
                return true;
            if (_mousePositionAction == null || _mousePositionAction.action == null)
                return false;

            Vector2 cursor = _mousePositionAction.action.ReadValue<Vector2>();
            bool moved = _waitingForCursorAfterPush && (cursor - _cursorAtFigurinePush).sqrMagnitude > .25f;
            bool scrolled = _mouseScrollAction != null && _mouseScrollAction.action != null &&
                Mathf.Abs(_mouseScrollAction.action.ReadValue<Vector2>().y) > .01f;
            if (_pushedByFigurine)
            {
                _cursorAtFigurinePush = cursor;
                _waitingForCursorAfterPush = true;
                if (!moved && !scrolled)
                    AnchorDrag(cursor);
            }
            else if (moved || scrolled)
            {
                _waitingForCursorAfterPush = false;
            }
            return moved || scrolled;
        }

        private Vector3 ClampToArmReachAtDepth(Vector3 position)
        {
            if (_armRoot == null || _armReach <= 0f) return position;

            // Keep the selected depth whenever it intersects the arm's reach.
            // Only the in-plane coordinates need to move toward the shoulder.
            Vector3 root = _armRoot.position;
            float reach = Mathf.Max(.001f, _armReach - .001f);
            if ((position - root).sqrMagnitude <= reach * reach) return position;
            float depth = Mathf.Clamp(position.z - root.z, -reach, reach);
            float planeReach = Mathf.Sqrt(Mathf.Max(0f, reach * reach - depth * depth));
            Vector2 offset = Vector2.ClampMagnitude(new Vector2(position.x - root.x, position.y - root.y), planeReach);
            return root + new Vector3(offset.x, offset.y, depth);
        }

        protected virtual void OnDestroy()
        {
            _collision?.Dispose();
        }

        protected virtual void OnEnable()
        {
            EnabledHands.Add(this);
        }

        protected virtual void OnDisable()
        {
            EnabledHands.Remove(this);
            _dragActive = false;
            _dragAnchored = false;
            _recoveringFromFigurine = false;
            _pushedByFigurine = false;
            _waitingForCursorAfterPush = false;
            _lastFigurinePoseVersion = 0;
            _collision?.Dispose();
            _collision = null;
            _handContacts = null;
            ContactGeometry = null;
            _fingerPoseFrame = -1;
            _collisionGeometryInitialized = false;
            _rigInitialized = false;
            _mouseMoveRequested = false;
            _movedVerticallyByDrag = false;
        }

        #if UNITY_EDITOR
        private Transform _gizmoWrist;
        private HandContactGeometry _gizmoGeometry;

        protected virtual void OnDrawGizmosSelected()
        {
            if (_handTransform == null) return;
            Vector3 end = _handTransform.position;
            Gizmos.color = Color.cyan;
            DrawCapsule(GetCapsuleStart(end), end, Mathf.Max(.001f, _capsuleRadius));

            HandContactGeometry geometry = ContactGeometry;
            if (geometry == null && _wristBoneTransform != null)
            {
                if (_gizmoWrist != _wristBoneTransform || _gizmoGeometry == null)
                {
                    _gizmoWrist = _wristBoneTransform;
                    _gizmoGeometry = new HandContactGeometry(_gizmoWrist);
                }
                geometry = _gizmoGeometry;
            }
            if (geometry == null) return;
            for (int i = 0; i < geometry.Count; i++)
            {
                geometry.GetCapsule(i, out Vector3 a, out Vector3 b, out float radius);
                DrawCapsule(a, b, radius);
            }
        }

        private static void DrawCapsule(Vector3 start, Vector3 end, float radius)
        {
            Vector3 axis = end - start;
            Quaternion rotation = axis.sqrMagnitude > 1e-10f
                ? Quaternion.FromToRotation(Vector3.up, axis.normalized) : Quaternion.identity;
            Vector3 side = rotation * Vector3.right * radius;
            Vector3 forward = rotation * Vector3.forward * radius;
            Gizmos.DrawWireSphere(start, radius);
            Gizmos.DrawWireSphere(end, radius);
            Gizmos.DrawLine(start + side, end + side);
            Gizmos.DrawLine(start - side, end - side);
            Gizmos.DrawLine(start + forward, end + forward);
            Gizmos.DrawLine(start - forward, end - forward);
        }
        #endif

        public bool MouseMoveDetected()
        {
            if (_mouseHoldAction == null ||
                _mouseHoldAction.action == null)
            {
                return false;
            }

            bool isMousePressed =
                _mouseHoldAction.action.IsPressed();

            if (!isMousePressed && !_isInitialized)
                return false;

            if (isMousePressed && !_isInitialized)
                _isInitialized = true;

            return isMousePressed;
        }

        protected virtual void Start()
        {
            _collisionGeometryInitialized = false;
            CaptureControlledPose();
            _handAnimator = GetComponent<Animator>();

            if (_handTransform != null &&
                _forearmTransform != null &&
                _wristBoneTransform != null)
            {
                _targetToWristRotationOffset =
                    Quaternion.Inverse(_handTransform.rotation) * _wristBoneTransform.rotation;
            }
            else
            {
                Debug.LogWarning(
                    $"{nameof(HandPosition)} on {name}: assign the IK target " +
                    "(_handTransform), matching forearm (_forearmTransform), and wrist bone " +
                    "(_wristBoneTransform).",
                    this
                );
            }
        }

        protected virtual void LateUpdate()
        {
            _movedVerticallyByDrag = false;
            if (_handTransform == null) return;
            if (!_rigInitialized) InitializeRigAfterAnimation();
            if (!_collisionGeometryInitialized) InitializeCollisionGeometry();
            // Animator clips (including Write Defaults) can overwrite IK targets.
            // Restore accepted targets AFTER animation, then evaluate the actual rig.
            RestoreControlledTargets();
            if (_sharedRigFrame != Time.frameCount)
            {
                EvaluateRig();
                RestoreControlledTargets();
                Physics.SyncTransforms();
                foreach (HandPosition hand in _handControllers)
                    hand._sharedRigFrame = Time.frameCount;
            }
            if (_handContacts != null)
            {
                ContactGeometry.LimitAnimation(_handContacts);
                _fingerPoseFrame = Time.frameCount;
            }
            _pushedByFigurine = RecoverFromFigurineContact();
            if (ShouldApplyMouseDrag())
            {
                float previousHeight = _solvedPosition.y;
                ApplyOffsetToHand();
                _movedVerticallyByDrag = Mathf.Abs(_solvedPosition.y - previousHeight) > .00001f;
            }

            // The elbow may have moved during collision resolution. Align the wrist
            // with this frame's accepted forearm, without accumulating input twice.
            Quaternion beforeRotation = _solvedRotation;
            ApplyWristRotation();
            if (!beforeRotation.Equals(_solvedRotation))
            {
                RestoreControlledTargets();
                EvaluateRig();
                RestoreControlledTargets();
            }
        }

        private static bool IsActionPressed(InputActionReference actionReference)
        {
            return actionReference != null &&
                   actionReference.action != null &&
                   actionReference.action.IsPressed();
        }

        private void ApplyWristRotation()
        {
            if (_handTransform == null ||
                _forearmTransform == null ||
                _wristBoneTransform == null)
            {
                return;
            }

            // Only accumulate anatomical rotation while either mouse button is held.
            bool mouseButtonHeld = IsActionPressed(_mouseHoldAction);

            float previousFlexion = _flexionExtensionAngle;
            float previousSupination = _pronationSupinationAngle;
            if (mouseButtonHeld && _wristRotationAction != null && _wristRotationAction.action != null)
            {
                // W/S produce input.y; A/D produce input.x.
                Vector2 input = _wristRotationAction.action.ReadValue<Vector2>();
                float angleStep = _wristRotationSpeed * Time.deltaTime;

                // W = flexion, S = extension. Clamp the accumulated angle.
                _flexionExtensionAngle = Mathf.Clamp(
                    _flexionExtensionAngle + -input.y * angleStep,
                    Mathf.Min(_minFlexionExtensionAngle, _maxFlexionExtensionAngle),
                    Mathf.Max(_minFlexionExtensionAngle, _maxFlexionExtensionAngle));

                // D = pronation, A = supination. Clamp independently.
                _pronationSupinationAngle = Mathf.Clamp(
                    _pronationSupinationAngle + -input.x * angleStep,
                    Mathf.Min(_minPronationSupinationAngle, _maxPronationSupinationAngle),
                    Mathf.Max(_minPronationSupinationAngle, _maxPronationSupinationAngle));
            }

            // Axes are expressed in the forearm's local rotation space. Their
            // directions depend on the imported rig; flip an axis if the movement
            // runs opposite to the anatomical direction you expect.
            Vector3 flexionAxis = _flexionExtensionAxis.normalized;
            Vector3 pronationAxis = _pronationSupinationAxis.normalized;

            Quaternion flexionExtension =
                Quaternion.AngleAxis(_flexionExtensionAngle + _toolFlexionOffset, flexionAxis);
            Quaternion pronationSupination =
                Quaternion.AngleAxis(_pronationSupinationAngle + _toolSupinationOffset, pronationAxis);
            Quaternion wristOffset = pronationSupination * flexionExtension;

            // Rotate about the forearm axes starting from the Animator's idle wrist pose.
            Quaternion requestedRotation =
                _forearmTransform.rotation *
                wristOffset *
                _idleWristRotation *
                Quaternion.Inverse(_targetToWristRotationOffset);
            if (_handContacts != null || HeldShovel != null)
            {
                _handContacts?.Capture(_solvedPosition, _solvedRotation);
                Quaternion accepted = _handContacts?.LimitRotation(_solvedPosition, _solvedRotation, requestedRotation)
                    ?? requestedRotation;
                if (HeldShovel != null)
                {
                    HeldShovel.Capture(_solvedPosition, _solvedRotation);
                    accepted = HeldShovel.LimitRotation(_solvedPosition, _solvedRotation, accepted);
                }
                float arc = Quaternion.Angle(_solvedRotation, requestedRotation);
                if (arc > .001f)
                {
                    float fraction = Quaternion.Angle(_solvedRotation, accepted) / arc;
                    _flexionExtensionAngle = Mathf.Lerp(previousFlexion, _flexionExtensionAngle, fraction);
                    _pronationSupinationAngle = Mathf.Lerp(previousSupination, _pronationSupinationAngle, fraction);
                }
                requestedRotation = accepted;
            }
            _solvedRotation = requestedRotation;
            _handTransform.rotation = _solvedRotation;
        }

        protected virtual void Update()
        {
            if (!_controlledPoseInitialized) CaptureControlledPose();
            bool dragRequested = MouseMoveDetected();
            if (dragRequested && !_mouseMoveRequested)
                _dragStartFrame = Time.frameCount;
            if (!dragRequested)
            {
                _dragActive = false;
                _dragAnchored = false;
            }
            _mouseMoveRequested = dragRequested;
        }
    }
}
