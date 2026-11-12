using SomeEngine.ECS.Systems;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>Runs requested view builds and changed calculations in the World's interface phase.</summary>
public sealed partial class ViewRebuildSystem : ISystem<ImmediateSystemContext>
{
    private readonly List<Entity> _views = new();
    private readonly List<ViewDepth> _changedViews = new();
    private readonly List<Computation> _computations = new();
    private readonly DependencyChanges _dependencyChanges = new();
    private Query _viewQuery;
    private ulong _frame;
    private int _iterationLimit = 8;
    private BuildQueue _builds = null!;

    /// <summary>View Build entries, including synchronous children, during the latest rebuild phase.</summary>
    public int BuildCount => _builds is null ? 0 : _builds.BuildCount;

    /// <summary>Reactive calculation entries during the latest rebuild phase.</summary>
    public int ComputationCount => _builds is null ? 0 : _builds.ComputationCount;

    /// <summary>The maximum number of rebuild rounds in one interface frame.</summary>
    public int IterationLimit
    {
        get => _iterationLimit;
        set
        {
            DevelopmentCheck.Require(value > 0, "The view rebuild iteration limit requires a positive value.");
            _iterationLimit = value;
        }
    }

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _builds = BuildQueue.For(context.World);
        _builds.Open(context.World);
        _dependencyChanges.Clear();
        _viewQuery = context.World.Query(new QueryDescription().WithStored<ViewType>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _builds.BeginFrame();
        RebuildDiagnostics.BeginFrame(context.World);
        _frame++;
#if DEBUG
        using var rebuild = Scope.EnterRebuild();
#endif
        World world = context.World;
        _builds.ReleaseDestroyed(world);
        using (WorldStructureWriteScope structure = world.OpenStructureWrite()) Update(world);
        _builds.ReleaseDestroyed(world);
    }

    private void Update(World world)
    {
        _dependencyChanges.Record(world, _frame);
        _builds.CancelChanged(world, _dependencyChanges);
        _builds.Resume(world, _frame);
        for (int iteration = 0; iteration < IterationLimit; iteration++)
        {
            CollectChangedViews(world);
            if (_changedViews.Count == 0)
                return;

            foreach (ViewDepth view in _changedViews)
            {
                Entity entity = view.Entity;
                if (!world.IsAlive(entity) || !world.Has<ViewType>(entity))
                    continue;
                if (world.Has<BuildRequest>(entity) || HasBuildChanges(world, entity, recheck: true))
                    ViewTypes.Build(world, entity, _frame);
                if (world.IsAlive(entity) && world.Has<ViewType>(entity) && world.HasSparse<Computations>(entity))
                    RebuildComputations(world, entity);
            }
        }
        CollectChangedViews(world);
        if (_changedViews.Count > 0)
            DevelopmentCheck.Fail($"View calculations exceeded the per-frame iteration limit of {IterationLimit}; " +
                $"view {_changedViews[0].Entity} still has changed dependencies or parameters.");
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        using WorldStructureWriteScope structure = context.World.OpenStructureWrite();
        _builds.Close(context.World, _frame);
    }

    private bool HasBuildChanges(World world, Entity entity, bool recheck = false) =>
        world.Read<ViewType>(entity).RebuildRequired || _builds.HasParameterChanges(world, entity) ||
        _dependencyChanges.HasChanges(world, entity, recheck);

    private void CollectComputations(World world, Entity entity)
    {
        _computations.Clear();
        _computations.AddRange(world.ReadSparse<Computations>(entity).List.Items);
    }

    private void RebuildComputations(World world, Entity entity)
    {
        CollectComputations(world, entity);
        foreach (Computation computation in _computations)
        {
            if (!world.IsAlive(entity) || !world.Has<ViewType>(entity))
                break;
            if (!computation.Released && !computation.EvaluatesOnRead &&
                computation.HasChanges(_dependencyChanges, recheck: true))
                computation.Rebuild(_frame);
        }
        _computations.Clear();
    }

    private readonly record struct ViewDepth(Entity Entity, int Depth);
}
