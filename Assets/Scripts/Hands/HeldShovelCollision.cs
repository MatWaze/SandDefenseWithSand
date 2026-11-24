using System;
using System.Collections.Generic;
using UnityEngine;

namespace SandDefenseWithSand.Hands
{
    // Tests the authored shovel hull at trial poses without moving the held rigidbody.
    internal sealed class HeldShovelCollision
    {
        private const float Tolerance = .00001f;
        private const float SurfaceGap = .0001f;
        // A resting tool can tilt slightly even on level sand. Treat contacts
        // within about six degrees of horizontal as sideways pushes.
        private const float SideContactVerticalLimit = .1f;

        private struct Shape
        {
            public BoxCollider box;
            public Vector3 originOffset;
            public Vector3 centerOffset;
            public Quaternion localRotation;
            public Vector3 halfSize;
            public float sampleDistance;
        }

        private readonly Shape[] _shapes;
        private readonly Transform _owner;
        private readonly Transform _itemRoot;
        private readonly int _layers;
        private readonly int _toolLayer = LayerMask.NameToLayer("Grabbable");
        private readonly int _terrainLayer = LayerMask.NameToLayer("Terrain");
        private readonly Dictionary<Collider, float> _startDepths = new Dictionary<Collider, float>();
        private Collider[] _overlaps = new Collider[8];
        private RaycastHit[] _hits = new RaycastHit[8];
        private Quaternion _captureRotation;
        private float _startingGroundDepth;

        internal HeldShovelCollision(Transform itemRoot, Transform owner, int obstacleLayers)
        {
            _itemRoot = itemRoot;
            _owner = owner;
            _layers = obstacleLayers | LayerMask.GetMask("Grabbable");
            // Both scene shovels have an authored hull on their root. Children include
            // optional sand and preview geometry which are independent of the shovel.
            var boxes = new List<BoxCollider>();
            itemRoot.GetComponents(boxes);
            boxes.RemoveAll(box => !box.enabled || box.isTrigger);
            if (boxes.Count == 0)
            {
                itemRoot.GetComponentsInChildren(false, boxes);
                boxes.RemoveAll(box => !box.enabled || box.isTrigger);
            }
            _shapes = new Shape[boxes.Count];
            for (int i = 0; i < boxes.Count; i++)
                _shapes[i].box = boxes[i];
        }

        internal void Capture(Vector3 pivot, Quaternion rotation)
        {
            _captureRotation = rotation;
            Quaternion inverse = Quaternion.Inverse(rotation);
            for (int i = 0; i < _shapes.Length; i++)
            {
                ref Shape shape = ref _shapes[i];
                if (shape.box == null) continue;
                Transform t = shape.box.transform;
                shape.originOffset = inverse * (t.position - pivot);
                shape.centerOffset = inverse * (t.TransformPoint(shape.box.center) - pivot);
                shape.localRotation = inverse * t.rotation;
                Vector3 size = Vector3.Scale(shape.box.size, t.lossyScale);
                shape.halfSize = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)) * .5f;
                float thinnest = Mathf.Min(shape.halfSize.x, Mathf.Min(shape.halfSize.y, shape.halfSize.z));
                shape.sampleDistance = Mathf.Clamp(thinnest * .5f, Tolerance * 2f, .002f);
            }
        }

        internal float LimitTranslation(Vector3 from, Vector3 to) => LimitTranslation(from, to, _captureRotation);

        private float LimitTranslation(Vector3 from, Vector3 to, Quaternion rotation)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= Tolerance) return 1f;
            Vector3 direction = delta / distance;
            float fraction = 1f;
            foreach (Shape shape in _shapes)
            {
                if (!IsActive(shape)) continue;
                ReadDepths(shape, from, rotation, remember: true);
                Vector3 center = from + rotation * shape.centerOffset;
                int count;
                while (true)
                {
                    count = Physics.BoxCastNonAlloc(center, shape.halfSize, direction, _hits,
                        rotation * shape.localRotation, distance * fraction, _layers, QueryTriggerInteraction.Ignore);
                    if (count < _hits.Length) break;
                    Array.Resize(ref _hits, _hits.Length * 2);
                }
                bool startsInContact = _startDepths.Count > 0;
                for (int i = 0; i < count; i++)
                {
                    RaycastHit hit = _hits[i];
                    if (Ignore(hit.collider)) continue;
                    if (hit.distance > Tolerance && IsPushableTool(hit.collider) &&
                        hit.normal.y <= SideContactVerticalLimit)
                        continue;
                    if (hit.distance <= Tolerance || _startDepths.ContainsKey(hit.collider) ||
                        (_startingGroundDepth > 0f && IsGroundMesh(hit.collider)))
                    {
                        // PhysX returns an artificial opposite-motion normal for a cast
                        // starting in contact. Compare actual penetration along the path.
                        startsInContact = true;
                        continue;
                    }
                    if (Vector3.Dot(direction, hit.normal) < -Tolerance)
                        fraction = Mathf.Min(fraction, Mathf.Max(0f, hit.distance - SurfaceGap) / distance);
                }
                if (fraction <= 0f) return 0f;
                int steps = startsInContact ? Mathf.Max(1, Mathf.CeilToInt(distance * fraction / shape.sampleDistance)) : 1;
                fraction = LimitPath(shape, from, delta, rotation, rotation, false, distance, fraction, steps);
            }
            return fraction;
        }

        internal Quaternion LimitRotation(Vector3 pivot, Quaternion from, Quaternion to)
        {
            float angle = Quaternion.Angle(from, to) * Mathf.Deg2Rad;
            if (angle <= 0f) return to;
            float fraction = 1f;
            foreach (Shape shape in _shapes)
            {
                if (!IsActive(shape)) continue;
                // Enclose the entire rotation, including the curved path of each corner.
                float reach = shape.centerOffset.magnitude + shape.halfSize.magnitude;
                if (!Physics.CheckSphere(pivot, reach, _layers, QueryTriggerInteraction.Ignore)) continue;
                ReadDepths(shape, pivot, from, remember: true);
                // Limit the arc travelled by the most distant blade corner, including
                // rotations about an offset wrist, to a fraction of the blade thickness.
                float travel = reach * angle;
                int steps = Mathf.Max(1, Mathf.CeilToInt(travel * fraction / shape.sampleDistance));
                fraction = LimitPath(shape, pivot, Vector3.zero, from, to, true, travel, fraction, steps);
                if (fraction <= 0f) return from;
            }
            return fraction >= 1f ? to : Quaternion.Slerp(from, to, fraction);
        }

        private float LimitPath(Shape shape, Vector3 start, Vector3 delta, Quaternion from, Quaternion to,
            bool rotating, float travel, float limit, int steps)
        {
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
                    return Mathf.Max(0f, low - SurfaceGap / Mathf.Max(travel, SurfaceGap));
                }
                low = high;
            }
            return limit;

            bool Blocked(float fraction) => ReadDepths(shape, start + delta * fraction,
                rotating ? Quaternion.Slerp(from, to, fraction) : from, remember: false);
        }

        // Measures the contacts at a trial pose relative to those allowed at the start.
        private bool ReadDepths(Shape shape, Vector3 pivot, Quaternion rotation, bool remember,
            float tolerance = Tolerance)
        {
            if (remember)
            {
                _startDepths.Clear();
                _startingGroundDepth = 0f;
            }
            Vector3 center = pivot + rotation * shape.centerOffset;
            Quaternion orientation = rotation * shape.localRotation;
            int count;
            while (true)
            {
                count = Physics.OverlapBoxNonAlloc(center, shape.halfSize, _overlaps, orientation,
                    _layers, QueryTriggerInteraction.Ignore);
                if (count < _overlaps.Length) break;
                Array.Resize(ref _overlaps, _overlaps.Length * 2);
            }
            Vector3 origin = pivot + rotation * shape.originOffset;
            for (int i = 0; i < count; i++)
            {
                Collider other = _overlaps[i];
                if (Ignore(other)) continue;
                bool inside = Physics.ComputePenetration(shape.box, origin, orientation,
                    other, other.transform.position, other.transform.rotation, out Vector3 separation, out float depth);
                if (IsPushableTool(other) && (!inside || separation.y <= SideContactVerticalLimit)) continue;
                // Adjacent terrain chunks can own different triangles of the same sand surface.
                // Compare penetration against that surface even when its collider ID changes.
                bool ground = IsGroundMesh(other) && (!inside || separation.y >= .5f ||
                    depth <= _startingGroundDepth + tolerance);
                if (ground || (!inside && other is MeshCollider mesh && !mesh.convex))
                {
                    float supportDepth = GroundDepth(shape.halfSize, center, orientation, other);
                    ground &= supportDepth >= 0f;
                    if (supportDepth >= 0f) depth = supportDepth;
                    else if (!inside) depth = 2f * Tolerance;
                }
                if (remember)
                {
                    _startDepths[other] = depth;
                    if (ground) _startingGroundDepth = Mathf.Max(_startingGroundDepth, depth);
                }
                else
                {
                    float allowed = ground ? _startingGroundDepth :
                        (_startDepths.TryGetValue(other, out float initial) ? initial : 0f);
                    if (depth > allowed + tolerance) return true;
                }
            }
            return false;
        }

        private float GroundDepth(Vector3 halfSize, Vector3 center, Quaternion rotation, Collider ground)
        {
            // Some heightfield/chunk contacts have no separation vector. Measure
            // the support depth from above so sideways or retreating motion stays free.
            float lift = 2f * halfSize.magnitude + SurfaceGap;
            int count;
            while (true)
            {
                count = Physics.BoxCastNonAlloc(center + Vector3.up * lift, halfSize, Vector3.down, _hits,
                    rotation, lift + SurfaceGap, 1 << ground.gameObject.layer, QueryTriggerInteraction.Ignore);
                if (count < _hits.Length) break;
                Array.Resize(ref _hits, _hits.Length * 2);
            }
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hits[i];
                if (hit.collider == ground && hit.distance > Tolerance && hit.normal.y >= .5f)
                    return Mathf.Max(0f, (lift - hit.distance) * hit.normal.y);
            }
            return -1f;
        }

        private bool IsGroundMesh(Collider collider) => collider is TerrainCollider ||
            (collider.gameObject.layer == _terrainLayer && collider is MeshCollider mesh && !mesh.convex);

        // Side contacts can move a loose tool. A contact that would push it down
        // must stop the held shovel before its kinematic body can drive it into sand.
        private bool IsPushableTool(Collider collider) => collider.gameObject.layer == _toolLayer &&
            collider.attachedRigidbody != null && !collider.attachedRigidbody.isKinematic;

        private bool Ignore(Collider other) => other == null || other.transform.IsChildOf(_owner) ||
            other.transform.IsChildOf(_itemRoot);

        private static bool IsActive(Shape shape) => shape.box != null && shape.box.enabled &&
            shape.box.gameObject.activeInHierarchy;

        internal Bounds SweepBounds
        {
            get
            {
                Bounds bounds = new Bounds(_itemRoot.position, Vector3.zero);
                foreach (Shape shape in _shapes)
                {
                    if (IsActive(shape))
                    {
                        Matrix4x4 box = GetWorldBox(shape.box);
                        Vector3 size = new Vector3(
                            Mathf.Abs(box.m00) + Mathf.Abs(box.m01) + Mathf.Abs(box.m02),
                            Mathf.Abs(box.m10) + Mathf.Abs(box.m11) + Mathf.Abs(box.m12),
                            Mathf.Abs(box.m20) + Mathf.Abs(box.m21) + Mathf.Abs(box.m22));
                        bounds.Encapsulate(new Bounds(box.GetColumn(3), size));
                    }
                }
                return bounds;
            }
        }

        private static Matrix4x4 GetWorldBox(BoxCollider box)
        {
            Transform t = box.transform;
            return Matrix4x4.TRS(t.TransformPoint(box.center), t.rotation, Vector3.Scale(box.size, t.lossyScale));
        }

        internal bool OverlapsPlacement(Vector3 position, Matrix4x4 placement)
        {
            foreach (Shape shape in _shapes)
            {
                if (!IsActive(shape)) continue;
                Matrix4x4 hull = GetWorldBox(shape.box);
                if (BoxesOverlap(hull, placement, position - (Vector3)hull.GetColumn(3)))
                    return true;
            }
            return false;
        }

        private static bool BoxesOverlap(Matrix4x4 hull, Matrix4x4 placement, Vector3 centerDelta)
        {
            // Separating axes are each face normal and each pair of cross-edges.
            // Using the transformed edges also handles a scaled, rotated clay parent.
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                int k = (i + 2) % 3;
                if (Separated(Vector3.Cross(hull.GetColumn(j), hull.GetColumn(k))) ||
                    Separated(Vector3.Cross(placement.GetColumn(j), placement.GetColumn(k))))
                    return false;
                for (int edge = 0; edge < 3; edge++)
                    if (Separated(Vector3.Cross(hull.GetColumn(i), placement.GetColumn(edge))))
                        return false;
            }
            return true;

            bool Separated(Vector3 axis)
            {
                float length = axis.magnitude;
                if (length <= 1e-10f) return false;
                float combinedRadius = 0f;
                for (int i = 0; i < 3; i++)
                    combinedRadius += Mathf.Abs(Vector3.Dot(axis, hull.GetColumn(i))) +
                        Mathf.Abs(Vector3.Dot(axis, placement.GetColumn(i)));
                return Mathf.Abs(Vector3.Dot(axis, centerDelta)) >= combinedRadius * .5f - Tolerance * length;
            }
        }

        internal float GetBoxDepth(BoxCollider box, Vector3 position, Quaternion rotation)
        {
            float deepest = 0f;
            foreach (Shape shape in _shapes)
            {
                if (!IsActive(shape)) continue;
                Transform t = shape.box.transform;
                if (Physics.ComputePenetration(shape.box, t.position, t.rotation,
                        box, position, rotation, out _, out float depth))
                    deepest = Mathf.Max(deepest, depth);
            }
            return deepest;
        }
    }
}
