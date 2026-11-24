using System.Collections.Generic;
using UnityEngine;

namespace SandDefenseWithSand.Hands
{
    // Four metacarpals span the palm. Each digit gets a separate capsule on
    // every phalanx, so curling one finger doesn't fill the space beside it.
    internal sealed class HandContactGeometry
    {
        private struct Segment
        {
            public Transform bone;
            public Transform end;
            public Transform wrist;
            public Vector3 fallbackTip;
            public float radius;
            public bool fingertip;
        }

        private struct JointGroup
        {
            public int first;
            public int jointCount;
            public int shapeCount;
        }

        private readonly List<Segment> _segments = new List<Segment>(19);
        private readonly List<JointGroup> _groups = new List<JointGroup>(9);
        private readonly Quaternion[] _accepted, _requested;
        private readonly Vector3[] _oldStarts, _oldEnds, _newStarts, _newEnds;
        private readonly float[] _radii;

        internal int Count => _segments.Count;

        internal HandContactGeometry(Transform wrist)
        {
            if (wrist != null)
            {
                string side = wrist.name.EndsWith(".R") ? ".R" : ".L";
                // Radii and lengths are in the imported arms_rig mesh's rest
                // metres. Each chain is scaled from its actual knuckle spacing.
                AddPalm("01", "f_index", .049f, .030f, new Vector3(.018f, .013f, .011f), .026f);
                AddPalm("02", "f_middle", .055f, .021f, new Vector3(.021f, .017f, .015f), .028f);
                AddPalm("03", "f_ring", .0505f, .0265f, new Vector3(.019f, .015f, .011f), .024f);
                AddPalm("04", "f_pinky", .0433f, .027f, new Vector3(.015f, .0115f, .010f), .022f);

                void AddPalm(string number, string digit, float referenceLength, float palmRadius,
                    Vector3 fingerRadii, float tipLength)
                {
                    Transform palm = wrist.Find("palm." + number + side);
                    if (palm == null) return;
                    Transform first = palm.Find(digit + ".01" + side);
                    Transform second = first != null ? first.Find(digit + ".02" + side) : null;
                    if (second == null) return;
                    float scale = Vector3.Distance(first.position, second.position) / referenceLength;
                    int palmIndex = Count;
                    _segments.Add(new Segment { bone = palm, end = first, wrist = wrist, radius = palmRadius * scale });
                    int digitCount = AddFinger(palm, digit, side, fingerRadii, tipLength, scale);
                    // The thumb and index both inherit palm.01. Moving that palm
                    // must clear both chains; the fingers then curl independently.
                    int thumbCount = number == "01"
                        ? AddFinger(palm, "thumb", side, new Vector3(.029f, .027f, .014f), .037f, scale) : 0;
                    AddGroup(palmIndex, 1, Count - palmIndex);
                    AddGroup(palmIndex + 1, digitCount, digitCount);
                    if (thumbCount != 0)
                        AddGroup(palmIndex + 1 + digitCount, thumbCount, thumbCount);
                }
            }

            _accepted = new Quaternion[Count];
            _requested = new Quaternion[Count];
            _oldStarts = new Vector3[Count];
            _oldEnds = new Vector3[Count];
            _newStarts = new Vector3[Count];
            _newEnds = new Vector3[Count];
            _radii = new float[Count];
            for (int i = 0; i < Count; i++)
                _accepted[i] = _segments[i].bone.localRotation;
        }

        private int AddFinger(Transform palm, string digit, string side, Vector3 radii,
            float tipLength, float scale)
        {
            Transform first = palm.Find(digit + ".01" + side);
            Transform second = first != null ? first.Find(digit + ".02" + side) : null;
            Transform third = second != null ? second.Find(digit + ".03" + side) : null;
            if (third == null) return 0;
            _segments.Add(new Segment { bone = first, end = second, radius = radii.x * scale });
            _segments.Add(new Segment { bone = second, end = third, radius = radii.y * scale });
            // FBX import settings can strip unweighted terminal bones. In that
            // case use the measured distal length along the bone's own axis.
            Vector3 tip = third.TransformDirection(third.localPosition.normalized) * (tipLength * scale);
            _segments.Add(new Segment
            {
                bone = third, end = third.Find(third.name + "_end"), fingertip = true,
                fallbackTip = third.InverseTransformVector(tip), radius = radii.z * scale
            });
            return 3;
        }

        private void AddGroup(int first, int joints, int shapes)
        {
            if (joints > 0)
                _groups.Add(new JointGroup { first = first, jointCount = joints, shapeCount = shapes });
        }

        internal void GetCapsule(int index, out Vector3 a, out Vector3 b, out float radius)
        {
            Segment s = _segments[index];
            a = s.bone.position;
            // The visible heel of the palm extends behind the metacarpal origins.
            if (s.wrist != null) a = Vector3.Lerp(a, s.wrist.position, .45f);
            b = s.end != null ? s.end.position : s.bone.TransformPoint(s.fallbackTip);
            radius = s.radius;
            // The round distal cap covers the fingertip pad just beyond the last joint.
            if (s.fingertip)
                b = Vector3.MoveTowards(b, a, radius * .5f);
        }

        internal void RestoreAnimation()
        {
            for (int i = 0; i < Count; i++)
            {
                Transform bone = _segments[i].bone;
                if (!bone.localRotation.Equals(_accepted[i]))
                    bone.localRotation = _accepted[i];
            }
        }

        internal void LimitAnimation(HandContactCollision collision)
        {
            for (int i = 0; i < Count; i++)
                _requested[i] = _segments[i].bone.localRotation;
            RestoreAnimation();
            foreach (JointGroup group in _groups)
            {
                float angle = 0f;
                for (int j = group.first; j < group.first + group.jointCount; j++)
                    angle += Quaternion.Angle(_accepted[j], _requested[j]);
                if (angle <= 0f) continue;
                // Sweep small arcs even across an abrupt jump between hand clips.
                int steps = Mathf.Max(1, Mathf.CeilToInt(angle / 8f));
                for (int step = 0; step < steps; step++)
                {
                    ReadGroup(group, _oldStarts, _oldEnds);
                    float end = (float)(step + 1) / steps;
                    ApplyGroup(group, end);
                    ReadGroup(group, _newStarts, _newEnds);
                    float allowed = collision.LimitPoseChange(_oldStarts, _oldEnds, _newStarts,
                        _newEnds, _radii, group.first, group.shapeCount);
                    if (allowed >= 1f) continue;

                    float start = (float)step / steps;
                    float clipped = Mathf.Lerp(start, end, allowed);
                    // Hierarchical joint interpolation has a slight arc even
                    // within this step. Check the actual partial joint pose.
                    ApplyGroup(group, clipped);
                    ReadGroup(group, _newStarts, _newEnds);
                    if (allowed > 0f && collision.LimitPoseChange(_oldStarts, _oldEnds, _newStarts,
                            _newEnds, _radii, group.first, group.shapeCount) < 1f)
                        ApplyGroup(group, start);
                    break;
                }
                for (int j = group.first; j < group.first + group.jointCount; j++)
                    _accepted[j] = _segments[j].bone.localRotation;
            }
        }

        private void ApplyGroup(JointGroup group, float fraction)
        {
            for (int i = group.first; i < group.first + group.jointCount; i++)
                _segments[i].bone.localRotation = Quaternion.Slerp(_accepted[i], _requested[i], fraction);
        }

        private void ReadGroup(JointGroup group, Vector3[] starts, Vector3[] ends)
        {
            for (int i = group.first; i < group.first + group.shapeCount; i++)
                GetCapsule(i, out starts[i], out ends[i], out _radii[i]);
        }
    }
}
