using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
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

        long byteOffset = 0;
        foreach (RenderInstancePropertyDescriptor property in layout.Properties)
        {
            RenderInstancePropertyEncoding encoding = property.Encoding;
            if (!encoding.HasManagedStorage)
                continue;
            byteOffset = AlignUp(byteOffset, encoding.StorageAlignment);
            byteOffset = checked(byteOffset + (long)BatchCapacity * encoding.StorageStride);
            byteOffset = AlignUp(byteOffset, encoding.StorageAlignment);
            byteOffset = checked(byteOffset + (long)RowCapacity * encoding.StorageStride);
        }

        if (layout.Properties.Count == 0)
            throw new ArgumentException("A render-instance property layout cannot be empty.", nameof(layout));
        if (byteOffset > RenderInstanceLinearMetadata.AddressMask)
        {
            throw new ArgumentException(
                "Render-instance property storage must fit below the metadata per-instance bit.",
                nameof(layout));
        }
        if (byteOffset > int.MaxValue)
        {
            throw new ArgumentException(
                "Render-instance property storage is too large for a CPU instance data.",
                nameof(layout));
        }
        return checked((int)byteOffset);
    }

    private static long AlignUp(long value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);
}

/// <summary>Dimensions and occupied rows of one instance store.</summary>
public sealed record RenderInstanceDiagnostics(
    int RowCapacity,
    int BatchCapacity,
    int ActiveRows,
    int ActiveBatches,
    int PropertyDataBytes);

/// <summary>Graph resource ranges for one completed instance batch.</summary>
public readonly record struct RenderInstanceBatchView(
    GraphBufferId PropertyData,
    GraphBufferId Metadata,
    BufferRange MetadataRange,
    int InstanceCount,
    ulong ContentRevision)
{
    public ReadOnlyMemory<BufferRange> PropertyDataRanges { get; init; }
}

/// <summary>
/// Row addresses, layout and change revisions for one live instance batch.
/// RenderInstanceData holds its property and metadata bytes; RenderGraph receives their changed ranges.
/// </summary>
public sealed class RenderInstanceBatch
{
    internal RenderInstanceBatch(
        RenderInstanceData data,
        object allocation,
        int index,
        RenderInstancePropertyLayout layout,
        int instanceCount,
        ulong contentRevision,
        BufferRange[] propertyDataRanges)
    {
        Data = data;
        Allocation = allocation;
        Index = index;
        Layout = layout;
        InstanceCount = instanceCount;
        ContentRevision = contentRevision;
        PropertyDataRanges = propertyDataRanges;
        _chunkRevisions = new ulong[(instanceCount + 63) / 64];
        Array.Fill(_chunkRevisions, contentRevision);
        _instanceRevisions = new ulong[instanceCount];
        Array.Fill(_instanceRevisions, contentRevision);
    }

    public int InstanceCount { get; }

    /// <summary>
    /// Monotonic revision of the batch's CPU values, advanced when a batch update completes.
    /// </summary>
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

    internal RenderInstanceData Data { get; }

    internal object Allocation { get; }

    internal int Index { get; }

    internal RenderInstancePropertyLayout Layout { get; }

    internal ReadOnlyMemory<BufferRange> PropertyDataRanges { get; }
}

/// <summary>CPU instance bytes and the persistent graph resources receiving their changed ranges.</summary>
internal sealed class RenderInstanceData : IDisposable, IRenderGraphFrameExecutionObserver
{
    private const int MaxConstantBufferRangeBytes = 65_536;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly byte[] _propertyBytes;
    private readonly byte[] _metadataBytes;
    private readonly int _batchMetadataStrideBytes;
    private readonly PropertyStorage[] _properties;
    private readonly object?[] _batchAllocations;
    private readonly bool[] _batchReady;
    private readonly PackedRange[] _batchRows;
    private readonly Stack<int> _freeBatchIndices = [];
    private readonly List<PackedRange> _freeRows = [];
    private readonly List<BufferRange> _changes = [];
    private readonly object _changesGate = new();
    private BufferRangeSet _changedRanges = BufferRangeSet.Empty;
    private readonly GraphPassId _propertyUploadPass;
    private readonly GraphPassId _metadataUploadPass;
    private int _nextUnusedBatchIndex;
    private int _activeBatchCount;
    private int _activeRowCount;
    private ulong _nextContentRevision;
    private bool _disposed;

    internal RenderInstanceData(global::SomeEngine.RenderGraph.RenderGraph graph,
        RenderInstancePropertyLayout layout, RenderInstanceOptions? options = null)
    {
        _graph = graph;
        Layout = layout;
        Options = options ?? new RenderInstanceOptions();
        PropertyDataBytes = Options.ValidateAndMeasure(layout);
        (_batchMetadataStrideBytes, int metadataBytes) = MeasureBatchMetadata(graph.Device, layout, Options);
        _propertyBytes = new byte[Math.Max(4, PropertyDataBytes)];
        _metadataBytes = new byte[metadataBytes];
        _properties = BuildStorage(layout, Options, PropertyDataBytes);
        _batchAllocations = new object?[Options.BatchCapacity];
        _batchReady = new bool[Options.BatchCapacity];
        _batchRows = new PackedRange[Options.BatchCapacity];
        _freeRows.Add(new(0, Options.RowCapacity));
        using RenderGraphEdit edit = graph.BeginEdit();
        PropertyData = edit.CreatePersistentBuffer(new BufferDesc((ulong)_propertyBytes.Length,
            BufferUsages.ShaderRead | BufferUsages.CopySource | BufferUsages.CopyDestination, "Render instance properties"));
        Metadata = edit.CreatePersistentBuffer(new BufferDesc((ulong)_metadataBytes.Length,
            BufferUsages.Constant | BufferUsages.CopySource | BufferUsages.CopyDestination, "Render instance metadata"));
        _propertyUploadPass = edit.AddBufferUploadPass(PropertyData);
        _metadataUploadPass = edit.AddBufferUploadPass(Metadata);
        edit.Commit();
        _changes.Add(new(0, (ulong)_propertyBytes.Length + (ulong)_metadataBytes.Length));
        FinishWrites();
    }

    public GraphBufferId PropertyData { get; }
    public GraphBufferId Metadata { get; }
    public RenderInstancePropertyLayout Layout { get; }
    public RenderInstanceOptions Options { get; }
    public int PropertyDataBytes { get; }
    internal int BatchMetadataStrideBytes => _batchMetadataStrideBytes;

    internal void FinishWrites()
    {
        lock (_changesGate)
        {
            _changes.Sort(static (first, second) => first.Offset.CompareTo(second.Offset));
            var merged = new List<BufferRange>();
            foreach (BufferRange range in _changes)
            {
                if (merged.Count == 0 || merged[^1].Offset + merged[^1].Size < range.Offset) merged.Add(range);
                else
                {
                    BufferRange previous = merged[^1];
                    merged[^1] = new(previous.Offset, Math.Max(previous.Offset + previous.Size, range.Offset + range.Size) - previous.Offset);
                }
            }
            _changedRanges = new(CollectionsMarshal.AsSpan(merged));
        }
    }

    internal void Upload(ref RenderGraphFrame frame)
    {
        if (_changedRanges.Count == 0) return;
        FrameUploads uploads = frame.GetFrameData(this, static () => new FrameUploads());
        if (uploads.Written) return;
        foreach (BufferRange range in _changedRanges.Ranges)
        {
            ulong start = range.Offset;
            ulong end = start + range.Size;
            ulong metadataBase = (ulong)_propertyBytes.Length;
            if (start < metadataBase)
            {
                int count = checked((int)(Math.Min(end, metadataBase) - start));
                frame.WriteBuffer(PropertyData, start, _propertyBytes.AsSpan((int)start, count));
            }
            if (end > metadataBase)
            {
                int offset = checked((int)(Math.Max(start, metadataBase) - metadataBase));
                int count = checked((int)(end - Math.Max(start, metadataBase)));
                frame.WriteBuffer(Metadata, (ulong)offset, _metadataBytes.AsSpan(offset, count));
            }
        }
        uploads.Written = true;
        frame.ObserveExecution(this);
    }

    public void OnFrameSubmitted(in RenderGraphSubmission submission)
    {
        _changes.Clear();
        _changedRanges = BufferRangeSet.Empty;
    }

    private sealed class FrameUploads { internal bool Written; }

    private Span<byte> WritePropertyBytes(BufferRange range)
    {
        AddChange(range);
        return _propertyBytes.AsSpan(checked((int)range.Offset), checked((int)range.Size));
    }

    private Span<byte> WriteMetadataBytes(BufferRange range)
    {
        AddChange(new((ulong)_propertyBytes.Length + range.Offset, range.Size));
        return _metadataBytes.AsSpan(checked((int)range.Offset), checked((int)range.Size));
    }

    private void AddChange(BufferRange range)
    {
        if (range.Size == 0) return;
        lock (_changesGate) _changes.Add(range);
    }

    public RenderInstanceDiagnostics CaptureDiagnostics() =>
        new(Options.RowCapacity, Options.BatchCapacity, _activeRowCount, _activeBatchCount, PropertyDataBytes);

    internal RenderInstanceBatchView GetBatchView(RenderInstanceBatch batch, RenderInstancePropertyLayout requiredLayout)
    {
        ValidateBatchData(batch, nameof(batch));
        ValidateBatch(batch.Index, batch.Allocation);
        ValidateBatchReady(batch.Index);
        if (!batch.Layout.Matches(requiredLayout)) throw new ArgumentException("The batch uses a different property layout.", nameof(requiredLayout));
        return new(PropertyData, Metadata, BatchMetadataRange(batch.Index), batch.InstanceCount, batch.ContentRevision)
            { PropertyDataRanges = batch.PropertyDataRanges };
    }

    internal RenderInstanceStorageView OpenRead() => new(this);

    internal T ReadInstance<T>(RenderInstanceBatch batch, int instanceIndex,
        ResolvedRenderInstanceProperty<T> property) where T : unmanaged
    {
        ValidateBatchData(batch, nameof(batch));
        PropertyStorage storage = RequireStorage(property, nameof(property));
        uint metadata = ReadMetadataWord(batch, property.Descriptor.MetadataWordOffset);
        int offset = checked((int)(metadata & RenderInstanceLinearMetadata.AddressMask));
        if ((metadata & RenderInstanceLinearMetadata.PerInstanceBit) != 0)
            offset += instanceIndex * storage.Descriptor.Encoding.StorageStride;
        return RenderInstancePropertyValue<T>.Read(_propertyBytes.AsSpan(offset, storage.Descriptor.Encoding.StorageStride));
    }

    internal void ReadInstances<T>(RenderInstanceBatch batch, ResolvedRenderInstanceProperty<T> property,
        Span<T> destination) where T : unmanaged
    {
        for (int index = 0; index < destination.Length; index++) destination[index] = ReadInstance(batch, index, property);
    }

    internal uint ReadMetadataWord(RenderInstanceBatch batch, int word)
    {
        ValidateBatchData(batch, nameof(batch));
        BufferRange range = BatchMetadataRange(batch.Index);
        return MemoryMarshal.Cast<byte, uint>(_metadataBytes.AsSpan((int)range.Offset, (int)range.Size))[word];
    }

    private ulong NextContentRevision() => ++_nextContentRevision;

    private void ResetRows(PackedRange rows)
    {
        foreach (PropertyStorage storage in _properties)
        {
            RenderInstancePropertyEncoding encoding = storage.Descriptor.Encoding;
            if (encoding.HasManagedStorage)
                WritePropertyBytes(new((ulong)(storage.InstanceBase + rows.Start * encoding.StorageStride),
                    (ulong)(rows.Count * encoding.StorageStride))).Clear();
        }
    }

    private void ResetSharedBatch(int batchIndex)
    {
        foreach (PropertyStorage storage in _properties)
            if (storage.Descriptor.Encoding.HasManagedStorage) WritePropertyBytes(SharedElementRange(storage, batchIndex)).Clear();
    }

    private void ValidateBatch(int index, object allocation)
    {
        if (!ReferenceEquals(_batchAllocations[index], allocation)) throw new InvalidOperationException("The instance batch has been released.");
    }

    private void ValidateBatchReady(int index)
    {
        if (!_batchReady[index]) throw new InvalidOperationException("The instance batch write has not completed.");
    }

    private void ValidateBatchData(RenderInstanceBatch batch, string parameterName)
    {
        if (!ReferenceEquals(batch.Data, this)) throw new ArgumentException("The batch belongs to different instance data.", parameterName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (!_graph.IsDisposed && !_graph.IsLost)
        {
            using RenderGraphEdit edit = _graph.BeginEdit();
            edit.Remove(_propertyUploadPass); edit.Remove(_metadataUploadPass);
            edit.Remove(PropertyData); edit.Remove(Metadata);
            edit.Commit();
        }
        _changes.Clear();
        _changedRanges = BufferRangeSet.Empty;
        _disposed = true;
    }

    internal BatchAllocation AllocateBatch(RenderInstancePropertyLayout layout, int instanceCount)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
        foreach (RenderInstancePropertyDescriptor property in layout.Properties)
            _ = Layout.RequireCompatible(property, nameof(layout));

        int batchIndex = AllocateBatchIndex();
        object allocation = new();
        PackedRange rows = default;
        bool rowsAllocated = false;
        bool batchActivated = false;
        try
        {
            rows = AllocateRows(instanceCount);
            rowsAllocated = true;
            if (_batchAllocations[batchIndex] is not null)
                throw new InvalidOperationException($"Render instance batch index {batchIndex} is already active.");

            _batchAllocations[batchIndex] = allocation;
            _batchReady[batchIndex] = false;
            _batchRows[batchIndex] = rows;
            _activeBatchCount++;
            _activeRowCount = checked(_activeRowCount + rows.Count);
            batchActivated = true;
            ResetSharedBatch(batchIndex);
            ResetRows(rows);
            ResetBatchMetadata(batchIndex);
            return new BatchAllocation(batchIndex, allocation, layout, rows);
        }
        catch
        {
            if (batchActivated)
            {
                _batchAllocations[batchIndex] = null;
                _batchReady[batchIndex] = false;
                _batchRows[batchIndex] = default;
                _activeBatchCount--;
                _activeRowCount -= rows.Count;
            }
            if (rowsAllocated)
                ReleaseRows(rows);
            _freeBatchIndices.Push(batchIndex);
            throw;
        }
    }

    internal void BindShared<T>(
        int batchIndex,
        object allocation,
        RenderInstancePropertyLayout layout,
        ResolvedRenderInstanceProperty<T> property,
        in T value)
        where T : unmanaged
    {
        ValidateBatch(batchIndex, allocation);
        layout.Validate(property, nameof(property));
        PropertyStorage storage = RequireStorage(property, nameof(property));
        RequireLinearBinding(storage.Descriptor, nameof(property));
        BufferRange propertyRange = SharedElementRange(storage, batchIndex);
        Span<byte> propertyMapping = WritePropertyBytes(propertyRange);
        Span<byte> element = propertyMapping;
        element.Clear();
        RenderInstancePropertyValue<T>.Write(element, in value);
        BufferRange metadataRange = BatchMetadataRange(batchIndex);
        Span<byte> metadataMapping = WriteMetadataBytes(metadataRange);
        MemoryMarshal.Cast<byte, uint>(metadataMapping)
            [property.Descriptor.MetadataWordOffset] =
            checked((uint)propertyRange.Offset);
    }

    internal void BindPerInstance<T>(
        int batchIndex,
        object allocation,
        RenderInstancePropertyLayout layout,
        ResolvedRenderInstanceProperty<T> property)
        where T : unmanaged
    {
        ValidateBatch(batchIndex, allocation);
        layout.Validate(property, nameof(property));
        PropertyStorage storage = RequireStorage(property, nameof(property));
        RequireLinearBinding(storage.Descriptor, nameof(property));
        PackedRange rows = _batchRows[batchIndex];
        int address = checked(storage.InstanceBase + rows.Start * property.Encoding.StorageStride);
        BufferRange metadataRange = BatchMetadataRange(batchIndex);
        Span<byte> metadataMapping = WriteMetadataBytes(metadataRange);
        MemoryMarshal.Cast<byte, uint>(metadataMapping)
            [property.Descriptor.MetadataWordOffset] =
            checked((uint)address) | RenderInstanceLinearMetadata.PerInstanceBit;
    }

    internal void BindEncodedPerInstance(
        int batchIndex,
        object allocation,
        RenderInstancePropertyLayout layout,
        RenderInstancePropertyDescriptor property)
    {
        ValidateBatch(batchIndex, allocation);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(property);
        RenderInstancePropertyDescriptor destination =
            layout.RequireCompatible(property, nameof(property));
        PropertyStorage storage = RequireStorage(destination, nameof(property));
        RequireLinearBinding(storage.Descriptor, nameof(property));
        PackedRange rows = _batchRows[batchIndex];
        int address = checked(
            storage.InstanceBase + rows.Start * destination.Encoding.StorageStride);
        BufferRange metadataRange = BatchMetadataRange(batchIndex);
        Span<byte> metadataMapping = WriteMetadataBytes(metadataRange);
        MemoryMarshal.Cast<byte, uint>(metadataMapping)
            [destination.MetadataWordOffset] =
            checked((uint)address) | RenderInstanceLinearMetadata.PerInstanceBit;
    }

    /// <summary>
    /// Copies one borrowed source span directly into its CPU property column. No CPU staging
    /// array or per-property cache is created. Callers normally pass an ECS chunk span here while
    /// its read-snapshot callback is active.
    /// </summary>
    internal void WriteInstances<T>(
        int batchIndex,
        object allocation,
        RenderInstancePropertyLayout layout,
        ResolvedRenderInstanceProperty<T> property,
        int destinationIndex,
        ReadOnlySpan<T> source)
        where T : unmanaged
    {
        ValidateBatch(batchIndex, allocation);
        layout.Validate(property, nameof(property));
        PackedRange rows = _batchRows[batchIndex];
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
        Span<byte> mapping = WritePropertyBytes(destinationRange);
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
        int batchIndex,
        object allocation,
        RenderInstancePropertyLayout layout,
        RenderInstancePropertyDescriptor property,
        int destinationIndex,
        int sourceCount,
        ReadOnlySpan<byte> source)
    {
        ValidateBatch(batchIndex, allocation);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(property);
        RenderInstancePropertyDescriptor destinationProperty =
            layout.RequireCompatible(property, nameof(property));
        PackedRange rows = _batchRows[batchIndex];
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
        Span<byte> mapping = WritePropertyBytes(destinationRange);
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
        int batchIndex,
        object allocation,
        RenderInstancePropertyLayout layout,
        ResolvedRenderInstanceProperty<T> property,
        int destinationIndex,
        in T value)
        where T : unmanaged
    {
        ValidateBatch(batchIndex, allocation);
        layout.Validate(property, nameof(property));
        PackedRange rows = _batchRows[batchIndex];
        if ((uint)destinationIndex >= (uint)rows.Count)
            throw new ArgumentOutOfRangeException(nameof(destinationIndex));
        PropertyStorage storage = RequireStorage(property, nameof(property));
        BufferRange range = InstanceElementRange(storage, checked(rows.Start + destinationIndex));
        Span<byte> mapping = WritePropertyBytes(range);
        Span<byte> element = mapping;
        element.Clear();
        RenderInstancePropertyValue<T>.Write(element, in value);
    }

    internal void BindMetadata<T>(
        int batchIndex,
        object allocation,
        RenderInstancePropertyLayout layout,
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<uint> words)
        where T : unmanaged
    {
        ValidateBatch(batchIndex, allocation);
        layout.Validate(property, nameof(property));
        int expected = property.Encoding.MetadataWordCount;
        if (words.Length != expected)
        {
            throw new ArgumentException(
                $"Property '{property.Key}' requires exactly {expected} metadata words.",
                nameof(words));
        }
        BufferRange metadataRange = BatchMetadataRange(batchIndex);
        Span<byte> mapping = WriteMetadataBytes(metadataRange);
        words.CopyTo(MemoryMarshal.Cast<byte, uint>(mapping).Slice(
            property.Descriptor.MetadataWordOffset,
            expected));
    }

    internal RenderInstanceBatch BuildBatch(
        int batchIndex,
        object allocation,
        RenderInstancePropertyLayout layout)
    {
        ValidateBatch(batchIndex, allocation);
        _batchReady[batchIndex] = true;
        return new RenderInstanceBatch(
            this,
            allocation,
            batchIndex,
            layout,
            _batchRows[batchIndex].Count,
            NextContentRevision(),
            GetBatchDataRanges(batchIndex, layout));
    }

    private BufferRange[] GetBatchDataRanges(int batchIndex, RenderInstancePropertyLayout properties)
    {
        PackedRange rows = _batchRows[batchIndex];
        var ranges = new List<BufferRange>();
        foreach (PropertyStorage storage in _properties)
        {
            RenderInstancePropertyEncoding encoding = storage.Descriptor.Encoding;
            if (!encoding.HasManagedStorage || !properties.Contains(storage.Descriptor.Key)) continue;
            Add(SharedElementRange(storage, batchIndex));
            if (rows.Count != 0)
                Add(new BufferRange(
                    checked((ulong)(storage.InstanceBase + rows.Start * encoding.StorageStride)),
                    checked((ulong)(rows.Count * encoding.StorageStride))));
        }
        return ranges.ToArray();

        void Add(BufferRange range)
        {
            if (ranges.Count != 0 && ranges[^1].Offset + ranges[^1].Size == range.Offset)
                ranges[^1] = new BufferRange(ranges[^1].Offset, ranges[^1].Size + range.Size);
            else
                ranges.Add(range);
        }
    }

    internal void ReleaseBatch(RenderInstanceBatch metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateBatchData(metadata, nameof(metadata));
        ReleaseBatch(metadata.Index, metadata.Allocation);
    }

    internal void CancelBatch(int batchIndex, object allocation) =>
        ReleaseBatch(batchIndex, allocation);

    internal BatchAllocation BeginBatchUpdate(
        RenderInstanceBatch batch,
        RenderInstancePropertyLayout properties,
        RenderInstancePropertyLayout? replacedProperties)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(properties);
        ValidateBatchData(batch, nameof(batch));
        ValidateBatch(batch.Index, batch.Allocation);
        ValidateBatchReady(batch.Index);
        foreach (RenderInstancePropertyDescriptor property in properties.Properties)
            _ = batch.Layout.RequireCompatible(property, nameof(properties));
        if (replacedProperties is not null)
            foreach (RenderInstancePropertyDescriptor property in replacedProperties.Properties)
                _ = properties.RequireCompatible(property, nameof(replacedProperties));
        _batchReady[batch.Index] = false;
        return new BatchAllocation(batch.Index, batch.Allocation, batch.Layout, _batchRows[batch.Index]);
    }

    internal ulong CompleteBatchUpdate(int batchIndex, object allocation)
    {
        ValidateBatch(batchIndex, allocation);
        ulong revision = NextContentRevision();
        _batchReady[batchIndex] = true;
        return revision;
    }

    internal void CancelBatchUpdate(int batchIndex, object allocation)
    {
        ValidateBatch(batchIndex, allocation);
        _batchReady[batchIndex] = true;
    }

    private int AllocateBatchIndex()
    {
        if (_freeBatchIndices.Count != 0)
            return _freeBatchIndices.Pop();
        if (_nextUnusedBatchIndex == Options.BatchCapacity)
            throw new InvalidOperationException("Render instance batch capacity is exhausted.");
        return _nextUnusedBatchIndex++;
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

    private void ReleaseBatch(int batchIndex, object allocation)
    {
        ValidateBatch(batchIndex, allocation);
        PackedRange rows = _batchRows[batchIndex];
        _batchAllocations[batchIndex] = null;
        _batchReady[batchIndex] = false;
        _batchRows[batchIndex] = default;
        _activeBatchCount--;
        _activeRowCount -= rows.Count;
        ReleaseRows(rows);
        _freeBatchIndices.Push(batchIndex);
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

    private void ResetBatchMetadata(int batchIndex)
    {
        BufferRange range = BatchMetadataRange(batchIndex);
        Span<byte> mapping = WriteMetadataBytes(range);
        mapping.Clear();
    }

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
        int byteOffset = 0;
        for (int index = 0; index < layout.Properties.Count; index++)
        {
            RenderInstancePropertyDescriptor property = layout.Properties[index];
            RenderInstancePropertyEncoding encoding = property.Encoding;
            if (!encoding.HasManagedStorage)
            {
                storage[property.Ordinal] = new PropertyStorage(property, -1, -1);
                continue;
            }
            byteOffset = AlignUp(byteOffset, encoding.StorageAlignment);
            int sharedBase = byteOffset;
            byteOffset = checked(byteOffset + options.BatchCapacity * encoding.StorageStride);
            byteOffset = AlignUp(byteOffset, encoding.StorageAlignment);
            int instanceBase = byteOffset;
            byteOffset = checked(byteOffset + options.RowCapacity * encoding.StorageStride);
            storage[property.Ordinal] = new PropertyStorage(property, sharedBase, instanceBase);
        }
        if (byteOffset != expectedBytes)
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
                $"A render-instance batch layout must occupy between 1 and " +
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

    internal readonly record struct BatchAllocation(
        int Index,
        object Allocation,
        RenderInstancePropertyLayout Layout,
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

    private RenderInstanceData? _data;
    // Binding kind, requiredness, and the atomic written-row count describe one property and
    // share the same lifetime. Packing them into one word avoids three independently allocated
    // side tables without changing the composition's unique ownership.
    private readonly long[] _propertyStates;
    private readonly long[] _changedChunks;
    private readonly RenderInstancePropertyLayout _layout;
    private readonly RenderInstancePropertyLayout _authorization;
    private readonly RenderInstanceBatch? _existingBatch;
    private readonly CompositionMode _mode;
    private readonly int _batchIndex;
    private readonly object _allocation;
    private readonly int _instanceCount;

    internal RenderInstanceBatchComposition(
        RenderInstanceData data,
        RenderInstanceData.BatchAllocation allocationData)
        : this(data, allocationData, null, allocationData.Layout, CompositionMode.Create)
    {
    }

    internal RenderInstanceBatchComposition(
        RenderInstanceData data,
        RenderInstanceData.BatchAllocation allocationData,
        RenderInstanceBatch existingBatch,
        RenderInstancePropertyLayout authorization,
        RenderInstancePropertyLayout? replacedProperties)
        : this(data, allocationData, existingBatch, authorization, CompositionMode.Update, replacedProperties)
    {
    }

    private RenderInstanceBatchComposition(
        RenderInstanceData data,
        RenderInstanceData.BatchAllocation allocationData,
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
        _data = data;
        _layout = allocationData.Layout;
        _authorization = authorization;
        _existingBatch = existingBatch;
        _mode = mode;
        _batchIndex = allocationData.Index;
        _allocation = allocationData.Allocation;
        _instanceCount = allocationData.Rows.Count;
        _propertyStates = new long[allocationData.Layout.Properties.Count];
        _changedChunks = mode == CompositionMode.Update ? new long[(_instanceCount + 63) / 64] : [];
        RenderInstancePropertyLayout? requiredProperties = mode == CompositionMode.Create ? authorization : replacedProperties;
        if (requiredProperties is not null)
        {
            foreach (RenderInstancePropertyDescriptor property in requiredProperties.Properties)
            {
                RenderInstancePropertyDescriptor destination =
                    allocationData.Layout.RequireCompatible(property, nameof(authorization));
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
        RequireData().BindShared(
            _batchIndex,
            _allocation,
            _layout,
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
        RequireData().BindPerInstance(
            _batchIndex,
            _allocation,
            _layout,
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
            _layout.RequireCompatible(authorized, nameof(property));
        RequireUnbound(destination.Ordinal);
        RequireData().BindEncodedPerInstance(
            _batchIndex,
            _allocation,
            _layout,
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
        RequireData().WriteInstances(
            _batchIndex,
            _allocation,
            _layout,
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
            _layout.RequireCompatible(authorized, nameof(property));
        RequirePerInstance(destination.Ordinal);
        RequireData().WriteEncodedInstances(
            _batchIndex,
            _allocation,
            _layout,
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
        RequireData().WriteInstance(
            _batchIndex,
            _allocation,
            _layout,
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
        RequireData().BindMetadata(
            _batchIndex,
            _allocation,
            _layout,
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
        _ = RequireData();
        authorization.Validate(property, nameof(property));
        if (ReferenceEquals(authorization, _layout))
            return property;
        RenderInstancePropertyDescriptor destination =
            _layout.RequireCompatible(property.Descriptor, nameof(property));
        return new ResolvedRenderInstanceProperty<T>(_layout, destination);
    }

    private RenderInstanceWriteSlice CreateWriteSlice(
        RenderInstancePropertyLayout properties,
        int destinationStart,
        int count)
    {
        _ = RequireData();
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
        RenderInstanceData data = RequireData();
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
                        throw new InvalidOperationException($"Replaced instance property '{_layout.Properties[ordinal].Key}' was not bound.");
                    continue;
                }
                touched = true;
                long updateWrittenCount = state >> WrittenCountShift;
                if (binding == PerInstance && updateWrittenCount == 0 && _instanceCount != 0)
                {
                    throw new InvalidOperationException(
                        $"Updated render instance property '{_layout.Properties[ordinal].Key}' " +
                        "was bound per-instance but no rows were written.");
                }
                if (binding == PerInstance && (state & Required) != 0 && updateWrittenCount != _instanceCount)
                    throw new InvalidOperationException($"Replaced instance property '{_layout.Properties[ordinal].Key}' must contain every instance value.");
                continue;
            }

            if ((state & Required) == 0)
                continue;
            if (binding == Unbound)
            {
                throw new InvalidOperationException(
                    $"Render instance property '{_layout.Properties[ordinal].Key}' was not bound.");
            }
            long writtenCount = state >> WrittenCountShift;
            if (binding == PerInstance && writtenCount != _instanceCount)
            {
                throw new InvalidOperationException(
                    $"Render instance property '{_layout.Properties[ordinal].Key}' contains " +
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
            result = data.BuildBatch(
                _batchIndex,
                _allocation,
                _layout);
        }
        else
        {
            _existingBatch.ContentRevision =
                data.CompleteBatchUpdate(_batchIndex, _allocation);
            _existingBatch.RecordChanges(_changedChunks);
            result = _existingBatch;
        }
        Complete(data);
        return result;
    }

    public void Dispose()
    {
        RenderInstanceData? data = Interlocked.Exchange(ref _data, null);
        if (data is null)
            return;
        if (_existingBatch is null) data.CancelBatch(_batchIndex, _allocation);
        else data.CancelBatchUpdate(_batchIndex, _allocation);
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

    private RenderInstanceData RequireData() =>
        _data ?? throw new ObjectDisposedException(nameof(RenderInstanceBatchComposition));

    private void Complete(RenderInstanceData data)
    {
        if (!ReferenceEquals(Interlocked.Exchange(ref _data, null), data))
            throw new InvalidOperationException("Render instance batch-builder ownership was lost.");

    }
}

/// <summary>
/// An expiring capability for one producer bundle and one logical row range. It can bind and
/// write declared columns, but cannot allocate, publish, release, or address physical storage.
/// Copies are safe to place in synchronous jobs; every operation is rejected after publication.
/// </summary>
public readonly struct RenderInstanceWriteSlice
{
    private readonly RenderInstanceBatchComposition? _composition;
    private readonly RenderInstancePropertyLayout? _properties;
    private readonly int _destinationStart;

    internal RenderInstanceWriteSlice(
        RenderInstanceBatchComposition data,
        RenderInstancePropertyLayout properties,
        int destinationStart,
        int count)
    {
        _composition = data;
        _properties = properties;
        _destinationStart = destinationStart;
        Count = count;
    }

    public bool IsValid => _composition is not null;

    public int Count { get; }

    public RenderInstancePropertyLayout Properties =>
        _properties ?? throw new InvalidOperationException(
            "The render-instance write slice is uninitialized.");

    /// <summary>
    /// Delegates a subset of the current bundle without widening its authority. The logical row
    /// range is preserved.
    /// </summary>
    public RenderInstanceWriteSlice Restrict(RenderInstancePropertyLayout properties) =>
        RequireComposition().Restrict(
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
        return RequireComposition().OpenWrite(
            Properties,
            checked(_destinationStart + start),
            count);
    }

    public void BindShared<T>(ResolvedRenderInstanceProperty<T> property, in T value)
        where T : unmanaged
    {
        RequireWholeBatch();
        RequireComposition().BindShared(Properties, property, in value);
    }

    public void BindPerInstance<T>(ResolvedRenderInstanceProperty<T> property)
        where T : unmanaged
    {
        RequireWholeBatch();
        RequireComposition().BindPerInstance(Properties, property);
    }

    /// <summary>
    /// Binds one already-linked property without assigning it a semantic managed type. This is
    /// internal transport for reflected/source-composed contracts; user code binds typed tokens.
    /// </summary>
    internal void BindEncodedPerInstance(RenderInstancePropertyDescriptor property)
    {
        RequireWholeBatch();
        RequireComposition().BindEncodedPerInstance(Properties, property);
    }

    public void BindMetadata<T>(
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<uint> words)
        where T : unmanaged
    {
        RequireWholeBatch();
        RequireComposition().BindMetadata(Properties, property, words);
    }

    /// <summary>Writes this slice's complete logical row range to one declared SoA column.</summary>
    public void Write<T>(
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<T> values)
        where T : unmanaged =>
        RequireComposition().Write(
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
        RequireComposition().WriteEncoded(
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
        RequireComposition().Write(
            Properties,
            property,
            _destinationStart,
            Count,
            destinationOffset,
            in value);

    private RenderInstanceBatchComposition RequireComposition() =>
        _composition ?? throw new InvalidOperationException(
            "The render-instance write slice is uninitialized.");

    private void RequireWholeBatch()
    {
        RenderInstanceBatchComposition data = RequireComposition();
        if (_destinationStart != 0 || Count != data.InstanceCount)
        {
            throw new InvalidOperationException(
                "Property metadata can be bound only through the producer's whole-batch slice.");
        }
    }
}
