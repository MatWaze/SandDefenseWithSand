using System.Collections.Generic;
using UnityEngine;
using SandDefenseWithSand.Hands;

namespace SandDefenseWithSand.Clay
{
    /// <summary>Limits a moving figurine's boxes against the arms, hands and held shovels.</summary>
    internal sealed class ClayxelsHandSweep
    {
        private const float HandContactDepth = 0.0015f;
        private const float ShovelSweepStep = 0.003f;
        private const int MaxHandSweepSteps = 16;

        private struct MovingBox
        {
            public BoxCollider collider;
            public Vector3 offset;
            public Quaternion rotation;
            public Vector3 center;
            public float radius;

            public bool IsActiveIn(int layers) => collider != null && collider.enabled &&
                collider.gameObject.activeInHierarchy && (layers & (1 << collider.gameObject.layer)) != 0;
        }

        private struct HandSweepPair
        {
            public MovingBox box;
            public HandPosition hand;
            public HeldShovelCollision shovel;
            public ArmCapsuleCollision.Pose arm;
            public float radius;
            public float allowedDepth;

            public float GetDepth(Vector3 rootPosition, Quaternion rootRotation)
            {
                Vector3 position = rootPosition + rootRotation * box.offset;
                Quaternion rotation = rootRotation * box.rotation;
                return shovel != null ? shovel.GetBoxDepth(box.collider, position, rotation) :
                    hand.GetFigurineBoxDepth(arm, radius, box.collider, position, rotation);
            }
        }

        private readonly List<MovingBox> _movingBoxes = new List<MovingBox>();
        private readonly List<HandSweepPair> _handSweepPairs = new List<HandSweepPair>();
        private float _boxSweepRadius;
        private Transform _root;
        private Vector3 _supportOffset;

        private static uint _syncedCollisionPoseVersion;
        internal static uint CollisionPoseVersion { get; private set; }

        internal static void NotifyPoseChanged()
        {
            CollisionPoseVersion++;
        }

        internal static void SyncCollisionPoses()
        {
            if (_syncedCollisionPoseVersion == CollisionPoseVersion)
                return;

            Physics.SyncTransforms();
            _syncedCollisionPoseVersion = CollisionPoseVersion;
        }

        public void Initialize(Transform root, Vector3 supportOffset)
        {
            _root = root;
            _supportOffset = supportOffset;
            _movingBoxes.Clear();
            _boxSweepRadius = 0f;
            Quaternion inverse = Quaternion.Inverse(_root.rotation);
            foreach (BoxCollider box in _root.GetComponentsInChildren<BoxCollider>())
            {
                if (!box.enabled || box.isTrigger)
                    continue;

                Transform boxTransform = box.transform;
                Vector3 scale = boxTransform.lossyScale;
                var movingBox = new MovingBox
                {
                    collider = box,
                    offset = inverse * (boxTransform.position - _root.position),
                    rotation = inverse * boxTransform.rotation,
                    center = inverse * (boxTransform.TransformPoint(box.center) - _root.position),
                    radius = Vector3.Scale(box.size * .5f, scale).magnitude
                };
                _movingBoxes.Add(movingBox);
                _boxSweepRadius = Mathf.Max(_boxSweepRadius, (movingBox.center - _supportOffset).magnitude + movingBox.radius);
            }
        }

        public void LimitMotion(ref Vector3 support, ref Quaternion rotation, bool preserveArmClearance = false)
        {
            if (_movingBoxes.Count == 0 || HandPosition.ActiveHands.Count == 0)
                return;

            Vector3 fromPosition = _root.position;
            Quaternion fromRotation = _root.rotation;
            Vector3 fromSupport = fromPosition + fromRotation * _supportOffset;
            Quaternion turn = Quaternion.Inverse(fromRotation) * rotation;
            float angle = 2f * Mathf.Atan2(new Vector3(turn.x, turn.y, turn.z).magnitude, Mathf.Abs(turn.w));
            float travel = Vector3.Distance(fromSupport, support) + angle * _boxSweepRadius;
            if (travel <= 0.00001f)
                return;

            float stepSize = .01f;
            Vector3 toPosition = support - rotation * _supportOffset;
            float arcDeviation = 1f - Mathf.Cos(angle * .5f);
            // Every moving box stays inside this swept support sphere, even while turning.
            Bounds figurineSweep = new Bounds(fromSupport, Vector3.zero);
            figurineSweep.Encapsulate(support);
            figurineSweep.Expand(2f * _boxSweepRadius);
            _handSweepPairs.Clear();
            foreach (HandPosition hand in HandPosition.ActiveHands)
            {
                if (!hand.TryGetFigurineSweepArm(out ArmCapsuleCollision.Pose arm, out float radius, out int layers))
                    continue;

                if (preserveArmClearance)
                    radius += hand.CollisionSkin;

                Bounds handBounds = new Bounds(arm.start, Vector3.zero);
                handBounds.Encapsulate(arm.end);
                handBounds.Expand(2f * radius);
                HandContactGeometry fingers = hand.ContactGeometry;
                for (int i = 0; fingers != null && i < fingers.Count; i++)
                {
                    fingers.GetCapsule(i, out Vector3 a, out Vector3 b, out float r);
                    Bounds digit = new Bounds(a, Vector3.zero);
                    digit.Encapsulate(b);
                    digit.Expand(2f * r);
                    handBounds.Encapsulate(digit);
                }
                if (figurineSweep.Intersects(handBounds))
                {
                    AddCapsulePairs(arm, radius, preserveArmClearance ? 0f : HandContactDepth);
                    for (int i = 0; fingers != null && i < fingers.Count; i++)
                    {
                        fingers.GetCapsule(i, out Vector3 a, out Vector3 b, out float r);
                        AddCapsulePairs(new ArmCapsuleCollision.Pose { start = a, end = b }, r, 0f);
                    }
                }

                HeldShovelCollision shovel = hand.HeldShovel;
                if (shovel == null) continue;
                Bounds shovelBounds = shovel.SweepBounds;
                if (!figurineSweep.Intersects(shovelBounds)) continue;
                foreach (MovingBox box in _movingBoxes)
                {
                    if (!box.IsActiveIn(layers))
                        continue;

                    Vector3 boxCenter = fromPosition + fromRotation * box.center;
                    // Enclose the center's chord plus its rotational arc and the box itself.
                    var sweptBounds = new Bounds(boxCenter, Vector3.zero);
                    sweptBounds.Encapsulate(toPosition + rotation * box.center);
                    sweptBounds.Expand(2f * (box.radius + arcDeviation * (box.center - _supportOffset).magnitude));
                    if (sweptBounds.Intersects(shovelBounds))
                    {
                        var pair = new HandSweepPair { box = box, shovel = shovel };
                        pair.allowedDepth = pair.GetDepth(fromPosition, fromRotation);
                        _handSweepPairs.Add(pair);
                        stepSize = Mathf.Min(stepSize, ShovelSweepStep);
                    }
                }

                void AddCapsulePairs(ArmCapsuleCollision.Pose capsule, float capsuleRadius, float allowedDepth)
                {
                    if (capsuleRadius <= 0f) return;
                    Vector3 center = (capsule.start + capsule.end) * .5f;
                    float extent = Vector3.Distance(capsule.start, capsule.end) * .5f + capsuleRadius + travel;
                    foreach (MovingBox box in _movingBoxes)
                    {
                        float reach = extent + box.radius;
                        if ((fromPosition + fromRotation * box.center - center).sqrMagnitude > reach * reach ||
                            !box.IsActiveIn(layers))
                            continue;
                        var pair = new HandSweepPair { box = box, hand = hand, arm = capsule, radius = capsuleRadius };
                        pair.allowedDepth = Mathf.Max(allowedDepth, pair.GetDepth(fromPosition, fromRotation));
                        _handSweepPairs.Add(pair);
                        stepSize = Mathf.Min(stepSize, capsuleRadius * .35f);
                    }
                }
            }

            if (_handSweepPairs.Count == 0)
                return;

            // Test intermediate poses even when the end pose is clear. Idle turns
            // leave the same clearance as a drag. Hops can make shallow contact.
            Vector3 toSupport = support;
            Quaternion toRotation = rotation;
            float fraction = Mathf.Min(1f, MaxHandSweepSteps * stepSize / travel);
            int steps = Mathf.Clamp(Mathf.CeilToInt(travel * fraction / stepSize), 1, MaxHandSweepSteps);
            float accepted = 0f;
            for (int i = 1; i <= steps; i++)
            {
                float next = fraction * i / steps;
                if (CanReach(next))
                {
                    accepted = next;
                    continue;
                }

                for (int check = 0; check < 6; check++)
                {
                    float middle = (accepted + next) * .5f;
                    if (CanReach(middle)) accepted = middle;
                    else next = middle;
                }
                break;
            }

            if (accepted < 1f)
            {
                support = Vector3.Lerp(fromSupport, toSupport, accepted);
                rotation = accepted > 0f ? Quaternion.Slerp(fromRotation, toRotation, accepted) : fromRotation;
            }

            bool CanReach(float t)
            {
                Quaternion rootRotation = Quaternion.Slerp(fromRotation, toRotation, t);
                Vector3 rootPosition = Vector3.Lerp(fromSupport, toSupport, t) - rootRotation * _supportOffset;
                foreach (HandSweepPair pair in _handSweepPairs)
                {
                    if (pair.GetDepth(rootPosition, rootRotation) > pair.allowedDepth)
                        return false;
                }
                return true;
            }
        }
    }
}
