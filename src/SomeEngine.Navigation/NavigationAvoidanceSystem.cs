using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>Joins guidance, common actual-motion neighbors and local boundaries before the selected Steering writer.</summary>
public sealed class NavigationAvoidanceSystem<TSolver, TMotion, TReader>(World world, NavigationNeighborSystem<TMotion, TReader> neighbors)
    where TSolver : struct, INavigationAvoidanceSolver
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private JobHandle _completion;
    private readonly QueryDefinition _actors = world.QueryDefinition()
        .All<NavigationAgent>().All<AvoidanceAgent>().All<TMotion>().Build();

    public JobHandle SchedulePathBoundaries<TSpace>(JobHandle motionReady = default)
        where TSpace : class, INavigationSpace<TSpace> =>
        new NavigationPathBoundaries<TSpace, TMotion, TReader>().ScheduleParallel(world,
            WorldJobs.CombineDependencies(world, [_completion, motionReady]));

    public JobHandle ScheduleFlowBoundaries(JobHandle motionReady = default) =>
        new NavigationFlowBoundaries<TMotion, TReader>().ScheduleParallel(world,
            WorldJobs.CombineDependencies(world, [_completion, motionReady]));

    public JobHandle Schedule(float deltaTime, JobHandle guidanceReady, JobHandle neighborsReady, JobHandle boundariesReady)
    {
        JobHandle ready = WorldJobs.CombineDependencies(world, [_completion, guidanceReady, neighborsReady, boundariesReady]);
        _completion = new NavigationAvoidanceJob<TSolver>(neighbors.Data, deltaTime).ScheduleParallel(world,
            [JobResourceAccess.Read(neighbors.Scheduler.GetResourceToken(neighbors.Data))],
            new JobEntityScheduleOptions(filter: _actors), ready);
        return _completion;
    }
}

internal partial struct NavigationPathBoundaries<TSpace, TMotion, TReader> : IJobEntity
    where TSpace : class, INavigationSpace<TSpace>
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    public void Execute([JobResources(typeof(PathNavigationResources<>))] in PathNavigation<TSpace> path,
        [JobResources<NavigationAgentResources>] in NavigationAgent agent, in AvoidanceAgent settings, in TMotion movement,
        [JobResources<NavigationAvoidanceWorkResources>] in NavigationAvoidanceWork work)
    {
        Vector3 position = TReader.Read(in movement).Position;
        float range = settings.BoundaryTimeHorizon * agent.Profile.MaximumSpeed + settings.Radius + settings.Spacing;
        var builder = new NavigationBoundaryBuilder(work.Workspace, position, range, agent.Profile);
        TSpace.CollectBoundaries(path.Space, position, range, agent.Profile, agent.Policy, ref builder);
    }
}

internal partial struct NavigationFlowBoundaries<TMotion, TReader> : IJobEntity
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    public void Execute([JobResources<FlowBoundaryResources>] in FlowFollower follower,
        [JobResources<NavigationAgentResources>] in NavigationAgent agent, in AvoidanceAgent settings, in TMotion movement,
        [JobResources<NavigationAvoidanceWorkResources>] in NavigationAvoidanceWork work)
    {
        Vector3 position = TReader.Read(in movement).Position;
        float range = settings.BoundaryTimeHorizon * agent.Profile.MaximumSpeed + settings.Radius + settings.Spacing;
        var builder = new NavigationBoundaryBuilder(work.Workspace, position, range, agent.Profile);
        if (follower.Flow.Field is { } field)
            NavigationGrid.CollectBoundaries(field.Input.Costs.Grid, position, range, agent.Profile, field.Input.Costs.Policy, ref builder);
    }
}

internal readonly struct FlowBoundaryResources : IJobComponentResources<FlowFollower>
{
    public static long GetVersion(in FlowFollower component) => FlowFollowerResources.GetVersion(in component);
    public static void Collect(in FlowFollower component, ref JobResourceAccessBuilder resources)
    {
        FlowFollowerResources.Collect(in component, ref resources);
        if (component.Flow.Field is not { } field) return;
        if (!field.Input.Costs.Policy.AreaCosts.IsEmpty) resources.Read(field.Input.Costs.Policy.AreaCosts);
        if (!field.Input.Costs.Grid.Shape.Footprint.IsEmpty) resources.Read(field.Input.Costs.Grid.Shape.Footprint);
    }
}

internal partial struct NavigationAvoidanceJob<TSolver>(NavigationNeighbors neighbors, float deltaTime) : IJobEntity
    where TSolver : struct, INavigationAvoidanceSolver
{
    private readonly NavigationNeighbors _neighbors = neighbors;
    private readonly float _deltaTime = deltaTime;

    public void Execute(Entity entity, in NavigationGuidance guidance,
        [JobResources<NavigationAvoidanceWorkResources>] in NavigationAvoidanceWork work, ref NavigationSteering steering)
    {
        int index = _neighbors.Find(entity);
        NavigationNeighborAgent self = _neighbors.Agents.Span[index];
        var input = new NavigationAvoidanceInput(self, _neighbors.Agents.Span, _neighbors.GetNeighbors(index).Span,
            CollectionsMarshal.AsSpan(work.Workspace.Boundaries), new(guidance.Velocity.X, guidance.Velocity.Z), _deltaTime);
        Vector2 velocity = TSolver.Solve(in input, work.Workspace);
        Vector3 motion = new(velocity.X, guidance.Velocity.Y, velocity.Y);
        float heading = velocity.LengthSquared() > 0.000001f ? NavigationHeading.FromDirection(motion) : guidance.Heading;
        steering = new(motion, heading, guidance.RequestVersion, guidance.Active, Response: TSolver.GetResponse(in self));
    }
}
