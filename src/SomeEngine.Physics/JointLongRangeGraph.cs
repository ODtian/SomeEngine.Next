using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.Physics;

// Joint bounds follow a nearest-attachment forest and doubled paths through that forest.
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
    private readonly Dictionary<(Entity, Entity), JointGraphRange> _ranges = [];
    private readonly Dictionary<Entity, JointGraphPath> _paths = [];
    private readonly Dictionary<Entity, JointGraphSegment> _jumps = [], _nextJumps = [];
    private readonly PriorityQueue<Entity, float> _queue = new();

    internal bool Refresh(World world, Query members, scoped PhysicsBodies bodies, in PhysicsStepContext context, int levels)
    {
        _nodes.Clear();
        _currentVersions.Clear();
        bool changed = false;
        using var scope = members.OpenRead();
        foreach (QueryChunk chunk in scope)
            foreach (Entity entity in chunk.Entities)
            {
                var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(entity);
                var version = new JointGraphVersion(world.ComponentDataWriteVersion<JointConstraint>(entity),
                    world.ComponentDataWriteVersion<RelationEndpoints<JointConstraint>>(entity),
                    world.ComponentDataWriteVersion<BodyMass>(endpoints.Source), world.ComponentDataWriteVersion<BodyMass>(endpoints.Target));
                changed |= !_versions.TryGetValue(entity, out JointGraphVersion previous) || previous != version;
                _nodes.Add(entity);
                _currentVersions.Add(version);
            }
        changed |= _nodes.Count != _versions.Count;
        if (!changed) return false;
        _versions.Clear();
        for (int index = 0; index < _nodes.Count; index++) _versions.Add(_nodes[index], _currentVersions[index]);
        BuildSegments(world, bodies);
        BuildAdjacent();
        _ranges.Clear();
        BuildPaths(world, bodies);
        _jumps.Clear();
        foreach (var entry in _paths)
        {
            JointGraphPath path = entry.Value;
            bool attached = bodies.ReadMass(ReadBody(world, path.Root)).Motion != BodyMotion.Dynamic;
            if (entry.Key != path.Root.Joint && attached)
                AddRange(world, bodies, path.Arrival.OtherSide(), path.Root, MathF.Max(0, 2 * path.Largest - path.Distance), path.Distance);
            if (!attached && path.Previous is { } previous) _jumps.Add(entry.Key, previous.Reverse());
        }
        for (int level = 0; level < levels && _jumps.Count > 0; level++)
        {
            _nextJumps.Clear();
            foreach (var entry in _jumps)
            {
                JointGraphSegment first = entry.Value;
                AddRange(world, bodies, first.First.OtherSide(), first.Last.OtherSide(), first.Minimum, first.Maximum);
                if (!_jumps.TryGetValue(first.Last.Joint, out JointGraphSegment second)) continue;
                float minimum = MathF.Max(0, MathF.Max(first.Minimum - second.Maximum, second.Minimum - first.Maximum));
                _nextJumps.Add(entry.Key, new(first.First, second.Last, minimum, first.Maximum + second.Maximum));
            }
            _jumps.Clear();
            foreach (var entry in _nextJumps) _jumps.Add(entry.Key, entry.Value);
        }
        foreach (Entity entity in _nodes) { using var bounds = world.GetBuffer<LongRangeJointBound>(entity); bounds.Clear(); }
        foreach (JointGraphRange range in _ranges.Values)
        {
            using var bounds = world.GetBuffer<LongRangeJointBound>(range.First.Joint);
            bounds.Add(new LongRangeJointBound
            {
                OtherJoint = range.Last.Joint,
                FirstEnd = range.First.End,
                OtherEnd = range.Last.End,
                Minimum = range.Minimum,
                Maximum = range.Maximum,
            });
        }
        _ranges.Clear();
        return true;
    }

    private void BuildSegments(World world, scoped PhysicsBodies bodies)
    {
        foreach (List<JointGraphAnchor> anchors in _bodyAnchors.Values) { anchors.Clear(); _unusedAnchors.Push(anchors); }
        _bodyAnchors.Clear();
        foreach (Entity node in _nodes)
        {
            if (!IsRigidNode(world, node)) continue;
            for (int end = 0; end < 2; end++)
            {
                var anchor = new JointGraphAnchor(node, (JointEnd)end);
                Entity body = ReadBody(world, anchor);
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
                    float length = Vector3.Distance(ReadLocal(world, anchors[first]), ReadLocal(world, anchors[last]));
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

    private void BuildPaths(World world, scoped PhysicsBodies bodies)
    {
        _paths.Clear();
        _queue.Clear();
        foreach (Entity root in _nodes)
        {
            if (!IsRigidNode(world, root)) continue;
            var anchor = new JointGraphAnchor(root, JointEnd.Source);
            if (bodies.ReadMass(ReadBody(world, anchor)).Motion == BodyMotion.Dynamic) anchor = anchor.OtherSide();
            if (bodies.ReadMass(ReadBody(world, anchor)).Motion == BodyMotion.Dynamic) continue;
            _paths.Add(root, new(anchor, anchor, null, 0, 0));
            _queue.Enqueue(root, 0);
        }
        GrowPaths();
        foreach (Entity root in _nodes)
        {
            if (_paths.ContainsKey(root) || !IsRigidNode(world, root)) continue;
            var anchor = new JointGraphAnchor(root, JointEnd.Source);
            _paths.Add(root, new(anchor, anchor, null, 0, 0));
            _queue.Enqueue(root, 0);
            GrowPaths();
        }
    }

    private void GrowPaths()
    {
        while (_queue.TryDequeue(out Entity current, out float distance))
        {
            JointGraphPath previous = _paths[current];
            if (distance != previous.Distance || !_adjacent.TryGetValue(current, out List<JointGraphSegment>? adjacent)) continue;
            foreach (JointGraphSegment segment in adjacent)
            {
                float total = distance + segment.Maximum;
                if (_paths.TryGetValue(segment.Last.Joint, out JointGraphPath old) && total >= old.Distance) continue;
                _paths[segment.Last.Joint] = new(previous.Root, segment.Last, segment, total, MathF.Max(previous.Largest, segment.Maximum));
                _queue.Enqueue(segment.Last.Joint, total);
            }
        }
    }

    private void AddRange(World world, scoped PhysicsBodies bodies,
        JointGraphAnchor first, JointGraphAnchor last, float minimum, float maximum)
    {
        Entity firstBody = ReadBody(world, first), lastBody = ReadBody(world, last);
        if (firstBody == lastBody || bodies.ReadMass(firstBody).Motion != BodyMotion.Dynamic && bodies.ReadMass(lastBody).Motion != BodyMotion.Dynamic) return;
        var key = (first.Joint, last.Joint);
        if (_ranges.TryGetValue(key, out JointGraphRange old))
        {
            minimum = MathF.Max(minimum, old.Minimum);
            maximum = MathF.Min(maximum, old.Maximum);
        }
        _ranges[key] = new(first, last, minimum, maximum);
    }

    internal static Entity ReadBody(World world, JointGraphAnchor anchor)
    {
        var endpoints = world.ReadComponentData<RelationEndpoints<JointConstraint>>(anchor.Joint);
        return anchor.End == JointEnd.Source ? endpoints.Source : endpoints.Target;
    }

    private static bool IsRigidNode(World world, Entity entity)
    {
        var joint = world.ReadComponentData<JointConstraint>(entity);
        return joint.LinearX.Motion == JointAxisMotion.Locked && joint.LinearY.Motion == JointAxisMotion.Locked
            && joint.LinearZ.Motion == JointAxisMotion.Locked && float.IsPositiveInfinity(joint.LinearStiffness.X)
            && float.IsPositiveInfinity(joint.LinearStiffness.Y) && float.IsPositiveInfinity(joint.LinearStiffness.Z);
    }

    internal static Vector3 ReadLocal(World world, JointGraphAnchor anchor)
    {
        var joint = world.ReadComponentData<JointConstraint>(anchor.Joint);
        return anchor.End == JointEnd.Source ? joint.SourceFrame.Position : joint.TargetFrame.Position;
    }

    private readonly record struct JointGraphPath(JointGraphAnchor Root, JointGraphAnchor Arrival, JointGraphSegment? Previous, float Distance, float Largest);
    private readonly record struct JointGraphVersion(ulong Joint, ulong Endpoints, ulong SourceMass, ulong TargetMass);
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
