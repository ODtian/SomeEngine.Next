using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

internal static class NavigationTargets<TMotion, TReader>
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    public static bool TryResolve(World world, in NavigationGoal goal, out Vector3 position)
    {
        position = goal.Position;
        if (!goal.IsEntityTarget)
            return true;
        if (!world.IsAlive(goal.Target) || !world.Has<TMotion>(goal.Target))
            return false;
        TMotion target = world.Read<TMotion>(goal.Target);
        position += TReader.Read(in target).Position;
        return true;
    }
}

internal partial struct NavigationPathJob<TSpace, TSearch, TState, TMotion, TReader>(World world, int expansionBudget) : IJobEntity
    where TSpace : class, INavigationSpace<TSpace>
    where TSearch : struct, INavigationSearch<TSpace, TState>
    where TState : class
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world = world;
    private readonly int _expansionBudget = expansionBudget;

    public void Execute(
        [JobResources<NavigationAgentResources>] in NavigationAgent agent,
        [JobResources(typeof(PathNavigationResources<>))] in PathNavigation<TSpace> navigation,
        in NavigationGoal goal,
        in TMotion movement,
        [JobResources(typeof(NavigationRequestResources<,>))] ref NavigationRequest<TSpace, TState> request,
        [JobResources<NavigationPathAssignment>] ref NavigationPathResult result)
    {
        NavigationRequestData<TSpace, TState> data = request.Data;
        bool available = NavigationTargets<TMotion, TReader>.TryResolve(_world, in goal, out Vector3 target);
        bool changed = !data.Active || data.TargetAvailable != available || data.Goal != goal ||
            data.TargetPosition != target || !ReferenceEquals(data.Space, navigation.Space) ||
            !ReferenceEquals(data.Profile, agent.Profile) || !ReferenceEquals(data.Policy, agent.Policy);
        if (changed)
        {
            data.Version++;
            data.Active = true;
            data.TargetAvailable = available;
            data.Goal = goal;
            data.TargetPosition = target;
            data.Space = navigation.Space;
            data.Profile = agent.Profile;
            data.Policy = agent.Policy;
            data.CornerIndex = 0;
            data.Search = null;
            if (available)
            {
                NavigationMotion motion = TReader.Read(in movement);
                var input = new NavigationSearchInput<TSpace>(navigation.Space, agent.Profile, agent.Policy,
                    motion.Position, target, goal.ArrivalRange, motion.Heading, goal.Heading, goal.HeadingTolerance);
                data.Search = new(TSearch.Create(in input), data.Version);
            }
        }

        if (data.Search is { } search)
        {
            NavigationMotion motion = TReader.Read(in movement);
            var input = new NavigationSearchInput<TSpace>(navigation.Space, agent.Profile, agent.Policy,
                motion.Position, target, goal.ArrivalRange, motion.Heading, goal.Heading, goal.HeadingTolerance);
            bool resumed = TSearch.Update(search.State, in input);
            if (search.Status == NavigationSearchStatus.Searching || resumed)
            {
                NavigationSearchStep step = TSearch.Advance(search.State, _expansionBudget);
                search.Status = step.Status;
                search.Path = step.Path;
                if (step.ResetPathProgress) data.CornerIndex = 0;
            }
            result = new(search.Path, data.Version, search.Status, target, true);
        }
        else
        {
            result = new(null, data.Version, NavigationSearchStatus.Searching, target, false);
        }
    }
}

internal partial struct NavigationStopJob<TSpace, TState> : IJobEntity
    where TSpace : class, INavigationSpace<TSpace>
    where TState : class
{
    public void Execute(
        [JobResources(typeof(NavigationRequestResources<,>))] ref NavigationRequest<TSpace, TState> request,
        [JobResources<NavigationPathAssignment>] ref NavigationPathResult path,
        ref NavigationGuidance guidance, ref NavigationSteering steering, ref NavigationProgress progress)
    {
        NavigationRequestData<TSpace, TState> data = request.Data;
        if (data.Active)
        {
            data.Version++;
            data.Active = false;
            data.Search = null;
        }
        path = new(null, data.Version, NavigationSearchStatus.Searching, default, false);
        guidance = new(default, 0, data.Version, false);
        steering = new(default, 0, data.Version, false);
        progress = new(NavigationStatus.Idle, 0, 0, data.Version);
    }
}
