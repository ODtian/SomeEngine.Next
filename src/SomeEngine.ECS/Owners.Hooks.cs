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

internal sealed class Hooks
{
    private World _world = null!;
    private object?[] _stores = new object?[8];
    private int _executionDepth;
    private int _executionThread;
    private long _executionEpoch;
    private long _nextExecutionEpoch;

    internal bool Any { get; private set; }

    internal void Clear()
    {
        if (Volatile.Read(ref _executionDepth) != 0)
            throw new InvalidOperationException("Component hooks are still executing.");
        _stores = new object?[8];
        Any = false;
    }

    internal void Bind(World world)
    {
        _world = world;
    }

    internal ComponentHooks<T> View<T>()
        where T : struct, IComponent
    {
        // Store creation resizes the registry and therefore belongs to the same topology-write
        // admission as callback binding. A propagation capture holding topology-read sees either
        // the complete old hook set or the complete new one.
        using WorldJobAdmissionScope admission = _world.EnterJobTopologyWrite();
        return new ComponentHooks<T>(Store<T>(), Mutate, Mark);
    }

    internal bool HasValueReplaceCallbacks(int componentId) =>
        Try(componentId, out IHookStore store) &&
        (store.HasReplaceCallbacks || store.HasInsertCallbacks);

    internal bool HasCreateCallbacks(int componentId) =>
        Try(componentId, out IHookStore store) &&
        (store.HasAddCallbacks || store.HasInsertCallbacks);

    internal bool Try<T>(out HookStore<T> store)
        where T : struct, IComponent
    {
        store = null!;
        int componentId = ComponentMetadata<T>.Id;
        if ((uint)componentId >= (uint)_stores.Length ||
            _stores[componentId] is not HookStore<T> existing)
        {
            return false;
        }

        store = existing;
        return true;
    }

    internal bool Try(int componentId, out IHookStore store)
    {
        store = null!;
        if ((uint)componentId >= (uint)_stores.Length ||
            _stores[componentId] is not IHookStore existing)
        {
            return false;
        }

        store = existing;
        return true;
    }

    internal void Add(int componentId, Entity entity, ref byte value)
    {
        if (Try(componentId, out var store) && store.HasAddCallbacks)
        {
            using HookExecutionScope execution = EnterExecution();
            store.RunAdd(new DeferredWorld(_world, execution.Token), entity, ref value);
        }
    }

    internal void Insert(int componentId, Entity entity, ref byte value)
    {
        if (Try(componentId, out var store) && store.HasInsertCallbacks)
        {
            using HookExecutionScope execution = EnterExecution();
            store.RunInsert(new DeferredWorld(_world, execution.Token), entity, ref value);
        }
    }

    internal void Replace(int componentId, Entity entity, ref byte value)
    {
        if (Try(componentId, out var store) && store.HasReplaceCallbacks)
        {
            using HookExecutionScope execution = EnterExecution();
            store.RunReplace(new DeferredWorld(_world, execution.Token), entity, ref value);
        }
    }

    internal void Remove(int componentId, Entity entity, ref byte value)
    {
        if (Try(componentId, out var store) && store.HasRemoveCallbacks)
        {
            using HookExecutionScope execution = EnterExecution();
            store.RunRemove(new DeferredWorld(_world, execution.Token), entity, ref value);
        }
    }

    internal void Despawn(int componentId, Entity entity, ref byte value)
    {
        if (Try(componentId, out var store) && store.HasDespawnCallbacks)
        {
            using HookExecutionScope execution = EnterExecution();
            store.RunDespawn(new DeferredWorld(_world, execution.Token), entity, ref value);
        }
    }

    internal void Insert<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (Try<T>(out var store) && store.HasInsertCallbacks)
        {
            using HookExecutionScope execution = EnterExecution();
            store.RunInsert(new DeferredWorld(_world, execution.Token), entity, in value);
        }
    }

    internal void Replace<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (Try<T>(out var store) && store.HasReplaceCallbacks)
        {
            using HookExecutionScope execution = EnterExecution();
            store.RunReplace(new DeferredWorld(_world, execution.Token), entity, in value);
        }
    }

    internal void Remove<T>(Entity entity, in T value)
        where T : struct, IComponent
    {
        if (Try<T>(out var store) && store.HasRemoveCallbacks)
        {
            using HookExecutionScope execution = EnterExecution();
            store.RunRemove(new DeferredWorld(_world, execution.Token), entity, in value);
        }
    }

    internal void ThrowIfReentrantWorldMutation(bool writeAccess)
    {
        if (!writeAccess ||
            Volatile.Read(ref _executionDepth) == 0 ||
            Volatile.Read(ref _executionThread) != Environment.CurrentManagedThreadId)
        {
            return;
        }

        throw new InvalidOperationException(
            "Synchronous component hooks cannot mutate World storage through an unadmitted " +
            "captured World. Read through DeferredWorld, use an already-declared non-topology " +
            "storage capability inside a serial Job, or record next-wave mutations through " +
            "DeferredWorld.Commands().");
    }

    internal bool IsExecutingOnCurrentThread =>
        Volatile.Read(ref _executionDepth) != 0 &&
        Volatile.Read(ref _executionThread) == Environment.CurrentManagedThreadId;

    internal void ValidateCommandToken(HookCommandToken token)
    {
        if (token.Epoch == 0 ||
            token.ThreadId != Environment.CurrentManagedThreadId ||
            Volatile.Read(ref _executionDepth) == 0 ||
            Volatile.Read(ref _executionThread) != token.ThreadId ||
            Volatile.Read(ref _executionEpoch) != token.Epoch)
        {
            throw new InvalidOperationException(
                "The deferred command writer is valid only on the thread and during the " +
                "immediate component hook invocation that created it.");
        }
    }

    private void Mark()
    {
        Any = true;
    }

    private void Mutate(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        _world.ThrowIfStructuralTransactionActive();
        using WorldJobAdmissionScope admission = _world.EnterJobTopologyWrite();
        mutation();
    }

    private HookExecutionScope EnterExecution()
    {
        int threadId = Environment.CurrentManagedThreadId;
        if (_executionDepth == 0)
        {
            _executionThread = threadId;
            _executionEpoch = NextExecutionEpoch();
        }
        else if (_executionThread != threadId)
            throw new InvalidOperationException("Component hooks cannot execute concurrently for one World.");
        _executionDepth++;
        return new HookExecutionScope(
            this,
            threadId,
            new HookCommandToken(threadId, _executionEpoch));
    }

    private long NextExecutionEpoch()
    {
        long epoch = unchecked(++_nextExecutionEpoch);
        if (epoch == 0)
            epoch = unchecked(++_nextExecutionEpoch);
        return epoch;
    }

    private void ExitExecution(int threadId, HookCommandToken token)
    {
        if (_executionDepth <= 0 || _executionThread != threadId ||
            threadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("Component hook execution scope is unbalanced.");
        }

        bool outermost = _executionDepth == 1;
        try
        {
            if (outermost)
                _world.EndHookCommandWriter(token);
        }
        finally
        {
            _executionDepth--;
            if (_executionDepth == 0)
            {
                _executionThread = 0;
                _executionEpoch = 0;
            }
        }
    }

    private readonly struct HookExecutionScope : IDisposable
    {
        private readonly Hooks? _owner;
        private readonly int _threadId;

        internal HookCommandToken Token { get; }

        internal HookExecutionScope(
            Hooks owner,
            int threadId,
            HookCommandToken token)
        {
            _owner = owner;
            _threadId = threadId;
            Token = token;
        }

        public void Dispose() => _owner?.ExitExecution(_threadId, Token);
    }

    private HookStore<T> Store<T>()
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        ArrayGrowthExtensions.EnsureCapacity(ref _stores, componentId + 1, 8);
        if (_stores[componentId] is HookStore<T> existing)
            return existing;

        var store = new HookStore<T>();
        _stores[componentId] = store;
        return store;
    }
}


