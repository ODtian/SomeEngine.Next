using System.Runtime.CompilerServices;
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
    private List<BuildStateMachine> _ready = new();
    private List<BuildStateMachine> _resuming = new();
    private List<Action> _nextFrame = new();
    private List<Action> _frameCallbacks = new();
    private readonly List<BuildDepth> _depths = new();
    private bool _closed;

    internal static BuildQueue For(World world) => s_worlds.GetValue(world, static _ => new());

    internal bool Contains(Entity entity) => _builds.ContainsKey(entity);
    internal void Add(Scope.ScopeData data) => _builds.Add(data.Entity, data);

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

    internal void Resume(World world)
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
            if (!world.IsAlive(entity))
            {
                _builds.Remove(entity);
                continue;
            }
            int depth = 0;
            Entity ancestor = Hierarchy<ElementTree>.GetParent(world, entity);
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
                if (!world.IsAlive(build.Entity))
                {
                    _builds.Remove(build.Entity);
                    continue;
                }
                if (!_builds.ContainsKey(build.Entity))
                    continue;
                bool completed = false;
                try
                {
                    using var scope = Scope.Resume(data);
                    stateMachine.Resume();
                    if (view.Task.IsCompleted)
                    {
                        completed = true;
                        view.Task.Complete();
                        Scope.CompleteBuild(view);
                    }
                }
                finally
                {
                    stateMachine.NotifyCompletion();
                    if (completed)
                    {
                        _builds.Remove(build.Entity);
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

    internal void Close(World world)
    {
        lock (_gate)
        {
            _closed = true;
            _ready.Clear();
            _nextFrame.Clear();
        }
        _resuming.Clear();
        _frameCallbacks.Clear();
        _depths.Clear();
        _builds.Clear();
        s_worlds.Remove(world);
    }

    private readonly record struct BuildDepth(BuildStateMachine StateMachine, Entity Entity, int Depth);
}
