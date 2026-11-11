using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public readonly record struct NavigationStep(JobHandle Paths, JobHandle Guidance, JobHandle Steering, JobHandle Traversal = default);

/// <summary>Connects path search, guidance, steering and progress to a concrete movement component.</summary>
public sealed class NavigationSystems<TSpace, TSearch, TState, TMotion, TReader>
    where TSpace : class, INavigationSpace<TSpace>
    where TSearch : struct, INavigationSearch<TSpace, TState>
    where TState : class
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world;
    private readonly QueryDescription _enabledGoal;
    private readonly QueryDescription _disabledGoal;
    private readonly NavigationTraversalSystem<TSpace, TState, TMotion, TReader> _traversal;
    private JobHandle _completion;

    public NavigationSystems(World world, NavigationPassageSystem? passages = null)
    {
        _world = world;
        _traversal = new(world, passages);
        _enabledGoal = new QueryDescription()
            .WithStored<PathNavigation<TSpace>>().WithStored<NavigationRequest<TSpace, TState>>().WithStored<TMotion>()
            .With<NavigationGoal>();
        _disabledGoal = new QueryDescription()
            .WithStored<PathNavigation<TSpace>>().WithStored<NavigationRequest<TSpace, TState>>().WithStored<TMotion>()
            .WithDisabled<NavigationGoal>();
    }

    public void AddAgent(Entity entity, TSpace space, NavigationProfile profile, NavigationPolicy policy,
        NavigationGoal goal, float desiredSpeed) =>
        NavigationComponents.AddPath<TSpace, TState>(_world, entity, space, profile, policy, goal, desiredSpeed);

    /// <summary>motionReady completes the producer of this step's actual movement input.</summary>
    public NavigationStep Schedule(float deltaTime, int expansionBudget, JobHandle motionReady = default)
    {
        NavigationStep step = ScheduleGuidance(deltaTime, expansionBudget, motionReady);
        JobHandle steering = new NavigationSteeringJob().ScheduleParallel(_world, dependency: step.Guidance, query: _world.Query(_enabledGoal));
        _completion = steering;
        return step with { Steering = steering };
    }

    /// <summary>Returns path guidance for a caller-selected steering or avoidance stage.</summary>
    public NavigationStep ScheduleGuidance(float deltaTime, int expansionBudget, JobHandle motionReady = default)
    {
        JobHandle ready = _world.Execution!.Scheduler!.CombineDependencies([_completion, motionReady]);
        Query enabled = _world.Query(_enabledGoal);
        Query disabled = _world.Query(_disabledGoal);
        JobHandle stopped = new NavigationStopJob<TSpace, TState>()
            .ScheduleParallel(_world, dependency: ready, query: disabled);
        JobHandle paths = new NavigationPathJob<TSpace, TSearch, TState, TMotion, TReader>(_world, expansionBudget)
            .ScheduleParallel(_world, dependency: stopped, query: enabled);
        JobHandle guidance = new NavigationGuidanceJob<TSpace, TState, TMotion, TReader>(deltaTime)
            .ScheduleParallel(_world, dependency: paths, query: enabled);
        JobHandle traversal = _traversal.Schedule(guidance);
        _completion = traversal;
        return new(paths, traversal, default, traversal);
    }

    /// <summary>Reads actual positions and headings after the movement consumer has completed.</summary>
    public JobHandle ScheduleProgress(JobHandle motionCompletion)
    {
        JobHandle moved = _world.Execution!.Scheduler!.CombineDependencies([_completion, motionCompletion]);
        _completion = new NavigationProgressJob<TMotion, TReader>(_world)
            .ScheduleParallel(_world, dependency: moved, query: _world.Query(_enabledGoal));
        return _completion;
    }
}
