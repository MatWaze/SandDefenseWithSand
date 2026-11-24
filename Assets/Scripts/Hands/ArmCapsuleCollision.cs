using System;
using System.Collections.Generic;
using UnityEngine;
using SandDefenseWithSand.Clay;

namespace SandDefenseWithSand.Hands
{
    // Constrains IK target motion by checking the capsule in the resulting IK pose.
    // A blocked drag can still move along its clear input axes. Moving clay uses a separate,
    // bounded recovery path when it enters the actual capsule.
    internal sealed class ArmCapsuleCollision
    {
        internal struct Pose
        {
            public Vector3 start;
            public Vector3 end;

            public bool Overlaps(Bounds bounds, float radius)
            {
                Vector3 segment = end - start;
                float low = 0f, high = 1f;
                // The squared distance from the segment to the box is convex.
                // Use its slope to find the closest point, including the end caps.
                for (int i = 0; i < 12; i++)
                {
                    float middle = (low + high) * 0.5f;
                    Vector3 point = start + segment * middle;
                    if (Vector3.Dot(segment, point - bounds.ClosestPoint(point)) < 0f)
                        low = middle;
                    else
                        high = middle;
                }

                float tolerance = segment.magnitude * (high - low) * 0.5f;
                float clearance = radius + tolerance;
                return bounds.SqrDistance(start + segment * ((low + high) * 0.5f)) <= clearance * clearance;
            }
        }

        private struct Contact
        {
            public Collider collider;
            public Vector3 normal;
            public float depth;
        }

        private const float PositionTolerance = 0.00001f;
        private const float DepthTolerance = 0.00001f;
        private const float GroundSupportMinUp = 0.5f;
        private const int MovementPoseChecks = 64;
        private const int RecoveryPoseChecks = 48;
        private readonly Transform _owner;
        private readonly int _figurineLayer;
        private readonly List<Contact> _contacts = new List<Contact>(16);
        private readonly Dictionary<Collider, float> _penetrationLimits = new Dictionary<Collider, float>();
        private readonly Dictionary<Collider, float> _recoveryDepths = new Dictionary<Collider, float>();
        private readonly HashSet<Collider> _recoveryBoxes = new HashSet<Collider>();
        private Collider _retreatCollider;
        private Vector3 _retreatDirection;
        private readonly List<Collider> _keys = new List<Collider>();
        private Collider[] _overlaps = new Collider[16];
        private RaycastHit[] _groundHits = new RaycastHit[8];
        private CapsuleCollider _probe;
        private float _radius;
        private float _skin;
        private int _layers;
        private readonly Vector3[] _recoveryDirections = new Vector3[26];

        public bool WasConstrained { get; private set; }

        public ArmCapsuleCollision(Transform owner)
        {
            _owner = owner;
            _figurineLayer = LayerMask.GetMask("Sand Figurine");
            int index = 0;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        if (x != 0 || y != 0 || z != 0)
                            _recoveryDirections[index++] = new Vector3(x, y, z).normalized;
        }

        public Vector3 Move(Vector3 current, Vector3 desired, Vector3 inputTarget, float radius, float skin,
            int layers, Func<Vector3, Pose> evaluatePose, Func<Vector3, int, float, float> clampAxisToReach,
            out int clearAxes, Func<Vector3, Vector3, float> limitTranslation = null)
        {
            WasConstrained = false;
            clearAxes = 0;
            float movementBudget = Mathf.Max(Vector3.Distance(current, desired), Vector3.Distance(current, inputTarget));
            if (movementBudget <= PositionTolerance)
                return current;

            ConfigureProbe(radius, skin, layers);
            Physics.SyncTransforms();
            Pose pose = evaluatePose(current);
            // Both endpoints share the travel budget below. Expanding the starting
            // capsule by that budget encloses every pose tested during this move.
            bool nearObstacle = Physics.CheckCapsule(pose.start, pose.end,
                _radius + _skin + movementBudget + PositionTolerance, layers, QueryTriggerInteraction.Ignore);
            if (nearObstacle)
                ReadContacts(pose, useFigurineSurface: true);
            else
                _contacts.Clear();
            // Accept existing depth caused by external motion at the starting pose.
            // A small escape step can then improve it without clearing it all at once.
            foreach (Contact contact in _contacts)
            {
                _penetrationLimits.TryGetValue(contact.collider, out float limit);
                if (contact.depth > limit + DepthTolerance)
                    _penetrationLimits[contact.collider] = contact.depth;
            }
            CommitContacts();

            Vector3 position = current;
            float travelled = 0f;
            int checks = 0;
            WasConstrained = Sweep(desired);
            if (WasConstrained)
            {
                Vector3 combined = desired - current;
                for (int axis = 0; axis < 3; axis++)
                {
                    float input = inputTarget[axis] - current[axis];
                    float remaining = inputTarget[axis] - position[axis];
                    Vector3 otherMotion = combined;
                    otherMotion[axis] = 0f;
                    // A pure move already tested on this axis has reached its stop.
                    // A different requested axis still deserves its own check.
                    if (Mathf.Abs(remaining) <= PositionTolerance || input * remaining <= 0f ||
                        (otherMotion.sqrMagnitude <= PositionTolerance * PositionTolerance &&
                        combined[axis] * input > 0f))
                        continue;

                    // Retry only this part of the player's request. The other
                    // coordinates stay at the stop pose, including wheel depth.
                    Vector3 axisGoal = position;
                    axisGoal[axis] = clampAxisToReach(position, axis, inputTarget[axis]);
                    Vector3 beforeAxis = position;
                    if (!Sweep(axisGoal) && !position.Equals(beforeAxis) &&
                        Mathf.Abs(axisGoal[axis] - inputTarget[axis]) <= PositionTolerance)
                        clearAxes |= 1 << axis;
                }
            }

            evaluatePose(position);
            return position;

            bool Sweep(Vector3 goal)
            {
                // The hand and held tool keep their orientation during each straight
                // segment. The elbow needs the intermediate IK poses below.
                float fractionAllowed = limitTranslation?.Invoke(position, goal) ?? 1f;
                bool handBlocked = fractionAllowed < 1f;
                if (handBlocked)
                    goal = Vector3.Lerp(position, goal, fractionAllowed);
                while ((goal - position).sqrMagnitude > PositionTolerance * PositionTolerance &&
                    travelled + PositionTolerance < movementBudget && checks < MovementPoseChecks)
                {
                    // Bound the travel of both capsule endpoints between samples
                    // and over the frame. The IK elbow can move faster than the hand.
                    float endpointStep = Mathf.Min(_skin * .5f, movementBudget - travelled);
                    Vector3 step = Vector3.ClampMagnitude(goal - position, endpointStep);
                    Pose candidate = default;
                    float endpointTravel = 0f;
                    for (int subdivision = 0; subdivision < 12 && checks < MovementPoseChecks; subdivision++)
                    {
                        candidate = evaluatePose(position + step);
                        checks++;
                        endpointTravel = Mathf.Max(Vector3.Distance(pose.start, candidate.start),
                            Vector3.Distance(pose.end, candidate.end));
                        if (endpointTravel <= endpointStep + PositionTolerance)
                            break;
                        step *= .5f;
                    }

                    if (endpointTravel > endpointStep + PositionTolerance ||
                        step.sqrMagnitude <= PositionTolerance * PositionTolerance)
                        break;

                    bool blocked = nearObstacle && !IsAllowed(candidate);
                    if (blocked)
                    {
                        float low = 0f, high = 1f;
                        for (int search = 0; search < 8 && checks < MovementPoseChecks; search++)
                        {
                            float fraction = (low + high) * .5f;
                            checks++;
                            if (IsAllowed(evaluatePose(position + step * fraction)))
                                low = fraction;
                            else
                                high = fraction;
                        }

                        // Leave a small gap for numerical variation at the surface.
                        float clearance = Mathf.Min(_skin * .02f, DepthTolerance * 10f);
                        low = Mathf.Max(0f, low - clearance / step.magnitude);
                        if (low <= 0f)
                            return true;

                        step *= low;
                        candidate = evaluatePose(position + step);
                        if (!IsAllowed(candidate))
                            return true;
                        endpointTravel = Mathf.Max(Vector3.Distance(pose.start, candidate.start),
                            Vector3.Distance(pose.end, candidate.end));
                    }

                    position += step;
                    travelled += endpointTravel;
                    pose = candidate;
                    if (nearObstacle) CommitContacts();
                    if (blocked)
                        return true;
                }

                return handBlocked && (goal - position).sqrMagnitude <= PositionTolerance * PositionTolerance;
            }
        }

        public Vector3 RecoverFromFigurine(Vector3 current, Pose observedPose, float radius, float skin, int layers,
            float maxTravel, Action preparePose, Func<Vector3, Pose> evaluatePose, Func<Vector3, Vector3> limitToReach,
            Func<Vector3, Vector3, float> limitTranslation = null)
        {
            if (maxTravel <= PositionTolerance || layers == 0)
                return current;

            ConfigureProbe(radius, skin, layers);
            int count = ReadOverlaps(observedPose, _radius, _layers);
            bool insideFigurine = false;
            for (int i = 0; i < count && !insideFigurine; i++)
            {
                Collider obstacle = _overlaps[i];
                insideFigurine = IsReanimatedBox(obstacle) &&
                    CapsuleDepth(observedPose, _radius, obstacle, obstacle.transform.position, obstacle.transform.rotation, out _) > DepthTolerance;
            }
            if (!insideFigurine)
                return current;

            preparePose();
            Pose start = evaluatePose(current);
            ReadContacts(start);
            _recoveryDepths.Clear();
            _recoveryBoxes.Clear();
            Vector3 combinedNormal = Vector3.zero;
            Contact deepest = default;
            float initialDepth = 0f;
            foreach (Contact contact in _contacts)
            {
                _recoveryDepths[contact.collider] = contact.depth;
                if (!IsReanimatedBox(contact.collider))
                    continue;

                _recoveryBoxes.Add(contact.collider);
                initialDepth += contact.depth;
                combinedNormal += contact.normal * contact.depth;
                if (contact.depth > deepest.depth)
                    deepest = contact;
            }

            // Retreat once clay has entered the actual capsule.
            if (deepest.depth <= _skin + DepthTolerance)
                return current;

            int checks = 0;
            Vector3 retreat = current;
            bool keptDirection = deepest.collider == _retreatCollider &&
                Vector3.Dot(_retreatDirection, combinedNormal) > 0f &&
                TryDirection(_retreatDirection, initialDepth, out retreat, out _);
            if (!keptDirection && !TryDirection(combinedNormal, initialDepth, out retreat, out _))
            {
                float bestDepth = initialDepth;
                foreach (Vector3 direction in _recoveryDirections)
                {
                    if (checks >= RecoveryPoseChecks)
                        break;
                    if (!TryDirection(direction, bestDepth, out Vector3 candidate, out float depth))
                        continue;

                    retreat = candidate;
                    bestDepth = depth;
                }
            }

            if (checks > 0)
                evaluatePose(retreat);
            if (retreat != current)
            {
                _retreatCollider = deepest.collider;
                _retreatDirection = (retreat - current).normalized;
                _penetrationLimits.Clear();
            }
            return retreat;

            bool TryDirection(Vector3 direction, float bestDepth, out Vector3 result, out float resultDepth)
            {
                result = current;
                resultDepth = bestDepth;
                if (direction.sqrMagnitude <= 1e-10f)
                    return false;

                Vector3 step = direction.normalized * maxTravel;
                for (int attempt = 0; attempt < 3 && checks + 2 <= RecoveryPoseChecks; attempt++, step *= .5f)
                {
                    Vector3 candidate = limitToReach(current + step);
                    if ((candidate - current).sqrMagnitude <= PositionTolerance * PositionTolerance ||
                        (limitTranslation != null && limitTranslation(current, candidate) < 1f))
                        continue;

                    Pose end = evaluatePose(candidate);
                    checks++;
                    if (Mathf.Max(Vector3.Distance(start.start, end.start), Vector3.Distance(start.end, end.end)) > maxTravel + PositionTolerance ||
                        !TryMeasureRecoveryDepth(end, out float depth) || depth >= bestDepth - DepthTolerance)
                        continue;

                    Pose middle = evaluatePose((current + candidate) * .5f);
                    checks++;
                    if (!TryMeasureRecoveryDepth(middle, out float middleDepth) || middleDepth > initialDepth + DepthTolerance)
                        continue;

                    result = candidate;
                    resultDepth = depth;
                    return true;
                }
                return false;
            }
        }

        private bool IsReanimatedBox(Collider collider)
        {
            if (!(collider is BoxCollider) || collider.transform.IsChildOf(_owner))
                return false;

            ClayxelsHopper hopper = collider.GetComponentInParent<ClayxelsHopper>();
            return hopper != null && hopper.HasReanimatedBoxes;
        }

        private bool TryMeasureRecoveryDepth(Pose pose, out float depth)
        {
            ReadContacts(pose);
            depth = 0f;
            foreach (Contact contact in _contacts)
            {
                if (_recoveryBoxes.Contains(contact.collider))
                {
                    depth += contact.depth;
                    continue;
                }

                _recoveryDepths.TryGetValue(contact.collider, out float allowedDepth);
                if (contact.depth > allowedDepth + DepthTolerance)
                    return false;
            }
            return true;
        }

        private bool IsAllowed(Pose pose)
        {
            ReadContacts(pose, useFigurineSurface: true);
            foreach (Contact contact in _contacts)
            {
                _penetrationLimits.TryGetValue(contact.collider, out float limit);
                if (contact.depth > limit + DepthTolerance)
                    return false;
            }
            return true;
        }

        internal float GetDepth(Pose pose, float radius, Collider obstacle)
        {
            EnsureProbe();
            return CapsuleDepth(pose, radius, obstacle, obstacle.transform.position, obstacle.transform.rotation, out _);
        }

        public float GetBoxDepth(Pose pose, float radius, BoxCollider box, Vector3 position, Quaternion rotation)
        {
            EnsureProbe();
            return CapsuleDepth(pose, radius, box, position, rotation, out _);
        }

        private float CapsuleDepth(Pose pose, float radius, Collider obstacle, Vector3 obstaclePosition,
            Quaternion obstacleRotation, out Vector3 normal)
        {
            Vector3 axis = pose.end - pose.start;
            Vector3 center = (pose.start + pose.end) * .5f;
            Quaternion rotation = axis.sqrMagnitude > 1e-10f
                ? Quaternion.FromToRotation(Vector3.up, axis.normalized) : Quaternion.identity;
            _probe.radius = radius;
            _probe.height = axis.magnitude + 2f * radius;
            _probe.enabled = true;
            try
            {
                return Physics.ComputePenetration(_probe, center, rotation,
                    obstacle, obstaclePosition, obstacleRotation, out normal, out float depth)
                    ? depth : 0f;
            }
            finally { _probe.enabled = false; }
        }

        private void CommitContacts()
        {
            _keys.Clear();
            _keys.AddRange(_penetrationLimits.Keys);
            foreach (Collider key in _keys)
            {
                if (key == null) { _penetrationLimits.Remove(key); continue; }
                float depth = 0f;
                bool found = false;
                foreach (Contact contact in _contacts)
                    if (contact.collider == key) { depth = contact.depth; found = true; break; }
                if (!found) { _penetrationLimits.Remove(key); continue; }
                _penetrationLimits[key] = Mathf.Min(_penetrationLimits[key], depth);
            }
            foreach (Contact contact in _contacts)
                if (!_penetrationLimits.ContainsKey(contact.collider))
                    _penetrationLimits[contact.collider] = 0f;
        }

        private void ReadContacts(Pose pose, bool useFigurineSurface = false)
        {
            _contacts.Clear();
            // Terrain keeps its clearance. Figurine faces are tested against the
            // actual capsule so the extra gap cannot catch a clear sideways move.
            int figurineLayers = useFigurineSurface ? _layers & _figurineLayer : 0;
            AddContacts(pose, _radius + _skin, _layers & ~figurineLayers);
            if (figurineLayers != 0)
                AddContacts(pose, _radius, figurineLayers);
        }

        private void AddContacts(Pose pose, float radius, int layers)
        {
            if (layers == 0) return;
            int count = ReadOverlaps(pose, radius, layers);
            if (count == 0) return;

            Vector3 axis = pose.end - pose.start;
            Vector3 center = (pose.start + pose.end) * 0.5f;
            Quaternion rotation = axis.sqrMagnitude > 1e-10f
                ? Quaternion.FromToRotation(Vector3.up, axis.normalized) : Quaternion.identity;
            _probe.radius = radius;
            _probe.height = axis.magnitude + 2f * radius;
            _probe.enabled = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Collider obstacle = _overlaps[i];
                    if (obstacle == null || obstacle == _probe || obstacle.transform.IsChildOf(_owner))
                        continue;

                    Vector3 normal = Vector3.zero;
                    float depth = 0f;
                    bool overlaps = obstacle is TerrainCollider && TryGroundContact(pose, radius, obstacle, out normal, out depth);
                    if (!overlaps)
                        overlaps = Physics.ComputePenetration(_probe, center, rotation,
                            obstacle, obstacle.transform.position, obstacle.transform.rotation, out normal, out depth);
                    if (!overlaps && (!IsTerrainMesh(obstacle) ||
                        !TryGroundContact(pose, radius, obstacle, out normal, out depth)))
                    {
                        // Mesh backfaces may not supply a separation vector. Treat
                        // a reported overlap conservatively rather than moving through it.
                        depth = obstacle is MeshCollider mesh && !mesh.convex ? radius : 0f;
                        normal = depth > 0f
                            ? (center - obstacle.bounds.ClosestPoint(center)).normalized
                            : Vector3.zero;
                    }
                    if (depth > 0f)
                        _contacts.Add(new Contact { collider = obstacle, normal = normal, depth = depth });
                }
            }
            finally { _probe.enabled = false; }
        }

        private int ReadOverlaps(Pose pose, float radius, int layers)
        {
            while (true)
            {
                int count = Physics.OverlapCapsuleNonAlloc(pose.start, pose.end, radius,
                    _overlaps, layers, QueryTriggerInteraction.Ignore);
                if (count < _overlaps.Length)
                    return count;

                Array.Resize(ref _overlaps, _overlaps.Length * 2);
            }
        }

        private static bool IsTerrainMesh(Collider collider)
        {
            return collider is MeshCollider mesh && !mesh.convex && mesh.GetComponentInParent<TerrainCollider>() != null;
        }

        private bool TryGroundContact(Pose pose, float radius, Collider obstacle, out Vector3 normal, out float depth)
        {
            // Terrain chunks can report a near-surface overlap without a separation
            // vector. Sweep down from just above the capsule to recover the support
            // distance and avoid treating every such overlap as a full-radius block.
            float lift = _skin * 2f;
            Vector3 offset = Vector3.up * lift;
            int count;
            while (true)
            {
                count = Physics.CapsuleCastNonAlloc(pose.start + offset, pose.end + offset,
                    radius, Vector3.down, _groundHits, lift + DepthTolerance,
                    1 << obstacle.gameObject.layer, QueryTriggerInteraction.Ignore);
                if (count < _groundHits.Length) break;
                Array.Resize(ref _groundHits, _groundHits.Length * 2);
            }

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _groundHits[i];
                if (hit.collider != obstacle || hit.distance <= DepthTolerance) continue;

                normal = hit.normal;
                // The sweep measures clearance, while the terrain triangle gives
                // the surface normal even when the capsule hits a triangle edge.
                if (obstacle is TerrainCollider terrain && terrain.Raycast(
                    new Ray(hit.point + offset, Vector3.down), out RaycastHit surface, lift * 2f))
                    normal = surface.normal;
                if (normal.y < GroundSupportMinUp) continue;

                depth = Mathf.Max(0f, (lift - hit.distance) * normal.y);
                return true;
            }

            normal = Vector3.zero;
            depth = 0f;
            return false;
        }

        private void ConfigureProbe(float radius, float skin, int layers)
        {
            EnsureProbe();
            radius = Mathf.Max(0.001f, radius);
            skin = Mathf.Max(0.001f, skin);
            if (Mathf.Abs(_radius - radius) > DepthTolerance || Mathf.Abs(_skin - skin) > DepthTolerance || _layers != layers)
                _penetrationLimits.Clear();
            _radius = radius;
            _skin = skin;
            _layers = layers;
        }

        private void EnsureProbe()
        {
            if (_probe != null) return;
            GameObject probe = new GameObject($"{_owner.name} Arm Collision Probe");
            probe.hideFlags = HideFlags.HideAndDontSave;
            _probe = probe.AddComponent<CapsuleCollider>();
            _probe.direction = 1;
            _probe.isTrigger = true;
            _probe.enabled = false;
        }

        public void Dispose()
        {
            if (_probe == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(_probe.gameObject);
            else UnityEngine.Object.DestroyImmediate(_probe.gameObject);
        }
    }
}
