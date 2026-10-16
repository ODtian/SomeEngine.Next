using System.Diagnostics;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Animation;

internal static class GraphCompiler
{
    public static void Compile(GraphData[] data)
    {
        CheckDefinitions(data[0]);
        AddPart(ref data[0], 0);
        Place(data, 0, new GraphPosition(), 0);
        SplitCapacity(ref data[0]);
        CompileRanges(ref data[0]);
        CompileLeaves(ref data[0]);
        CompileConditions(ref data[0]);
    }

    private static GraphPosition Place(GraphData[] graph, int index, GraphPosition start, int part, int motion = 0, int group = 0)
    {
        ref GraphData data = ref graph[0];
        GraphNode node = data.Nodes![index];
        bool openParallel = node.Kind is GraphNodeKind.Parallel or GraphNodeKind.Stagger &&
            node.FirstChild != 0 && data.Nodes[node.FirstChild].Next != 0 && ContainsOpenStage(data.Nodes, index);
        if (node.Kind is GraphNodeKind.StateMachine or GraphNodeKind.Blend1D || openParallel)
        {
            GraphArrays.Grow(ref data.Groups, data.GroupCount + 1);
            int machine = data.GroupCount++;
            data.Groups![machine] = new GraphGroupData
            {
                Node = index, Part = part, Stage = start.Stage, Start = start.Time, ParentMotion = motion,
                OutputGroup = group == 0 ? machine + 1 : group,
            };
            bool hasState = false;
            int childNumber = 0;
            for (int child = node.FirstChild; child != 0; child = data.Nodes[child].Next)
            {
                if (!openParallel && data.Nodes[child].Kind is not (GraphNodeKind.State or GraphNodeKind.BlendPoint))
                    continue;
                hasState = true;
                GraphArrays.Grow(ref data.Motions, data.MotionCount + 1);
                int state = data.MotionCount++;
                data.Motions![state] = new GraphMotionData { Group = machine, Node = child };
                if (openParallel)
                {
                    var childStart = new GraphPosition { Time = node.Kind == GraphNodeKind.Stagger ? childNumber * node.StaggerInterval : 0 };
                    Place(graph, child, childStart, part, state + 1, group == 0 ? -(state + 1) : group);
                }
                else
                    PlaceChildren(graph, in data.Nodes[child], new GraphPosition(), part, state + 1, data.Groups[machine].OutputGroup);
                childNumber++;
            }
            DevelopmentCheck.Require(hasState, "A graph group requires samples.");
            AddStage(graph, part, motion, start.Stage, openParallel ? StageEnd.Settled : StageEnd.External, start.Time);
            return new GraphPosition { Stage = (ushort)(start.Stage + 1) };
        }
        if (node.Kind == GraphNodeKind.Branch)
        {
            int branch = AddPart(ref data, index);
            GraphPosition end = PlaceChildren(graph, in node, start, branch);
            AddStage(ref data.Parts![part], end.Stage, StageEnd.Duration, end.Time);
            return end;
        }
        if (node.Kind == GraphNodeKind.Boundary)
        {
            AddStage(graph, part, motion, start.Stage, node.End, start.Time);
            return new GraphPosition { Stage = (ushort)(start.Stage + 1) };
        }
        if (node.Kind == GraphNodeKind.Track)
        {
            GraphArrays.Grow(ref data.Tracks, data.TrackCount + 1);
            data.Tracks![data.TrackCount++] = new GraphTrack
            {
                Order = data.TrackCount - 1,
                Node = node, Stage = motion == 0 ? start.Stage : data.Groups![data.Motions![motion - 1].Group].Stage,
                Start = start.Time, Part = part, TimelinePart = part, Motion = motion, Group = group, MotionStage = start.Stage,
            };
            data.Parts![part].TrackCount++;
            MotionBoundary boundary = node.Leaf is not null ? node.Boundary :
                new MotionBoundary(node.Spec.Curve.Kind == CurveKind.Spring ? StageEnd.Settled : StageEnd.Duration, node.Spec.Duration);
            if (boundary.End != StageEnd.Duration)
            {
                AddStage(graph, part, motion, start.Stage, boundary.End, start.Time + boundary.Duration);
                return new GraphPosition { Stage = (ushort)(start.Stage + 1) };
            }
            start.Time += boundary.Duration;
            AddStage(graph, part, motion, start.Stage, StageEnd.Duration, start.Time);
            return start;
        }
        if (node.Kind == GraphNodeKind.Delay)
        {
            start.Time += node.DelayDuration;
            AddStage(graph, part, motion, start.Stage, StageEnd.Duration, start.Time);
            return start;
        }
        if (node.Kind == GraphNodeKind.Repeat)
        {
            GraphPosition end = start;
            for (int repeat = 0; repeat < node.Repetitions; repeat++)
                end = PlaceChildren(graph, in node, end, part, motion, group);
            return end;
        }
        return PlaceChildren(graph, in node, start, part, motion, group);
    }

    private static GraphPosition PlaceChildren(GraphData[] graph, in GraphNode node, GraphPosition start, int part, int motion = 0, int group = 0)
    {
        GraphPosition end = start;
        int childNumber = 0;
        for (int child = node.FirstChild; child != 0; child = graph[0].Nodes![child].Next)
        {
            GraphPosition childStart = node.Kind == GraphNodeKind.Sequence ? end : start;
            if (node.Kind == GraphNodeKind.Stagger)
                childStart.Time += childNumber * node.StaggerInterval;
            GraphPosition childEnd = Place(graph, child, childStart, part, motion, group);
            if (childEnd.Stage > end.Stage || childEnd.Stage == end.Stage && childEnd.Time > end.Time)
                end = childEnd;
            childNumber++;
        }
        return end;
    }

    private static int AddPart(ref GraphData data, int id)
    {
        GraphArrays.Grow(ref data.Parts, data.PartCount + 1);
        int part = data.PartCount++;
        data.Parts![part] = new GraphPart { Id = id, TimelinePart = part };
        return part;
    }

    private static void AddStage(ref GraphPart part, ushort stage, StageEnd end, float duration)
    {
        GraphArrays.Grow(ref part.Stages, stage + 1);
        while (part.StageCount <= stage)
            part.Stages![part.StageCount++] = default;
        ref TimelineStage entry = ref part.Stages![stage];
        if (entry.End is not (StageEnd.Settled or StageEnd.External))
            entry.End = end;
        entry.Duration = Math.Max(entry.Duration, duration);
    }

    private static void AddStage(GraphData[] graph, int part, int motion, ushort stage, StageEnd end, float duration)
    {
        if (motion == 0)
        {
            AddStage(ref graph[0].Parts![part], stage, end, duration);
            return;
        }
        ref GraphMotionData data = ref graph[0].Motions![motion - 1];
        GraphArrays.Grow(ref data.Stages, stage + 1);
        while (data.StageCount <= stage)
            data.Stages![data.StageCount++] = default;
        ref TimelineStage entry = ref data.Stages![stage];
        if (entry.End is not (StageEnd.Settled or StageEnd.External))
            entry.End = end;
        entry.Duration = Math.Max(entry.Duration, duration);
    }

    private static void SplitCapacity(ref GraphData data)
    {
        int originalCount = data.PartCount;
        for (int part = 0; part < originalCount; part++)
        {
            if (data.Parts![part].TrackCount <= AnimationBuilder.TrackCapacity)
                continue;
            int destination = part;
            data.Parts[part].TrackCount = 0;
            for (int i = 0; i < data.TrackCount; i++)
            {
                GraphTrack field = data.Tracks![i];
                if (field.TimelinePart != part)
                    continue;
                bool earlier = false;
                for (int j = 0; j < i; j++)
                    if (data.Tracks[j].TimelinePart == part && SameOutput(in data.Tracks[j], in field))
                        earlier = true;
                if (earlier)
                    continue;
                int count = 0;
                for (int j = i; j < data.TrackCount; j++)
                    if (data.Tracks[j].TimelinePart == part && SameOutput(in data.Tracks[j], in field))
                        count++;
                if (data.Parts[destination].TrackCount > 0 && data.Parts[destination].TrackCount + count > AnimationBuilder.TrackCapacity)
                {
                    GraphArrays.Grow(ref data.Parts, data.PartCount + 1);
                    destination = data.PartCount++;
                    data.Parts![destination] = new GraphPart
                    {
                        Id = -destination, TimelinePart = part, Stages = data.Parts[part].Stages, StageCount = data.Parts[part].StageCount,
                    };
                }
                for (int j = i; j < data.TrackCount; j++)
                    if (data.Tracks[j].TimelinePart == part && SameOutput(in data.Tracks[j], in field))
                        data.Tracks[j].Part = destination;
                data.Parts[destination].TrackCount += count;
            }
        }
    }

    private static bool SameOutput(in GraphTrack first, in GraphTrack second) =>
        first.Node.Target == second.Node.Target && first.Node.Field.Component == second.Node.Field.Component &&
        first.Node.Field.Field == second.Node.Field.Field && first.Node.Layer == second.Node.Layer && first.Group == second.Group;

    private static bool ContainsOpenStage(GraphNode[] nodes, int node)
    {
        GraphNode current = nodes[node];
        if (current.Kind == GraphNodeKind.Branch)
            return false;
        if (current.Kind is GraphNodeKind.StateMachine or GraphNodeKind.Blend1D or GraphNodeKind.Boundary ||
            current.Kind == GraphNodeKind.Track && (current.Leaf is not null ? current.Boundary.End != StageEnd.Duration : current.Spec.Curve.Kind == CurveKind.Spring))
            return true;
        for (int child = current.FirstChild; child != 0; child = nodes[child].Next)
            if (ContainsOpenStage(nodes, child))
                return true;
        return false;
    }

    private static void CompileLeaves(ref GraphData data)
    {
        var batches = new HashSet<(Type Type, int Part)>();
        var outputs = new Dictionary<(int Part, SomeEngine.ECS.Entities.Entity Target, ushort Component, uint Field, byte Layer, int Group), int>();
        Span<GraphTrack> tracks = data.Tracks.AsSpan(0, data.TrackCount);
        for (int index = 0; index < tracks.Length; index++)
        {
            ref GraphTrack track = ref tracks[index];
            var output = (track.Part, track.Node.Target, track.Node.Field.Component, track.Node.Field.Field, track.Node.Layer, track.Group);
            if (!outputs.TryGetValue(output, out int outputId))
                outputs.Add(output, outputId = outputs.Count);
            track.Output = outputId;
            GraphLeafData? leaf = tracks[index].Node.Leaf;
            if (leaf is not null && batches.Add((leaf.Type, tracks[index].Part)))
                leaf.CreateBatch(tracks, tracks[index].Part);
        }
    }

    private static void CompileRanges(ref GraphData data)
    {
        if (data.TrackCount > 1)
            Array.Sort(data.Tracks!, 0, data.TrackCount, Comparer<GraphTrack>.Create(static (first, second) =>
        {
            int order = first.Part.CompareTo(second.Part);
            return order == 0 ? first.Order.CompareTo(second.Order) : order;
        }));
        int nextTrack = 0;
        for (int part = 0; part < data.PartCount; part++)
        {
            data.Parts![part].TrackFirst = nextTrack;
            while (nextTrack < data.TrackCount && data.Tracks![nextTrack].Part == part)
                nextTrack++;
            data.Parts[part].TrackCount = nextTrack - data.Parts[part].TrackFirst;
        }
        var groups = new List<int>[data.PartCount];
        var motions = new List<int>[data.GroupCount];
        for (int part = 0; part < groups.Length; part++) groups[part] = new List<int>();
        for (int group = 0; group < motions.Length; group++)
        {
            motions[group] = new List<int>();
            groups[data.Groups![group].Part].Add(group);
        }
        for (int motion = 0; motion < data.MotionCount; motion++)
            motions[data.Motions![motion].Group].Add(motion);
        var groupMotions = new List<int>();
        var blendMotions = new List<int>();
        var transitions = new List<int>();
        for (int groupIndex = 0; groupIndex < data.GroupCount; groupIndex++)
        {
            ref GraphGroupData group = ref data.Groups![groupIndex];
            group.MotionFirst = groupMotions.Count;
            group.MotionCount = motions[groupIndex].Count;
            groupMotions.AddRange(motions[groupIndex]);
            if (data.Nodes![group.Node].Kind == GraphNodeKind.Blend1D)
            {
                group.BlendFirst = blendMotions.Count;
                int[] sorted = motions[groupIndex].ToArray();
                GraphMotionData[] motionData = data.Motions!;
                GraphNode[] nodes = data.Nodes;
                Array.Sort(sorted, (first, second) => nodes[motionData[first].Node].Coordinate.CompareTo(nodes[motionData[second].Node].Coordinate));
                blendMotions.AddRange(sorted);
                group.BlendCount = sorted.Length;
            }
            GraphNode machine = data.Nodes[group.Node];
            foreach (int motionIndex in motions[groupIndex])
            {
                ref GraphMotionData motion = ref data.Motions![motionIndex];
                motion.TransitionFirst = transitions.Count;
                int state = data.Nodes[motion.Node].StateId;
                for (int child = machine.FirstChild; child != 0; child = data.Nodes[child].Next)
                    if (data.Nodes[child].Kind == GraphNodeKind.Transition && data.Nodes[child].StateId == state)
                        transitions.Add(child);
                motion.TransitionCount = transitions.Count - motion.TransitionFirst;
            }
        }
        var partGroups = new List<int>();
        for (int part = 0; part < data.PartCount; part++)
        {
            data.Parts![part].GroupFirst = partGroups.Count;
            data.Parts[part].GroupCount = groups[part].Count;
            partGroups.AddRange(groups[part]);
        }
        data.GroupMotions = groupMotions.ToArray();
        data.BlendMotions = blendMotions.ToArray();
        data.TransitionNodes = transitions.ToArray();
        data.PartGroups = partGroups.ToArray();
    }

    private static void CompileConditions(ref GraphData data)
    {
        var types = new Dictionary<Type, int>();
        var inputs = new HashSet<SomeEngine.ECS.Queries.QueryDefinition>();
        for (int index = 0; index < data.NodeCount; index++)
        {
            ref GraphNode node = ref data.Nodes![index];
            if (node.ConditionData is not { } condition)
                continue;
            types.TryGetValue(condition.Type, out int count);
            node.ConditionIndex = count;
            types[condition.Type] = count + 1;
            if (condition.Inputs != SomeEngine.ECS.Queries.QueryDefinition.Empty)
                inputs.Add(condition.Inputs);
        }
        data.Inputs = inputs.ToArray();
    }

    [Conditional("DEBUG")]
    private static void CheckDefinitions(in GraphData data)
    {
        for (int i = 0; i < data.NodeCount; i++)
        {
            GraphNode node = data.Nodes![i];
            if (node.Kind is not (GraphNodeKind.StateMachine or GraphNodeKind.Blend1D))
                continue;
            int samples = 0, initials = 0;
            for (int child = node.FirstChild; child != 0; child = data.Nodes[child].Next)
            {
                GraphNode sample = data.Nodes[child];
                if (sample.Kind is GraphNodeKind.State or GraphNodeKind.BlendPoint)
                {
                    samples++;
                    if (sample.Initial)
                        initials++;
                    for (int earlier = node.FirstChild; earlier != child; earlier = data.Nodes[earlier].Next)
                    {
                        GraphNode other = data.Nodes[earlier];
                        if (other.Kind != sample.Kind)
                            continue;
                        DevelopmentCheck.Require(sample.Kind == GraphNodeKind.State ? other.StateId != sample.StateId : other.Coordinate != sample.Coordinate,
                            "State ids and blend coordinates must be distinct within their scope.");
                    }
                }
                else if (sample.Kind == GraphNodeKind.Transition)
                {
                    bool hasFrom = false, hasTo = false;
                    for (int state = node.FirstChild; state != 0; state = data.Nodes[state].Next)
                        if (data.Nodes[state].Kind == GraphNodeKind.State)
                        {
                            hasFrom |= data.Nodes[state].StateId == sample.StateId;
                            hasTo |= data.Nodes[state].StateId == sample.NextState;
                        }
                    DevelopmentCheck.Require(hasFrom && hasTo, "A transition requires declared source and destination states.");
                }
                else
                    DevelopmentCheck.Fail("Declare animation tracks inside a state or blend sample scope.");
            }
            DevelopmentCheck.Require(samples > 0 && initials <= 1, "A graph group requires samples and at most one initial state.");
        }
    }
}

internal struct GraphPosition
{
    public ushort Stage;
    public float Time;
}
