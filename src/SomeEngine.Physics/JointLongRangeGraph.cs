using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

// JointDistances.cs: rigid-body segments, rooted shortest paths and doubled graph levels.
internal sealed class JointLongRangeGraph
{
    private readonly List<Entity> _nodes = [];
    private readonly List<JointGraphVersion> _currentVersions = [];
    private readonly Dictionary<Entity, JointGraphVersion> _versions = [];
    private readonly Dictionary<Entity, List<JointGraphAnchor>> _bodyAnchors = [];
    private readonly Stack<List<JointGraphAnchor>> _unusedAnchors = [];
    private readonly Dictionary<Entity, List<JointGraphSegment>> _adjacent = [];
    private readonly Stack<List<JointGraphSegment>> _unusedSegments = [];
    private readonly List<JointGraphSegment> _segments = [];
    private readonly Dictionary<(Entity, Entity), JointGraphSegment> _combined = [];
    private readonly Dictionary<(Entity, Entity), JointGraphRange> _ranges = [];
    private readonly Dictionary<Entity, (float Distance, float Largest)> _distances = [];
    private readonly PriorityQueue<Entity, float> _queue = new();

    internal void Refresh(scoped PhysicsQueryRows joints, scoped PhysicsBodies bodies, in PhysicsStepContext context, int levels)
    {
        _nodes.Clear();
        _currentVersions.Clear();
        bool changed = false;
        foreach (QueryRow row in joints.Rows)
        {
            ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
            var version = new JointGraphVersion(row.WriteVersion<JointConstraint>(),
                row.WriteVersion<DirectedRelationEndpoints<JointConstraint>>(),
                bodies.GetRow(endpoints.Source).WriteVersion<BodyMass>(), bodies.GetRow(endpoints.Target).WriteVersion<BodyMass>());
            changed |= !_versions.TryGetValue(row.Entity, out JointGraphVersion previous) || previous != version;
            _nodes.Add(row.Entity);
            _currentVersions.Add(version);
        }
        changed |= _nodes.Count != _versions.Count;
        if (!changed) return;
        _versions.Clear();
        for (int index = 0; index < _nodes.Count; index++) _versions.Add(_nodes[index], _currentVersions[index]);
        BuildSegments(joints, bodies);
        BuildAdjacent();
        _ranges.Clear();
        BuildAttachments(joints, bodies);
        for (int level = 0; level < levels && _segments.Count > 0; level++)
        {
            BuildAdjacent();
            _combined.Clear();
            foreach (List<JointGraphSegment> touching in _adjacent.Values)
                for (int first = 0; first < touching.Count; first++)
                    for (int second = first + 1; second < touching.Count; second++)
                    {
                        JointGraphSegment left = touching[first].Reverse(), right = touching[second];
                        if (left.First.Joint == right.Last.Joint || ReadBody(joints, left.Last) == ReadBody(joints, right.First)) continue;
                        float minimum = MathF.Max(0, MathF.Max(left.Minimum - right.Maximum, right.Minimum - left.Maximum));
                        var segment = new JointGraphSegment(left.First, right.Last, minimum, left.Maximum + right.Maximum);
                        if (EntityOrder.Compare(segment.First.Joint, segment.Last.Joint) > 0) segment = segment.Reverse();
                        var key = (segment.First.Joint, segment.Last.Joint);
                        if (_combined.TryGetValue(key, out JointGraphSegment old))
                            segment = segment with { Minimum = MathF.Max(old.Minimum, segment.Minimum), Maximum = MathF.Min(old.Maximum, segment.Maximum) };
                        _combined[key] = segment;
                    }
            _segments.Clear();
            _segments.AddRange(_combined.Values);
            foreach (JointGraphSegment segment in _segments)
                AddRange(joints, bodies, segment.First.OtherSide(), segment.Last.OtherSide(), segment.Minimum, segment.Maximum);
        }
        foreach (QueryRow row in joints.Rows) row.Buffer<LongRangeJointBound>().Clear();
        foreach (JointGraphRange range in _ranges.Values)
        {
            joints.TryGetRow(range.First.Joint, out QueryRow row);
            row.Buffer<LongRangeJointBound>().Add(new LongRangeJointBound
            {
                OtherJoint = range.Last.Joint, FirstEnd = range.First.End, OtherEnd = range.Last.End,
                Minimum = range.Minimum, Maximum = range.Maximum,
            });
        }
        _ranges.Clear();
    }

    private void BuildSegments(scoped PhysicsQueryRows joints, scoped PhysicsBodies bodies)
    {
        foreach (List<JointGraphAnchor> anchors in _bodyAnchors.Values) { anchors.Clear(); _unusedAnchors.Push(anchors); }
        _bodyAnchors.Clear();
        foreach (Entity node in _nodes)
        {
            if (!IsRigidNode(joints, node)) continue;
            for (int end = 0; end < 2; end++)
            {
                var anchor = new JointGraphAnchor(node, (JointEnd)end);
                Entity body = ReadBody(joints, anchor);
                if (bodies.ReadMass(body).Motion != BodyMotion.Dynamic) continue;
                if (!_bodyAnchors.TryGetValue(body, out List<JointGraphAnchor>? anchors))
                {
                    anchors = _unusedAnchors.TryPop(out List<JointGraphAnchor>? reused) ? reused : [];
                    _bodyAnchors.Add(body, anchors);
                }
                anchors.Add(anchor);
            }
        }
        _segments.Clear();
        foreach (List<JointGraphAnchor> anchors in _bodyAnchors.Values)
            for (int first = 0; first < anchors.Count; first++)
                for (int last = first + 1; last < anchors.Count; last++)
                {
                    float length = Vector3.Distance(ReadLocal(joints, anchors[first]), ReadLocal(joints, anchors[last]));
                    _segments.Add(new(anchors[first], anchors[last], length, length));
                }
    }

    private void BuildAdjacent()
    {
        foreach (List<JointGraphSegment> adjacent in _adjacent.Values) { adjacent.Clear(); _unusedSegments.Push(adjacent); }
        _adjacent.Clear();
        foreach (JointGraphSegment segment in _segments) { AddAdjacent(segment); AddAdjacent(segment.Reverse()); }
    }

    private void AddAdjacent(JointGraphSegment segment)
    {
        if (!_adjacent.TryGetValue(segment.First.Joint, out List<JointGraphSegment>? adjacent))
        {
            adjacent = _unusedSegments.TryPop(out List<JointGraphSegment>? reused) ? reused : [];
            _adjacent.Add(segment.First.Joint, adjacent);
        }
        adjacent.Add(segment);
    }

    private void BuildAttachments(scoped PhysicsQueryRows joints, scoped PhysicsBodies bodies)
    {
        foreach (Entity root in _nodes)
        {
            if (!IsRigidNode(joints, root)) continue;
            var fixedAnchor = new JointGraphAnchor(root, JointEnd.Source);
            if (bodies.ReadMass(ReadBody(joints, fixedAnchor)).Motion == BodyMotion.Dynamic)
                fixedAnchor = fixedAnchor.OtherSide();
            if (bodies.ReadMass(ReadBody(joints, fixedAnchor)).Motion == BodyMotion.Dynamic) continue;
            _distances.Clear();
            _queue.Clear();
            _distances.Add(root, (0, 0));
            _queue.Enqueue(root, 0);
            while (_queue.TryDequeue(out Entity current, out float distance))
            {
                var previous = _distances[current];
                if (distance != previous.Distance || !_adjacent.TryGetValue(current, out List<JointGraphSegment>? adjacent)) continue;
                foreach (JointGraphSegment segment in adjacent)
                {
                    float total = distance + segment.Maximum;
                    if (_distances.TryGetValue(segment.Last.Joint, out var old) && total >= old.Distance) continue;
                    float largest = MathF.Max(previous.Largest, segment.Maximum);
                    _distances[segment.Last.Joint] = (total, largest);
                    _queue.Enqueue(segment.Last.Joint, total);
                    AddRange(joints, bodies, fixedAnchor, segment.Last.OtherSide(), MathF.Max(0, 2 * largest - total), total);
                }
            }
        }
    }

    private void AddRange(scoped PhysicsQueryRows joints, scoped PhysicsBodies bodies,
        JointGraphAnchor first, JointGraphAnchor last, float minimum, float maximum)
    {
        Entity firstBody = ReadBody(joints, first), lastBody = ReadBody(joints, last);
        if (firstBody == lastBody || bodies.ReadMass(firstBody).Motion != BodyMotion.Dynamic && bodies.ReadMass(lastBody).Motion != BodyMotion.Dynamic) return;
        if (EntityOrder.Compare(first.Joint, last.Joint) > 0) (first, last) = (last, first);
        var key = (first.Joint, last.Joint);
        if (_ranges.TryGetValue(key, out JointGraphRange old))
        {
            minimum = MathF.Max(minimum, old.Minimum);
            maximum = MathF.Min(maximum, old.Maximum);
        }
        _ranges[key] = new(first, last, minimum, maximum);
    }

    internal static Entity ReadBody(scoped PhysicsQueryRows joints, JointGraphAnchor anchor)
    {
        joints.TryGetRow(anchor.Joint, out QueryRow row);
        ref readonly var endpoints = ref row.ReadRef<DirectedRelationEndpoints<JointConstraint>>();
        return anchor.End == JointEnd.Source ? endpoints.Source : endpoints.Target;
    }

    private static bool IsRigidNode(scoped PhysicsQueryRows joints, Entity entity)
    {
        joints.TryGetRow(entity, out QueryRow row);
        ref readonly JointConstraint joint = ref row.ReadRef<JointConstraint>();
        return joint.LinearX.Motion == JointAxisMotion.Locked && joint.LinearY.Motion == JointAxisMotion.Locked
            && joint.LinearZ.Motion == JointAxisMotion.Locked && float.IsPositiveInfinity(joint.LinearStiffness.X)
            && float.IsPositiveInfinity(joint.LinearStiffness.Y) && float.IsPositiveInfinity(joint.LinearStiffness.Z);
    }

    internal static Vector3 ReadLocal(scoped PhysicsQueryRows joints, JointGraphAnchor anchor)
    {
        joints.TryGetRow(anchor.Joint, out QueryRow row);
        ref readonly JointConstraint joint = ref row.ReadRef<JointConstraint>();
        return anchor.End == JointEnd.Source ? joint.SourceFrame.Position : joint.TargetFrame.Position;
    }

    private readonly record struct JointGraphVersion(uint Joint, uint Endpoints, uint SourceMass, uint TargetMass);
    private readonly record struct JointGraphRange(JointGraphAnchor First, JointGraphAnchor Last, float Minimum, float Maximum);
    private readonly record struct JointGraphSegment(JointGraphAnchor First, JointGraphAnchor Last, float Minimum, float Maximum)
    {
        internal JointGraphSegment Reverse() => new(Last, First, Minimum, Maximum);
    }
}

internal readonly record struct JointGraphAnchor(Entity Joint, JointEnd End)
{
    internal JointGraphAnchor OtherSide() => new(Joint, End == JointEnd.Source ? JointEnd.Target : JointEnd.Source);
}
