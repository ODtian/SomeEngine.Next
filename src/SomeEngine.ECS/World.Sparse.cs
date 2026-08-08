using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>Adds a sparse component without changing archetype identity.</summary>
    public void AddSparse<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        using WorldJobAdmissionScope admission = EnterJobSparse<T>(WorldStorageAccess.Write);
        _sparse.Add(entity, value);
    }

    /// <summary>Replaces an existing sparse component without changing archetype identity.</summary>
    public void ReplaceSparse<T>(Entity entity, in T value)
        where T : struct, ISparseComponent
    {
        using WorldJobAdmissionScope admission = EnterJobSparse<T>(WorldStorageAccess.Write);
        _sparse.Replace(entity, value);
    }

    /// <summary>Removes a sparse component.</summary>
    public void RemoveSparse<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        using WorldJobAdmissionScope admission = EnterJobSparse<T>(WorldStorageAccess.Write);
        _sparse.Remove<T>(entity);
    }

    /// <summary>Reads a sparse component by value.</summary>
    public T ReadSparse<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        using WorldJobAdmissionScope admission = EnterJobSparse<T>(WorldStorageAccess.Read);
        return _sparse.Read<T>(entity);
    }

    /// <summary>Checks whether an entity has a sparse component.</summary>
    public bool HasSparse<T>(Entity entity)
        where T : struct, ISparseComponent
    {
        using WorldJobAdmissionScope admission = EnterJobSparse<T>(WorldStorageAccess.Read);
        return _sparse.Has<T>(entity);
    }

    /// <summary>
    /// Borrows the compact entity and value arrays for exactly the duration of
    /// <paramref name="execution"/>.
    /// </summary>
    public void ExecuteSparseRead<T>(SparseReadExecution<T> execution)
        where T : struct, ISparseComponent
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobSparse<T>(WorldStorageAccess.Read);
        BeginStorageBorrow();
        try
        {
            if (_sparse.TrySet<T>(out SparseSet<T> sparseSet))
                execution(sparseSet.DenseEntities, sparseSet.DenseData);
            else
                execution(ReadOnlySpan<Entity>.Empty, ReadOnlySpan<T>.Empty);
        }
        finally
        {
            EndStorageBorrow();
        }
    }

    /// <summary>
    /// Borrows compact sparse arrays with caller-owned state passed by reference, allowing a
    /// static callback on allocation-sensitive paths.
    /// </summary>
    public void ExecuteSparseRead<T, TState>(
        ref TState state,
        SparseReadExecution<T, TState> execution)
        where T : struct, ISparseComponent
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobSparse<T>(WorldStorageAccess.Read);
        BeginStorageBorrow();
        try
        {
            if (_sparse.TrySet<T>(out SparseSet<T> sparseSet))
                execution(sparseSet.DenseEntities, sparseSet.DenseData, ref state);
            else
                execution(ReadOnlySpan<Entity>.Empty, ReadOnlySpan<T>.Empty, ref state);
        }
        finally
        {
            EndStorageBorrow();
        }
    }

    /// <summary>
    /// Borrows compact sparse arrays for mutation. Value writes are intentionally not rolled back
    /// if the callback faults, matching writable table-query semantics.
    /// </summary>
    public void ExecuteSparseWrite<T>(SparseWriteExecution<T> execution)
        where T : struct, ISparseComponent
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobSparse<T>(WorldStorageAccess.Write);
        BeginStorageBorrow();
        try
        {
            if (_sparse.TrySet<T>(out SparseSet<T> sparseSet))
            {
                execution(sparseSet.DenseEntities, sparseSet.BorrowDenseWrite());
            }
            else
            {
                execution(ReadOnlySpan<Entity>.Empty, Span<T>.Empty);
            }
        }
        finally
        {
            EndStorageBorrow();
        }
    }

    /// <summary>
    /// Borrows writable compact sparse arrays with caller-owned state passed by reference.
    /// See <see cref="ExecuteSparseWrite{T}(SparseWriteExecution{T})"/> for fault semantics.
    /// </summary>
    public void ExecuteSparseWrite<T, TState>(
        ref TState state,
        SparseWriteExecution<T, TState> execution)
        where T : struct, ISparseComponent
    {
        ArgumentNullException.ThrowIfNull(execution);
        using WorldJobAdmissionScope admission = EnterJobSparse<T>(WorldStorageAccess.Write);
        BeginStorageBorrow();
        try
        {
            if (_sparse.TrySet<T>(out SparseSet<T> sparseSet))
            {
                execution(sparseSet.DenseEntities, sparseSet.BorrowDenseWrite(), ref state);
            }
            else
            {
                execution(ReadOnlySpan<Entity>.Empty, Span<T>.Empty, ref state);
            }
        }
        finally
        {
            EndStorageBorrow();
        }
    }
}
