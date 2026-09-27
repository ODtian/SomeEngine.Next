using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Frame;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Instances;

/// <summary>Fixed limits for one batch-packed render-instance storage arena.</summary>
public sealed record RenderInstanceOptions
{
    /// <summary>
    /// Maximum number of simultaneously live batch-local rows. A RenderWorld entity may occupy
    /// more than one row when it participates in more than one batch; rows are never entity
    /// identities and are never written back to ECS.
    /// </summary>
    public int RowCapacity { get; init; } = 65_536;

    public int BatchCapacity { get; init; } = 4_096;

    internal int ValidateAndMeasure(RenderInstancePropertyLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RowCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BatchCapacity);

        long cursor = 0;
        foreach (RenderInstancePropertyDescriptor property in layout.Properties)
        {
            RenderInstancePropertyEncoding encoding = property.Encoding;
            if (!encoding.HasManagedStorage)
                continue;
            cursor = AlignUp(cursor, encoding.StorageAlignment);
            cursor = checked(cursor + (long)BatchCapacity * encoding.StorageStride);
            cursor = AlignUp(cursor, encoding.StorageAlignment);
            cursor = checked(cursor + (long)RowCapacity * encoding.StorageStride);
        }

        if (layout.Properties.Count == 0)
            throw new ArgumentException("A render-instance property layout cannot be empty.", nameof(layout));
        if (cursor > RenderInstanceLinearMetadata.AddressMask)
        {
            throw new ArgumentException(
                "Render-instance property storage must fit below the metadata per-instance bit.",
                nameof(layout));
        }
        if (cursor > int.MaxValue)
        {
            throw new ArgumentException(
                "Render-instance property storage is too large for a persistently mapped buffer.",
                nameof(layout));
        }
        return checked((int)cursor);
    }

    private static long AlignUp(long value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}

/// <summary>Snapshot of one batch-packed property-storage arena.</summary>
public sealed record RenderInstanceDiagnostics(
    int RowCapacity,
    int BatchCapacity,
    int ActiveRows,
    int ActiveBatches,
    int PropertyDataBytes);

/// <summary>Immutable GPU-facing view of one exact live batch.</summary>
public readonly record struct RenderInstanceBatchView(
    Buffer? PropertyData,
    Buffer Metadata,
    BufferRange MetadataRange,
    int InstanceCount,
    int Generation,
    ulong ContentRevision)
{
    internal BufferBoundaryState[] PropertyDataEndpoints { get; init; } = [];

    internal BufferBoundaryState[] MetadataEndpoints { get; init; } = [];
}

/// <summary>
/// Opaque handle and exact ABI of one live physical batch. It is not an entity or instance
/// identity. Property words already live in the mapped constant buffer, and this object
/// deliberately does not retain a duplicate CPU metadata array.
/// </summary>
public sealed class RenderInstanceBatch
{
    internal RenderInstanceBatch(
        RenderInstanceResources owner,
        object allocation,
        int slot,
        RenderInstancePropertyLayout contract,
        int instanceCount,
        ulong contentRevision)
    {
        Owner = owner;
        Allocation = allocation;
        Slot = slot;
        Contract = contract;
        InstanceCount = instanceCount;
        ContentRevision = contentRevision;
        _chunkRevisions = new ulong[(instanceCount + 63) / 64];
        Array.Fill(_chunkRevisions, contentRevision);
        _instanceRevisions = new ulong[instanceCount];
        Array.Fill(_instanceRevisions, contentRevision);
    }

    public int InstanceCount { get; }

    /// <summary>
    /// Monotonic revision of the batch's published values. Unlike the physical upload-buffer
    /// generation, this changes on every successful content publication and never identifies a
    /// transport slot.
    /// </summary>
    public ulong ContentRevision { get; internal set; }

    private readonly ulong[] _chunkRevisions;
    private readonly ulong[] _instanceRevisions;

    /// <summary>Appends rows modified after the supplied revision, skipping unchanged chunks.</summary>
    public void CollectChangedInstances(ulong afterRevision, List<uint> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (afterRevision >= ContentRevision)
            return;
        for (int chunk = 0; chunk < _chunkRevisions.Length; chunk++)
        {
            if (_chunkRevisions[chunk] <= afterRevision)
                continue;
            int end = Math.Min(InstanceCount, (chunk + 1) * 64);
            for (int row = chunk * 64; row < end; row++)
                if (_instanceRevisions[row] > afterRevision)
                    destination.Add(checked((uint)row));
        }
    }

    internal void RecordChanges(ReadOnlySpan<long> changedChunks)
    {
        for (int chunk = 0; chunk < changedChunks.Length; chunk++)
            if (changedChunks[chunk] != 0)
            {
                _chunkRevisions[chunk] = ContentRevision;
                ulong bits = unchecked((ulong)changedChunks[chunk]);
                while (bits != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    _instanceRevisions[chunk * 64 + bit] = ContentRevision;
                    bits &= bits - 1;
                }
            }
    }

    internal RenderInstanceResources Owner { get; }

    internal object Allocation { get; }

    internal int Slot { get; }

    internal RenderInstancePropertyLayout Contract { get; }
}

/// <summary>
/// Owns one GPU-facing arena of batch-shared values, batch-local per-entity rows, and metadata.
/// It has no RenderWorld reference and no entity ownership table: ECS systems borrow component
/// spans and write final batch rows only while building a batch.
/// </summary>
internal sealed class RenderInstanceResources : IDisposable
{
    private const int MaxConstantBufferRangeBytes = 65_536;
    private const int LifecycleActive = 0;
    private const int LifecycleShutdown = 1;
    private const int LifecycleDisposed = 2;
    private static readonly TimeSpan GenerationWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly RenderTimeline _timeline;
    private readonly UploadBuffer?[] _propertyData;
    private readonly UploadBuffer[] _batchMetadata;
    private readonly bool[] _generationNeedsFlush = new bool[2];
    private readonly int _batchMetadataStrideBytes;
    private readonly PropertyStorage[] _properties;
    private readonly record struct BufferBytes(Buffer Resource, BufferRange Range, byte[] Data);
    private readonly Dictionary<int, BufferBytes[]> _batchOriginalBytes = [];
    private readonly object?[] _batchAllocations;
    private readonly bool[] _batchReady;
    private readonly PackedRange[] _batchRows;
    private readonly int[] _batchGenerations;
    private readonly Stack<int> _freeBatchSlots = [];
    private readonly List<PackedRange> _freeRows = [];
    private readonly object _lifecycleGate = new();
    private readonly object _disposeGate = new();
    private RenderPrepareScope? _shutdownScope;
    private int _nextUnusedBatchSlot;
    private int _activeBatchCount;
    private int _activeRowCount;
    private int _activeBorrowerCount;
    private int _operationActive;
    private int _writeGeneration = -1;
    private MappedBuffer? _propertyWrite;
    private MappedBuffer? _metadataWrite;
    private int _preferredGeneration;
    private ulong _nextContentRevision;
    private int _lifecycleState;
    private bool _cleanupCompleted;

    public RenderInstanceResources(
        IGraphicsBackend backend,
        Device device,
        RenderFrameCoordinator coordinator,
        RenderInstancePropertyLayout layout,
        RenderInstanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(layout);
        RenderInstanceOptions selected = options ?? new RenderInstanceOptions();
        int propertyDataBytes = selected.ValidateAndMeasure(layout);
        (int metadataStride, int metadataBytes) = MeasureBatchMetadata(device, layout, selected);

        RenderTimeline timeline = coordinator.CreateTimeline(generationCount: 2);
        if (!ReferenceEquals(timeline.Device, device))
        {
            throw new ArgumentException(
                "Instance storage and its frame coordinator must use the same device domain.",
                nameof(coordinator));
        }

        var propertyData = new UploadBuffer?[2];
        var batchMetadata = new UploadBuffer[2];
        try
        {
            PropertyStorage[] storage = BuildStorage(layout, selected, propertyDataBytes);
            for (int generation = 0; generation < 2; generation++)
            {
                if (propertyDataBytes != 0)
                {
                    propertyData[generation] = CreateMappedBuffer(
                        backend,
                        device,
                        checked((ulong)propertyDataBytes),
                        $"Render instance batch property data {generation}");
                    BufferRange propertyRange = new(0, checked((ulong)propertyDataBytes));
                    using MappedBuffer propertyMapping = backend.Map(
                        propertyData[generation]!.Handle,
                        MapType.Write,
                        propertyRange);
                    propertyMapping.Bytes.Clear();
                    propertyMapping.Flush(propertyRange);
                }
                batchMetadata[generation] = CreateMappedConstantBuffer(
                    backend,
                    device,
                    checked((ulong)metadataBytes),
                    $"Render instance batch metadata {generation}");
                BufferRange metadataRange = new(0, checked((ulong)metadataBytes));
                using MappedBuffer metadataMapping = backend.Map(
                    batchMetadata[generation].Handle,
                    MapType.Write,
                    metadataRange);
                metadataMapping.Bytes.Clear();
                metadataMapping.Flush(metadataRange);
            }

            _backend = backend;
            _device = device;
            _timeline = timeline;
            Layout = layout;
            Options = selected;
            PropertyDataBytes = propertyDataBytes;
            _propertyData = propertyData;
            _batchMetadata = batchMetadata;
            _batchMetadataStrideBytes = metadataStride;
            _properties = storage;
            _batchAllocations = new object?[selected.BatchCapacity];
            _batchReady = new bool[selected.BatchCapacity];
            _batchRows = new PackedRange[selected.BatchCapacity];
            _batchGenerations = new int[selected.BatchCapacity];
            _freeRows.Add(new PackedRange(0, selected.RowCapacity));
        }
        catch
        {
            for (int generation = 0; generation < 2; generation++)
            {
                TryDispose(batchMetadata[generation]);
                TryDispose(propertyData[generation]);
            }
            throw;
        }
    }

    public RenderInstancePropertyLayout Layout { get; }

    public RenderInstanceOptions Options { get; }

    public int PropertyDataBytes { get; }

    internal RenderTimeline Timeline => _timeline;

    internal int BatchMetadataStrideBytes => _batchMetadataStrideBytes;

    internal RenderInstanceBorrowerLease AcquireBorrower()
    {
        lock (_lifecycleGate)
        {
            if (_lifecycleState != LifecycleActive)
                throw new ObjectDisposedException(nameof(RenderInstanceResources));
            _activeBorrowerCount = checked(_activeBorrowerCount + 1);
            try
            {
                return new RenderInstanceBorrowerLease(this);
            }
            catch
            {
                _activeBorrowerCount--;
                throw;
            }
        }
    }

    internal RenderInstanceWriteScope BeginPrepare(RenderPrepareScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnterOperation();
        RenderTimelineLease? lease = null;
        try
        {
            lease = scope.AcquireTimeline(_timeline);
            int generation = _writeGeneration = SelectWritableGeneration();
            if (_propertyData[generation] is { } data)
                _propertyWrite = _backend.Map(data.Handle, MapType.Write, new BufferRange(0, data.Size));
            _metadataWrite = _backend.Map(_batchMetadata[generation].Handle, MapType.Write,
                new BufferRange(0, _batchMetadata[generation].Size));
            _generationNeedsFlush[generation] = true;
            RenderInstanceWriteScope result = new(this, scope, lease);
            lease = null;
            return result;
        }
        catch
        {
            try { CloseWriteMappings(flush: false); }
            finally
            {
                try { lease?.Dispose(); }
                finally { _writeGeneration = -1; ExitOperation(); }
            }
            throw;
        }
    }

    internal int AdmitFrameResources()
    {
        EnterOperation();
        try
        {
            _ = SelectWritableGeneration(out int availableGenerationCount);
            return availableGenerationCount;
        }
        finally
        {
            ExitOperation();
        }
    }

    internal bool TryAdmitFrameResources(
        out int availableGenerationCount,
        out QueueCompletion[] retirementFences)
    {
        EnterOperation();
        try
        {
            return TrySelectWritableGeneration(
                out _,
                out availableGenerationCount,
                out retirementFences);
        }
        finally
        {
            ExitOperation();
        }
    }

    /// <summary>
    /// Acquires read-only frame access to published instance storage. The returned view can bind
    /// batches but exposes no allocation or mutation surface.
    /// </summary>
    public RenderInstanceStorageView OpenRead(RenderFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ThrowIfNotActive();
        RenderFrameUseLease lease = frame.AcquireUse([_timeline]);
        try
        {
            lock (_lifecycleGate)
            {
                for (int generation = 0; generation < _generationNeedsFlush.Length; generation++)
                {
                    if (!_generationNeedsFlush[generation]) continue;
                    Flush(_propertyData[generation]);
                    Flush(_batchMetadata[generation]);
                    _generationNeedsFlush[generation] = false;
                }
            }
            return new RenderInstanceStorageView(this, lease);
        }
        catch { lease.Dispose(); throw; }

        void Flush(UploadBuffer? buffer)
        {
            if (buffer is null) return;
            BufferRange range = new(0, buffer.Size);
            using MappedBuffer mapping = _backend.Map(buffer.Handle, MapType.Write, range);
            mapping.Flush(range);
        }
    }

    internal RenderInstanceBatchView GetBatchView(
        RenderInstanceBatch metadata,
        RenderInstancePropertyLayout requiredLayout,
        RenderFrameUseLease? frameUse = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(requiredLayout);
        ThrowIfNotActive();
        ValidateBatchOwner(metadata, nameof(metadata));
        ValidateBatch(metadata.Slot, metadata.Allocation);
        ValidateBatchReady(metadata.Slot);
        if (!metadata.Contract.HasSameContract(requiredLayout))
        {
            throw new ArgumentException(
                "The batch instance-property layout does not exactly match the shader layout.",
                nameof(requiredLayout));
        }

        int generation = _batchGenerations[metadata.Slot];
        frameUse?.RegisterGeneration(_timeline, generation);
        return new RenderInstanceBatchView(
            _propertyData[generation]?.Handle,
            _batchMetadata[generation].Handle,
            BatchMetadataRange(metadata.Slot),
            metadata.InstanceCount,
            generation,
            metadata.ContentRevision)
        {
            PropertyDataEndpoints = _propertyData[generation]?.CaptureEndpoints() ?? [],
            MetadataEndpoints = _batchMetadata[generation].CaptureEndpoints(),
        };
    }

    internal void StoreReadStates(int generation, ReadOnlySpan<BufferBoundaryState> properties,
        ReadOnlySpan<BufferBoundaryState> metadata)
    {
        if ((uint)generation >= (uint)_batchMetadata.Length)
            throw new ArgumentOutOfRangeException(nameof(generation));
        _propertyData[generation]?.StoreStates(properties);
        _batchMetadata[generation].StoreStates(metadata);
    }

    public RenderInstanceDiagnostics CaptureDiagnostics()
    {
        ThrowIfNotActive();
        return new RenderInstanceDiagnostics(
            Options.RowCapacity,
            Options.BatchCapacity,
            _activeRowCount,
            _activeBatchCount,
            PropertyDataBytes);
    }

    internal BatchLease AllocateBatch(RenderInstancePropertyLayout contract, int instanceCount)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
        foreach (RenderInstancePropertyDescriptor property in contract.Properties)
            _ = Layout.RequireCompatible(property, nameof(contract));

        _ = RequireWriteGeneration();
        int batchSlot = AllocateBatchSlot();
        object allocation = new();
        PackedRange rows = default;
        bool rowsAllocated = false;
        bool batchActivated = false;
        try
        {
            rows = AllocateRows(instanceCount);
            rowsAllocated = true;
            if (_batchAllocations[batchSlot] is not null)
                throw new InvalidOperationException($"Render instance batch slot {batchSlot} is already active.");

            _batchAllocations[batchSlot] = allocation;
            _batchReady[batchSlot] = false;
            _batchRows[batchSlot] = rows;
            _activeBatchCount++;
            _activeRowCount = checked(_activeRowCount + rows.Count);
            batchActivated = true;
            ResetSharedBatch(batchSlot);
            ResetRows(rows);
            ResetBatchMetadata(batchSlot);
            return new BatchLease(batchSlot, allocation, contract, rows);
        }
        catch
        {
            if (batchActivated)
            {
                _batchAllocations[batchSlot] = null;
                _batchReady[batchSlot] = false;
                _batchRows[batchSlot] = default;
                _activeBatchCount--;
                _activeRowCount -= rows.Count;
            }
            if (rowsAllocated)
                ReleaseRows(rows);
            _freeBatchSlots.Push(batchSlot);
            throw;
        }
    }

    internal void BindShared<T>(
        int batchSlot,
        object allocation,
        RenderInstancePropertyLayout contract,
        ResolvedRenderInstanceProperty<T> property,
        in T value)
        where T : unmanaged
    {
        ValidateBatch(batchSlot, allocation);
        contract.Validate(property, nameof(property));
        PropertyStorage storage = RequireStorage(property, nameof(property));
        RequireLinearBinding(storage.Descriptor, nameof(property));
        int generation = RequireWriteGeneration();
        BufferRange propertyRange = SharedElementRange(storage, batchSlot);
        Span<byte> propertyMapping = GetWriteBytes(RequirePropertyData(generation).Handle, propertyRange);
        Span<byte> element = propertyMapping;
        element.Clear();
        RenderInstancePropertyValue<T>.Write(element, in value);
        BufferRange metadataRange = BatchMetadataRange(batchSlot);
        Span<byte> metadataMapping = GetWriteBytes(_batchMetadata[generation].Handle, metadataRange);
        MemoryMarshal.Cast<byte, uint>(metadataMapping)
            [property.Descriptor.MetadataWordOffset] =
            checked((uint)SharedAddress(storage, batchSlot));
    }

    internal void BindPerInstance<T>(
        int batchSlot,
        object allocation,
        RenderInstancePropertyLayout contract,
        ResolvedRenderInstanceProperty<T> property)
        where T : unmanaged
    {
        ValidateBatch(batchSlot, allocation);
        contract.Validate(property, nameof(property));
        PropertyStorage storage = RequireStorage(property, nameof(property));
        RequireLinearBinding(storage.Descriptor, nameof(property));
        PackedRange rows = _batchRows[batchSlot];
        int address = checked(storage.InstanceBase + rows.Start * property.Encoding.StorageStride);
        int generation = RequireWriteGeneration();
        BufferRange metadataRange = BatchMetadataRange(batchSlot);
        Span<byte> metadataMapping = GetWriteBytes(_batchMetadata[generation].Handle, metadataRange);
        MemoryMarshal.Cast<byte, uint>(metadataMapping)
            [property.Descriptor.MetadataWordOffset] =
            checked((uint)address) | RenderInstanceLinearMetadata.PerInstanceBit;
    }

    internal void BindEncodedPerInstance(
        int batchSlot,
        object allocation,
        RenderInstancePropertyLayout contract,
        RenderInstancePropertyDescriptor property)
    {
        ValidateBatch(batchSlot, allocation);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(property);
        RenderInstancePropertyDescriptor destination =
            contract.RequireCompatible(property, nameof(property));
        PropertyStorage storage = RequireStorage(destination, nameof(property));
        RequireLinearBinding(storage.Descriptor, nameof(property));
        PackedRange rows = _batchRows[batchSlot];
        int address = checked(
            storage.InstanceBase + rows.Start * destination.Encoding.StorageStride);
        int generation = RequireWriteGeneration();
        BufferRange metadataRange = BatchMetadataRange(batchSlot);
        Span<byte> metadataMapping = GetWriteBytes(_batchMetadata[generation].Handle, metadataRange);
        MemoryMarshal.Cast<byte, uint>(metadataMapping)
            [destination.MetadataWordOffset] =
            checked((uint)address) | RenderInstanceLinearMetadata.PerInstanceBit;
    }

    /// <summary>
    /// Copies one borrowed source span directly into its final mapped GPU column. No CPU staging
    /// array or per-property cache is created. Callers normally pass an ECS chunk span here while
    /// its read-snapshot callback is active.
    /// </summary>
    internal void WriteInstances<T>(
        int batchSlot,
        object allocation,
        RenderInstancePropertyLayout contract,
        ResolvedRenderInstanceProperty<T> property,
        int destinationIndex,
        ReadOnlySpan<T> source)
        where T : unmanaged
    {
        ValidateBatch(batchSlot, allocation);
        contract.Validate(property, nameof(property));
        PackedRange rows = _batchRows[batchSlot];
        if ((uint)destinationIndex > (uint)rows.Count
            || source.Length > rows.Count - destinationIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        }
        if (source.Length == 0)
            return;

        PropertyStorage storage = RequireStorage(property, nameof(property));
        int stride = property.Encoding.StorageStride;
        int valueSize = property.Encoding.ValueSize;
        int firstRow = checked(rows.Start + destinationIndex);
        BufferRange destinationRange = new(
            checked((ulong)(storage.InstanceBase + firstRow * stride)),
            checked((ulong)(source.Length * stride)));
        Span<byte> mapping = GetWriteBytes(RequirePropertyData(RequireWriteGeneration()).Handle, destinationRange);
        Span<byte> destination = mapping;

        if (stride == valueSize)
        {
            MemoryMarshal.AsBytes(source).CopyTo(destination);
            return;
        }

        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source);
        for (int index = 0; index < source.Length; index++)
        {
            Span<byte> element = destination.Slice(index * stride, stride);
            element.Clear();
            bytes.Slice(index * valueSize, valueSize).CopyTo(element);
        }
    }

    internal void WriteEncodedInstances(
        int batchSlot,
        object allocation,
        RenderInstancePropertyLayout contract,
        RenderInstancePropertyDescriptor property,
        int destinationIndex,
        int sourceCount,
        ReadOnlySpan<byte> source)
    {
        ValidateBatch(batchSlot, allocation);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(property);
        RenderInstancePropertyDescriptor destinationProperty =
            contract.RequireCompatible(property, nameof(property));
        PackedRange rows = _batchRows[batchSlot];
        if ((uint)destinationIndex > (uint)rows.Count
            || (uint)sourceCount > (uint)(rows.Count - destinationIndex))
        {
            throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        }

        int valueSize = destinationProperty.Encoding.ValueSize;
        if (source.Length != checked(sourceCount * valueSize))
        {
            throw new ArgumentException(
                $"Property '{destinationProperty.Key}' supplied {source.Length} encoded bytes for " +
                $"{sourceCount} rows; exactly {checked(sourceCount * valueSize)} are required.",
                nameof(source));
        }
        if (sourceCount == 0)
            return;

        PropertyStorage storage = RequireStorage(destinationProperty, nameof(property));
        int stride = destinationProperty.Encoding.StorageStride;
        int firstRow = checked(rows.Start + destinationIndex);
        BufferRange destinationRange = new(
            checked((ulong)(storage.InstanceBase + firstRow * stride)),
            checked((ulong)(sourceCount * stride)));
        Span<byte> mapping = GetWriteBytes(RequirePropertyData(RequireWriteGeneration()).Handle, destinationRange);
        Span<byte> destination = mapping;

        if (stride == valueSize)
        {
            source.CopyTo(destination);
            return;
        }

        for (int index = 0; index < sourceCount; index++)
        {
            Span<byte> element = destination.Slice(index * stride, stride);
            element.Clear();
            source.Slice(index * valueSize, valueSize).CopyTo(element);
        }
    }

    internal void WriteInstance<T>(
        int batchSlot,
        object allocation,
        RenderInstancePropertyLayout contract,
        ResolvedRenderInstanceProperty<T> property,
        int destinationIndex,
        in T value)
        where T : unmanaged
    {
        ValidateBatch(batchSlot, allocation);
        contract.Validate(property, nameof(property));
        PackedRange rows = _batchRows[batchSlot];
        if ((uint)destinationIndex >= (uint)rows.Count)
            throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        PropertyStorage storage = RequireStorage(property, nameof(property));
        BufferRange range = InstanceElementRange(storage, checked(rows.Start + destinationIndex));
        Span<byte> mapping = GetWriteBytes(RequirePropertyData(RequireWriteGeneration()).Handle, range);
        Span<byte> element = mapping;
        element.Clear();
        RenderInstancePropertyValue<T>.Write(element, in value);
    }

    internal void BindMetadata<T>(
        int batchSlot,
        object allocation,
        RenderInstancePropertyLayout contract,
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<uint> words)
        where T : unmanaged
    {
        ValidateBatch(batchSlot, allocation);
        contract.Validate(property, nameof(property));
        int expected = property.Encoding.MetadataWordCount;
        if (words.Length != expected)
        {
            throw new ArgumentException(
                $"Property '{property.Key}' requires exactly {expected} metadata words.",
                nameof(words));
        }
        int generation = RequireWriteGeneration();
        BufferRange metadataRange = BatchMetadataRange(batchSlot);
        Span<byte> mapping = GetWriteBytes(_batchMetadata[generation].Handle, metadataRange);
        words.CopyTo(MemoryMarshal.Cast<byte, uint>(mapping).Slice(
            property.Descriptor.MetadataWordOffset,
            expected));
    }

    internal RenderInstanceBatch BuildBatch(
        int batchSlot,
        object allocation,
        RenderInstancePropertyLayout contract)
    {
        ValidateBatch(batchSlot, allocation);
        _batchGenerations[batchSlot] = RequireWriteGeneration();
        _batchReady[batchSlot] = true;
        return new RenderInstanceBatch(
            this,
            allocation,
            batchSlot,
            contract,
            _batchRows[batchSlot].Count,
            NextContentRevision());
    }

    internal void ReleaseBatch(RenderInstanceBatch metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateBatchOwner(metadata, nameof(metadata));
        ReleaseBatch(metadata.Slot, metadata.Allocation);
    }

    internal void CancelBatch(int batchSlot, object allocation) =>
        ReleaseBatch(batchSlot, allocation);

    internal BatchLease BeginBatchUpdate(
        RenderInstanceBatch batch,
        RenderInstancePropertyLayout properties)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(properties);
        ValidateBatchOwner(batch, nameof(batch));
        ValidateBatch(batch.Slot, batch.Allocation);
        ValidateBatchReady(batch.Slot);
        foreach (RenderInstancePropertyDescriptor property in properties.Properties)
            _ = batch.Contract.RequireCompatible(property, nameof(properties));
        int writeGeneration = RequireWriteGeneration();
        int publishedGeneration = _batchGenerations[batch.Slot];
        if (writeGeneration != publishedGeneration)
            CopyBatchState(batch.Slot, _batchRows[batch.Slot], publishedGeneration, writeGeneration);
        else
            _batchOriginalBytes.Add(batch.Slot, CaptureBatchBytes(batch.Slot, properties, writeGeneration));
        _batchReady[batch.Slot] = false;
        return new BatchLease(batch.Slot, batch.Allocation, batch.Contract, _batchRows[batch.Slot]);
    }

    internal ulong CompleteBatchUpdate(int batchSlot, object allocation)
    {
        ValidateBatch(batchSlot, allocation);
        int generation = RequireWriteGeneration();
        ulong revision = NextContentRevision();
        _batchGenerations[batchSlot] = generation;
        _batchReady[batchSlot] = true;
        ReleaseBatchBytes(batchSlot);
        return revision;
    }

    internal void CancelBatchUpdate(int batchSlot, object allocation)
    {
        ValidateBatch(batchSlot, allocation);
        if (_batchOriginalBytes.TryGetValue(batchSlot, out BufferBytes[]? original))
        {
            foreach (BufferBytes value in original)
            {
                Span<byte> mapping = GetWriteBytes(value.Resource, value.Range);
                value.Data.AsSpan(0, checked((int)value.Range.Size)).CopyTo(mapping);
            }
            ReleaseBatchBytes(batchSlot);
        }
        _batchReady[batchSlot] = true;
    }

    private void ReleaseBatchBytes(int batchIndex)
    {
        if (!_batchOriginalBytes.Remove(batchIndex, out BufferBytes[]? bytes)) return;
        foreach (BufferBytes value in bytes)
            ArrayPool<byte>.Shared.Return(value.Data);
    }

    private BufferBytes[] CaptureBatchBytes(int batchIndex, RenderInstancePropertyLayout properties, int generation)
    {
        var bytes = new List<BufferBytes>();
        try
        {
            Capture(_batchMetadata[generation].Handle, BatchMetadataRange(batchIndex));
            if (_propertyData[generation] is { } data)
            {
                PackedRange rows = _batchRows[batchIndex];
                foreach (PropertyStorage storage in _properties)
                {
                    RenderInstancePropertyEncoding encoding = storage.Descriptor.Encoding;
                    if (!encoding.HasManagedStorage || !properties.Contains(storage.Descriptor.Key)) continue;
                    Capture(data.Handle, SharedElementRange(storage, batchIndex));
                    Capture(data.Handle, new BufferRange(
                        checked((ulong)(storage.InstanceBase + rows.Start * encoding.StorageStride)),
                        checked((ulong)(rows.Count * encoding.StorageStride))));
                }
            }
            return bytes.ToArray();
        }
        catch
        {
            foreach (BufferBytes value in bytes)
                ArrayPool<byte>.Shared.Return(value.Data);
            throw;
        }

        void Capture(Buffer resource, BufferRange range)
        {
            if (range.Size == 0) return;
            int size = checked((int)range.Size);
            byte[] data = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                GetWriteMapping(resource).Read(data.AsSpan(0, size), checked((int)range.Offset));
                bytes.Add(new BufferBytes(resource, range, data));
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(data);
                throw;
            }
        }
    }

    private ulong NextContentRevision()
    {
        _nextContentRevision = checked(_nextContentRevision + 1ul);
        return _nextContentRevision;
    }

    private void CopyBatchState(
        int batchSlot,
        PackedRange rows,
        int sourceGeneration,
        int destinationGeneration)
    {
        if (sourceGeneration == destinationGeneration)
            return;

        BufferRange metadataRange = BatchMetadataRange(batchSlot);
        using (MappedBuffer sourceMetadata = _backend.Map(
                   _batchMetadata[sourceGeneration].Handle,
                   MapType.Read,
                   metadataRange))
        {
            sourceMetadata.Read(GetWriteBytes(_batchMetadata[destinationGeneration].Handle, metadataRange));
        }

        UploadBuffer? sourceData = _propertyData[sourceGeneration];
        UploadBuffer? destinationData = _propertyData[destinationGeneration];
        if (sourceData is null || destinationData is null)
            return;

        BufferRange fullRange = new(0, sourceData.Size);
        using MappedBuffer sourceMapping = _backend.Map(
            sourceData.Handle,
            MapType.Read,
            fullRange);
        Span<byte> destinationMapping = GetWriteBytes(destinationData.Handle, fullRange);
        for (int ordinal = 0; ordinal < _properties.Length; ordinal++)
        {
            PropertyStorage storage = _properties[ordinal];
            RenderInstancePropertyEncoding encoding = storage.Descriptor.Encoding;
            if (!encoding.HasManagedStorage)
                continue;

            BufferRange shared = SharedElementRange(storage, batchSlot);
            sourceMapping.Read(destinationMapping.Slice(
                checked((int)shared.Offset), checked((int)shared.Size)), checked((int)shared.Offset));

            int instanceOffset = checked(
                storage.InstanceBase + rows.Start * encoding.StorageStride);
            int instanceBytes = checked(rows.Count * encoding.StorageStride);
            sourceMapping.Read(destinationMapping.Slice(instanceOffset, instanceBytes), instanceOffset);
        }
    }

    internal T ReadInstance<T>(
        RenderInstanceBatch metadata,
        int instanceIndex,
        ResolvedRenderInstanceProperty<T> property)
        where T : unmanaged
    {
        ValidateBatchOwner(metadata, nameof(metadata));
        ValidateBatch(metadata.Slot, metadata.Allocation);
        ValidateBatchReady(metadata.Slot);
        if ((uint)instanceIndex >= (uint)metadata.InstanceCount)
            throw new ArgumentOutOfRangeException(nameof(instanceIndex));
        PropertyStorage storage = RequireStorage(property, nameof(property));
        PackedRange rows = _batchRows[metadata.Slot];
        int generation = _batchGenerations[metadata.Slot];
        BufferRange range = InstanceElementRange(storage, checked(rows.Start + instanceIndex));
        bool writing = generation == _writeGeneration && _propertyWrite.HasValue;
        using MappedBuffer mapping = writing ? default : _backend.Map(
            RequirePropertyData(generation).Handle, MapType.Read, range);
        return RenderInstancePropertyValue<T>.Read(writing
            ? GetWriteBytes(RequirePropertyData(generation).Handle, range) : mapping.Bytes);
    }

    internal void ReadInstances<T>(RenderInstanceBatch batch,
        ResolvedRenderInstanceProperty<T> property, Span<T> destination) where T : unmanaged
    {
        ValidateBatchOwner(batch, nameof(batch));
        ValidateBatch(batch.Slot, batch.Allocation);
        ValidateBatchReady(batch.Slot);
        if (destination.Length != batch.InstanceCount)
            throw new ArgumentException("The destination must cover the batch.", nameof(destination));
        if (destination.IsEmpty) return;
        PropertyStorage storage = RequireStorage(property, nameof(property));
        uint metadata = ReadMetadataWord(batch, property.Descriptor.MetadataWordOffset);
        bool perInstance = (metadata & 0x80000000u) != 0u;
        int stride = storage.Descriptor.Encoding.StorageStride;
        ulong offset = metadata & 0x7FFFFFFFu;
        ulong bytes = checked((ulong)(perInstance ? destination.Length : 1) * (ulong)stride);
        int generation = _batchGenerations[batch.Slot];
        Buffer resource = RequirePropertyData(generation).Handle;
        BufferRange range = new(offset, bytes);
        bool writing = generation == _writeGeneration && _propertyWrite.HasValue;
        using MappedBuffer mapping = writing ? default : _backend.Map(resource, MapType.Read, range);
        if (perInstance && stride == Unsafe.SizeOf<T>())
        {
            MappedBuffer sourceMapping = writing ? GetWriteMapping(resource) : mapping;
            sourceMapping.Read(MemoryMarshal.AsBytes(destination), writing ? checked((int)range.Offset) : 0);
            return;
        }
        ReadOnlySpan<byte> source = writing ? GetWriteBytes(resource, range) : mapping.Bytes;
        for (int index = 0; index < destination.Length; index++)
            destination[index] = RenderInstancePropertyValue<T>.Read(source.Slice(perInstance ? index * stride : 0, stride));
    }

    internal uint ReadMetadataWord(RenderInstanceBatch metadata, int word)
    {
        ValidateBatchOwner(metadata, nameof(metadata));
        ValidateBatch(metadata.Slot, metadata.Allocation);
        ValidateBatchReady(metadata.Slot);
        if ((uint)word >= (uint)metadata.Contract.MetadataWordCount)
            throw new ArgumentOutOfRangeException(nameof(word));
        int generation = _batchGenerations[metadata.Slot];
        BufferRange metadataRange = BatchMetadataRange(metadata.Slot);
        bool writing = generation == _writeGeneration && _metadataWrite.HasValue;
        using MappedBuffer mapping = writing ? default : _backend.Map(
            _batchMetadata[generation].Handle, MapType.Read, metadataRange);
        ReadOnlySpan<byte> source = writing
            ? GetWriteBytes(_batchMetadata[generation].Handle, metadataRange) : mapping.Bytes;
        return MemoryMarshal.Cast<byte, uint>(source)[word];
    }

    public void Shutdown(RenderPrepareScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnterOperation(allowShutdown: true);
        try
        {
            lock (_lifecycleGate)
            {
                if (_lifecycleState == LifecycleDisposed)
                    throw new ObjectDisposedException(nameof(RenderInstanceResources));
                if (_lifecycleState == LifecycleActive
                    && (_activeBorrowerCount != 0 || _activeBatchCount != 0))
                {
                    throw new InvalidOperationException(
                        "Batch instance resources cannot shut down while " +
                        $"{_activeBorrowerCount} borrower(s) and {_activeBatchCount} batch(es) are live.");
                }

                using RenderTimelineLease lease = scope.AcquireTimeline(_timeline);
                _lifecycleState = LifecycleShutdown;
                _shutdownScope = scope;
            }
        }
        finally
        {
            ExitOperation();
        }
    }

    internal void ReleaseBorrower()
    {
        lock (_lifecycleGate)
        {
            if (_activeBorrowerCount <= 0)
                throw new InvalidOperationException("Render instance borrower ownership was lost.");
            _activeBorrowerCount--;
        }
    }

    internal void EndWrite()
    {
        try
        {
            CloseWriteMappings();
            _generationNeedsFlush[RequireWriteGeneration()] = false;
        }
        finally
        {
            // Reuse the current buffer when its readers have finished; the selection
            // path still moves to the other buffer whenever that one is busy.
            if (_writeGeneration >= 0) _preferredGeneration = _writeGeneration;
            _writeGeneration = -1;
            ExitOperation();
        }
    }

    private void CloseWriteMappings(bool flush = true)
    {
        try { Close(_propertyWrite); }
        finally
        {
            _propertyWrite = null;
            try { Close(_metadataWrite); }
            finally { _metadataWrite = null; }
        }

        void Close(MappedBuffer? mapping)
        {
            if (mapping is not { } value) return;
            try { if (flush) value.Flush(value.Range); }
            finally { value.Dispose(); }
        }
    }

    private MappedBuffer GetWriteMapping(Buffer resource)
    {
        int generation = RequireWriteGeneration();
        return ReferenceEquals(resource, _batchMetadata[generation].Handle)
            ? _metadataWrite.GetValueOrDefault() : _propertyWrite.GetValueOrDefault();
    }

    private Span<byte> GetWriteBytes(Buffer resource, BufferRange range) =>
        GetWriteMapping(resource).Bytes.Slice(checked((int)range.Offset), checked((int)range.Size));

    private int AllocateBatchSlot()
    {
        if (_freeBatchSlots.Count != 0)
            return _freeBatchSlots.Pop();
        if (_nextUnusedBatchSlot == Options.BatchCapacity)
            throw new InvalidOperationException("Render instance batch capacity is exhausted.");
        return _nextUnusedBatchSlot++;
    }

    private PackedRange AllocateRows(int count)
    {
        if (count == 0) return default;
        for (int index = 0; index < _freeRows.Count; index++)
        {
            PackedRange free = _freeRows[index];
            if (free.Count < count)
                continue;
            var allocated = new PackedRange(free.Start, count);
            if (free.Count == count)
                _freeRows.RemoveAt(index);
            else
                _freeRows[index] = new PackedRange(free.Start + count, free.Count - count);
            return allocated;
        }
        throw new InvalidOperationException(
            $"Render instance row capacity cannot fit a contiguous batch of {count} rows.");
    }

    private void ReleaseRows(PackedRange released)
    {
        if (released.Count == 0) return;
        int insertion = 0;
        while (insertion < _freeRows.Count && _freeRows[insertion].Start < released.Start)
            insertion++;
        _freeRows.Insert(insertion, released);

        if (insertion > 0)
        {
            PackedRange previous = _freeRows[insertion - 1];
            PackedRange current = _freeRows[insertion];
            if (previous.End == current.Start)
            {
                _freeRows[insertion - 1] = new PackedRange(
                    previous.Start,
                    checked(previous.Count + current.Count));
                _freeRows.RemoveAt(insertion);
                insertion--;
            }
        }
        if (insertion + 1 < _freeRows.Count)
        {
            PackedRange current = _freeRows[insertion];
            PackedRange next = _freeRows[insertion + 1];
            if (current.End == next.Start)
            {
                _freeRows[insertion] = new PackedRange(
                    current.Start,
                    checked(current.Count + next.Count));
                _freeRows.RemoveAt(insertion + 1);
            }
        }
    }

    private void ReleaseBatch(int batchSlot, object allocation)
    {
        ValidateBatch(batchSlot, allocation);
        PackedRange rows = _batchRows[batchSlot];
        ReleaseBatchBytes(batchSlot);
        _batchAllocations[batchSlot] = null;
        _batchReady[batchSlot] = false;
        _batchRows[batchSlot] = default;
        _activeBatchCount--;
        _activeRowCount -= rows.Count;
        ReleaseRows(rows);
        _freeBatchSlots.Push(batchSlot);
    }

    private PropertyStorage RequireStorage<T>(
        ResolvedRenderInstanceProperty<T> property,
        string parameterName)
        where T : unmanaged
    {
        if (!property.IsValid)
            throw new ArgumentException("The resolved render-instance property is uninitialized.", parameterName);
        RenderInstancePropertyDescriptor descriptor = property.BelongsTo(Layout)
            ? property.Descriptor
            : Layout.RequireCompatible(property.Descriptor, parameterName);
        PropertyStorage storage = _properties[descriptor.Ordinal];
        if (!storage.Descriptor.Encoding.HasManagedStorage)
        {
            throw new ArgumentException(
                $"Property '{descriptor.Key}' owns its metadata interpretation and has no linear storage.",
                parameterName);
        }
        return storage;
    }

    private PropertyStorage RequireStorage(
        RenderInstancePropertyDescriptor property,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(property);
        RenderInstancePropertyDescriptor descriptor =
            Layout.RequireCompatible(property, parameterName);
        PropertyStorage storage = _properties[descriptor.Ordinal];
        if (!storage.Descriptor.Encoding.HasManagedStorage)
        {
            throw new ArgumentException(
                $"Property '{descriptor.Key}' owns its metadata interpretation and has no linear storage.",
                parameterName);
        }
        return storage;
    }

    private void ResetRows(PackedRange rows)
    {
        int generation = RequireWriteGeneration();
        UploadBuffer? propertyData = _propertyData[generation];
        if (propertyData is null) return;
        BufferRange fullRange = new(0, propertyData.Size);
        Span<byte> mapping = GetWriteBytes(propertyData.Handle, fullRange);
        for (int index = 0; index < _properties.Length; index++)
        {
            PropertyStorage storage = _properties[index];
            if (!storage.Descriptor.Encoding.HasManagedStorage)
                continue;
            mapping.Slice(
                checked(storage.InstanceBase + rows.Start * storage.Descriptor.Encoding.StorageStride),
                checked(rows.Count * storage.Descriptor.Encoding.StorageStride)).Clear();
        }
    }

    private void ResetSharedBatch(int batchIndex)
    {
        int generation = RequireWriteGeneration();
        UploadBuffer? propertyData = _propertyData[generation];
        if (propertyData is null)
            return;
        BufferRange fullRange = new(0, propertyData.Size);
        Span<byte> mapping = GetWriteBytes(propertyData.Handle, fullRange);
        for (int index = 0; index < _properties.Length; index++)
        {
            PropertyStorage storage = _properties[index];
            if (storage.Descriptor.Encoding.HasManagedStorage)
            {
                BufferRange range = SharedElementRange(storage, batchIndex);
                mapping.Slice(checked((int)range.Offset), checked((int)range.Size)).Clear();
            }
        }
    }

    private void ValidateBatch(int batchSlot, object allocation)
    {
        if ((uint)batchSlot >= (uint)Options.BatchCapacity)
            throw new ArgumentOutOfRangeException(nameof(batchSlot));
        if (!ReferenceEquals(_batchAllocations[batchSlot], allocation))
            throw new InvalidOperationException("The render instance batch allocation is no longer live.");
    }

    private void ValidateBatchReady(int batchSlot)
    {
        if (!_batchReady[batchSlot])
        {
            throw new InvalidOperationException(
                "The render instance batch has not completed publication.");
        }
    }

    private void ValidateBatchOwner(RenderInstanceBatch metadata, string parameterName)
    {
        if (!ReferenceEquals(metadata.Owner, this))
        {
            throw new ArgumentException(
                "Batch metadata belongs to different render instance storage.",
                parameterName);
        }
    }

    private void EnterOperation(bool allowShutdown = false)
    {
        lock (_lifecycleGate)
        {
            if (_lifecycleState == LifecycleDisposed
                || (!allowShutdown && _lifecycleState != LifecycleActive))
            {
                throw new ObjectDisposedException(nameof(RenderInstanceResources));
            }
            if (_operationActive != 0)
                throw new InvalidOperationException("Render instance preparation is already active.");
            _operationActive = 1;
        }
    }

    private void ExitOperation()
    {
        lock (_lifecycleGate)
        {
            if (_operationActive == 0)
                throw new InvalidOperationException("Render instance operation ownership was lost.");
            _operationActive = 0;
            Monitor.PulseAll(_lifecycleGate);
        }
    }

    private void ThrowIfNotActive()
    {
        lock (_lifecycleGate)
        {
            if (_lifecycleState != LifecycleActive)
                throw new ObjectDisposedException(nameof(RenderInstanceResources));
        }
    }

    private static BufferRange SharedElementRange(PropertyStorage storage, int batchIndex) =>
        new(
            checked((ulong)(storage.SharedBase +
                batchIndex * storage.Descriptor.Encoding.StorageStride)),
            checked((ulong)storage.Descriptor.Encoding.StorageStride));

    private static BufferRange InstanceElementRange(PropertyStorage storage, int row) =>
        new(
            checked((ulong)(storage.InstanceBase +
                row * storage.Descriptor.Encoding.StorageStride)),
            checked((ulong)storage.Descriptor.Encoding.StorageStride));

    private UploadBuffer RequirePropertyData(int generation) =>
        _propertyData[generation] ?? throw new InvalidOperationException(
            "The property contract does not allocate standard linear storage.");

    private BufferRange BatchMetadataRange(int batchIndex) =>
        new(
            checked((ulong)batchIndex * (ulong)_batchMetadataStrideBytes),
            checked((ulong)_batchMetadataStrideBytes));

    private void ResetBatchMetadata(int batchIndex)
    {
        int generation = RequireWriteGeneration();
        BufferRange range = BatchMetadataRange(batchIndex);
        Span<byte> mapping = GetWriteBytes(_batchMetadata[generation].Handle, range);
        mapping.Clear();
    }

    private int SelectWritableGeneration() => SelectWritableGeneration(out _);

    private int SelectWritableGeneration(out int availableGenerationCount)
    {
        if (TrySelectWritableGeneration(
                out int admitted,
                out availableGenerationCount,
                out _))
        {
            return admitted;
        }

        int preferred = _preferredGeneration;
        int alternate = 1 - preferred;
        long started = Environment.TickCount64;
        while (true)
        {
            TimeSpan remaining = GenerationWaitTimeout -
                TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"Both render instance generations remained in flight for {GenerationWaitTimeout}.");
            }

            TimeSpan slice = remaining > TimeSpan.FromMilliseconds(1)
                ? TimeSpan.FromMilliseconds(1)
                : remaining;
            if (_timeline.WaitForGeneration(preferred, slice))
            {
                availableGenerationCount = 1;
                return preferred;
            }
            if (_timeline.IsGenerationAvailable(alternate))
            {
                availableGenerationCount = 1;
                return alternate;
            }
            (preferred, alternate) = (alternate, preferred);
        }
    }

    private bool TrySelectWritableGeneration(
        out int generation,
        out int availableGenerationCount,
        out QueueCompletion[] retirementFences)
    {
        int preferred = _preferredGeneration;
        int alternate = 1 - preferred;
        bool preferredAvailable = _timeline.IsGenerationAvailable(preferred);
        bool alternateAvailable = _timeline.IsGenerationAvailable(alternate);
        availableGenerationCount =
            (preferredAvailable ? 1 : 0) + (alternateAvailable ? 1 : 0);
        if (preferredAvailable)
        {
            generation = preferred;
            retirementFences = [];
            return true;
        }
        if (alternateAvailable)
        {
            generation = alternate;
            retirementFences = [];
            return true;
        }

        generation = -1;
        retirementFences = _timeline.GetGenerationFences(preferred);
        if (retirementFences.Length == 0)
        {
            throw new InvalidOperationException(
                "An unavailable render instance generation has no retirement position.");
        }
        return false;
    }

    private int RequireWriteGeneration()
    {
        if (_writeGeneration < 0)
        {
            throw new InvalidOperationException(
                "Render instance mapped storage has no admitted write generation.");
        }
        return _writeGeneration;
    }

    private static int SharedAddress(PropertyStorage storage, int batchIndex) =>
        checked(storage.SharedBase + batchIndex * storage.Descriptor.Encoding.StorageStride);

    private static void RequireLinearBinding(
        RenderInstancePropertyDescriptor descriptor,
        string parameterName)
    {
        if (!descriptor.Encoding.HasManagedStorage || descriptor.Encoding.MetadataWordCount != 1)
        {
            throw new ArgumentException(
                $"Property '{descriptor.Key}' does not use the standard one-word linear-storage encoding.",
                parameterName);
        }
    }

    private static PropertyStorage[] BuildStorage(
        RenderInstancePropertyLayout layout,
        RenderInstanceOptions options,
        int expectedBytes)
    {
        var storage = new PropertyStorage[layout.Properties.Count];
        int cursor = 0;
        for (int index = 0; index < layout.Properties.Count; index++)
        {
            RenderInstancePropertyDescriptor property = layout.Properties[index];
            RenderInstancePropertyEncoding encoding = property.Encoding;
            if (!encoding.HasManagedStorage)
            {
                storage[property.Ordinal] = new PropertyStorage(property, -1, -1);
                continue;
            }
            cursor = AlignUp(cursor, encoding.StorageAlignment);
            int sharedBase = cursor;
            cursor = checked(cursor + options.BatchCapacity * encoding.StorageStride);
            cursor = AlignUp(cursor, encoding.StorageAlignment);
            int instanceBase = cursor;
            cursor = checked(cursor + options.RowCapacity * encoding.StorageStride);
            storage[property.Ordinal] = new PropertyStorage(property, sharedBase, instanceBase);
        }
        if (cursor != expectedBytes)
            throw new InvalidOperationException("Render-instance property storage measurement is inconsistent.");
        return storage;
    }

    private static int AlignUp(int value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    private static UploadBuffer CreateMappedBuffer(
        IGraphicsBackend backend,
        Device device,
        ulong size,
        string name) =>
        new(
            backend,
            device,
            new BufferDesc(size, BufferUsages.ShaderRead | BufferUsages.CopySource, name));

    private static UploadBuffer CreateMappedConstantBuffer(
        IGraphicsBackend backend,
        Device device,
        ulong size,
        string name) =>
        new(
            backend,
            device,
            new BufferDesc(size, BufferUsages.Constant | BufferUsages.CopySource, name));

    private static (int StrideBytes, int BufferBytes) MeasureBatchMetadata(
        Device device,
        RenderInstancePropertyLayout layout,
        RenderInstanceOptions options)
    {
        DeviceLimits limits = device.Capabilities.Limits;
        uint deviceAlignment = limits.ConstantBufferAlignment;
        if (deviceAlignment == 0 || deviceAlignment > int.MaxValue)
        {
            throw new ArgumentException(
                "The device must report a usable constant-buffer offset alignment.",
                nameof(device));
        }

        int payloadBytes = checked(layout.MetadataWordCount * sizeof(uint));
        if (payloadBytes == 0 || payloadBytes > MaxConstantBufferRangeBytes)
        {
            throw new ArgumentException(
                $"A render-instance batch contract must occupy between 1 and " +
                $"{MaxConstantBufferRangeBytes} constant-buffer bytes.",
                nameof(layout));
        }

        long strideBytes = checked(
            ((long)payloadBytes + deviceAlignment - 1L) / deviceAlignment * deviceAlignment);
        if (strideBytes > MaxConstantBufferRangeBytes)
        {
            throw new ArgumentException(
                "The aligned render-instance batch metadata range exceeds the portable constant-buffer limit.",
                nameof(layout));
        }

        long bufferBytes = checked(strideBytes * options.BatchCapacity);
        if (limits.MaximumBufferSize == 0 || checked((ulong)bufferBytes) > limits.MaximumBufferSize)
        {
            throw new ArgumentException(
                "The render-instance batch metadata buffer exceeds the device maximum buffer size.",
                nameof(options));
        }
        if (bufferBytes > int.MaxValue)
        {
            throw new ArgumentException(
                "The render-instance batch metadata buffer is too large to remain persistently mapped.",
                nameof(options));
        }
        return (checked((int)strideBytes), checked((int)bufferBytes));
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_cleanupCompleted)
                return;
            lock (_lifecycleGate)
            {
                if (_lifecycleState == LifecycleActive)
                {
                    throw new InvalidOperationException(
                        "Batch instance resources must be shut down at a render prepare boundary before disposal.");
                }
                if (_operationActive != 0)
                    throw new InvalidOperationException("Batch instance shutdown is still active.");
                if (_shutdownScope is null || !_shutdownScope.IsCommitted)
                {
                    throw new InvalidOperationException(
                        "The prepare scope used for batch instance shutdown must commit before disposal.");
                }
                if (_lifecycleState == LifecycleDisposed)
                    return;
                _lifecycleState = LifecycleDisposed;
            }

            List<Exception>? failures = null;
            for (int generation = 0; generation < 2; generation++)
            {
                try
                {
                    _propertyData[generation]?.Dispose();
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
                try
                {
                    _batchMetadata[generation].Dispose();
                }
                catch (Exception error)
                {
                    (failures ??= []).Add(error);
                }
            }

            if (failures is not null)
            {
                lock (_lifecycleGate)
                    _lifecycleState = LifecycleShutdown;
                throw failures.Count == 1
                    ? failures[0]
                    : new AggregateException(
                        "Not every render-instance storage generation could be released.",
                        failures);
            }
            _cleanupCompleted = true;
        }
    }

    private static void TryDispose(IDisposable? value)
    {
        try
        {
            value?.Dispose();
        }
        catch
        {
        }
    }

    internal readonly record struct BatchLease(
        int Slot,
        object Allocation,
        RenderInstancePropertyLayout Contract,
        PackedRange Rows);

    internal readonly record struct PropertyStorage(
        RenderInstancePropertyDescriptor Descriptor,
        int SharedBase,
        int InstanceBase);

    internal readonly record struct PackedRange(int Start, int Count)
    {
        internal int End => checked(Start + Count);
    }

    private sealed class UploadBuffer : IDisposable
    {
        private readonly ulong _size;
        private BufferBoundaryState[] _states;

        internal UploadBuffer(
            IGraphicsBackend backend,
            Device device,
            in BufferDesc description)
        {
            _size = description.Size;
            Buffer? handle = null;
            try
            {
                handle = backend.CreateBuffer(device, description, MemoryType.Upload);
                Handle = handle;
                _states = [new BufferBoundaryState(
                    new BufferRange(0, description.Size),
                    handle.InitialSync,
                    handle.InitialAccess,
                    ResourceContentState.Defined)];
            }
            catch
            {
                if (handle is not null)
                {
                    try
                    {
                        handle.Dispose();
                    }
                    catch
                    {
                    }
                }
                throw;
            }
        }

        internal Buffer Handle { get; }

        internal ulong Size => _size;

        internal BufferBoundaryState[] CaptureEndpoints() => (BufferBoundaryState[])_states.Clone();

        internal void StoreStates(ReadOnlySpan<BufferBoundaryState> states)
        {
            if (!states.IsEmpty)
                _states = states.ToArray();
        }

        public void Dispose() => Handle.Dispose();
    }
}

/// <summary>Keeps the batch-storage arena alive while a rendering subsystem retains its ABI.</summary>
internal sealed class RenderInstanceBorrowerLease : IDisposable
{
    private RenderInstanceResources? _owner;

    internal RenderInstanceBorrowerLease(RenderInstanceResources owner) => _owner = owner;

    public void Dispose()
    {
        RenderInstanceResources? owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
            return;
        try
        {
            owner.ReleaseBorrower();
        }
        catch
        {
            _ = Interlocked.CompareExchange(ref _owner, owner, null);
            throw;
        }
    }
}

/// <summary>
/// Exclusive prepare capability for allocating and filling batch-local rows. It never exposes an
/// entity slot because physical rows exist only inside child batch builders.
/// </summary>
internal sealed class RenderInstanceWriteScope : IDisposable
{
    private RenderInstanceResources? _owner;
    private readonly RenderPrepareScope _prepareScope;
    private RenderTimelineLease? _lease;
    private int _activeBatchBuilders;

    internal RenderInstanceWriteScope(
        RenderInstanceResources owner,
        RenderPrepareScope prepareScope,
        RenderTimelineLease lease)
    {
        _owner = owner;
        _prepareScope = prepareScope;
        _lease = lease;
    }

    internal RenderInstanceResources Resources =>
        _owner ?? throw new ObjectDisposedException(nameof(RenderInstanceWriteScope));

    internal RenderPrepareScope PrepareScope
    {
        get
        {
            _ = Resources;
            return _prepareScope;
        }
    }

    internal RenderInstanceBatchComposition BeginBatch(
        RenderInstancePropertyLayout contract,
        int instanceCount)
    {
        RenderInstanceResources.BatchLease lease = Resources.AllocateBatch(contract, instanceCount);
        try
        {
            var builder = new RenderInstanceBatchComposition(this, lease);
            _activeBatchBuilders++;
            return builder;
        }
        catch
        {
            Resources.CancelBatch(lease.Slot, lease.Allocation);
            throw;
        }
    }

    internal RenderInstanceBatchComposition BeginBatchUpdate(
        RenderInstanceBatch batch,
        RenderInstancePropertyLayout properties)
    {
        RenderInstanceResources.BatchLease lease =
            Resources.BeginBatchUpdate(batch, properties);
        try
        {
            var composition = new RenderInstanceBatchComposition(this, lease, batch, properties);
            _activeBatchBuilders++;
            return composition;
        }
        catch
        {
            Resources.CancelBatchUpdate(lease.Slot, lease.Allocation);
            throw;
        }
    }

    internal void ReleaseBatch(RenderInstanceBatch metadata) => Resources.ReleaseBatch(metadata);

    public RenderInstanceDiagnostics CaptureDiagnostics() => Resources.CaptureDiagnostics();

    internal bool BelongsTo(RenderPrepareScope scope, RenderInstanceResources resources) =>
        ReferenceEquals(_prepareScope, scope) && ReferenceEquals(_owner, resources);

    internal void CompleteBatchBuilder()
    {
        if (_activeBatchBuilders <= 0)
            throw new InvalidOperationException("Render instance batch-builder ownership was lost.");
        _activeBatchBuilders--;
    }

    public void Dispose()
    {
        if (_owner is null)
            return;
        if (_activeBatchBuilders != 0)
        {
            throw new InvalidOperationException(
                "Every render instance batch builder must be built or disposed before its write scope.");
        }
        RenderInstanceResources? owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
            return;
        RenderTimelineLease? lease = Interlocked.Exchange(ref _lease, null);
        try
        {
            owner.EndWrite();
        }
        finally
        {
            lease?.Dispose();
        }
    }
}

/// <summary>
/// Writes one exact batch contract. Per-instance values are appended directly from borrowed ECS
/// spans into their final mapped GPU columns; no value array is retained by the builder.
/// </summary>
internal sealed class RenderInstanceBatchComposition : IDisposable
{
    private enum CompositionMode : byte
    {
        Create,
        Update,
    }

    private const long Unbound = 0;
    private const long Shared = 1;
    private const long PerInstance = 2;
    private const long CustomMetadata = 3;
    private const long BindingMask = 3;
    private const long Required = 4;
    private const int WrittenCountShift = 3;

    private RenderInstanceWriteScope? _scope;
    // Binding kind, requiredness, and the atomic written-row count describe one property and
    // share the same lifetime. Packing them into one word avoids three independently allocated
    // side tables without changing the composition's unique ownership.
    private readonly long[] _propertyStates;
    private readonly long[] _changedChunks;
    private readonly RenderInstancePropertyLayout _contract;
    private readonly RenderInstancePropertyLayout _authorization;
    private readonly RenderInstanceBatch? _existingBatch;
    private readonly CompositionMode _mode;
    private readonly int _batchSlot;
    private readonly object _allocation;
    private readonly int _instanceCount;

    internal RenderInstanceBatchComposition(
        RenderInstanceWriteScope scope,
        RenderInstanceResources.BatchLease lease)
        : this(scope, lease, null, lease.Contract, CompositionMode.Create)
    {
    }

    internal RenderInstanceBatchComposition(
        RenderInstanceWriteScope scope,
        RenderInstanceResources.BatchLease lease,
        RenderInstanceBatch existingBatch,
        RenderInstancePropertyLayout authorization)
        : this(scope, lease, existingBatch, authorization, CompositionMode.Update)
    {
    }

    private RenderInstanceBatchComposition(
        RenderInstanceWriteScope scope,
        RenderInstanceResources.BatchLease lease,
        RenderInstanceBatch? existingBatch,
        RenderInstancePropertyLayout authorization,
        CompositionMode mode)
    {
        if ((mode == CompositionMode.Create) != (existingBatch is null))
        {
            throw new ArgumentException(
                "A create composition cannot target an existing batch, and an update composition must target one.",
                nameof(existingBatch));
        }
        _scope = scope;
        _contract = lease.Contract;
        _authorization = authorization;
        _existingBatch = existingBatch;
        _mode = mode;
        _batchSlot = lease.Slot;
        _allocation = lease.Allocation;
        _instanceCount = lease.Rows.Count;
        _propertyStates = new long[lease.Contract.Properties.Count];
        _changedChunks = mode == CompositionMode.Update ? new long[(_instanceCount + 63) / 64] : [];
        if (mode == CompositionMode.Create)
        {
            foreach (RenderInstancePropertyDescriptor property in authorization.Properties)
            {
                RenderInstancePropertyDescriptor destination =
                    lease.Contract.RequireCompatible(property, nameof(authorization));
                _propertyStates[destination.Ordinal] |= Required;
            }
        }
    }

    internal int InstanceCount => _instanceCount;

    internal RenderInstancePropertyLayout Authorization => _authorization;

    internal RenderInstanceWriteSlice OpenWrite(RenderInstancePropertyLayout properties) =>
        OpenWrite(properties, 0, _instanceCount);

    internal RenderInstanceWriteSlice OpenWrite(
        RenderInstancePropertyLayout properties,
        int destinationStart,
        int count)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (!ReferenceEquals(properties, _authorization))
        {
            foreach (RenderInstancePropertyDescriptor property in properties.Properties)
                _ = _authorization.RequireCompatible(property, nameof(properties));
        }
        return CreateWriteSlice(properties, destinationStart, count);
    }

    internal RenderInstanceWriteSlice Restrict(
        RenderInstancePropertyLayout current,
        RenderInstancePropertyLayout requested,
        int destinationStart,
        int count)
    {
        ArgumentNullException.ThrowIfNull(requested);
        foreach (RenderInstancePropertyDescriptor property in requested.Properties)
            _ = current.RequireCompatible(property, nameof(requested));
        // The current slice was already authorized by this composition. Compatibility is
        // transitive, so validating the requested subset against it is sufficient and avoids
        // repeating the same root-authorization scan for every nested producer.
        return CreateWriteSlice(requested, destinationStart, count);
    }

    internal void BindShared<T>(
        RenderInstancePropertyLayout authorization,
        ResolvedRenderInstanceProperty<T> property,
        in T value)
        where T : unmanaged
    {
        ResolvedRenderInstanceProperty<T> destination = Resolve(authorization, property);
        RequireUnbound(destination.Ordinal);
        RequireScope().Resources.BindShared(
            _batchSlot,
            _allocation,
            _contract,
            destination,
            in value);
        _propertyStates[destination.Ordinal] =
            (_propertyStates[destination.Ordinal] & ~BindingMask) | Shared;
        RecordChangedRows(0, _instanceCount);
    }

    internal void BindPerInstance<T>(
        RenderInstancePropertyLayout authorization,
        ResolvedRenderInstanceProperty<T> property)
        where T : unmanaged
    {
        ResolvedRenderInstanceProperty<T> destination = Resolve(authorization, property);
        RequireUnbound(destination.Ordinal);
        RequireScope().Resources.BindPerInstance(
            _batchSlot,
            _allocation,
            _contract,
            destination);
        _propertyStates[destination.Ordinal] =
            (_propertyStates[destination.Ordinal] & ~BindingMask) | PerInstance;
    }

    internal void BindEncodedPerInstance(
        RenderInstancePropertyLayout authorization,
        RenderInstancePropertyDescriptor property)
    {
        ArgumentNullException.ThrowIfNull(property);
        RenderInstancePropertyDescriptor authorized =
            authorization.RequireCompatible(property, nameof(property));
        RenderInstancePropertyDescriptor destination =
            _contract.RequireCompatible(authorized, nameof(property));
        RequireUnbound(destination.Ordinal);
        RequireScope().Resources.BindEncodedPerInstance(
            _batchSlot,
            _allocation,
            _contract,
            destination);
        _propertyStates[destination.Ordinal] =
            (_propertyStates[destination.Ordinal] & ~BindingMask) | PerInstance;
    }

    internal void Write<T>(
        RenderInstancePropertyLayout authorization,
        ResolvedRenderInstanceProperty<T> property,
        int destinationStart,
        int destinationCount,
        ReadOnlySpan<T> source)
        where T : unmanaged
    {
        if (source.Length != destinationCount)
        {
            throw new ArgumentException(
                $"A render-instance write slice contains {destinationCount} rows, but " +
                $"property '{property.Key}' supplied {source.Length} values.",
                nameof(source));
        }
        ResolvedRenderInstanceProperty<T> destination = Resolve(authorization, property);
        RequirePerInstance(destination.Ordinal);
        RequireScope().Resources.WriteInstances(
            _batchSlot,
            _allocation,
            _contract,
            destination,
            destinationStart,
            source);
        _ = Interlocked.Add(
            ref _propertyStates[destination.Ordinal],
            (long)source.Length << WrittenCountShift);
        RecordChangedRows(destinationStart, source.Length);
    }

    internal void WriteEncoded(
        RenderInstancePropertyLayout authorization,
        RenderInstancePropertyDescriptor property,
        int destinationStart,
        int destinationCount,
        ReadOnlySpan<byte> source)
    {
        ArgumentNullException.ThrowIfNull(property);
        RenderInstancePropertyDescriptor authorized =
            authorization.RequireCompatible(property, nameof(property));
        RenderInstancePropertyDescriptor destination =
            _contract.RequireCompatible(authorized, nameof(property));
        RequirePerInstance(destination.Ordinal);
        RequireScope().Resources.WriteEncodedInstances(
            _batchSlot,
            _allocation,
            _contract,
            destination,
            destinationStart,
            destinationCount,
            source);
        _ = Interlocked.Add(
            ref _propertyStates[destination.Ordinal],
            (long)destinationCount << WrittenCountShift);
        RecordChangedRows(destinationStart, destinationCount);
    }

    internal void Write<T>(
        RenderInstancePropertyLayout authorization,
        ResolvedRenderInstanceProperty<T> property,
        int destinationStart,
        int destinationCount,
        int destinationOffset,
        in T value)
        where T : unmanaged
    {
        if ((uint)destinationOffset >= (uint)destinationCount)
            throw new ArgumentOutOfRangeException(nameof(destinationOffset));
        ResolvedRenderInstanceProperty<T> destination = Resolve(authorization, property);
        RequirePerInstance(destination.Ordinal);
        RequireScope().Resources.WriteInstance(
            _batchSlot,
            _allocation,
            _contract,
            destination,
            checked(destinationStart + destinationOffset),
            in value);
        _ = Interlocked.Add(
            ref _propertyStates[destination.Ordinal],
            1L << WrittenCountShift);
        RecordChangedRows(checked(destinationStart + destinationOffset), 1);
    }

    /// <summary>Copies producer-authored words directly into the final mapped metadata range.</summary>
    internal void BindMetadata<T>(
        RenderInstancePropertyLayout authorization,
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<uint> words)
        where T : unmanaged
    {
        ResolvedRenderInstanceProperty<T> destination = Resolve(authorization, property);
        RequireUnbound(destination.Ordinal);
        RequireScope().Resources.BindMetadata(
            _batchSlot,
            _allocation,
            _contract,
            destination,
            words);
        _propertyStates[destination.Ordinal] =
            (_propertyStates[destination.Ordinal] & ~BindingMask) | CustomMetadata;
        RecordChangedRows(0, _instanceCount);
    }

    private void RecordChangedRows(int start, int count)
    {
        if (_changedChunks.Length == 0 || count == 0)
            return;
        int last = (start + count - 1) / 64;
        for (int chunk = start / 64; chunk <= last; chunk++)
        {
            int firstBit = Math.Max(0, start - chunk * 64);
            int endBit = Math.Min(64, start + count - chunk * 64);
            ulong bits = (ulong.MaxValue << firstBit) & (ulong.MaxValue >> (64 - endBit));
            _ = Interlocked.Or(ref _changedChunks[chunk], unchecked((long)bits));
        }
    }

    private ResolvedRenderInstanceProperty<T> Resolve<T>(
        RenderInstancePropertyLayout authorization,
        ResolvedRenderInstanceProperty<T> property)
        where T : unmanaged
    {
        _ = RequireScope();
        authorization.Validate(property, nameof(property));
        if (ReferenceEquals(authorization, _contract))
            return property;
        RenderInstancePropertyDescriptor destination =
            _contract.RequireCompatible(property.Descriptor, nameof(property));
        return new ResolvedRenderInstanceProperty<T>(_contract, destination);
    }

    private RenderInstanceWriteSlice CreateWriteSlice(
        RenderInstancePropertyLayout properties,
        int destinationStart,
        int count)
    {
        _ = RequireScope();
        if ((uint)destinationStart > (uint)_instanceCount
            || (uint)count > (uint)(_instanceCount - destinationStart))
        {
            throw new ArgumentOutOfRangeException(nameof(destinationStart));
        }
        return new RenderInstanceWriteSlice(
            this,
            properties,
            destinationStart,
            count);
    }

    internal RenderInstanceBatch Publish()
    {
        RenderInstanceWriteScope scope = RequireScope();
        bool touched = false;
        for (int ordinal = 0; ordinal < _propertyStates.Length; ordinal++)
        {
            long state = Volatile.Read(ref _propertyStates[ordinal]);
            long binding = state & BindingMask;
            if (_mode == CompositionMode.Update)
            {
                if (binding == Unbound)
                    continue;
                touched = true;
                long updateWrittenCount = state >> WrittenCountShift;
                if (binding == PerInstance && updateWrittenCount == 0 && _instanceCount != 0)
                {
                    throw new InvalidOperationException(
                        $"Updated render instance property '{_contract.Properties[ordinal].Key}' " +
                        "was bound per-instance but no rows were written.");
                }
                continue;
            }

            if ((state & Required) == 0)
                continue;
            if (binding == Unbound)
            {
                throw new InvalidOperationException(
                    $"Render instance property '{_contract.Properties[ordinal].Key}' was not bound.");
            }
            long writtenCount = state >> WrittenCountShift;
            if (binding == PerInstance && writtenCount != _instanceCount)
            {
                throw new InvalidOperationException(
                    $"Render instance property '{_contract.Properties[ordinal].Key}' contains " +
                    $"{writtenCount} rows, but this batch requires {_instanceCount}.");
            }
        }

        if (_mode == CompositionMode.Update && !touched)
        {
            throw new InvalidOperationException(
                "A render-instance batch update did not bind or write any authorized property.");
        }

        RenderInstanceBatch result;
        if (_existingBatch is null)
        {
            result = scope.Resources.BuildBatch(
                _batchSlot,
                _allocation,
                _contract);
        }
        else
        {
            _existingBatch.ContentRevision =
                scope.Resources.CompleteBatchUpdate(_batchSlot, _allocation);
            _existingBatch.RecordChanges(_changedChunks);
            result = _existingBatch;
        }
        Complete(scope);
        return result;
    }

    public void Dispose()
    {
        RenderInstanceWriteScope? scope = Interlocked.Exchange(ref _scope, null);
        if (scope is null)
            return;
        try
        {
            if (_existingBatch is null)
                scope.Resources.CancelBatch(_batchSlot, _allocation);
            else
                scope.Resources.CancelBatchUpdate(_batchSlot, _allocation);
        }
        finally
        {
            scope.CompleteBatchBuilder();
        }
    }

    private void RequireUnbound(int ordinal)
    {
        if ((_propertyStates[ordinal] & BindingMask) != Unbound)
            throw new InvalidOperationException("A render instance property can be bound only once per batch.");
    }

    private void RequirePerInstance(int ordinal)
    {
        if ((_propertyStates[ordinal] & BindingMask) != PerInstance)
        {
            throw new InvalidOperationException(
                "A render instance property must be bound per-instance before rows are appended.");
        }
    }

    private RenderInstanceWriteScope RequireScope() =>
        _scope ?? throw new ObjectDisposedException(nameof(RenderInstanceBatchComposition));

    private void Complete(RenderInstanceWriteScope scope)
    {
        if (!ReferenceEquals(Interlocked.Exchange(ref _scope, null), scope))
            throw new InvalidOperationException("Render instance batch-builder ownership was lost.");
        scope.CompleteBatchBuilder();
    }
}

/// <summary>
/// An expiring capability for one producer bundle and one logical row range. It can bind and
/// write declared columns, but cannot allocate, publish, release, or address physical storage.
/// Copies are safe to place in synchronous jobs; every operation is rejected after publication.
/// </summary>
public readonly struct RenderInstanceWriteSlice
{
    private readonly RenderInstanceBatchComposition? _owner;
    private readonly RenderInstancePropertyLayout? _properties;
    private readonly int _destinationStart;

    internal RenderInstanceWriteSlice(
        RenderInstanceBatchComposition owner,
        RenderInstancePropertyLayout properties,
        int destinationStart,
        int count)
    {
        _owner = owner;
        _properties = properties;
        _destinationStart = destinationStart;
        Count = count;
    }

    public bool IsValid => _owner is not null;

    public int Count { get; }

    public RenderInstancePropertyLayout Properties =>
        _properties ?? throw new InvalidOperationException(
            "The render-instance write slice is uninitialized.");

    /// <summary>
    /// Delegates a subset of the current bundle without widening its authority. The logical row
    /// range is preserved.
    /// </summary>
    public RenderInstanceWriteSlice Restrict(RenderInstancePropertyLayout properties) =>
        RequireOwner().Restrict(
            Properties,
            properties,
            _destinationStart,
            Count);

    /// <summary>
    /// Narrows this capability to one relative, contiguous row range. Parallel producers use
    /// disjoint slices derived from a query-packet prefix sum; no physical storage row escapes.
    /// </summary>
    public RenderInstanceWriteSlice Slice(int start, int count)
    {
        if ((uint)start > (uint)Count || (uint)count > (uint)(Count - start))
            throw new ArgumentOutOfRangeException(nameof(start));
        return RequireOwner().OpenWrite(
            Properties,
            checked(_destinationStart + start),
            count);
    }

    public void BindShared<T>(ResolvedRenderInstanceProperty<T> property, in T value)
        where T : unmanaged
    {
        RequireWholeBatch();
        RequireOwner().BindShared(Properties, property, in value);
    }

    public void BindPerInstance<T>(ResolvedRenderInstanceProperty<T> property)
        where T : unmanaged
    {
        RequireWholeBatch();
        RequireOwner().BindPerInstance(Properties, property);
    }

    /// <summary>
    /// Binds one already-linked property without assigning it a semantic managed type. This is
    /// internal transport for reflected/source-composed contracts; user code binds typed tokens.
    /// </summary>
    internal void BindEncodedPerInstance(RenderInstancePropertyDescriptor property)
    {
        RequireWholeBatch();
        RequireOwner().BindEncodedPerInstance(Properties, property);
    }

    public void BindMetadata<T>(
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<uint> words)
        where T : unmanaged
    {
        RequireWholeBatch();
        RequireOwner().BindMetadata(Properties, property, words);
    }

    /// <summary>Writes this slice's complete logical row range to one declared SoA column.</summary>
    public void Write<T>(
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<T> values)
        where T : unmanaged =>
        RequireOwner().Write(
            Properties,
            property,
            _destinationStart,
            Count,
            values);

    /// <summary>
    /// Writes tightly packed encoded values for one declared property. This is internal transport
    /// support for layout-driven sources; semantic callers continue to use typed property tokens.
    /// </summary>
    internal void WriteEncoded(
        RenderInstancePropertyDescriptor property,
        ReadOnlySpan<byte> values) =>
        RequireOwner().WriteEncoded(
            Properties,
            property,
            _destinationStart,
            Count,
            values);

    /// <summary>Writes one value at an offset relative to this slice, never a physical row.</summary>
    public void Write<T>(
        ResolvedRenderInstanceProperty<T> property,
        int destinationOffset,
        in T value)
        where T : unmanaged =>
        RequireOwner().Write(
            Properties,
            property,
            _destinationStart,
            Count,
            destinationOffset,
            in value);

    private RenderInstanceBatchComposition RequireOwner() =>
        _owner ?? throw new InvalidOperationException(
            "The render-instance write slice is uninitialized.");

    private void RequireWholeBatch()
    {
        RenderInstanceBatchComposition owner = RequireOwner();
        if (_destinationStart != 0 || Count != owner.InstanceCount)
        {
            throw new InvalidOperationException(
                "Property metadata can be bound only through the producer's whole-batch slice.");
        }
    }
}
