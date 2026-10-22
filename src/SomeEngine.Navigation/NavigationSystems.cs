using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public readonly record struct NavigationStep(JobHandle Paths, JobHandle Guidance, JobHandle Steering);

/// <summary>Connects path search, guidance, steering and progress to a concrete movement component.</summary>
public sealed class NavigationSystems<TSpace, TSearch, TState, TMotion, TReader>
    where TSpace : class, INavigationSpace<TSpace>
    where TSearch : struct, INavigationSearch<TSpace, TState>
    where TState : class
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world;
    private readonly QueryDefinition _enabledGoal;
    private readonly QueryDefinition _disabledGoal;
    private JobHandle _completion;
    private JobHandle _steering;

    public NavigationSystems(World world)
    {
        _world = world;
        _enabledGoal = world.QueryDefinition().Enabled<NavigationGoal>().Build();
        _disabledGoal = world.QueryDefinition().Disabled<NavigationGoal>().Build();
    }

    public void AddAgent(Entity entity, TSpace space, NavigationProfile profile, NavigationPolicy policy,
        NavigationGoal goal, float desiredSpeed) =>
        NavigationComponents.AddPath<TSpace, TState>(_world, entity, space, profile, policy, goal, desiredSpeed);

    /// <summary>motionReady completes the producer of this step's actual movement input.</summary>
    public NavigationStep Schedule(float deltaTime, int expansionBudget, JobHandle motionReady = default)
    {
        JobHandle ready = WorldJobs.CombineDependencies(_world, [_completion, motionReady]);
        var enabled = new JobEntityScheduleOptions(filter: _enabledGoal);
        var disabled = new JobEntityScheduleOptions(filter: _disabledGoal);
        JobHandle stopped = new NavigationStopJob<TSpace, TState>()
            .ScheduleParallel(_world, disabled, ready);
        JobHandle paths = new NavigationPathJob<TSpace, TSearch, TState, TMotion, TReader>(_world, expansionBudget)
            .ScheduleParallel(_world, [ComponentJobAccess<TMotion>.Read(_world)], enabled, stopped);
        JobHandle guidance = new NavigationGuidanceJob<TSpace, TState, TMotion, TReader>(deltaTime)
            .ScheduleParallel(_world, enabled, paths);
        _steering = new NavigationSteeringJob().ScheduleParallel(_world, enabled, guidance);
        _completion = _steering;
        return new(paths, guidance, _steering);
    }

    /// <summary>Reads actual positions and headings after the movement consumer has completed.</summary>
    public JobHandle ScheduleProgress(JobHandle motionCompletion)
    {
        JobHandle moved = WorldJobs.CombineDependencies(_world, [_steering, motionCompletion]);
        _completion = new NavigationProgressJob<TMotion, TReader>(_world)
            .ScheduleParallel(_world, [ComponentJobAccess<TMotion>.Read(_world)],
                new JobEntityScheduleOptions(filter: _enabledGoal), moved);
        return _completion;
    }
}
