using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Views;

public sealed partial class ViewRebuildSystem
{
    private readonly List<DependencyVersion> _versions = [];
    private readonly List<ViewDependencies> _viewDependencies = [];
    private ViewChanges[] _viewResults = [];
    private readonly List<ViewParent> _viewParents = [];
    private readonly Dictionary<Entity, int> _parentIndices = [];
    private readonly List<int> _parentLinks = [];

    private void CollectChangedViews(World world)
    {
        _views.Clear();
        _changedViews.Clear();
        List<Entity> views = _views;
        world.ExecuteQuery(_viewQuery, ref views, static (rows, ref viewEntities) =>
        {
            foreach (var row in rows.Rows) viewEntities.Add(row.Entity);
        });
        if (_views.Count == 0) return;

        RefreshDependencies(world);
        _dependencyChanges.Record(world, _frame);
        _versions.Clear();
        _viewDependencies.Clear();
        foreach (Entity entity in _views)
        {
            int start = _versions.Count;
            bool requested = world.Has<BuildRequest>(entity) || world.Read<ViewType>(entity).RebuildRequired ||
                _builds.HasParameterChanges(world, entity);
            if (!requested)
            {
                _dependencyChanges.CopyVersions(world, entity, _versions);
                if (world.HasSparse<Computations>(entity))
                {
                    CollectComputations(world, entity);
                    foreach (Computation computation in _computations)
                    {
                        if (computation.Released || computation.EvaluatesOnRead) continue;
                        computation.ObjectReads.CopyVersions(_versions, _frame);
                        _dependencyChanges.CopyVersions(world, entity,
                            CollectionsMarshal.AsSpan(computation.Reads), _versions);
                    }
                    _computations.Clear();
                }
            }
            _viewDependencies.Add(new(start, _versions.Count - start, requested));
        }
        if (_viewResults.Length < _views.Count) Array.Resize(ref _viewResults, _views.Count);
        WorldJobs.TryGetScheduler(world, out Scheduler? scheduler);
        ExecuteParallel(scheduler, new ViewChangesJob(this), _views.Count);

        _viewParents.Clear();
        _parentIndices.Clear();
        _parentLinks.Clear();
        for (int index = 0; index < _views.Count; index++)
            if (_viewResults[index].Changed)
                _viewParents.Add(new(index, CopyParentLinks(world,
                    Hierarchy<ElementTree>.GetParent(world, _views[index]))));
        if (_viewParents.Count == 0) return;
        ExecuteParallel(scheduler, new ViewDepthJob(this), _viewParents.Count);
        foreach (ViewParent parent in _viewParents)
            _changedViews.Add(new(_views[parent.ViewIndex], _viewResults[parent.ViewIndex].Depth));
        _changedViews.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));
    }

    private static void ExecuteParallel<T>(Scheduler? scheduler, in T job, int length)
        where T : struct, IJobParallelFor
    {
        if (scheduler is not null)
            scheduler.ExecuteParallel(job, length, JobScheduleOptions.AutomaticBatchSize);
        else
            global::SomeEngine.Job.JobSystem.ExecuteParallel(job, length, JobScheduleOptions.AutomaticBatchSize);
    }

    private void RefreshDependencies(World world)
    {
        foreach (Entity entity in _views)
        {
            if (!world.Has<ObjectDependencies>(entity) && !world.HasSparse<Computations>(entity)) continue;
            if (world.Has<BuildRequest>(entity) || world.Read<ViewType>(entity).RebuildRequired ||
                _builds.HasParameterChanges(world, entity)) continue;
            if (world.Has<ObjectDependencies>(entity))
                world.Read<ObjectDependencies>(entity).Reads.Refresh(_frame);
            if (!world.HasSparse<Computations>(entity)) continue;
            CollectComputations(world, entity);
            foreach (Computation computation in _computations)
                if (!computation.Released && !computation.EvaluatesOnRead)
                    computation.ObjectReads.Refresh(_frame);
            _computations.Clear();
        }
    }

    private int CopyParentLinks(World world, Entity entity)
    {
        int first = -1;
        int previous = -1;
        while (entity != Entity.Null)
        {
            if (_parentIndices.TryGetValue(entity, out int existing))
            {
                if (previous >= 0) _parentLinks[previous] = existing;
                return first >= 0 ? first : existing;
            }
            int current = _parentLinks.Count;
            _parentIndices.Add(entity, current);
            _parentLinks.Add(-1);
            if (previous >= 0) _parentLinks[previous] = current;
            else first = current;
            previous = current;
            entity = Hierarchy<ElementTree>.GetParent(world, entity);
        }
        return first;
    }

    private readonly record struct ViewDependencies(int Start, int Count, bool BuildRequested);
    private readonly record struct ViewParent(int ViewIndex, int ParentIndex);
    private struct ViewChanges
    {
        internal bool Changed;
        internal int Depth;
    }

    private readonly struct ViewChangesJob(ViewRebuildSystem system) : IJobParallelFor
    {
        public void Execute(int index)
        {
            ViewDependencies dependencies = system._viewDependencies[index];
            bool changed = dependencies.BuildRequested;
            for (int read = dependencies.Start; !changed && read < dependencies.Start + dependencies.Count; read++)
                changed = system._versions[read].HasChanges;
            system._viewResults[index] = new ViewChanges { Changed = changed };
        }
    }

    private readonly struct ViewDepthJob(ViewRebuildSystem system) : IJobParallelFor
    {
        public void Execute(int index)
        {
            ViewParent view = system._viewParents[index];
            int depth = 0;
            for (int parent = view.ParentIndex; parent >= 0; parent = system._parentLinks[parent]) depth++;
            system._viewResults[view.ViewIndex].Depth = depth;
        }
    }
}
