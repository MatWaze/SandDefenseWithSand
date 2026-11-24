using System;
using System.Collections.Generic;
using UnityEngine;

namespace SandDefenseWithSand.Hands
{
    // Tests the animated palm and finger capsules without adding solid colliders to the rig.
    internal sealed class HandContactCollision
    {
        private const float DepthTolerance = .00001f;
        private const float SurfaceGap = .0001f;
        private readonly HandContactGeometry _geometry;
        private readonly Transform _owner;
        private readonly int _layers;
        private readonly ArmCapsuleCollision _probe;
        private readonly Vector3[] _starts, _ends;
        private readonly float[] _radii;
        private readonly Dictionary<Collider, float> _allowedDepths = new Dictionary<Collider, float>();
        private Collider[] _overlaps = new Collider[8];
        private RaycastHit[] _hits = new RaycastHit[8];
        private Bounds _offsetBounds;
        private Quaternion _captureRotation;
        private float _reach;

        internal HandContactCollision(HandContactGeometry geometry, Transform owner, int layers, ArmCapsuleCollision probe)
        {
            _geometry = geometry;
            _owner = owner;
            _layers = layers;
            _probe = probe;
            _starts = new Vector3[geometry.Count];
            _ends = new Vector3[geometry.Count];
            _radii = new float[geometry.Count];
        }

        internal void Capture(Vector3 pivot, Quaternion rotation)
        {
            _captureRotation = rotation;
            Quaternion inverse = Quaternion.Inverse(rotation);
            _offsetBounds = new Bounds(Vector3.zero, Vector3.zero);
            _reach = 0f;
            for (int i = 0; i < _radii.Length; i++)
            {
                _geometry.GetCapsule(i, out Vector3 a, out Vector3 b, out float radius);
                a -= pivot;
                b -= pivot;
                _starts[i] = inverse * a;
                _ends[i] = inverse * b;
                _radii[i] = radius;
                _offsetBounds.Encapsulate(CapsuleBounds(a, b, radius));
                _reach = Mathf.Max(_reach, Mathf.Max(a.magnitude, b.magnitude) + radius);
            }
        }

        internal float LimitTranslation(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= 0f || _layers == 0 || _radii.Length == 0) return 1f;

            Bounds bounds = _offsetBounds;
            bounds.center += from;
            bounds.Encapsulate(bounds.min + delta);
            bounds.Encapsulate(_offsetBounds.max + to);
            if (!HasObstacle(bounds)) return 1f;

            float fraction = 1f;
            Vector3 direction = delta / distance;
            for (int i = 0; i < _radii.Length && fraction > 0f; i++)
            {
                float radius = _radii[i];
                if (radius <= 0f) continue;
                Vector3 a = from + _captureRotation * _starts[i];
                Vector3 b = from + _captureRotation * _ends[i];
                ReadContacts(a, b, radius, remember: true);
                int count = Cast(a, b, radius, direction, distance * fraction, _layers);
                bool atSurface = _allowedDepths.Count > 0;
                for (int hitIndex = 0; hitIndex < count; hitIndex++)
                {
                    RaycastHit hit = _hits[hitIndex];
                    if (Ignore(hit.collider)) continue;
                    if (hit.distance <= DepthTolerance)
                    {
                        // A sweep starting in contact can report a synthetic hit normal
                        // opposite the cast even for sideways motion. Compare actual depths.
                        atSurface = true;
                    }
                    else if (Vector3.Dot(direction, hit.normal) < -DepthTolerance)
                    {
                        fraction = Mathf.Min(fraction, Mathf.Max(0f, hit.distance - SurfaceGap) / distance);
                    }
                }
                if (atSurface && fraction > 0f)
                    fraction = LimitPath(a, b, a + delta, b + delta, radius, distance, fraction);
            }
            return fraction;
        }

        internal Quaternion LimitRotation(Vector3 pivot, Quaternion from, Quaternion to)
        {
            Quaternion turn = Quaternion.Inverse(from) * to;
            float angle = 2f * Mathf.Atan2(new Vector3(turn.x, turn.y, turn.z).magnitude, Mathf.Abs(turn.w));
            if (angle == 0f || _radii.Length == 0 || _layers == 0 ||
                !Physics.CheckSphere(pivot, _reach, _layers, QueryTriggerInteraction.Ignore))
                return to;

            float fraction = 1f;
            for (int i = 0; i < _radii.Length && fraction > 0f; i++)
            {
                float radius = _radii[i];
                if (radius <= 0f) continue;
                Vector3 a = _starts[i], b = _ends[i];
                float length = Mathf.Max(a.magnitude, b.magnitude);
                Vector3 startA = pivot + from * a, startB = pivot + from * b;
                Bounds swept = CapsuleBounds(startA, startB, radius);
                swept.Encapsulate(CapsuleBounds(pivot + to * a, pivot + to * b, radius));
                // Include the maximum distance between each endpoint's arc and chord.
                swept.Expand(2f * length * (1f - Mathf.Cos(angle * .5f)));
                if (!HasObstacle(swept)) continue;
                ReadContacts(startA, startB, radius, remember: true);
                fraction = LimitPath(a, b, a, b, radius, angle * length, fraction, true, pivot, from, to);
            }
            return fraction >= 1f ? to : Quaternion.Slerp(from, to, fraction);
        }

        internal float LimitPoseChange(Vector3[] oldStarts, Vector3[] oldEnds, Vector3[] newStarts,
            Vector3[] newEnds, float[] radii, int startIndex, int count)
        {
            if (count == 0 || _layers == 0) return 1f;
            float fraction = 1f;
            for (int i = startIndex; i < startIndex + count && fraction > 0f; i++)
            {
                float radius = radii[i];
                if (radius <= 0f) continue;
                float travel = Mathf.Max(Vector3.Distance(oldStarts[i], newStarts[i]),
                    Vector3.Distance(oldEnds[i], newEnds[i]));
                if (travel <= 0f) continue;
                Bounds swept = CapsuleBounds(oldStarts[i], oldEnds[i], radius);
                swept.Encapsulate(CapsuleBounds(newStarts[i], newEnds[i], radius));
                if (!HasObstacle(swept)) continue;
                ReadContacts(oldStarts[i], oldEnds[i], radius, remember: true);
                fraction = LimitPath(oldStarts[i], oldEnds[i], newStarts[i], newEnds[i], radius, travel, fraction);
            }
            return fraction;
        }

        // Sample rotations and changing finger shapes at intervals smaller than their
        // radius, then refine first contact. Pure translations ordinarily use a single cast.
        private float LimitPath(Vector3 a, Vector3 b, Vector3 nextA, Vector3 nextB, float radius, float travel,
            float limit, bool rotating = false, Vector3 pivot = default, Quaternion from = default, Quaternion to = default)
        {
            if (travel <= 0f) return limit;
            int steps = Mathf.Max(1, Mathf.CeilToInt(travel * limit / Mathf.Max(radius * .4f, DepthTolerance)));
            float low = 0f;
            for (int step = 1; step <= steps; step++)
            {
                float high = limit * step / steps;
                if (Blocked(high))
                {
                    for (int j = 0; j < 8; j++)
                    {
                        float mid = (low + high) * .5f;
                        if (Blocked(mid)) high = mid;
                        else low = mid;
                    }
                    return Mathf.Max(0f, low - SurfaceGap / travel);
                }
                low = high;
            }
            return limit;

            bool Blocked(float t)
            {
                if (rotating)
                {
                    Quaternion rotation = Quaternion.Slerp(from, to, t);
                    return ReadContacts(pivot + rotation * a, pivot + rotation * b, radius, remember: false);
                }
                return ReadContacts(Vector3.Lerp(a, nextA, t), Vector3.Lerp(b, nextB, t), radius, remember: false);
            }
        }

        private static Bounds CapsuleBounds(Vector3 a, Vector3 b, float radius)
        {
            Bounds bounds = new Bounds(a, Vector3.zero);
            bounds.Encapsulate(b);
            bounds.Expand(2f * radius);
            return bounds;
        }

        private bool HasObstacle(Bounds bounds) => Physics.CheckBox(bounds.center, bounds.extents,
            Quaternion.identity, _layers, QueryTriggerInteraction.Ignore);

        private bool Ignore(Collider collider) => collider == null || collider.transform.IsChildOf(_owner);

        private int Cast(Vector3 a, Vector3 b, float radius, Vector3 direction, float distance, int layers)
        {
            while (true)
            {
                int count = Physics.CapsuleCastNonAlloc(a, b, radius, direction, _hits, distance,
                    layers, QueryTriggerInteraction.Ignore);
                if (count < _hits.Length) return count;
                Array.Resize(ref _hits, _hits.Length * 2);
            }
        }

        private bool ReadContacts(Vector3 a, Vector3 b, float radius, bool remember)
        {
            if (remember) _allowedDepths.Clear();
            int count;
            while (true)
            {
                count = Physics.OverlapCapsuleNonAlloc(a, b, radius, _overlaps, _layers, QueryTriggerInteraction.Ignore);
                if (count < _overlaps.Length) break;
                Array.Resize(ref _overlaps, _overlaps.Length * 2);
            }
            var pose = new ArmCapsuleCollision.Pose { start = a, end = b };
            for (int i = 0; i < count; i++)
            {
                Collider obstacle = _overlaps[i];
                if (Ignore(obstacle)) continue;
                float depth = _probe.GetDepth(pose, radius, obstacle);
                if (depth <= 0f && (obstacle is TerrainCollider || (obstacle is MeshCollider mesh && !mesh.convex)))
                    depth = GroundDepth(a, b, radius, obstacle);
                if (remember)
                    _allowedDepths[obstacle] = depth;
                else if (depth > (_allowedDepths.TryGetValue(obstacle, out float allowed) ? allowed : 0f) + DepthTolerance)
                    return true;
            }
            return false;
        }

        private float GroundDepth(Vector3 a, Vector3 b, float radius, Collider obstacle)
        {
            // A heightfield or triangle mesh sometimes reports overlap without an MTD.
            // Measure the support depth so an already touching digit can still retreat.
            float lift = Mathf.Abs(a.y - b.y) + 2f * radius + SurfaceGap;
            Vector3 offset = Vector3.up * lift;
            int count = Cast(a + offset, b + offset, radius, Vector3.down, lift + SurfaceGap,
                1 << obstacle.gameObject.layer);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hits[i];
                if (hit.collider == obstacle && hit.distance > DepthTolerance && hit.normal.y > .5f)
                    return Mathf.Max(0f, (lift - hit.distance) * hit.normal.y);
            }
            return 2f * DepthTolerance;
        }
    }
}
