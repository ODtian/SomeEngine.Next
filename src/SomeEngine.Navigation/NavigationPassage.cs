using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public readonly record struct NavigationPassageUser(Entity Entity, ulong RequestId);

/// <summary>The connection's waiting order and current users, replaced together by its queue job.</summary>
public sealed class NavigationPassage(int capacity)
{
    private sealed record PassageData(int Capacity, ulong Version, NavigationPassageUser[] Waiting, NavigationPassageUser[] InUse);
    private PassageData _data = new(capacity, 0, [], []);

    public int Capacity => Volatile.Read(ref _data).Capacity;
    public ReadOnlyMemory<NavigationPassageUser> Waiting => Volatile.Read(ref _data).Waiting;
    public ReadOnlyMemory<NavigationPassageUser> InUse => Volatile.Read(ref _data).InUse;

    internal bool Contains(NavigationPassageUser user) => InUse.Span.Contains(user);

    internal void RestoreUser(NavigationPassageUser user, int capacity, ulong version)
    {
        PassageData previous = _data;
        Volatile.Write(ref _data, new(capacity, version, previous.Waiting, [.. previous.InUse, user]));
    }

    internal void SetCapacity(int capacity, ulong version)
    {
        PassageData previous = _data;
        Volatile.Write(ref _data, previous with { Capacity = capacity, Version = version });
    }

    internal void Update(NavigationPassageActor[] actors)
    {
        PassageData previous = _data;
        int capacity = previous.Capacity;
        ulong version = previous.Version;
        var current = new Dictionary<NavigationPassageUser, NavigationPassageActor>();
        foreach (NavigationPassageActor actor in actors)
        {
            current.Add(actor.User, actor);
            if (actor.Use.Version >= version)
            {
                capacity = actor.Use.Capacity;
                version = actor.Use.Version;
            }
        }
        var inUse = new List<NavigationPassageUser>();
        var waiting = new List<NavigationPassageUser>();
        foreach (NavigationPassageUser user in previous.InUse)
            if (current.Remove(user)) inUse.Add(user);
        foreach (NavigationPassageUser user in previous.Waiting)
            if (current.Remove(user)) waiting.Add(user);
        foreach (NavigationPassageUser user in current.Keys.OrderBy(user => user.Entity.Index)) waiting.Add(user);
        int granted = Math.Min(Math.Max(0, capacity - inUse.Count), waiting.Count);
        for (int index = 0; index < granted; index++) inUse.Add(waiting[index]);
        waiting.RemoveRange(0, granted);
        Volatile.Write(ref _data, new(capacity, version, waiting.ToArray(), inUse.ToArray()));
    }
}

internal readonly record struct NavigationPassageUse(NavigationPassage? Passage, int Capacity, ulong Version) : IComponent;
internal readonly record struct NavigationPassageActor(NavigationPassageUser User, NavigationPassageUse Use);

/// <summary>Shares one queue update across navigation combinations; separate connections run as independent jobs.</summary>
public sealed class NavigationPassageSystem(World world)
{
    private readonly Scheduler _scheduler = WorldJobs.GetScheduler(world);
    private readonly HashSet<NavigationPassage> _passages = [];
    private JobHandle _completion;

    public JobHandle Schedule(JobHandle traversalReady = default)
    {
        var actors = new List<NavigationPassageActor>();
        JobHandle collected = new CollectNavigationPassages(actors).Schedule(world,
            [JobResourceAccess.Write(_scheduler.GetResourceToken(actors))],
            _scheduler.CombineDependencies([_completion, traversalReady]));
        JobHandle updated = _scheduler.Schedule(new UpdatePassages(_scheduler, _passages, actors),
            [JobResourceAccess.Write(_scheduler.GetResourceToken(_passages)), JobResourceAccess.Read(_scheduler.GetResourceToken(actors))], collected);
        _completion = new ApplyNavigationPassageGrants().ScheduleParallel(world, updated);
        return _completion;
    }

    private readonly struct UpdatePassages(Scheduler scheduler, HashSet<NavigationPassage> known,
        List<NavigationPassageActor> actors) : IJob
    {
        public void Execute()
        {
            var groups = new Dictionary<NavigationPassage, List<NavigationPassageActor>>();
            foreach (NavigationPassage passage in known) groups.Add(passage, []);
            known.Clear();
            foreach (NavigationPassageActor actor in actors)
            {
                NavigationPassage passage = actor.Use.Passage!;
                if (!groups.TryGetValue(passage, out var group)) groups.Add(passage, group = []);
                group.Add(actor);
                known.Add(passage);
            }
            foreach (var group in groups)
                scheduler.Schedule(new UpdatePassage(group.Key, group.Value.ToArray()),
                    [JobResourceAccess.Write(scheduler.GetResourceToken(group.Key))]);
        }
    }

    private readonly struct UpdatePassage(NavigationPassage passage, NavigationPassageActor[] actors) : IJob
    {
        public void Execute() => passage.Update(actors);
    }
}

internal readonly struct NavigationPassageReference : IJobComponentResources<NavigationPassageUse>
{
    public static void Collect(in NavigationPassageUse component, ref JobResourceAccessBuilder resources) { }
}

internal readonly struct NavigationPassageRead : IJobComponentResources<NavigationPassageUse>
{
    public static void Collect(in NavigationPassageUse component, ref JobResourceAccessBuilder resources)
    {
        if (component.Passage is { } passage) resources.Read(passage);
    }
}

internal partial struct CollectNavigationPassages(List<NavigationPassageActor> actors) : IJobEntity
{
    private readonly List<NavigationPassageActor> _actors = actors;
    public void Execute(Entity entity, [JobResources<NavigationPassageReference>] in NavigationPassageUse use,
        in NavigationTraversalRequest request)
    {
        if (use.Passage is not null && request.Status is NavigationTraversalStatus.Waiting or
            NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
            _actors.Add(new(new(entity, request.RequestId), use));
    }
}

internal partial struct ApplyNavigationPassageGrants : IJobEntity
{
    public void Execute(Entity entity, [JobResources<NavigationPassageRead>] in NavigationPassageUse use,
        ref NavigationTraversalRequest request)
    {
        if (request.Status == NavigationTraversalStatus.Waiting && use.Passage!.Contains(new(entity, request.RequestId)))
            request = request with { Status = NavigationTraversalStatus.Requested };
    }
}
