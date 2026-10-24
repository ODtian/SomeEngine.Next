using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public readonly record struct NavigationFlowStep(JobHandle Integration, JobHandle Guidance, JobHandle Steering, JobHandle Traversal = default);

/// <summary>Connects shared flow guidance to the same steering and actual-motion progress components.</summary>
public sealed class NavigationSystems<TMotion, TReader>
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world;
    private readonly QueryDefinition _enabledFlow;
    private readonly QueryDefinition _disabledFlow;
    private readonly NavigationPassageSystem _passages;
    private JobHandle _completion;

    public NavigationSystems(World world, NavigationPassageSystem? passages = null)
    {
        _world = world;
        _passages = passages ?? new(world);
        _enabledFlow = world.QueryDefinition().All<TMotion>().Enabled<FlowFollower>().Build();
        _disabledFlow = world.QueryDefinition().All<TMotion>().Disabled<FlowFollower>().Build();
    }

    public void AddFlowAgent(Entity entity, NavigationFlow flow, float desiredSpeed) =>
        NavigationComponents.AddFlow(_world, entity, flow, desiredSpeed);

    public NavigationFlowStep Schedule(float deltaTime, JobHandle flowReady = default, JobHandle motionReady = default)
    {
        NavigationFlowStep step = ScheduleGuidance(deltaTime, flowReady, motionReady);
        JobHandle steering = new NavigationSteeringJob().ScheduleParallel(_world,
            new JobEntityScheduleOptions(filter: _enabledFlow), step.Guidance);
        _completion = steering;
        return step with { Steering = steering };
    }

    /// <summary>Returns flow guidance for a caller-selected steering or avoidance stage.</summary>
    public NavigationFlowStep ScheduleGuidance(float deltaTime, JobHandle flowReady = default, JobHandle motionReady = default)
    {
        JobHandle ready = WorldJobs.CombineDependencies(_world, [_completion, flowReady, motionReady]);
        JobHandle stopped = new NavigationFlowStopJob().ScheduleParallel(_world,
            new JobEntityScheduleOptions(filter: _disabledFlow), ready);
        JobHandle guidance = new NavigationFlowGuidanceJob<TMotion, TReader>(deltaTime).ScheduleParallel(_world,
            new JobEntityScheduleOptions(filter: _enabledFlow), stopped);
        JobHandle traversal = new NavigationFlowTraversalJob<TMotion, TReader>().ScheduleParallel(_world,
            new JobEntityScheduleOptions(filter: _enabledFlow), guidance);
        _completion = _passages.Schedule(traversal);
        return new(flowReady, _completion, default, _completion);
    }

    public JobHandle ScheduleProgress(JobHandle motionCompletion)
    {
        JobHandle moved = WorldJobs.CombineDependencies(_world, [_completion, motionCompletion]);
        _completion = new NavigationFlowProgressJob<TMotion, TReader>().ScheduleParallel(_world,
            new JobEntityScheduleOptions(filter: _enabledFlow), moved);
        return _completion;
    }
}
