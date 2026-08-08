using System.Buffers;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

/// <summary>
/// Runtime-owned bundle write callback. The byref-like view cannot be retained by ordinary state,
/// and every operation revalidates the callback token before touching the World.
/// </summary>
public delegate void BundleWriteAction(BundleWriteView view);

/// <summary>Allocation-free bundle write callback with caller-owned ordinary state.</summary>
public delegate void BundleWriteAction<TState>(BundleWriteView view, ref TState state);

/// <summary>
/// A callback-scoped bundle writer. It contains no Chunk, Archetype, owner, or pool storage; those
/// remain behind the runtime token for the exact duration of ExecuteBundle*.
/// </summary>
public readonly ref struct BundleWriteView
{
    private readonly BundleWriteRuntime _runtime;
    private readonly long _token;
    private readonly int _index;

    internal BundleWriteView(BundleWriteRuntime runtime, long token)
    {
        _runtime = runtime;
        _token = token;
        _index = runtime.Index;
    }

    /// <summary>The zero-based item index for batch execution; zero for single-row execution.</summary>
    public int Index => _index;

    /// <summary>
    /// The entity being written. Reading this property materializes the row, so all declared
    /// shared values must have been supplied first.
    /// </summary>
    public Entity Entity => _runtime.GetEntity(_token);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write<T>(in T value)
        where T : struct, IComponent =>
        _runtime.Write(_token, in value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSparse<T>(in T value)
        where T : struct, ISparseComponent =>
        _runtime.WriteSparse(_token, in value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBuffer<T>(in ReadOnlyMemory<T> values)
        where T : struct, IBufferElement =>
        _runtime.WriteBuffer(_token, values.Span);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteShared<T>(in T value)
        where T : struct, ISharedComponent =>
        _runtime.WriteShared(_token, in value);
}

internal enum BundleWriteMode : byte
{
    Spawn,
    Add,
    Replace,
}

internal readonly record struct BundleSharedAssignment(int ComponentId, int SharedIndex);

internal readonly struct BundleMaterializedRow
{
    internal BundleMaterializedRow(
        Entity entity,
        Archetype? sourceArchetype,
        Archetype archetype,
        BundleSpawnMap plan,
        Chunk chunk,
        int row)
    {
        Entity = entity;
        SourceArchetype = sourceArchetype;
        Archetype = archetype;
        Plan = plan;
        Chunk = chunk;
        Row = row;
    }

    internal Entity Entity { get; }

    internal Archetype? SourceArchetype { get; }

    internal Archetype Archetype { get; }

    internal BundleSpawnMap Plan { get; }

    internal Chunk Chunk { get; }

    internal int Row { get; }
}

internal sealed class BundleWriteRuntime
{
    [ThreadStatic]
    private static BundleWriteRuntime? s_pool;

    private Owners.Bundles? _owner;
    private BundleWriteRuntime? _next;
    private BundleSpawnMap _plan = null!;
    private int[]? _sparseIds;
    private int _sparseCount;
    private BundleSharedAssignment[]? _sharedAssignments;
    private int _sharedCount;
    private ulong[]? _writtenOverflow;
    private UInt128 _written;
    private BundleMaterializedRow _materialized;
    private Entity _target;
    private BundleWriteMode _mode;
    private long _token;
    private int _threadId;
    private int _index;
    private bool _preserveEntity;
    private bool _preparedBatch;
    private bool _hasMaterialized;
    private bool _active;

    private BundleWriteRuntime()
    {
    }

    internal BundleSpawnMap Plan => _plan;

    internal ReadOnlySpan<int> SparseIds =>
        _sparseIds is null ? ReadOnlySpan<int>.Empty : _sparseIds.AsSpan(0, _sparseCount);

    internal ReadOnlySpan<BundleSharedAssignment> SharedAssignments =>
        _sharedAssignments is null
            ? ReadOnlySpan<BundleSharedAssignment>.Empty
            : _sharedAssignments.AsSpan(0, _sharedCount);

    internal BundleWriteMode Mode => _mode;

    internal Entity Target => _target;

    internal bool PreserveEntity => _preserveEntity;

    internal int Index => _index;

    internal bool IsPreparedBatch => _preparedBatch;

    internal Chunk? PreparedChunk { get; set; }

    internal static BundleWriteRuntime Rent()
    {
        BundleWriteRuntime? runtime = s_pool;
        if (runtime is null)
            return new BundleWriteRuntime();

        s_pool = runtime._next;
        runtime._next = null;
        return runtime;
    }

    internal void Begin(
        Owners.Bundles owner,
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        Entity target,
        BundleWriteMode mode,
        bool preserveEntity,
        long token,
        int index)
    {
        if (_active)
            throw new InvalidOperationException("Bundle write runtime is already active.");

        _owner = owner;
        _plan = plan;
        _target = target;
        _mode = mode;
        _preserveEntity = preserveEntity;
        _token = token;
        _threadId = Environment.CurrentManagedThreadId;
        _index = index;
        _written = 0;
        _hasMaterialized = false;
        _materialized = default;

        if (sparseComponentIds.Length > 0)
        {
            _sparseIds = ArrayPool<int>.Shared.Rent(sparseComponentIds.Length);
            sparseComponentIds.CopyTo(_sparseIds);
            _sparseCount = sparseComponentIds.Length;
            BundleComponents.SortAndValidate(_sparseIds.AsSpan(0, _sparseCount));
            for (int i = 0; i < _sparseCount; i++)
            {
                ref readonly ComponentInfo info = ref ComponentRegistry.Get(_sparseIds[i]);
                if (info.Storage != StoragePath.Sparse)
                {
                    throw new InvalidOperationException(
                        $"Bundle sparse descriptor component ID {_sparseIds[i]} is not sparse storage.");
                }
            }
        }

        int sharedCapacity = plan.Archetype.SharedComponentIds.Length;
        if (sharedCapacity > 0)
            _sharedAssignments = ArrayPool<BundleSharedAssignment>.Shared.Rent(sharedCapacity);

        int writeCount = plan.ComponentIds.Length + _sparseCount;
        if (writeCount > 128)
        {
            int overflowLength = (writeCount - 128 + 63) / 64;
            _writtenOverflow = ArrayPool<ulong>.Shared.Rent(overflowLength);
            _writtenOverflow.AsSpan(0, overflowLength).Clear();
        }

        owner.AttachRuntime(this, token);
        _active = true;
    }

    internal void BeginPreparedBatch(
        Owners.Bundles owner,
        BundleSpawnMap plan,
        ReadOnlySpan<int> sparseComponentIds,
        long token)
    {
        Begin(
            owner,
            plan,
            sparseComponentIds,
            Entity.Null,
            BundleWriteMode.Spawn,
            preserveEntity: false,
            token,
            index: 0);
        _preparedBatch = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BeginPreparedRow(int index)
    {
        if (!_active || _mode != BundleWriteMode.Spawn)
            throw new InvalidOperationException("Prepared bundle batch runtime is not active.");

        _target = Entity.Null;
        _index = index;
        _written = 0;
        if (_writtenOverflow is not null)
        {
            int writeCount = _plan.ComponentIds.Length + _sparseCount;
            int overflowLength = (writeCount - 128 + 63) / 64;
            _writtenOverflow.AsSpan(0, overflowLength).Clear();
        }
        _materialized = default;
        _hasMaterialized = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CompletePreparedRow()
    {
        ValidateRequiredWrites();
        EnsureMaterialized();
    }

    internal Entity GetEntity(long token)
    {
        Validate(token);
        EnsureMaterialized();
        return _materialized.Entity;
    }

    internal void Write<T>(long token, in T value)
        where T : struct, IComponent
    {
        ValidateCallbackAccess(token);
        int componentId = ComponentMetadata<T>.Id;
        if (ComponentMetadata<T>.Storage != StoragePath.Table || ComponentMetadata<T>.IsBufferStorage)
        {
            throw new InvalidOperationException(
                $"Component {typeof(T).Name} must be a declared ordinary table value for BundleWriteView.Write<T>.");
        }

        int descriptorIndex = _plan.DescriptorIndex(componentId);
        if (descriptorIndex < 0)
            ThrowUndeclared<T>();

        GuardCallbackWrite<T>();
        MarkWritten(descriptorIndex, typeof(T).Name);
        EnsureMaterialized();
        if (_preparedBatch && _owner!.CanUsePreparedRawComponentWrites())
            _owner.WritePreparedComponent(in _materialized, in value);
        else
            _owner!.WriteComponent(in _materialized, in value, _mode);
    }

    internal void WriteSparse<T>(long token, in T value)
        where T : struct, ISparseComponent
    {
        ValidateCallbackAccess(token);
        int componentId = ComponentMetadata<T>.Id;
        int sparseIndex = SparseIds.BinarySearch(componentId);
        if (sparseIndex < 0)
            ThrowUndeclared<T>();

        GuardCallbackWrite<T>();
        MarkWritten(_plan.ComponentIds.Length + sparseIndex, typeof(T).Name);
        EnsureMaterialized();
        _owner!.WriteSparse(_materialized.Entity, in value, _mode);
    }

    internal void WriteBuffer<T>(long token, ReadOnlySpan<T> values)
        where T : struct, IBufferElement
    {
        ValidateCallbackAccess(token);
        int headerId = BufferComponents.Header<T>();
        int inlineId = BufferComponents.Inline<T>();
        int headerIndex = _plan.ComponentIds.BinarySearch(headerId);
        int inlineIndex = _plan.ComponentIds.BinarySearch(inlineId);
        if (headerIndex < 0 || inlineIndex < 0)
            ThrowUndeclared<T>();

        PublicComponentMutationGuard.Structural<DynamicBufferHeader<T>>("BundleWriteView.WriteBuffer");
        MarkWritten(headerIndex, typeof(T).Name);
        MarkWritten(inlineIndex, typeof(T).Name);
        EnsureMaterialized();
        _owner!.WriteBuffer(in _materialized, values, _mode);
    }

    internal void WriteShared<T>(long token, in T value)
        where T : struct, ISharedComponent
    {
        ValidateCallbackAccess(token);
        if (_hasMaterialized)
        {
            throw new InvalidOperationException(
                "Shared bundle values must be written before the entity row is materialized.");
        }

        int componentId = ComponentMetadata<T>.Id;
        int descriptorIndex = _plan.ComponentIds.BinarySearch(componentId);
        if (descriptorIndex < 0)
            ThrowUndeclared<T>();
        if (ComponentMetadata<T>.Storage != StoragePath.Shared)
            throw new InvalidOperationException($"Component {typeof(T).Name} is not shared storage.");

        GuardCallbackWrite<T>();
        MarkWritten(descriptorIndex, typeof(T).Name);
        int sharedIndex = _owner!.AddSharedIndex(componentId, in value);
        _sharedAssignments![_sharedCount++] = new BundleSharedAssignment(componentId, sharedIndex);
    }

    internal Entity Complete(long token)
    {
        Validate(token);
        ValidateRequiredWrites();
        EnsureMaterialized();
        return _materialized.Entity;
    }

    internal void Return()
    {
        Owners.Bundles? owner = _owner;
        if (_active && owner is not null)
            owner.DetachRuntime(this);
        _active = false;
        _owner = null;
        _plan = null!;
        _target = default;
        _token = 0;
        _threadId = 0;
        _index = 0;
        _sharedCount = 0;
        _written = 0;
        _materialized = default;
        _preparedBatch = false;
        PreparedChunk = null;
        _hasMaterialized = false;

        if (_sparseIds is { } sparseIds)
        {
            sparseIds.AsSpan(0, _sparseCount).Clear();
            ArrayPool<int>.Shared.Return(sparseIds);
            _sparseIds = null;
        }
        _sparseCount = 0;

        if (_sharedAssignments is { } assignments)
        {
            assignments.AsSpan().Clear();
            ArrayPool<BundleSharedAssignment>.Shared.Return(assignments);
            _sharedAssignments = null;
        }

        if (_writtenOverflow is { } overflow)
        {
            overflow.AsSpan().Clear();
            ArrayPool<ulong>.Shared.Return(overflow);
            _writtenOverflow = null;
        }

        _next = s_pool;
        s_pool = this;
    }

    internal void ThrowIfPendingIndexBackfill(int componentId)
    {
        if (!_active || !_hasMaterialized)
            return;

        int descriptorIndex = _plan.DescriptorIndex(componentId);
        if (descriptorIndex < 0 || IsWritten(descriptorIndex))
            return;

        ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
        throw new InvalidOperationException(
            $"Cannot build index {info.Type.Name} while the current bundle row has not written that component.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Validate(long token)
    {
        if (!_active || token != _token || _owner is null)
            throw new InvalidOperationException("Bundle write view is outside its runtime callback lifetime.");
        int currentThreadId = Environment.CurrentManagedThreadId;
        if (_threadId != currentThreadId)
            throw new InvalidOperationException("Bundle write view can only be used by its owning callback thread.");

        _owner.ValidateExecution(token, currentThreadId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateCallbackAccess(long token)
    {
        // BundleWriteView is a ref struct passed by value to a synchronous delegate. It cannot be
        // captured, boxed, stored in a field, or survive the callback. A prepared batch also owns
        // one runtime/token/thread for the complete callback loop, so repeating volatile token and
        // managed-thread validation for every component of every row proves no additional fact.
        // Non-batch execution retains the full runtime validation because its runtime is recycled
        // after each individual callback.
        if (_preparedBatch)
            return;

        Validate(token);
    }

    private void EnsureMaterialized()
    {
        if (_hasMaterialized)
            return;

        ReadOnlySpan<int> sharedIds = _plan.Archetype.SharedComponentIds;
        for (int i = 0; i < sharedIds.Length; i++)
        {
            int descriptorIndex = _plan.ComponentIds.BinarySearch(sharedIds[i]);
            if (descriptorIndex < 0 || !IsWritten(descriptorIndex))
            {
                throw new InvalidOperationException(
                    $"Shared component ID {sharedIds[i]} must be written before row materialization.");
            }
        }

        _materialized = _owner!.Materialize(this);
        _hasMaterialized = true;
    }

    private void ValidateRequiredWrites()
    {
        if (_plan.ComponentIds.Length <= 128 &&
            _sparseCount == 0 &&
            (_written & _plan.RequiredWrites) == _plan.RequiredWrites)
        {
            return;
        }

        for (int i = 0; i < _plan.ComponentIds.Length; i++)
        {
            int componentId = _plan.ComponentIds[i];
            ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
            if (info.Storage == StoragePath.Tag)
                continue;
            if (!IsWritten(i))
            {
                throw new InvalidOperationException(
                    $"Bundle callback did not write required component {info.Type.Name}.");
            }
        }

        for (int i = 0; i < _sparseCount; i++)
        {
            int writeIndex = _plan.ComponentIds.Length + i;
            if (!IsWritten(writeIndex))
            {
                ref readonly ComponentInfo info = ref ComponentRegistry.Get(_sparseIds![i]);
                throw new InvalidOperationException(
                    $"Bundle callback did not write required sparse component {info.Type.Name}.");
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GuardCallbackWrite<T>()
        where T : struct
    {
        // World.ExecuteBundleSpawnBatch validates the complete descriptor before opening the
        // structural candidate. The descriptor is invariant for all prepared rows.
        if (_preparedBatch)
            return;

        if (_mode == BundleWriteMode.Replace)
            PublicComponentMutationGuard.Value<T>("BundleWriteView");
        else
            PublicComponentMutationGuard.Structural<T>("BundleWriteView");
    }

    private void MarkWritten(int index, string name)
    {
        if (index < 128)
        {
            UInt128 mask = (UInt128)1 << index;
            if ((_written & mask) != 0)
                throw new InvalidOperationException($"Bundle callback wrote {name} more than once.");
            _written |= mask;
            return;
        }

        int overflowIndex = index - 128;
        ulong mask64 = 1UL << (overflowIndex & 63);
        ref ulong word = ref _writtenOverflow![overflowIndex >> 6];
        if ((word & mask64) != 0)
            throw new InvalidOperationException($"Bundle callback wrote {name} more than once.");
        word |= mask64;
    }

    private bool IsWritten(int index)
    {
        if (index < 128)
            return (_written & ((UInt128)1 << index)) != 0;

        int overflowIndex = index - 128;
        return (_writtenOverflow![overflowIndex >> 6] & (1UL << (overflowIndex & 63))) != 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowUndeclared<T>()
    {
        throw new InvalidOperationException(
            $"Component {typeof(T).Name} was not declared by this bundle descriptor.");
    }
}
