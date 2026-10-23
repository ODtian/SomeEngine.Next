using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

public readonly record struct FlowFollower(NavigationFlow Flow) : IEnableableComponent;

public static partial class NavigationComponents
{
    public static void AddFlow(World world, Entity entity, NavigationFlow flow, float desiredSpeed)
    {
        world.Add(entity, new NavigationAgent(flow.Profile, flow.Costs.Policy, desiredSpeed));
        world.Add(entity, new FlowFollower(flow));
        world.Add(entity, default(NavigationFlowFollowState));
        world.Add(entity, default(NavigationGuidance));
        world.Add(entity, default(NavigationSteering));
        world.Add(entity, default(NavigationProgress));
    }
}

internal struct NavigationFlowFollowState : IComponent
{
    public int Cell;
    public int Destination;
    public long FieldVersion;
}

public readonly struct FlowFollowerResources : IJobComponentResources<FlowFollower>
{
    public static long GetVersion(in FlowFollower component) => component.Flow.FieldVersion;

    public static void Collect(in FlowFollower component, ref JobResourceAccessBuilder resources)
    {
        resources.Read(component.Flow);
        NavigationFlowInput current = component.Flow.Input;
        resources.Read(current.Costs.Grid.Cells);
        resources.Read(current.Costs.Grid.CellTiles);
        resources.Read(current.Goals);
        if (!component.Flow.TileStates.IsEmpty) resources.Read(component.Flow.TileStates);
        if (component.Flow.Field is not { } field) return;
        if (field.Input.HeadingGraph is { } heading) HeadingNavigationFlowGraph.CollectResources(heading, ref resources);
        NavigationGrid grid = field.Input.Costs.Grid;
        resources.Read(grid.Cells);
        resources.Read(grid.Edges);
        resources.Read(grid.CellTiles);
        if (!grid.TileRanges.IsEmpty) resources.Read(grid.TileRanges);
        resources.Read(field.Input.Costs.CellCosts);
        resources.Read(field.Input.Costs.EdgeCosts);
        resources.Read(field.CumulativeCosts);
        resources.Read(field.SuccessorEdges);
        resources.Read(field.Directions);
        resources.Read(field.GoalIndices);
        resources.Read(field.Dependencies);
        resources.Read(field.Input.Goals);
        if (!field.Input.Profile.Footprint.IsEmpty) resources.Read(field.Input.Profile.Footprint);
        if (!field.Tiles.IsEmpty) resources.Read(field.Tiles);
        if (!field.BoundaryDependencies.IsEmpty) resources.Read(field.BoundaryDependencies);
        if (!field.RequiredTiles.IsEmpty) resources.Read(field.RequiredTiles);
    }
}
