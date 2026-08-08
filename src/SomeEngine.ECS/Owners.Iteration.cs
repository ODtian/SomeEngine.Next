using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed class Iteration
{
    private int _depth;
    private int _relationshipWriteOwnerDepth;
    private int _ownerThreadId;
    private int _queryBorrowCount;
    private int _storageBorrowCount;
    private bool _rollbackRequested;

    internal bool Active => _depth > 0;

    internal bool HasOwner =>
        Volatile.Read(ref _ownerThreadId) != 0 ||
        Volatile.Read(ref _queryBorrowCount) != 0 ||
        Volatile.Read(ref _storageBorrowCount) != 0;

    internal bool HasRelationshipWriteOwner =>
        _relationshipWriteOwnerDepth > 0 &&
        Volatile.Read(ref _ownerThreadId) == Environment.CurrentManagedThreadId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Begin()
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        int ownerThreadId = Volatile.Read(ref _ownerThreadId);
        if (ownerThreadId == 0)
        {
            ownerThreadId = Interlocked.CompareExchange(
                ref _ownerThreadId,
                currentThreadId,
                comparand: 0);
            if (ownerThreadId == 0)
                ownerThreadId = currentThreadId;
        }
        if (ownerThreadId != currentThreadId)
        {
            throw new InvalidOperationException(
                "World query ownership is already held by another thread.");
        }

        if (_depth == 0 &&
            (Volatile.Read(ref _queryBorrowCount) != 0 ||
             Volatile.Read(ref _storageBorrowCount) != 0))
        {
            Volatile.Write(ref _ownerThreadId, 0);
            throw new InvalidOperationException(
                "World storage is already borrowed by another runtime-owned callback.");
        }

        if (_depth == 0)
            _rollbackRequested = false;
        _depth++;
        _relationshipWriteOwnerDepth++;
    }

    /// <summary>
    /// Acquires the ordinary-query lease. Query storage conflicts are governed by the Job
    /// component-resource frontier, so unrelated queries may borrow the same World concurrently.
    /// Relationship writers retain the exclusive, rollback-capable owner and may not coexist with
    /// this lease, including a same-thread nested ordinary query.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BeginQueryBorrow()
    {
        if (Volatile.Read(ref _ownerThreadId) != 0)
        {
            throw new InvalidOperationException(
                "World relationship query ownership is already held by another runtime-owned callback.");
        }

        Interlocked.Increment(ref _queryBorrowCount);
        if (Volatile.Read(ref _ownerThreadId) == 0)
            return;

        Interlocked.Decrement(ref _queryBorrowCount);
        throw new InvalidOperationException(
            "World relationship query ownership is already held by another runtime-owned callback.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EndQueryBorrow()
    {
        int remaining = Interlocked.Decrement(ref _queryBorrowCount);
        if (remaining < 0)
        {
            Interlocked.Increment(ref _queryBorrowCount);
            throw new InvalidOperationException("World query borrow depth underflow.");
        }
    }

    /// <summary>
    /// Acquires a non-structural storage borrow. Multiple callbacks may coexist; Job/storage
    /// resources decide which logical components can overlap. An exclusive query owner may nest
    /// a storage borrow only on its own thread.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BeginStorageBorrow()
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        int ownerThreadId = Volatile.Read(ref _ownerThreadId);
        if (ownerThreadId != 0 && ownerThreadId != currentThreadId)
        {
            throw new InvalidOperationException(
                "World query ownership is already held by another thread.");
        }

        Interlocked.Increment(ref _storageBorrowCount);
        ownerThreadId = Volatile.Read(ref _ownerThreadId);
        if (ownerThreadId == 0 || ownerThreadId == currentThreadId)
            return;

        Interlocked.Decrement(ref _storageBorrowCount);
        throw new InvalidOperationException(
            "World query ownership is already held by another thread.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EndStorageBorrow()
    {
        int remaining = Interlocked.Decrement(ref _storageBorrowCount);
        if (remaining < 0)
        {
            Interlocked.Increment(ref _storageBorrowCount);
            throw new InvalidOperationException("World storage borrow depth underflow.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool End(bool completed)
    {
        if (_depth <= 0)
            throw new InvalidOperationException("Iteration owner depth underflow.");
        if (Volatile.Read(ref _ownerThreadId) != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Only the owning thread can release World query ownership.");
        if (_relationshipWriteOwnerDepth <= 0)
            throw new InvalidOperationException("Relationship write owner depth underflow.");
        _relationshipWriteOwnerDepth--;
        if (!completed)
            _rollbackRequested = true;
        _depth--;
        return _depth == 0 && _rollbackRequested;
    }

    internal void ReleaseOwner()
    {
        if (_depth != 0)
            throw new InvalidOperationException("Cannot release World query ownership while nested scopes remain.");
        if (Volatile.Read(ref _ownerThreadId) != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Only the owning thread can release World query ownership.");
        Volatile.Write(ref _ownerThreadId, 0);
    }

    internal void Throw()
    {
        if (HasOwner)
            throw new InvalidOperationException(
                "Cannot perform structural changes during iteration. Use CommandBuffer.");
    }
}


