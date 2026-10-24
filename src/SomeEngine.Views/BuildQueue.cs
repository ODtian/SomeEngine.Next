using System.Runtime.CompilerServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views;

/// <summary>The suspended builds and ready resumptions belonging to one World.</summary>
internal sealed class BuildQueue
{
    private static readonly ConditionalWeakTable<World, BuildQueue> s_worlds = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<Entity, Scope.ScopeData> _builds = new();
    private readonly HashSet<Scope.ScopeData> _computations = new();
    private List<BuildStateMachine> _ready = new();
    private List<BuildStateMachine> _resuming = new();
    private List<Action> _nextFrame = new();
    private List<Action> _frameCallbacks = new();
    private readonly List<BuildDepth> _depths = new();
    private readonly List<Scope.ScopeData> _changedBuilds = new();
    private bool _closed;
    internal int BuildCount { get; private set; }
    internal int ComputationCount { get; private set; }

    internal void BeginFrame() => (BuildCount, ComputationCount) = (0, 0);
    internal void CountBuild() => BuildCount++;
    internal void CountComputation() => ComputationCount++;

    internal static BuildQueue For(World world) => s_worlds.GetValue(world, static world =>
    {
        var queue = new BuildQueue();
        world.EntityDestroying += (_, entity) => queue.Release(world, entity);
        return queue;
    });

    internal void Add(Scope.ScopeData data)
    {
        if (data.Computation is null)
            _builds.Add(data.Entity, data);
        else
            _computations.Add(data);
    }

    internal void Open()
    {
        lock (_gate)
            _closed = false;
    }

    internal bool HasParameterChanges(World world, Entity entity) =>
        _builds.TryGetValue(entity, out Scope.ScopeData? data)
            ? data.ParameterVersion is uint version &&
                world.ComponentDataWriteVersion(entity, data.ParameterComponent) != version
            : ViewTypes.HasParameterChanges(world, entity);

    internal void Cancel(Entity entity, bool restore)
    {
        if (_builds.Remove(entity, out Scope.ScopeData? data))
            Scope.CancelBuild(data, restore);
    }

    internal void Cancel(Scope.ScopeData data, bool restore)
    {
        if (_computations.Remove(data))
            Scope.CancelBuild(data, restore);
        else if (_builds.TryGetValue(data.Entity, out Scope.ScopeData? current) && current == data)
            Cancel(data.Entity, restore);
    }

    private void Release(World world, Entity entity)
    {
        Cancel(entity, restore: false);
        if (world.HasSparse<Computations>(entity))
            world.ReadSparse<Computations>(entity).List.ReleaseChildren();
        while (true)
        {
            Scope.ScopeData? next = null;
            foreach (Scope.ScopeData data in _computations)
                if (data.Entity == entity)
                {
                    next = data;
                    break;
                }
            if (next is null)
                return;
            Cancel(next, restore: false);
        }
    }

    internal void CancelChanged(World world, DependencyChanges changes)
    {
        foreach (Scope.ScopeData data in _builds.Values)
            if (!world.IsAlive(data.Entity) || world.Read<ViewType>(data.Entity).RebuildRequired ||
                HasParameterChanges(world, data.Entity) || changes.HasChanges(world, data.Entity, recheck: true))
                _changedBuilds.Add(data);
        foreach (Scope.ScopeData data in _changedBuilds)
        {
            data.Frame = changes.Frame;
            Cancel(data.Entity, restore: world.IsAlive(data.Entity));
        }
        _changedBuilds.Clear();
    }

    internal void Enqueue(BuildStateMachine stateMachine)
    {
        lock (_gate)
            if (!_closed)
                _ready.Add(stateMachine);
    }

    internal void NextFrame(Action continuation)
    {
        lock (_gate)
            if (!_closed)
                _nextFrame.Add(continuation);
    }

    internal void Resume(World world, ulong frame)
    {
        lock (_gate)
            (_nextFrame, _frameCallbacks) = (_frameCallbacks, _nextFrame);
        foreach (Action callback in _frameCallbacks)
            callback();
        _frameCallbacks.Clear();

        // Swap once at the start of the phase. Awaits reached during this phase target the next frame.
        lock (_gate)
            (_ready, _resuming) = (_resuming, _ready);

        foreach (BuildStateMachine stateMachine in _resuming)
        {
            Entity entity = stateMachine.Data.Entity;
            int depth = 0;
            Entity ancestor = world.IsAlive(entity) ? Hierarchy<ElementTree>.GetParent(world, entity) : Entity.Null;
            while (ancestor != Entity.Null)
            {
                depth++;
                ancestor = Hierarchy<ElementTree>.GetParent(world, ancestor);
            }
            _depths.Add(new BuildDepth(stateMachine, entity, depth));
        }
        _resuming.Clear();
        _depths.Sort(static (left, right) =>
        {
            int comparison = left.Depth.CompareTo(right.Depth);
            if (comparison == 0)
                comparison = left.Entity.Index.CompareTo(right.Entity.Index);
            if (comparison == 0)
                comparison = left.Entity.Generation.CompareTo(right.Entity.Generation);
            return comparison;
        });

        int nextBuild = 0;
        try
        {
            while (nextBuild < _depths.Count)
            {
                BuildDepth build = _depths[nextBuild++];
                BuildStateMachine stateMachine = build.StateMachine;
                Scope.ScopeData data = stateMachine.Data;
                Scope.ScopeData view = data.ViewData;
                if (!view.IsCanceled && !world.IsAlive(build.Entity))
                    Cancel(view, restore: false);
                bool active = view.Computation is null
                    ? _builds.TryGetValue(build.Entity, out Scope.ScopeData? currentBuild) && currentBuild == view
                    : _computations.Contains(view);
                if (!view.IsCanceled && !active)
                    continue;
                bool completed = false;
                try
                {
                    data.Frame = frame;
                    view.Frame = frame;
                    using var scope = Scope.Resume(data);
                    stateMachine.Resume();
                    if (view.Task.IsCompleted)
                    {
                        completed = true;
                        view.Task.Complete();
                        if (!view.IsCanceled)
                        {
                            Scope.CompleteBuild(view);
                            view.Computation?.Complete();
                        }
                    }
                    else if (!view.IsCanceled)
                        Scope.CommitWrites(view, submitAnimations: true);
                }
                catch (OperationCanceledException) when (view.IsCanceled)
                {
                    completed = true;
                }
                catch (Exception exception) when (view.IsCanceled)
                {
                    completed = true;
                    DevelopmentCheck.Report($"A canceled build on {build.Entity} failed during cleanup: {exception}");
                }
                catch (Exception exception)
                {
                    completed = true;
                    view.Computation?.Fail(exception, frame);
                    BuildErrors.Record(world, build.Entity, exception, frame);
                }
                finally
                {
                    stateMachine.NotifyCompletion();
                    if (completed)
                    {
                        Scope.FinishChildren(view);
                        if (!view.IsCanceled && view.ParameterVersion is uint version)
                        {
                            if (world.Has<ParameterVersion>(build.Entity))
                                world.Replace(build.Entity, new ParameterVersion(version));
                            else
                                world.Add(build.Entity, new ParameterVersion(version));
                        }
                        if (_builds.TryGetValue(build.Entity, out Scope.ScopeData? current) && current == view)
                            _builds.Remove(build.Entity);
                        _computations.Remove(view);
                        Scope.Release(view);
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
                while (nextBuild < _depths.Count)
                    _ready.Add(_depths[nextBuild++].StateMachine);
            _depths.Clear();
        }
    }

    internal void Close(World world, ulong frame)
    {
        _changedBuilds.AddRange(_builds.Values);
        _changedBuilds.AddRange(_computations);
        foreach (Scope.ScopeData data in _changedBuilds)
            Cancel(data, restore: false);
        _changedBuilds.Clear();
        while (true)
        {
            Resume(world, frame);
            lock (_gate)
            {
                if (_ready.Count > 0 || _nextFrame.Count > 0)
                    continue;
                _closed = true;
                break;
            }
        }
        _resuming.Clear();
        _frameCallbacks.Clear();
        _depths.Clear();
        _builds.Clear();
        _computations.Clear();
    }

    private readonly record struct BuildDepth(BuildStateMachine StateMachine, Entity Entity, int Depth);
}
