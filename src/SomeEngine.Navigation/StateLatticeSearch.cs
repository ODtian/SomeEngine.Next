using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>One request's discrete pose states, selected primitives and budgeted search queue.</summary>
public sealed class StateLatticeSearchState
{
    private readonly NavigationSearchInput<NavigationVehicleSpace> _input;
    private readonly List<Node> _nodes = [];
    private readonly Dictionary<PoseKey, int> _best = [];
    private readonly PriorityQueue<int, float> _open = new();
    private readonly float _originHeading;
    private readonly float _minimumCost;
    private NavigationSearchStatus _status;
    private NavigationPath? _path;

    internal StateLatticeSearchState(in NavigationSearchInput<NavigationVehicleSpace> input)
    {
        _input = input;
        _originHeading = input.StartHeading - input.Space.Motions.Headings.Span[0];
        _minimumCost = float.PositiveInfinity;
        foreach (NavigationGridCell cell in input.Space.Grid.Cells.Span)
            _minimumCost = MathF.Min(_minimumCost, cell.BaseCost * input.Policy.GetAreaCost(cell.Area));
        if (input.Space.Costs is { } costs)
            foreach (float cost in costs.CellCosts.Span) _minimumCost = MathF.Min(_minimumCost, cost);
        var start = new NavigationPose(input.Start, input.StartHeading);
        if (!input.Space.CanOccupy(start, input.Profile, input.Policy))
        {
            _status = NavigationSearchStatus.Unreachable;
            return;
        }
        Add(new(start, 0, 0, -1, []));
    }

    internal NavigationSearchStep Advance(int budget)
    {
        for (int expanded = 0; expanded < budget && _status == NavigationSearchStatus.Searching; expanded++)
        {
            if (!_open.TryDequeue(out int index, out _))
            {
                _status = NavigationSearchStatus.Unreachable;
                break;
            }
            Node node = _nodes[index];
            if (_best[Key(node.Pose, node.Heading)] != index) continue;
            if (Vector3.Distance(node.Pose.Position, _input.Goal) <= _input.GoalRange + 0.0001f &&
                (_input.GoalHeading is not { } goalHeading ||
                 MathF.Abs(NavigationHeading.Difference(goalHeading, node.Pose.Heading)) <= _input.GoalHeadingTolerance))
            {
                Complete(index);
                break;
            }
            NavigationMotionSet motions = _input.Space.Motions;
            foreach (NavigationMotionPrimitive primitive in motions.Primitives.Span)
            {
                if (primitive.StartHeading != node.Heading) continue;
                NavigationPose pose = node.Pose;
                float cost = node.Cost;
                var segments = new NavigationMotionSegment[primitive.ControlCount];
                bool traversable = true;
                for (int controlIndex = 0; controlIndex < segments.Length; controlIndex++)
                {
                    NavigationVehicleControl control = motions.Controls.Span[primitive.ControlStart + controlIndex];
                    if (!_input.Space.TryTraverse(pose, control, _input.Profile, _input.Policy, out NavigationPose end, out float movementCost))
                    {
                        traversable = false;
                        break;
                    }
                    segments[controlIndex] = new(pose, end, control);
                    pose = end;
                    cost += movementCost;
                }
                if (traversable) Add(new(pose, primitive.EndHeading, cost, index, segments));
            }
        }
        return new(_status, _path);
    }

    private void Add(Node node)
    {
        PoseKey key = Key(node.Pose, node.Heading);
        if (_best.TryGetValue(key, out int previous) && _nodes[previous].Cost <= node.Cost) return;
        int index = _nodes.Count;
        _nodes.Add(node);
        _best[key] = index;
        float heuristic = MathF.Max(0, Vector3.Distance(node.Pose.Position, _input.Goal) - _input.GoalRange) * _minimumCost;
        _open.Enqueue(index, node.Cost + heuristic);
    }

    private PoseKey Key(NavigationPose pose, int heading)
    {
        Vector3 offset = pose.Position - _input.Start;
        float cosine = MathF.Cos(_originHeading), sine = MathF.Sin(_originHeading);
        float resolution = _input.Space.Motions.Resolution;
        int cell = _input.Space.Grid.FindCell(pose.Position);
        return new((int)MathF.Round((cosine * offset.X - sine * offset.Z) / resolution),
            (int)MathF.Round((sine * offset.X + cosine * offset.Z) / resolution),
            _input.Space.Grid.Cells.Span[cell].Layer, heading);
    }

    private void Complete(int index)
    {
        float cost = _nodes[index].Cost;
        var route = new List<int>();
        for (int current = index; _nodes[current].Parent >= 0; current = _nodes[current].Parent) route.Add(current);
        route.Reverse();
        NavigationMotionSegment[] segments = route.SelectMany(current => _nodes[current].Segments).ToArray();
        _path = NavigationVehiclePath.Create(_input, segments, cost);
        _status = NavigationSearchStatus.Found;
    }

    private readonly record struct PoseKey(int X, int Z, int Layer, int Heading);
    private sealed record Node(NavigationPose Pose, int Heading, float Cost, int Parent, NavigationMotionSegment[] Segments);
}

public readonly struct StateLatticeSearch : INavigationSearch<NavigationVehicleSpace, StateLatticeSearchState>
{
    public static StateLatticeSearchState Create(in NavigationSearchInput<NavigationVehicleSpace> input) => new(in input);
    public static NavigationSearchStep Advance(StateLatticeSearchState state, int expansionBudget) => state.Advance(expansionBudget);
}
