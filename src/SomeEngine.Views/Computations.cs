using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

internal readonly record struct Computations(ComputationList List) : ISparseComponent;

/// <summary>The view's calculations in creation order, with their creating calculation.</summary>
internal sealed class ComputationList
{
    internal List<Computation> Items { get; } = new();

    internal ComputationList? TakeAsyncChildren(Computation parent)
    {
        ComputationList? previous = null;
        for (int index = 0; index < Items.Count;)
        {
            Computation computation = Items[index];
            bool retain = computation.Parent == parent && computation.Async is not null;
            if (!retain && previous is not null)
                foreach (Computation ancestor in previous.Items)
                    if (computation.IsChildOf(ancestor))
                    {
                        retain = true;
                        break;
                    }
            if (retain)
            {
                (previous ??= new()).Items.Add(computation);
                Items.RemoveAt(index);
            }
            else
                index++;
        }
        return previous;
    }

    internal void ReleaseChildren(Computation? parent = null, ComputationList? retained = null)
    {
        for (int index = Items.Count - 1; index >= 0; index--)
        {
            Computation computation = Items[index];
            if (parent is not null && !computation.IsChildOf(parent))
                continue;
            if (retained is null || !retained.Items.Contains(computation))
                computation.Release();
            Items.RemoveAt(index);
        }
    }
}

/// <summary>A view's callback and the data read during its latest evaluation.</summary>
internal sealed class Computation(World world, Entity entity, Computation? parent,
    Action? run, ulong frame, bool evaluatesOnRead)
{
    private Action? _run = run;
    private Computation? _parent = parent;
    private Exception? _error;
    private bool _released;

    internal World World { get; } = world;
    internal Entity Entity { get; } = entity;
    internal ulong Frame { get; private set; } = frame;
    internal bool EvaluatesOnRead { get; } = evaluatesOnRead;
    internal bool Released => _released;
    internal Computation? Parent => _parent;
    internal AsyncComputation? Async;
    internal Exception? Error => _error;
    internal List<Dependency> Reads { get; } = new();
    internal ObjectDependencyList ObjectReads { get; } = new(frame);
    internal Scope.ScopeData? ScopeData;

    internal void Execute(ulong frame)
    {
        BuildQueue.For(World).CountComputation();
        RebuildDiagnostics.Record(this, frame);
        Frame = frame;
        ComputationList calculations = World.ReadSparse<Computations>(Entity).List;
        ComputationList? previous = Async is null ? calculations.TakeAsyncChildren(this) : null;
        calculations.ReleaseChildren(this);
        Reads.Clear();
        ObjectReads.Clear(frame);
        using var scope = Scope.Enter(this, frame);
        ScopeData!.PreviousComputations = previous;
        try
        {
            if (Async is { } asynchronous)
                scope.Finish(asynchronous.Run(Scope.Cancel), null, 0);
            else
            {
                _run!();
                scope.Complete();
            }
            Exception? previousError = _error;
            _error = null;
            RestoreError(previousError);
        }
        catch (Exception exception)
        {
            _error = exception;
            throw;
        }
        finally
        {
            previous?.ReleaseChildren(retained: calculations);
        }
    }

    private void RestoreError(Exception? previousError)
    {
        if (previousError is null || !World.HasSparse<BuildError>(Entity) ||
            !ReferenceEquals(World.ReadSparse<BuildError>(Entity).Exception, previousError))
            return;
        Computation? failed = null;
        foreach (Computation computation in World.ReadSparse<Computations>(Entity).List.Items)
            if (computation.Error is not null && (failed is null || computation.Frame >= failed.Frame))
                failed = computation;
        if (failed is null)
            World.RemoveSparse<BuildError>(Entity);
        else
            World.ReplaceSparse(Entity, new BuildError { Exception = failed.Error!, Frame = failed.Frame });
    }

    internal void Rebuild(ulong frame)
    {
        try
        {
            Execute(frame);
        }
        catch (Exception exception)
        {
            BuildErrors.Record(World, Entity, exception, frame);
        }
    }

    internal bool IsChildOf(Computation parent)
    {
        for (Computation? ancestor = _parent; ancestor is not null; ancestor = ancestor._parent)
            if (ReferenceEquals(ancestor, parent))
                return true;
        return false;
    }

    internal void Release()
    {
        if (ScopeData is { Suspended: true } data)
            data.Queue.Cancel(data, restore: false);
        Reads.Clear();
        ObjectReads.Clear(Frame);
        _run = null;
        _parent = null;
        _error = null;
        _released = true;
        Async = null;
        ScopeData = null;
    }

    internal bool HasChanges(DependencyChanges changes, bool recheck = false)
    {
        if (ObjectReads.HasChanges(changes.Frame))
            return true;
        foreach (Dependency read in Reads)
            if (changes.HasChanges(World, Entity, read, recheck))
                return true;
        return false;
    }

    internal bool HasChanges(ulong frame)
    {
        if (ObjectReads.HasChanges(frame))
            return true;
        foreach (Dependency read in Reads)
            if (DependencyChanges.HasChanges(World, Entity, read))
                return true;
        return false;
    }

    internal void Add(Dependency read)
    {
        foreach (Dependency existing in Reads)
            if (existing.HasSameSource(in read))
                return;
        Reads.Add(read);
    }
}
