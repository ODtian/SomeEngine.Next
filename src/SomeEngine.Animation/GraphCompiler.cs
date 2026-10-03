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
                Node = node, Stage = motion == 0 ? start.Stage : data.Groups![data.Motions![motion - 1].Group].Stage,
                Start = start.Time, Part = part, TimelinePart = part, Motion = motion, Group = group, MotionStage = start.Stage,
            };
            data.Parts![part].TrackCount++;
            if (node.Spec.Curve.Kind == CurveKind.Spring)
            {
                AddStage(graph, part, motion, start.Stage, StageEnd.Settled, start.Time);
                return new GraphPosition { Stage = (ushort)(start.Stage + 1) };
            }
            start.Time += node.Spec.Duration;
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
        first.Node.Field.Offset == second.Node.Field.Offset && first.Node.Layer == second.Node.Layer && first.Group == second.Group;

    private static bool ContainsOpenStage(GraphNode[] nodes, int node)
    {
        GraphNode current = nodes[node];
        if (current.Kind == GraphNodeKind.Branch)
            return false;
        if (current.Kind is GraphNodeKind.StateMachine or GraphNodeKind.Blend1D or GraphNodeKind.Boundary ||
            current.Kind == GraphNodeKind.Track && current.Spec.Curve.Kind == CurveKind.Spring)
            return true;
        for (int child = current.FirstChild; child != 0; child = nodes[child].Next)
            if (ContainsOpenStage(nodes, child))
                return true;
        return false;
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
