using System.Numerics;

namespace SomeEngine.Physics.Research;

// Joint locations are graph nodes. Segments inside a rigid body form level zero
// (SCA 2017, sections 3.1 and 3.2). Higher levels span 2, 4, 8 and 16 segments.
internal sealed unsafe class JointDistances
{
    private readonly BodyFrame[] bodies;
    private readonly JointData[] joints;
    private readonly List<int>[] bodyJoints;
    private sealed record Segment(int First, int Last, int BodyA, int BodyB, Vector3 AnchorA, Vector3 AnchorB,
        double Minimum, double Maximum)
    {
        public Segment Reverse() => new(Last, First, BodyB, BodyA, AnchorB, AnchorA, Minimum, Maximum);
    }

    public JointDistances(BodyFrame[] bodies, JointData[] joints)
    {
        this.bodies = bodies; this.joints = joints;
        bodyJoints = Enumerable.Range(0, bodies.Length).Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < joints.Length; i++)
        {
            if (joints[i].BodyA >= 0) bodyJoints[joints[i].BodyA].Add(i);
            bodyJoints[joints[i].BodyB].Add(i);
        }
    }

    public DistanceRange[] Build()
    {
        var result = new Dictionary<(int, int), DistanceRange>();
        List<Segment> segments = BuildSegments();
        BuildAttachments(segments, result);
        for (int level = 0; level < 4 && segments.Count > 0; level++)
        {
            var touching = Enumerable.Range(0, joints.Length).Select(_ => new List<Segment>()).ToArray();
            foreach (Segment segment in segments)
            {
                touching[segment.First].Add(segment);
                touching[segment.Last].Add(segment.Reverse());
            }
            var combined = new Dictionary<(int, int), Segment>();
            for (int node = 0; node < touching.Length; node++)
            {
                List<Segment> adjacent = touching[node];
                for (int a = 0; a < adjacent.Count; a++)
                    for (int b = a + 1; b < adjacent.Count; b++)
                    {
                        Segment left = adjacent[a].Reverse(), right = adjacent[b];
                        if (left.First == right.Last || left.BodyB == right.BodyA) continue;
                        double minimum = Math.Max(0, Math.Max(left.Minimum - right.Maximum, right.Minimum - left.Maximum));
                        double maximum = left.Maximum + right.Maximum;
                        var segment = new Segment(left.First, right.Last, left.BodyA, right.BodyB, left.AnchorA, right.AnchorB, minimum, maximum);
                        var key = SortJointPair(segment.First, segment.Last);
                        if (segment.First > segment.Last) segment = segment.Reverse();
                        if (combined.TryGetValue(key, out Segment? other))
                            segment = segment with { Minimum = Math.Max(other.Minimum, segment.Minimum), Maximum = Math.Min(other.Maximum, segment.Maximum) };
                        combined[key] = segment;
                    }
            }
            segments = combined.Values.ToList();
            foreach (Segment segment in segments)
            {
                (int bodyA, Vector3 anchorA) = ReadOtherSide(segment.First, segment.BodyA);
                (int bodyB, Vector3 anchorB) = ReadOtherSide(segment.Last, segment.BodyB);
                AddRange(result, segment.First, segment.Last, bodyA, bodyB, anchorA, anchorB, segment.Minimum, segment.Maximum);
            }
        }
        return result.Values.ToArray();
    }

    private List<Segment> BuildSegments()
    {
        var segments = new List<Segment>();
        for (int body = 0; body < bodies.Length; body++)
        {
            if (bodies[body].Mass == 0) continue;
            List<int> nodes = bodyJoints[body];
            for (int a = 0; a < nodes.Count; a++)
                for (int b = a + 1; b < nodes.Count; b++)
                {
                    Vector3 anchorA = ReadAnchor(nodes[a], body), anchorB = ReadAnchor(nodes[b], body);
                    double distance = Vector3.Distance(anchorA, anchorB);
                    segments.Add(new(nodes[a], nodes[b], body, body, anchorA, anchorB, distance, distance));
                }
        }
        return segments;
    }

    private void BuildAttachments(List<Segment> segments, Dictionary<(int, int), DistanceRange> result)
    {
        var adjacent = Enumerable.Range(0, joints.Length).Select(_ => new List<Segment>()).ToArray();
        foreach (Segment segment in segments)
        {
            adjacent[segment.First].Add(segment); adjacent[segment.Last].Add(segment.Reverse());
        }
        for (int root = 0; root < joints.Length; root++)
        {
            JointData joint = joints[root];
            int fixedBody = joint.BodyA < 0 || bodies[joint.BodyA].Mass == 0 ? joint.BodyA : joint.BodyB;
            if (fixedBody >= 0 && bodies[fixedBody].Mass > 0) continue;
            Vector3 fixedAnchor = ReadAnchor(root, fixedBody);
            var distances = Enumerable.Repeat(double.PositiveInfinity, joints.Length).ToArray();
            var largest = new double[joints.Length];
            var queue = new PriorityQueue<int, double>();
            distances[root] = 0; queue.Enqueue(root, 0);
            while (queue.TryDequeue(out int current, out double distance))
            {
                if (distance != distances[current]) continue;
                foreach (Segment segment in adjacent[current])
                {
                    double total = distance + segment.Maximum;
                    if (total >= distances[segment.Last]) continue;
                    distances[segment.Last] = total;
                    largest[segment.Last] = Math.Max(largest[current], segment.Maximum);
                    queue.Enqueue(segment.Last, total);
                    (int body, Vector3 anchor) = ReadOtherSide(segment.Last, segment.BodyB);
                    double lower = Math.Max(0, 2 * largest[segment.Last] - total), upper = total;
                    AddRange(result, root, segment.Last, fixedBody, body, fixedAnchor, anchor, lower, upper);
                }
            }
        }
    }

    private void AddRange(Dictionary<(int, int), DistanceRange> result, int first, int last,
        int bodyA, int bodyB, Vector3 anchorA, Vector3 anchorB, double minimum, double maximum)
    {
        if (bodyA == bodyB || (bodyA < 0 || bodies[bodyA].Mass == 0) && (bodyB < 0 || bodies[bodyB].Mass == 0)) return;
        var key = SortJointPair(first, last);
        if (result.TryGetValue(key, out DistanceRange other))
        {
            minimum = Math.Max(minimum, other.Minimum); maximum = Math.Min(maximum, other.Maximum);
        }
        result[key] = BodyGeometry.CreateRange(bodyA, bodyB, anchorA, anchorB, minimum, maximum);
    }

    private Vector3 ReadAnchor(int node, int body)
    {
        JointData joint = joints[node];
        return BodyGeometry.Read(body == joint.BodyA ? joint.PointA : joint.PointB);
    }
    private (int, Vector3) ReadOtherSide(int node, int body)
    {
        JointData joint = joints[node];
        int other = joint.BodyA == body ? joint.BodyB : joint.BodyA;
        return (other, ReadAnchor(node, other));
    }
    private static (int, int) SortJointPair(int first, int last) => first < last ? (first, last) : (last, first);
}
