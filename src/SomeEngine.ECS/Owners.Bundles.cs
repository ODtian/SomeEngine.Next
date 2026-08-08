using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Collections;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Indexing;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Sparse;

namespace SomeEngine.ECS.Owners;

internal sealed class Bundles
{
    private const int MaximumStackSharedValues = 256;
    private Entities _entities = null!;
    private Tables _tables = null!;
    private Components _components = null!;
    private Buffers _buffers = null!;
    private Shared _shared = null!;
    private Sparse _sparse = null!;
    private Indices _indices = null!;
    private Hooks _hooks = null!;
    private Clock _clock = null!;
    private Iteration _iteration = null!;
    private Hierarchy _hierarchy = null!;
    private readonly Dictionary<SortedValueKey, BundleSpawnMap> _plans =
        new(SortedValueComparer.Instance);
    private BundleSpawnMap? _plan;
    private long _nextExecutionToken;
    private long _activeExecutionToken;
    private int _activeExecutionThread;
    private BundleWriteRuntime? _activeRuntime;

    internal void Bind(
        Entities entities,
        Tables tables,
        Components components,
        Buffers buffers,
        Shared shared,
        Sparse sparse,
        Indices indices,
        Hooks hooks,
        Clock clock,
        Iteration iteration,
        Hierarchy hierarchy)
    {
        _entities = entities;
        _tables = tables;
        _components = components;
        _buffers = buffers;
        _shared = shared;
        _sparse = sparse;
        _indices = indices;
        _hooks = hooks;
        _clock = clock;
        _iteration = iteration;
        _hierarchy = hierarchy;
    }

    internal Entity ExecuteSpawn(
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        BundleWriteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        BundleSpawnMap plan = ResolveMap(componentIds);
        return ExecuteSpawn(plan, sparseComponentIds, action, index: 0);
    }

    internal Entity ExecuteSpawn<TState>(
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        BundleSpawnMap plan = ResolveMap(componentIds);
        return ExecuteSpawn(plan, sparseComponentIds, ref state, action, index: 0);
    }

    internal void ExecuteSpawnBatch(
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        int count,
        BundleWriteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        BundleSpawnMap plan = ResolveMap(componentIds);
        if (count == 0)
            return;
        if (!plan.HasSharedComponents)
        {
            ExecuteReusableSpawnBatch(plan, sparseComponentIds, count, action);
            return;
        }

        long token = ClaimExecution();
        try
        {
            for (int index = 0; index < count; index++)
            {
                _ = ExecuteCoreClaimed(
                    plan,
                    sparseComponentIds,
                    Entity.Null,
                    BundleWriteMode.Spawn,
                    preserveEntity: false,
                    index,
                    token,
                    action);
            }
        }
        finally
        {
            ReleaseExecution(token);
        }
    }

    internal void ExecuteSpawnBatch<TState>(
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        int count,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        BundleSpawnMap plan = ResolveMap(componentIds);
        if (count == 0)
            return;
        if (!plan.HasSharedComponents)
        {
            ExecuteReusableSpawnBatch(
                plan,
                sparseComponentIds,
                count,
                ref state,
                action);
            return;
        }

        long token = ClaimExecution();
        try
        {
            for (int index = 0; index < count; index++)
            {
                _ = ExecuteCoreClaimed(
                    plan,
                    sparseComponentIds,
                    Entity.Null,
                    BundleWriteMode.Spawn,
                    preserveEntity: false,
                    index,
                    token,
                    ref state,
                    action);
            }
        }
        finally
        {
            ReleaseExecution(token);
        }
    }

    internal void ExecuteAdd(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        BundleWriteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        BundleSpawnMap plan = ResolveMap(componentIds);
        ExecuteExisting(entity, plan, sparseComponentIds, BundleWriteMode.Add, action);
    }

    internal void ExecuteAdd<TState>(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        BundleSpawnMap plan = ResolveMap(componentIds);
        ExecuteExisting(entity, plan, sparseComponentIds, BundleWriteMode.Add, ref state, action);
    }

    internal void ExecuteReplace(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        BundleWriteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        BundleSpawnMap plan = ResolveMap(componentIds);
        ExecuteExisting(entity, plan, sparseComponentIds, BundleWriteMode.Replace, action);
    }

    internal void ExecuteReplace<TState>(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        BundleSpawnMap plan = ResolveMap(componentIds);
        ExecuteExisting(entity, plan, sparseComponentIds, BundleWriteMode.Replace, ref state, action);
    }

    internal void ExecuteLoad<TState>(
        Entity entity,
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        BundleSpawnMap plan = ResolveMap(componentIds);
        ExecuteCore(
            plan,
            sparseComponentIds,
            entity,
            BundleWriteMode.Add,
            preserveEntity: false,
            index: 0,
            ref state,
            action);
    }

    internal void Reserve(ReadOnlySpan<int> componentIds, int entityCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entityCapacity);
        _iteration.Throw();
        BundleSpawnMap plan = ResolveMap(componentIds);
        _entities.Store.EnsureAdditionalCapacity(entityCapacity);
        _tables.EnsureCapacity(plan.Archetype, entityCapacity);
    }

    internal BundleSpawnMap ResolveSortedMap(ReadOnlySpan<int> sortedComponentIds)
    {
        if (_plan is { } cached &&
            sortedComponentIds.SequenceEqual(cached.ComponentIds))
        {
            return cached;
        }

        var lookup = _plans.GetAlternateLookup<ReadOnlySpan<int>>();
        if (lookup.TryGetValue(sortedComponentIds, out BundleSpawnMap? plan))
        {
            Cache(plan);
            return plan;
        }

        ValidateTableDescriptor(sortedComponentIds);
        Archetype archetype = _tables.Registry.GetOrCreate(sortedComponentIds);
        plan = new BundleSpawnMap(sortedComponentIds, archetype);
        _plans.Add(new SortedValueKey(plan.ComponentIds), plan);
        Cache(plan);
        return plan;
    }

    internal BundleSpawnMap ResolveMap(ReadOnlySpan<int> componentIds)
    {
        if (componentIds.Length <= 16)
        {
            Span<int> sortedComponentIds = stackalloc int[componentIds.Length];
            componentIds.CopyTo(sortedComponentIds);
            BundleComponents.SortAndValidate(sortedComponentIds);
            return ResolveSortedMap(sortedComponentIds);
        }

        int[] rented = ArrayPool<int>.Shared.Rent(componentIds.Length);
        Span<int> pooledSortedComponentIds = rented.AsSpan(0, componentIds.Length);
        try
        {
            componentIds.CopyTo(pooledSortedComponentIds);
            BundleComponents.SortAndValidate(pooledSortedComponentIds);
            return ResolveSortedMap(pooledSortedComponentIds);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ValidateExecution(long token, int currentThreadId)
    {
        if (Volatile.Read(ref _activeExecutionToken) != token)
            throw new InvalidOperationException("Bundle callback root epoch is no longer active.");
        if (Volatile.Read(ref _activeExecutionThread) != currentThreadId)
        {
            throw new InvalidOperationException("Bundle callback is not owned by the current thread.");
        }
    }

    internal void AttachRuntime(BundleWriteRuntime runtime, long token)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (Volatile.Read(ref _activeExecutionToken) != token ||
            _activeRuntime is not null)
        {
            throw new InvalidOperationException("Bundle runtime attachment is unbalanced.");
        }
        _activeRuntime = runtime;
    }

    internal void DetachRuntime(BundleWriteRuntime runtime)
    {
        if (!ReferenceEquals(_activeRuntime, runtime))
            throw new InvalidOperationException("Bundle runtime detachment is unbalanced.");
        _activeRuntime = null;
    }

    internal void ThrowIfReentrantWorldMutation(bool writeAccess)
    {
        if (!writeAccess ||
            Volatile.Read(ref _activeExecutionToken) == 0 ||
            Volatile.Read(ref _activeExecutionThread) != Environment.CurrentManagedThreadId)
        {
            return;
        }

        throw new InvalidOperationException(
            "Bundle callbacks cannot mutate World storage directly. Write through BundleWriteView " +
            "or record next-wave structural work through World.Commands().");
    }

    internal void ThrowIfPendingIndexBackfill(int componentId, bool requiresBackfill)
    {
        if (!requiresBackfill ||
            Volatile.Read(ref _activeExecutionToken) == 0 ||
            Volatile.Read(ref _activeExecutionThread) != Environment.CurrentManagedThreadId)
        {
            return;
        }

        _activeRuntime?.ThrowIfPendingIndexBackfill(componentId);
    }

    internal int AddSharedIndex<T>(int componentId, in T value)
        where T : struct, ISharedComponent =>
        _shared.AddIndex(componentId, in value);

    internal BundleMaterializedRow Materialize(BundleWriteRuntime runtime)
    {
        if (runtime.IsPreparedBatch)
            return MaterializePreparedSpawn(runtime);

        return runtime.Mode == BundleWriteMode.Spawn
            ? MaterializeSpawn(runtime)
            : MaterializeExisting(runtime);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteComponent<T>(
        in BundleMaterializedRow row,
        in T value,
        BundleWriteMode mode)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        int columnIndex = row.Archetype.Column(componentId);
        if (mode == BundleWriteMode.Replace)
        {
            _components.WriteExisting(row.Entity, row.Chunk, row.Row, columnIndex, in value);
            return;
        }

        _components.WriteAdded(
            row.Entity,
            row.Archetype,
            row.Chunk,
            row.Row,
            columnIndex,
            in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePreparedComponent<T>(
        in BundleMaterializedRow row,
        in T value)
        where T : struct, IComponent
    {
        int componentId = ComponentMetadata<T>.Id;
        int columnIndex = row.Plan.Column(componentId);
        int enableMaskIndex = ComponentMetadata<T>.IsEnableable
            ? row.Archetype.EnableMask(componentId)
            : -1;
        row.Chunk.WritePreparedComponent(
            columnIndex,
            row.Row,
            in value,
            _clock.Tick,
            enableMaskIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteSparse<T>(Entity entity, in T value, BundleWriteMode mode)
        where T : struct, ISparseComponent
    {
        if (mode == BundleWriteMode.Replace)
            _sparse.Replace(entity, in value);
        else
            _sparse.Add(entity, in value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteBuffer<T>(
        in BundleMaterializedRow row,
        scoped ReadOnlySpan<T> values,
        BundleWriteMode mode)
        where T : struct, IBufferElement
    {
        if (mode != BundleWriteMode.Replace)
        {
            int headerColumn = row.Archetype.Column(BufferComponents.Header<T>());
            int inlineColumn = row.Archetype.Column(BufferComponents.Inline<T>());
            row.Chunk.GetComponentRef<DynamicBufferHeader<T>>(headerColumn, row.Row) =
                DynamicBufferHeader<T>.Create();
            row.Chunk.GetComponentRef<DynamicBufferInline<T>>(inlineColumn, row.Row) = default;
        }

        DynamicBuffer<T> buffer = _buffers.BorrowWrite<T>(row.Entity);
        if (mode == BundleWriteMode.Replace)
            buffer.ReplaceWith(values);
        else
            buffer.InitializeWith(values);
    }

    internal void Reset()
    {
        if (Volatile.Read(ref _activeExecutionToken) != 0)
            throw new InvalidOperationException("Cannot reset bundle runtime during an active callback.");

        _plans.Clear();
        _plan = null;
    }

    private Entity ExecuteSpawn(
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        BundleWriteAction action,
        int index)
    {
        return ExecuteCore(
            plan,
            sparseComponentIds,
            Entity.Null,
            BundleWriteMode.Spawn,
            preserveEntity: false,
            index,
            action);
    }

    private Entity ExecuteSpawn<TState>(
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        ref TState state,
        BundleWriteAction<TState> action,
        int index)
    {
        return ExecuteCore(
            plan,
            sparseComponentIds,
            Entity.Null,
            BundleWriteMode.Spawn,
            preserveEntity: false,
            index,
            ref state,
            action);
    }

    private void ExecuteExisting(
        Entity entity,
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        BundleWriteMode mode,
        BundleWriteAction action)
    {
        _ = ExecuteCore(
            plan,
            sparseComponentIds,
            entity,
            mode,
            preserveEntity: false,
            index: 0,
            action);
    }

    private void ExecuteExisting<TState>(
        Entity entity,
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        BundleWriteMode mode,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        _ = ExecuteCore(
            plan,
            sparseComponentIds,
            entity,
            mode,
            preserveEntity: false,
            index: 0,
            ref state,
            action);
    }

    private Entity ExecuteCore(
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        Entity target,
        BundleWriteMode mode,
        bool preserveEntity,
        int index,
        BundleWriteAction action)
    {
        long token = ClaimExecution();
        try
        {
            return ExecuteCoreClaimed(
                plan,
                sparseComponentIds,
                target,
                mode,
                preserveEntity,
                index,
                token,
                action);
        }
        finally
        {
            ReleaseExecution(token);
        }
    }

    private Entity ExecuteCore<TState>(
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        Entity target,
        BundleWriteMode mode,
        bool preserveEntity,
        int index,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        long token = ClaimExecution();
        try
        {
            return ExecuteCoreClaimed(
                plan,
                sparseComponentIds,
                target,
                mode,
                preserveEntity,
                index,
                token,
                ref state,
                action);
        }
        finally
        {
            ReleaseExecution(token);
        }
    }

    private Entity ExecuteCoreClaimed(
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        Entity target,
        BundleWriteMode mode,
        bool preserveEntity,
        int index,
        long token,
        BundleWriteAction action)
    {
        BundleWriteRuntime runtime = BundleWriteRuntime.Rent();
        try
        {
            runtime.Begin(
                this,
                plan,
                sparseComponentIds,
                target,
                mode,
                preserveEntity,
                token,
                index);
            ValidateMode(runtime);
            action(new BundleWriteView(runtime, token));
            return runtime.Complete(token);
        }
        finally
        {
            runtime.Return();
        }
    }

    private Entity ExecuteCoreClaimed<TState>(
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        Entity target,
        BundleWriteMode mode,
        bool preserveEntity,
        int index,
        long token,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        BundleWriteRuntime runtime = BundleWriteRuntime.Rent();
        try
        {
            runtime.Begin(
                this,
                plan,
                sparseComponentIds,
                target,
                mode,
                preserveEntity,
                token,
                index);
            ValidateMode(runtime);
            action(new BundleWriteView(runtime, token), ref state);
            return runtime.Complete(token);
        }
        finally
        {
            runtime.Return();
        }
    }

    private void ExecuteReusableSpawnBatch(
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        int count,
        BundleWriteAction action)
    {
        PrepareReusableSpawnBatch(plan, count);
        BundleWriteRuntime runtime = BundleWriteRuntime.Rent();
        long token = 0;
        try
        {
            token = ClaimExecution();
            runtime.BeginPreparedBatch(
                this,
                plan,
                sparseComponentIds,
                token);
            for (int index = 0; index < count; index++)
            {
                runtime.BeginPreparedRow(index);
                action(new BundleWriteView(runtime, token));
                runtime.CompletePreparedRow();
            }
        }
        finally
        {
            runtime.Return();
            if (token != 0)
                ReleaseExecution(token);
        }
    }

    private void ExecuteReusableSpawnBatch<TState>(
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        int count,
        ref TState state,
        BundleWriteAction<TState> action)
    {
        PrepareReusableSpawnBatch(plan, count);
        BundleWriteRuntime runtime = BundleWriteRuntime.Rent();
        long token = 0;
        try
        {
            token = ClaimExecution();
            runtime.BeginPreparedBatch(
                this,
                plan,
                sparseComponentIds,
                token);
            for (int index = 0; index < count; index++)
            {
                runtime.BeginPreparedRow(index);
                action(new BundleWriteView(runtime, token), ref state);
                runtime.CompletePreparedRow();
            }
        }
        finally
        {
            runtime.Return();
            if (token != 0)
                ReleaseExecution(token);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool CanUsePreparedRawComponentWrites() =>
        !_hooks.Any &&
        !_indices.Any &&
        !_hierarchy.Any;

    private void PrepareReusableSpawnBatch(BundleSpawnMap plan, int count)
    {
        _entities.Store.EnsureAdditionalCapacity(count);
        _tables.EnsureCapacity(plan.Archetype, count);
    }

    private long ClaimExecution()
    {
        _iteration.Throw();
        long token = Interlocked.Increment(ref _nextExecutionToken);
        if (token <= 0)
            throw new InvalidOperationException("Bundle callback token space was exhausted.");
        if (Interlocked.CompareExchange(ref _activeExecutionToken, token, comparand: 0) != 0)
        {
            throw new InvalidOperationException(
                "A bundle callback is already active for this World; nested bundle execution is not allowed.");
        }

        Volatile.Write(ref _activeExecutionThread, Environment.CurrentManagedThreadId);
        return token;
    }

    private void ReleaseExecution(long token)
    {
        Volatile.Write(ref _activeExecutionThread, 0);
        if (Interlocked.CompareExchange(ref _activeExecutionToken, 0, token) != token)
            throw new InvalidOperationException("Bundle callback token release is unbalanced.");
    }

    private void ValidateMode(BundleWriteRuntime runtime)
    {
        if (runtime.Mode == BundleWriteMode.Spawn)
            return;

        _entities.ThrowDead(runtime.Target);
        EntityRecord record = _entities.Store.GetRecordReadOnly(runtime.Target);
        Archetype source = record.Archetype!;
        if (runtime.Mode == BundleWriteMode.Add)
        {
            ValidateAdd(source, runtime.Target, runtime.Plan.ComponentIds);
            ValidateSparseAdd(runtime.Target, runtime.SparseIds);
        }
        else
        {
            ValidateReplace(source, runtime.Target, runtime.Plan.ComponentIds);
            ValidateSparseReplace(runtime.Target, runtime.SparseIds);
        }
    }

    private BundleMaterializedRow MaterializeSpawn(BundleWriteRuntime runtime)
    {
        BundleSpawnMap plan = runtime.Plan;
        ReadOnlySpan<BundleSharedAssignment> sharedValues = runtime.SharedAssignments;
        ValidateSharedAssignments(plan.ComponentIds, sharedValues);

        if (runtime.PreserveEntity)
        {
            EntityRecordWriter preserved = _entities.Store.AllocatePreserved(runtime.Target);
            return FinishSpawn(
                preserved,
                runtime.Target,
                plan,
                sharedValues);
        }

        EntityRecordWriter record = _entities.Store.Allocate(out Entity entity);
        return FinishSpawn(record, entity, plan, sharedValues);
    }

    private BundleMaterializedRow MaterializePreparedSpawn(BundleWriteRuntime runtime)
    {
        BundleSpawnMap plan = runtime.Plan;
        EntityRecordWriter record = _entities.Store.AllocatePrepared(out Entity entity);
        (Chunk chunk, int row) = _tables.AllocatePrepared(
            plan.Archetype,
            entity,
            runtime.PreparedChunk);
        runtime.PreparedChunk = chunk;
        record.Archetype = plan.Archetype;
        record.Chunk = chunk;
        record.RowInChunk = row;
        return new BundleMaterializedRow(
            entity,
            sourceArchetype: null,
            plan.Archetype,
            plan,
            chunk,
            row);
    }

    private BundleMaterializedRow FinishSpawn(
        EntityRecordWriter record,
        Entity entity,
        BundleSpawnMap plan,
        ReadOnlySpan<BundleSharedAssignment> sharedValues)
    {
        (Chunk chunk, int row) = AllocateRow(plan.Archetype, entity, sharedValues);
        record.Archetype = plan.Archetype;
        record.Chunk = chunk;
        record.RowInChunk = row;
        return new BundleMaterializedRow(
            entity,
            sourceArchetype: null,
            plan.Archetype,
            plan,
            chunk,
            row);
    }

    private BundleMaterializedRow MaterializeExisting(BundleWriteRuntime runtime)
    {
        Entity entity = runtime.Target;
        _entities.ThrowDead(entity);
        EntityRecord record = _entities.Store.GetRecordReadOnly(entity);
        Archetype sourceArchetype = record.Archetype!;
        Chunk sourceChunk = record.Chunk!;
        if (runtime.Mode == BundleWriteMode.Add)
            ValidateAdd(sourceArchetype, entity, runtime.Plan.ComponentIds);
        else
            ValidateReplace(sourceArchetype, entity, runtime.Plan.ComponentIds);

        ReadOnlySpan<BundleSharedAssignment> sharedValues = runtime.SharedAssignments;
        ValidateSharedAssignments(runtime.Plan.ComponentIds, sharedValues);
        StructuralTransition transition = _tables.Registry.IncludeTransition(
            sourceArchetype,
            runtime.Plan.ComponentIds);
        MoveForWrite(
            entity,
            sourceArchetype,
            sourceChunk,
            transition,
            sharedValues);
        EntityRecord destination = _entities.Store.GetRecordReadOnly(entity);
        Archetype archetype = destination.Archetype!;
        return new BundleMaterializedRow(
            entity,
            sourceArchetype,
            archetype,
            runtime.Plan,
            destination.Chunk!,
            destination.RowInChunk);
    }

    private void Cache(BundleSpawnMap plan)
    {
        _plan = plan;
    }

    private static void ValidateTableDescriptor(ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentIds[i]);
            if (info.Storage == StoragePath.Sparse)
            {
                throw new InvalidOperationException(
                    $"Sparse component {info.Type.Name} must be declared through sparseComponentIds.");
            }
        }
    }

    private static void ValidateAdd(
        Archetype source,
        Entity entity,
        ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            if (source.HasComponent(componentIds[i]))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} already has component ID {componentIds[i]}.");
            }
        }
    }

    private static void ValidateReplace(
        Archetype source,
        Entity entity,
        ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            if (!source.HasComponent(componentIds[i]))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} does not have component ID {componentIds[i]}.");
            }
        }
    }

    private void ValidateSparseAdd(Entity entity, ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            int componentId = componentIds[i];
            if (_sparse.HasValue(componentId, entity))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} already has sparse component ID {componentId}.");
            }
        }
    }

    private void ValidateSparseReplace(Entity entity, ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
        {
            int componentId = componentIds[i];
            if (!_sparse.HasValue(componentId, entity))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} does not have sparse component ID {componentId}.");
            }
        }
    }

    private bool MoveForWrite(
        Entity entity,
        Archetype sourceArchetype,
        Chunk sourceChunk,
        StructuralTransition transition,
        ReadOnlySpan<BundleSharedAssignment> sharedValues)
    {
        if (transition.Target.SharedComponentIds.Length > 0)
        {
            return MoveForSharedWrite(
                entity,
                sourceArchetype,
                sourceChunk,
                transition,
                sharedValues);
        }

        if (transition.IsIdentityFor(sourceArchetype))
            return false;

        EntityRecordWriter record = _entities.Store.GetRecord(entity);
        _tables.MoveEntity(entity, record, transition);
        return true;
    }

    private bool MoveForSharedWrite(
        Entity entity,
        Archetype sourceArchetype,
        Chunk sourceChunk,
        StructuralTransition transition,
        ReadOnlySpan<BundleSharedAssignment> sharedValues)
    {
        int count = transition.Target.SharedComponentIds.Length;
        int[]? rented = null;
        Span<int> destinationSharedValues = count <= MaximumStackSharedValues
            ? stackalloc int[count]
            : (rented = ArrayPool<int>.Shared.Rent(count)).AsSpan(0, count);
        try
        {
            bool sharedChanged = FillTargetShared(
                sourceArchetype,
                sourceChunk,
                transition.Target,
                sharedValues,
                destinationSharedValues);
            if (!transition.IsIdentityFor(sourceArchetype))
            {
                EntityRecordWriter movedRecord = _entities.Store.GetRecord(entity);
                _tables.MoveRow(
                    entity,
                    movedRecord,
                    transition.Target,
                    transition.SharedColumns,
                    destinationSharedValues);
                return true;
            }

            if (!sharedChanged)
                return false;

            EntityRecordWriter record = _entities.Store.GetRecord(entity);
            _shared.MoveTo(entity, record, destinationSharedValues);
            return true;
        }
        finally
        {
            if (rented is not null)
                ArrayPool<int>.Shared.Return(rented);
        }
    }

    private (Chunk Chunk, int Row) AllocateRow(
        Archetype archetype,
        Entity entity,
        ReadOnlySpan<BundleSharedAssignment> sharedValues)
    {
        if (archetype.SharedComponentIds.Length == 0)
            return _tables.AllocateInChunk(archetype, entity);

        int count = archetype.SharedComponentIds.Length;
        int[]? rented = null;
        Span<int> destinationSharedValues = count <= MaximumStackSharedValues
            ? stackalloc int[count]
            : (rented = ArrayPool<int>.Shared.Rent(count)).AsSpan(0, count);
        try
        {
            FillSpawnShared(archetype, sharedValues, destinationSharedValues);
            return _tables.AllocateShared(archetype, entity, destinationSharedValues);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<int>.Shared.Return(rented);
        }
    }

    private static void ValidateSharedAssignments(
        ReadOnlySpan<int> componentIds,
        ReadOnlySpan<BundleSharedAssignment> sharedValues)
    {
        for (int i = 0; i < sharedValues.Length; i++)
        {
            int componentId = sharedValues[i].ComponentId;
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
            if (info.Storage != StoragePath.Shared)
                throw new InvalidOperationException($"Component ID {componentId} is not shared storage.");
            if (componentIds.BinarySearch(componentId) < 0)
                throw new InvalidOperationException($"Shared component ID {componentId} is undeclared.");
            for (int j = i + 1; j < sharedValues.Length; j++)
            {
                if (sharedValues[j].ComponentId == componentId)
                    throw new InvalidOperationException($"Duplicate shared component ID {componentId}.");
            }
        }

        for (int i = 0; i < componentIds.Length; i++)
        {
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentIds[i]);
            if (info.Storage == StoragePath.Shared &&
                !TryFindShared(sharedValues, componentIds[i], out _))
            {
                throw new InvalidOperationException(
                    $"Shared component {info.Type.Name} was not supplied by the bundle callback.");
            }
        }
    }

    private static void FillSpawnShared(
        Archetype archetype,
        ReadOnlySpan<BundleSharedAssignment> sharedValues,
        Span<int> destination)
    {
        for (int i = 0; i < archetype.SharedComponentIds.Length; i++)
        {
            int componentId = archetype.SharedComponentIds[i];
            if (!TryFindShared(sharedValues, componentId, out int sharedIndex))
                throw new InvalidOperationException($"Shared component ID {componentId} is missing.");
            destination[i] = sharedIndex;
        }
    }

    private static bool FillTargetShared(
        Archetype sourceArchetype,
        Chunk sourceChunk,
        Archetype destinationArchetype,
        ReadOnlySpan<BundleSharedAssignment> sharedValues,
        Span<int> destination)
    {
        bool changed = false;
        for (int i = 0; i < destinationArchetype.SharedComponentIds.Length; i++)
        {
            int componentId = destinationArchetype.SharedComponentIds[i];
            if (TryFindShared(sharedValues, componentId, out int sharedIndex))
            {
                destination[i] = sharedIndex;
                int oldSlot = sourceArchetype.SharedComponentIds.BinarySearch(componentId);
                changed |= oldSlot < 0 ||
                    sourceChunk.SharedValues is null ||
                    sourceChunk.SharedValues[oldSlot] != sharedIndex;
                continue;
            }

            int sourceSlot = sourceArchetype.SharedComponentIds.BinarySearch(componentId);
            if (sourceSlot < 0 || sourceChunk.SharedValues is null)
            {
                throw new InvalidOperationException(
                    $"Shared component ID {componentId} is missing for the destination archetype.");
            }
            destination[i] = sourceChunk.SharedValues[sourceSlot];
        }

        return changed;
    }

    private static bool TryFindShared(
        ReadOnlySpan<BundleSharedAssignment> sharedValues,
        int componentId,
        out int sharedIndex)
    {
        for (int i = 0; i < sharedValues.Length; i++)
        {
            if (sharedValues[i].ComponentId == componentId)
            {
                sharedIndex = sharedValues[i].SharedIndex;
                return true;
            }
        }

        sharedIndex = default;
        return false;
    }

}
