using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>Continuous control search resolution and distance-cost penalties.</summary>
public sealed record HybridAStarSettings
{
    public int HeadingBins { get; init; } = 72;
    public int CurvatureSteps { get; init; } = 2;
    public float? SpatialResolution { get; init; }
    public float? StepLength { get; init; }
    public float? AnalyticExpansionDistance { get; init; }
    public float TurningPenalty { get; init; } = 1.05f;
    public float ReversePenalty { get; init; } = 1.5f;
    public float DirectionChangeCost { get; init; } = 0.5f;
}

/// <summary>One request's continuous poses, quantized best-state index, controls and budgeted open queue.</summary>
public sealed class HybridAStarSearchState
{
    private readonly NavigationSearchInput<NavigationVehicleSpace> _input;
    private readonly HybridAStarSettings _settings;
    private readonly List<Node> _nodes = [];
    private readonly Dictionary<PoseKey, int> _best = [];
    private readonly PriorityQueue<int, float> _open = new();
    private readonly float _resolution, _stepLength, _analyticDistance, _minimumCost;
    private float _goalCost = float.PositiveInfinity;
    private NavigationSearchStatus _status;
    private NavigationPath? _path;

    internal HybridAStarSearchState(in NavigationSearchInput<NavigationVehicleSpace> input)
    {
        _input = input;
        _settings = input.Space.HybridSettings;
        _resolution = _settings.SpatialResolution ?? input.Space.Grid.CellSize;
        _stepLength = _settings.StepLength ?? MathF.Max(MathF.Sqrt(2) * _resolution,
            input.Space.Motions.Model.MinimumTurningRadius * MathF.Tau / _settings.HeadingBins);
        _analyticDistance = _settings.AnalyticExpansionDistance ?? 4 * input.Space.Motions.Model.MinimumTurningRadius;
        _minimumCost = float.PositiveInfinity;
        if (input.Space.Costs is { } costs)
            foreach (float cost in costs.CellCosts.Span) _minimumCost = MathF.Min(_minimumCost, cost);
        else
            foreach (NavigationGridCell cell in input.Space.Grid.Cells.Span)
                _minimumCost = MathF.Min(_minimumCost, cell.BaseCost * input.Policy.GetAreaCost(cell.Area));
        var start = new NavigationPose(input.Start, input.StartHeading);
        if (!input.Space.CanOccupy(start, input.Profile, input.Policy)) { _status = NavigationSearchStatus.Unreachable; return; }
        Add(new(start, 0, 0, -1, [], false));
    }

    internal NavigationSearchStep Advance(int budget)
    {
        for (int expanded = 0; expanded < budget && _status == NavigationSearchStatus.Searching; expanded++)
        {
            if (!_open.TryDequeue(out int index, out _)) { _status = NavigationSearchStatus.Unreachable; break; }
            Node node = _nodes[index];
            if (node.Terminal ? node.Cost != _goalCost : _best[Key(node.Pose, node.Direction)] != index) continue;
            if (Reached(node.Pose)) { Complete(index); break; }
            if (HorizontalDistance(node.Pose.Position, _input.Goal) <= _analyticDistance)
                ConnectGoal(index);
            int firstDirection = _input.Space.Motions.Model.AllowReverse ? -1 : 1;
            for (int direction = firstDirection; direction <= 1; direction += 2)
            for (int turn = -_settings.CurvatureSteps; turn <= _settings.CurvatureSteps; turn++)
            {
                float curvature = _input.Space.Motions.Model.MaximumCurvature * turn / _settings.CurvatureSteps;
                NavigationVehicleControl control = Control(direction, curvature, _stepLength);
                if (TryControls(node.Pose, node.Direction, [control], out NavigationPose end,
                    out NavigationMotionSegment[] segments, out float cost))
                    Add(new(end, direction, node.Cost + cost, index, segments, false));
            }
        }
        return new(_status, _path);
    }

    private void ConnectGoal(int parent)
    {
        Node node = _nodes[parent];
        float heading = _input.GoalHeading ?? NavigationHeading.FromDirection(_input.Goal - node.Pose.Position);
        foreach (NavigationVehicleControl[] controls in NavigationDubinsConnection.Create(node.Pose,
            new(_input.Goal, heading), _input.Space.Motions.Model, _input.Profile))
        {
            if (!TryControls(node.Pose, node.Direction, controls, out NavigationPose end,
                out NavigationMotionSegment[] segments, out float cost) || !Reached(end)) continue;
            cost += node.Cost;
            if (cost >= _goalCost) continue;
            _goalCost = cost;
            int index = _nodes.Count;
            _nodes.Add(new(end, controls.Length == 0 ? node.Direction : Math.Sign(controls[^1].Speed), cost, parent, segments, true));
            _open.Enqueue(index, cost);
        }
    }

    private bool TryControls(NavigationPose start, int previousDirection, NavigationVehicleControl[] controls,
        out NavigationPose end, out NavigationMotionSegment[] segments, out float cost)
    {
        end = start; cost = 0;
        var result = new List<NavigationMotionSegment>();
        foreach (NavigationVehicleControl control in controls)
        {
            int direction = Math.Sign(control.Speed);
            if (previousDirection != 0 && previousDirection != direction) cost += _settings.DirectionChangeCost;
            previousDirection = direction;
            int parts = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(control.Speed * control.Curvature * control.Duration) / (MathF.PI / 2)));
            NavigationVehicleControl part = control with { Duration = control.Duration / parts };
            for (int index = 0; index < parts; index++)
            {
                if (!_input.Space.TryTraverse(end, part, _input.Profile, _input.Policy, out NavigationPose next, out float travelCost))
                { segments = []; return false; }
                result.Add(new(end, next, part));
                cost += travelCost * (part.Curvature == 0 ? 1 : _settings.TurningPenalty) *
                    (part.Speed < 0 ? _settings.ReversePenalty : 1);
                end = next;
            }
        }
        segments = result.ToArray();
        return true;
    }

    private NavigationVehicleControl Control(int direction, float curvature, float distance)
    {
        float speed = _input.Profile.MaximumSpeed;
        if (curvature != 0) speed = MathF.Min(speed, _input.Profile.MaximumAngularSpeed / MathF.Abs(curvature));
        return new(direction * speed, curvature, distance / speed);
    }

    private bool Reached(NavigationPose pose) => Vector3.Distance(pose.Position, _input.Goal) <= _input.GoalRange + 0.0001f &&
        (_input.GoalHeading is not { } heading || MathF.Abs(NavigationHeading.Difference(heading, pose.Heading)) <= _input.GoalHeadingTolerance);

    private void Add(Node node)
    {
        PoseKey key = Key(node.Pose, node.Direction);
        if (_best.TryGetValue(key, out int previous) && _nodes[previous].Cost <= node.Cost) return;
        int index = _nodes.Count;
        _nodes.Add(node); _best[key] = index;
        float distance = MathF.Max(0, HorizontalDistance(node.Pose.Position, _input.Goal) - _input.GoalRange);
        if (_input.GoalHeading is { } heading)
            distance = MathF.Max(distance, MathF.Max(0, MathF.Abs(NavigationHeading.Difference(heading, node.Pose.Heading)) -
                _input.GoalHeadingTolerance) * _input.Space.Motions.Model.MinimumTurningRadius);
        _open.Enqueue(index, node.Cost + distance * _minimumCost);
    }

    private PoseKey Key(NavigationPose pose, int direction)
    {
        Vector3 offset = pose.Position - _input.Space.Grid.Bounds.Minimum;
        float heading = pose.Heading % MathF.Tau;
        if (heading < 0) heading += MathF.Tau;
        int bin = (int)MathF.Round(heading * _settings.HeadingBins / MathF.Tau) % _settings.HeadingBins;
        int cell = _input.Space.Grid.FindCell(pose.Position);
        return new((int)MathF.Floor(offset.X / _resolution), (int)MathF.Floor(offset.Z / _resolution),
            _input.Space.Grid.Cells.Span[cell].Layer, bin, direction);
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
    private static float HorizontalDistance(Vector3 first, Vector3 second) => new Vector2(first.X - second.X, first.Z - second.Z).Length();
    private readonly record struct PoseKey(int X, int Z, int Layer, int Heading, int Direction);
    private sealed record Node(NavigationPose Pose, int Direction, float Cost, int Parent, NavigationMotionSegment[] Segments, bool Terminal);
}

/// <summary>Hybrid A* integrates controls from continuous poses and indexes candidates by spatial and heading bins.</summary>
public readonly struct HybridAStarSearch : INavigationSearch<NavigationVehicleSpace, HybridAStarSearchState>
{
    public static HybridAStarSearchState Create(in NavigationSearchInput<NavigationVehicleSpace> input) => new(in input);
    public static NavigationSearchStep Advance(HybridAStarSearchState state, int expansionBudget) => state.Advance(expansionBudget);
}
