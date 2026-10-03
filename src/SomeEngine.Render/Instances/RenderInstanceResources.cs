using System.Runtime.InteropServices;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Graphics;
using SomeEngine.Render.Frame;
using SomeEngine.RenderGraph;

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
                "Render-instance property storage exceeds its byte addressing range.",
                nameof(layout));
        }
        return checked((int)cursor);
    }

    private static long AlignUp(long value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}

/// <summary>Usage counts for the batch property storage.</summary>
public sealed record RenderInstanceDiagnostics(
    int RowCapacity,
    int BatchCapacity,
    int ActiveRows,
    int ActiveBatches,
    int PropertyDataBytes);

/// <summary>Graph buffers and the metadata range for one live batch.</summary>
public readonly record struct RenderInstanceBatchView(
    GraphBufferId PropertyData,
    GraphBufferId Metadata,
    BufferRange MetadataRange,
    int InstanceCount,
    ulong ContentRevision);

/// <summary>One live batch, its property layout, and the revisions consumed by scene features.</summary>
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

    /// <summary>Revision of the latest successful batch value update.</summary>
    public ulong ContentRevision { get; internal set; }

    private readonly ulong[] _chunkRevisions;
    private readonly ulong[] _instanceRevisions;

    /// <summary>Appends rows modified after the supplied revision in increasing row order, skipping unchanged chunks.</summary>
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
internal sealed class RenderInstanceResources : IDisposable, IRenderGraphFrameExecutionObserver
{
    private const int MaxConstantBufferRangeBytes = 65_536;
    private const int PropertyBytesWritten = 1;
    private const int MetadataWordsWritten = 2;
    private const int LifecycleActive = 0;
    private const int LifecycleShutdown = 1;
    private const int LifecycleDisposed = 2;
    private readonly Device _device;
    private global::SomeEngine.RenderGraph.RenderGraph? _graph;
    private bool _initialized;
    private bool _valuesUploaded;
    private GraphPassId _propertyUpload;
    private GraphPassId _metadataUpload;
    private readonly int[] _batchWrittenBuffers;
    private int _writtenBuffers;
    private RenderGraphNextFrame? _nextFrame;
    private readonly RenderGraphBufferWrites?[] _batchWrites;
    private readonly int _batchMetadataStrideBytes;
    private readonly PropertyStorage[] _properties;
    private readonly object?[] _batchAllocations;
    private readonly bool[] _batchReady;
    private readonly PackedRange[] _batchRows;
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
    private ulong _nextContentRevision;
    private int _lifecycleState;
    private bool _cleanupCompleted;

    public RenderInstanceResources(
        Device device,
        RenderInstancePropertyLayout layout,
        RenderInstanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(layout);
        RenderInstanceOptions selected = options ?? new RenderInstanceOptions();
        int propertyDataBytes = selected.ValidateAndMeasure(layout);
        (int metadataStride, _) = MeasureBatchMetadata(device, layout, selected);

        _device = device;
        Layout = layout;
        Options = selected;
        PropertyDataBytes = propertyDataBytes;
        _batchMetadataStrideBytes = metadataStride;
        _properties = BuildStorage(layout, selected, propertyDataBytes);
        _batchAllocations = new object?[selected.BatchCapacity];
        _batchReady = new bool[selected.BatchCapacity];
        _batchRows = new PackedRange[selected.BatchCapacity];
        _batchWrites = new RenderGraphBufferWrites?[selected.BatchCapacity];
        _batchWrittenBuffers = new int[selected.BatchCapacity];
        _freeRows.Add(new PackedRange(0, selected.RowCapacity));
    }

    internal GraphBufferId PropertyData { get; private set; }
    internal GraphBufferId BatchMetadata { get; private set; }

    internal void Install(global::SomeEngine.RenderGraph.RenderGraph graph)
    {
        if (ReferenceEquals(_graph, graph)) return;
        DevelopmentCheck.Require(_graph is null && ReferenceEquals(graph.Device, _device),
            "Instance buffers require one graph on the storage device.");
        using var edit = graph.BeginEdit();
        PropertyData = edit.CreatePersistentBuffer(new BufferDesc(
            Math.Max(4ul, ((ulong)PropertyDataBytes + 3ul) & ~3ul),
            BufferUsages.ShaderRead | BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Render instance properties"));
        BatchMetadata = edit.CreatePersistentBuffer(new BufferDesc(
            checked((ulong)_batchMetadataStrideBytes * (ulong)Options.BatchCapacity),
            BufferUsages.Constant | BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Render instance batch metadata"));
        _propertyUpload = edit.AddBufferUploadPass(PropertyData);
        _metadataUpload = edit.AddBufferUploadPass(BatchMetadata);
        edit.Commit();
        _graph = graph;
    }

    internal void BindFrame(ref RenderGraphFrame frame)
    {
        if (!_initialized)
        {
            foreach (BufferRange range in UnwrittenRanges(Math.Max(4ul, ((ulong)PropertyDataBytes + 3ul) & ~3ul),
                         frame.GetBufferUploadRanges(PropertyData).Ranges))
                frame.GetWriteSpan(PropertyData, range.Offset, checked((int)range.Size)).Clear();
            foreach (BufferRange range in UnwrittenRanges(
                         checked((ulong)_batchMetadataStrideBytes * (ulong)Options.BatchCapacity),
                         frame.GetBufferUploadRanges(BatchMetadata).Ranges))
                frame.GetWriteSpan(BatchMetadata, range.Offset, checked((int)range.Size)).Clear();
        }
        frame.ObserveExecution(this);
    }

    private static BufferRange[] UnwrittenRanges(ulong size, ReadOnlySpan<BufferRange> writes)
    {
        var ranges = new List<BufferRange>();
        ulong end = 0;
        foreach (BufferRange write in writes)
        {
            if (write.Offset > end) ranges.Add(new BufferRange(end, write.Offset - end));
            end = Math.Max(end, write.Offset + write.Size);
        }
        if (end < size) ranges.Add(new BufferRange(end, size - end));
        return ranges.ToArray();
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (!_initialized && result.TryGetPassCompletion(_propertyUpload, out _) &&
            result.TryGetPassCompletion(_metadataUpload, out _)) _initialized = true;
        bool propertiesUploaded = (_writtenBuffers & PropertyBytesWritten) == 0 || result.TryGetPassCompletion(_propertyUpload, out _);
        bool metadataUploaded = (_writtenBuffers & MetadataWordsWritten) == 0 || result.TryGetPassCompletion(_metadataUpload, out _);
        if (_initialized && propertiesUploaded && metadataUploaded)
        {
            _valuesUploaded = true;
            _writtenBuffers = 0;
        }
    }

    public RenderInstancePropertyLayout Layout { get; }

    public RenderInstanceOptions Options { get; }

    public int PropertyDataBytes { get; }

    internal int BatchMetadataStrideBytes => _batchMetadataStrideBytes;

    internal bool RequiresValueWrite => !_valuesUploaded;

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

    internal RenderInstanceWriteScope BeginPrepare(RenderPrepareScope scope, RenderGraphNextFrame? nextFrame)
    {
        DevelopmentCheck.Require(scope is not null && !scope.IsClosed, "Instance preparation requires an open write scope.");
        EnterOperation();
        try
        {
            if (nextFrame is not null) Install(nextFrame.Graph);
            _nextFrame = nextFrame;
            return new RenderInstanceWriteScope(this, scope);
        }
        catch { ExitOperation(); throw; }
    }

    public RenderInstanceStorageView OpenRead(RenderReadScope readScope, scoped ref RenderGraphFrame graph)
    {
        ThrowIfNotActive();
        BindFrame(ref graph);
        return new RenderInstanceStorageView(this, readScope);
    }

    internal RenderInstanceBatchView GetBatchView(
        RenderInstanceBatch batch, RenderInstancePropertyLayout requiredLayout)
    {
        ThrowIfNotActive();
        ValidateBatchOwner(batch, nameof(batch));
        ValidateBatch(batch.Slot, batch.Allocation);
        ValidateBatchReady(batch.Slot);
        if (!batch.Contract.HasSameContract(requiredLayout))
            throw new ArgumentException("The batch property layout must match the shader layout.", nameof(requiredLayout));
        return new RenderInstanceBatchView(PropertyData, BatchMetadata, BatchMetadataRange(batch.Slot),
            batch.InstanceCount, batch.ContentRevision);
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

        RenderGraphNextFrame nextFrame = RequireNextFrame();
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
            _batchWrites[batchSlot] = nextFrame.BeginBufferWrites();
            return new BatchLease(batchSlot, allocation, contract, rows);
        }
        catch
        {
            if (batchActivated)
            {
                _batchWrites[batchSlot]?.Dispose();
                _batchWrites[batchSlot] = null;
                _batchWrittenBuffers[batchSlot] = 0;
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
        BufferRange propertyRange = SharedElementRange(storage, batchSlot);
        Span<byte> propertyMapping = GetWriteBytes(batchSlot, PropertyData, propertyRange);
        Span<byte> element = propertyMapping;
        element.Slice(RenderInstancePropertyValue<T>.Size).Clear();
        RenderInstancePropertyValue<T>.Write(element, in value);
        BufferRange metadataRange = PropertyMetadataRange(batchSlot, property.Descriptor);
        Span<byte> metadataMapping = GetWriteBytes(batchSlot, BatchMetadata, metadataRange);
        MemoryMarshal.Cast<byte, uint>(metadataMapping)[0] =
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
        BufferRange metadataRange = PropertyMetadataRange(batchSlot, property.Descriptor);
        Span<byte> metadataMapping = GetWriteBytes(batchSlot, BatchMetadata, metadataRange);
        MemoryMarshal.Cast<byte, uint>(metadataMapping)[0] =
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
        BufferRange metadataRange = PropertyMetadataRange(batchSlot, destination);
        Span<byte> metadataMapping = GetWriteBytes(batchSlot, BatchMetadata, metadataRange);
        MemoryMarshal.Cast<byte, uint>(metadataMapping)[0] =
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
        Span<byte> mapping = GetWriteBytes(batchSlot, PropertyData, destinationRange);
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
            element.Slice(valueSize).Clear();
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
        Span<byte> mapping = GetWriteBytes(batchSlot, PropertyData, destinationRange);
        Span<byte> destination = mapping;

        if (stride == valueSize)
        {
            source.CopyTo(destination);
            return;
        }

        for (int index = 0; index < sourceCount; index++)
        {
            Span<byte> element = destination.Slice(index * stride, stride);
            element.Slice(valueSize).Clear();
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
        Span<byte> mapping = GetWriteBytes(batchSlot, PropertyData, range);
        Span<byte> element = mapping;
        element.Slice(RenderInstancePropertyValue<T>.Size).Clear();
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
        BufferRange metadataRange = PropertyMetadataRange(batchSlot, property.Descriptor);
        Span<byte> mapping = GetWriteBytes(batchSlot, BatchMetadata, metadataRange);
        words.CopyTo(MemoryMarshal.Cast<byte, uint>(mapping));
    }

    internal RenderInstanceBatch BuildBatch(
        int batchSlot,
        object allocation,
        RenderInstancePropertyLayout contract)
    {
        ValidateBatch(batchSlot, allocation);
        CommitBatchWrites(batchSlot);
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
        RenderInstancePropertyLayout properties,
        RenderInstancePropertyLayout? replacedProperties)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(properties);
        ValidateBatchOwner(batch, nameof(batch));
        ValidateBatch(batch.Slot, batch.Allocation);
        ValidateBatchReady(batch.Slot);
        foreach (RenderInstancePropertyDescriptor property in properties.Properties)
            _ = batch.Contract.RequireCompatible(property, nameof(properties));
        if (replacedProperties is not null)
            foreach (RenderInstancePropertyDescriptor property in replacedProperties.Properties)
                _ = properties.RequireCompatible(property, nameof(replacedProperties));
        _batchWrites[batch.Slot] = RequireNextFrame().BeginBufferWrites();
        _batchReady[batch.Slot] = false;
        return new BatchLease(batch.Slot, batch.Allocation, batch.Contract, _batchRows[batch.Slot]);
    }

    internal ulong CompleteBatchUpdate(int batchSlot, object allocation)
    {
        ValidateBatch(batchSlot, allocation);
        CommitBatchWrites(batchSlot);
        _batchReady[batchSlot] = true;
        return NextContentRevision();
    }

    internal void CancelBatchUpdate(int batchSlot, object allocation)
    {
        ValidateBatch(batchSlot, allocation);
        _batchWrites[batchSlot]?.Dispose();
        _batchWrites[batchSlot] = null;
        _batchWrittenBuffers[batchSlot] = 0;
        _batchReady[batchSlot] = true;
    }

    private void CommitBatchWrites(int batchIndex)
    {
        _batchWrites[batchIndex]!.Commit();
        _batchWrites[batchIndex] = null;
        _writtenBuffers |= _batchWrittenBuffers[batchIndex];
        _batchWrittenBuffers[batchIndex] = 0;
        _valuesUploaded = false;
    }

    private ulong NextContentRevision()
    {
        _nextContentRevision = checked(_nextContentRevision + 1ul);
        return _nextContentRevision;
    }

    public void Shutdown(RenderPrepareScope scope)
    {
        DevelopmentCheck.Require(scope is not null && !scope.IsClosed, "Instance shutdown requires an open write scope.");
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
        _nextFrame = null;
        ExitOperation();
    }

    private RenderGraphNextFrame RequireNextFrame() => _nextFrame ??
        throw new InvalidOperationException("Instance writing requires the next graph frame.");

    private Span<byte> GetWriteBytes(int batchIndex, GraphBufferId buffer, BufferRange range)
    {
        Interlocked.Or(ref _batchWrittenBuffers[batchIndex], buffer == PropertyData ? PropertyBytesWritten : MetadataWordsWritten);
        return _batchWrites[batchIndex]!.GetWriteSpan(buffer, range.Offset, checked((int)range.Size));
    }

    private BufferRange PropertyMetadataRange(int batchIndex, RenderInstancePropertyDescriptor property) =>
        new(checked(BatchMetadataRange(batchIndex).Offset + (ulong)property.MetadataWordOffset * 4ul),
            checked((ulong)property.Encoding.MetadataWordCount * 4ul));

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
        _batchWrites[batchSlot]?.Dispose();
        _batchWrites[batchSlot] = null;
        _batchWrittenBuffers[batchSlot] = 0;
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

    private BufferRange BatchMetadataRange(int batchIndex) =>
        new(
            checked((ulong)batchIndex * (ulong)_batchMetadataStrideBytes),
            checked((ulong)_batchMetadataStrideBytes));

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
                "The render-instance batch metadata exceeds its byte addressing range.",
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
                if (_shutdownScope is null || !_shutdownScope.IsClosed)
                {
                    throw new InvalidOperationException(
                        "The prepare scope used for batch instance shutdown must be released before disposal.");
                }
                if (_lifecycleState == LifecycleDisposed)
                    return;
                _lifecycleState = LifecycleDisposed;
            }

            foreach (RenderGraphBufferWrites? writes in _batchWrites) writes?.Dispose();
            _cleanupCompleted = true;
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
    private int _activeBatchBuilders;

    internal RenderInstanceWriteScope(
        RenderInstanceResources owner,
        RenderPrepareScope prepareScope)
    {
        _owner = owner;
        _prepareScope = prepareScope;
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
        RenderInstancePropertyLayout properties,
        RenderInstancePropertyLayout? replacedProperties)
    {
        RenderInstanceResources.BatchLease lease =
            Resources.BeginBatchUpdate(batch, properties, replacedProperties);
        try
        {
            var composition = new RenderInstanceBatchComposition(this, lease, batch, properties, replacedProperties);
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
        owner.EndWrite();
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
        RenderInstancePropertyLayout authorization,
        RenderInstancePropertyLayout? replacedProperties)
        : this(scope, lease, existingBatch, authorization, CompositionMode.Update, replacedProperties)
    {
    }

    private RenderInstanceBatchComposition(
        RenderInstanceWriteScope scope,
        RenderInstanceResources.BatchLease lease,
        RenderInstanceBatch? existingBatch,
        RenderInstancePropertyLayout authorization,
        CompositionMode mode,
        RenderInstancePropertyLayout? replacedProperties = null)
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
        RenderInstancePropertyLayout? requiredProperties = mode == CompositionMode.Create ? authorization : replacedProperties;
        if (requiredProperties is not null)
        {
            foreach (RenderInstancePropertyDescriptor property in requiredProperties.Properties)
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
                {
                    if ((state & Required) != 0)
                        throw new InvalidOperationException($"Replaced instance property '{_contract.Properties[ordinal].Key}' was not bound.");
                    continue;
                }
                touched = true;
                long updateWrittenCount = state >> WrittenCountShift;
                if (binding == PerInstance && updateWrittenCount == 0 && _instanceCount != 0)
                {
                    throw new InvalidOperationException(
                        $"Updated render instance property '{_contract.Properties[ordinal].Key}' " +
                        "was bound per-instance but no rows were written.");
                }
                if (binding == PerInstance && (state & Required) != 0 && updateWrittenCount != _instanceCount)
                    throw new InvalidOperationException($"Replaced instance property '{_contract.Properties[ordinal].Key}' must contain every instance value.");
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
