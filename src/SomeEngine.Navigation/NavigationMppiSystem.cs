using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>MPPI preparation, independent candidate jobs, per-actor reduction and selected-trajectory output.</summary>
public sealed class NavigationAvoidanceSystem<TSpace, TModel, TMotion, TReader>
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world;
    private readonly NavigationNeighborSystem<TMotion, TReader> _neighbors;
    private readonly QueryDefinition _active, _stopped;
    private JobHandle _completion;

    public NavigationAvoidanceSystem(World world, NavigationNeighborSystem<TMotion, TReader> neighbors)
    {
        _world = world;
        _neighbors = neighbors;
        _active = world.QueryDefinition().All<NavigationMppi<TSpace, TModel>>().All<TMotion>().Enabled<NavigationGoal>().Build();
        _stopped = world.QueryDefinition().All<NavigationMppi<TSpace, TModel>>().All<TMotion>().Disabled<NavigationGoal>().Build();
    }

    public JobHandle Schedule(float time, JobHandle guidanceReady, JobHandle neighborsReady, JobHandle motionReady = default)
    {
        JobHandle ready = WorldJobs.CombineDependencies(_world, [_completion, guidanceReady, neighborsReady, motionReady]);
        JobHandle stopped = new NavigationMppiStop<TSpace, TModel, TMotion, TReader>(time)
            .ScheduleParallel(_world, new JobEntityScheduleOptions(filter: _stopped), ready);
        JobHandle candidates = new NavigationMppiPrepare<TSpace, TModel, TMotion, TReader>(time, _neighbors.Data, _neighbors.Scheduler)
            .ScheduleParallel(_world, [JobResourceAccess.Read(_neighbors.Scheduler.GetResourceToken(_neighbors.Data))],
                new JobEntityScheduleOptions(filter: _active), stopped);
        _completion = new NavigationMppiOutput<TSpace, TModel>()
            .ScheduleParallel(_world, new JobEntityScheduleOptions(filter: _active), candidates);
        return _completion;
    }
}

internal partial struct NavigationMppiPrepare<TSpace, TModel, TMotion, TReader>(float time, NavigationNeighbors neighbors, Scheduler scheduler) : IJobEntity
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly float _time = time;
    private readonly NavigationNeighbors _neighbors = neighbors;
    private readonly Scheduler _scheduler = scheduler;

    public void Execute(Entity entity,
        [JobResources(typeof(NavigationMppiResources<,>))] in NavigationMppi<TSpace, TModel> mppi,
        [JobResources<NavigationAgentResources>] in NavigationAgent agent,
        [JobResources<NavigationPathResources>] in NavigationPathResult path,
        in NavigationGuidance guidance, in NavigationGoal goal, in TMotion movement)
    {
        NavigationMppiData<TSpace> data = mppi.Data;
        NavigationMppiSettings settings = mppi.Settings;
        NavigationMotion actual = TReader.Read(in movement);
        data.Configure(settings);
        data.Frame = new(mppi.Space, agent.Profile, agent.Policy, settings, actual, guidance, goal,
            path.HasTarget ? path.TargetPosition : goal.Position, _neighbors, _neighbors.Find(entity), _time, agent.DesiredSpeed);
        if (!guidance.Active)
        {
            data.Initialized = false;
            data.Valid = false;
            data.SelectedCount = 1;
            data.SelectedCost = 0;
            data.SelectedMotions[0] = actual;
            data.Steering = new(default, actual.Heading, guidance.RequestVersion, false);
            return;
        }
        NavigationMppiReference<TSpace, TModel>.Prepare(data, path);
        var handles = new JobHandle[settings.CandidateCount];
        JobHandle previous = default;
        // The generated parent keeps its complete component and space read grants through these descendants.
        for (int iteration = 0; iteration < settings.Iterations; iteration++)
        {
            for (int candidate = 0; candidate < settings.CandidateCount; candidate++)
            {
                int start = candidate * settings.HorizonSteps;
                handles[candidate] = _scheduler.Schedule(new NavigationMppiCandidate<TSpace, TModel>(data, candidate, iteration),
                    [Read(data), Read(data.Controls), Read(data.Reference), Read(data.ReferenceSpeeds), Read(_neighbors),
                     Write(data.Noise, start, settings.HorizonSteps), Write(data.CandidateControls, start, settings.HorizonSteps),
                     Write(data.CandidateMotions, candidate * (settings.HorizonSteps + 1), settings.HorizonSteps + 1),
                     Write(data.Scores, candidate, 1)], previous);
            }
            previous = _scheduler.Schedule(new NavigationMppiImprove<TSpace, TModel>(data),
                [JobResourceAccess.Write(_scheduler.GetResourceToken(data)), Read(data.Noise), Read(data.Scores),
                 Read(data.CandidateControls), Read(data.CandidateMotions), Read(data.Reference), Read(data.ReferenceSpeeds), Read(_neighbors),
                 Write(data.Controls, 0, settings.HorizonSteps), Write(data.UpdatedControls, 0, settings.HorizonSteps),
                 Write(data.SelectedControls, 0, settings.HorizonSteps), Write(data.SelectedMotions, 0, settings.HorizonSteps + 1),
                 Write(data.Weights, 0, settings.CandidateCount)], _scheduler.CombineDependencies(handles));
        }
    }

    private JobResourceAccess Read(object value) => JobResourceAccess.Read(_scheduler.GetResourceToken(value));
    private JobResourceAccess Write(object value, int start, int length) => JobResourceAccess.Write(_scheduler.GetResourceToken(value), start, length);
}

internal readonly struct NavigationMppiCandidate<TSpace, TModel>(NavigationMppiData<TSpace> data, int candidate, int iteration) : IJob
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
{
    public void Execute() => NavigationMppiSampling<TSpace, TModel>.Candidate(data, candidate, iteration);
}

internal readonly struct NavigationMppiImprove<TSpace, TModel>(NavigationMppiData<TSpace> data) : IJob
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
{
    public void Execute() => NavigationMppiSampling<TSpace, TModel>.Improve(data);
}

internal partial struct NavigationMppiOutput<TSpace, TModel> : IJobEntity
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
{
    public void Execute([JobResources(typeof(NavigationMppiResources<,>))] in NavigationMppi<TSpace, TModel> mppi,
        in NavigationTrajectorySource source, ref NavigationSteering steering, ref NavigationMppiResult result,
        DynamicBuffer<NavigationTrajectoryPoint> trajectory)
    {
        NavigationMppiData<TSpace> data = mppi.Data;
        steering = data.Steering;
        result = new(!data.Frame.Guidance.Active ? NavigationMppiStatus.Idle : data.Valid ? NavigationMppiStatus.Ready : NavigationMppiStatus.NoFeasibleTrajectory,
            data.SelectedCost, data.Frame.Guidance.RequestVersion);
        if (source.Kind != NavigationTrajectoryKind.Mppi) return;
        trajectory.Clear();
        for (int sample = 0; sample < data.SelectedCount; sample++)
        {
            NavigationMotion point = data.SelectedMotions[sample];
            trajectory.Add(new(data.Frame.Time + sample * mppi.Settings.TimeStep, point.Position, point.Velocity, point.Heading));
        }
    }
}

internal partial struct NavigationMppiStop<TSpace, TModel, TMotion, TReader>(float time) : IJobEntity
    where TSpace : class, INavigationSpace<TSpace>
    where TModel : struct, INavigationMppiModel<TSpace>
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly float _time = time;
    public void Execute([JobResources(typeof(NavigationMppiResources<,>))] in NavigationMppi<TSpace, TModel> mppi,
        in NavigationTrajectorySource source, in TMotion movement, in NavigationGuidance guidance,
        ref NavigationSteering steering, ref NavigationMppiResult result, DynamicBuffer<NavigationTrajectoryPoint> trajectory)
    {
        mppi.Data.Initialized = false;
        NavigationMotion motion = TReader.Read(in movement);
        steering = new(default, motion.Heading, guidance.RequestVersion, false);
        result = new(NavigationMppiStatus.Idle, 0, guidance.RequestVersion);
        if (source.Kind != NavigationTrajectoryKind.Mppi) return;
        trajectory.Clear();
        trajectory.Add(new(_time, motion.Position, motion.Velocity, motion.Heading));
    }
}
