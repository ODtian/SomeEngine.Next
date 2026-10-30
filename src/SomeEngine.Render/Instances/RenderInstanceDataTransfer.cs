using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Instances;

/// <summary>
/// One GPU copy of an instance store in one render graph. Consumers stage their batches
/// before execution and share device properties and metadata; only submitted copies advance the stored versions.
/// The graph owns the GPU buffers and their passes.
/// </summary>
public sealed class RenderInstanceDataTransfer : IRenderGraphFrameExecutionObserver
{
    private readonly RenderInstanceResources _storage;
    private readonly GraphBufferId[] _propertySources = new GraphBufferId[2];
    private readonly GraphBufferId[] _metadataSources = new GraphBufferId[2];
    private readonly GraphPassId[] _copies = new GraphPassId[2];
    private readonly List<BufferRange>[] _ranges = [[], []];
    private readonly List<BufferRange>[] _metadataRanges = [[], []];
    private readonly GraphPassId _initialize;
    // Retiring a batch must release its CPU change history even while the graph lives.
    private readonly (object? Allocation, ulong Revision)[] _dataVersions;
    private readonly HashSet<RenderInstanceBatch> _frameBatches = [];
    private readonly List<(RenderInstanceBatch Batch, ulong Revision)>[] _changes = [[], []];
    private bool _initialized;
    private bool _frameActive;

    internal RenderInstanceDataTransfer(RenderInstanceResources storage, ref RenderGraphEdit edit)
    {
        _storage = storage;
        ulong bytes = checked((ulong)storage.PropertyDataBytes);
        _dataVersions = new (object?, ulong)[storage.Options.BatchCapacity];
        Data = edit.CreatePersistentBuffer(new BufferDesc((bytes + 3ul) & ~3ul,
            BufferUsages.ShaderRead | BufferUsages.CopySource | BufferUsages.CopyDestination, "Render instance data"));
        Metadata = edit.CreatePersistentBuffer(new BufferDesc(storage.MetadataBytes,
            BufferUsages.Constant | BufferUsages.CopySource | BufferUsages.CopyDestination, "Render instance metadata"));
        var buffers = new DeviceBuffers(Data, Metadata);
        _initialize = edit.AddCopyFramePass("Initialize render instance data",
            PassQueueSelection.AnyOfType(QueueType.Graphics), buffers, default,
            static (ref PassDefinition pass, ref DeviceBuffers values) =>
            {
                pass.Write(values.Properties, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
                pass.Write(values.Metadata, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in DeviceBuffers values) =>
            {
                commands.ClearBuffer(commands.GetBuffer(values.Properties), BufferRange.Whole);
                commands.ClearBuffer(commands.GetBuffer(values.Metadata), BufferRange.Whole);
            });
        for (int generation = 0; generation < _propertySources.Length; generation++)
        {
            _propertySources[generation] = edit.DeclareExternalBuffer(new BufferDesc(bytes,
                BufferUsages.CopySource | BufferUsages.ShaderRead, $"Render instance upload data {generation}"), MemoryType.Upload);
            _metadataSources[generation] = edit.DeclareExternalBuffer(new BufferDesc(storage.MetadataBytes,
                BufferUsages.CopySource | BufferUsages.Constant, $"Render instance upload metadata {generation}"), MemoryType.Upload);
            var data = new CopyData(_propertySources[generation], _metadataSources[generation],
                buffers, _ranges[generation], _metadataRanges[generation]);
            _copies[generation] = edit.AddCopyFramePass($"Copy render instance data {generation}",
                PassQueueSelection.AnyOfType(QueueType.Graphics), data, new PassOptions { SubmitAfter = true },
                static (ref PassDefinition pass, ref CopyData values) =>
                {
                    pass.Read(values.Properties, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Read(values.Metadata, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(values.Destination.Properties, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Partial);
                    pass.Write(values.Destination.Metadata, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Partial);
                }, CopyData.Record);
            edit.OrderAfter(_copies[generation], _initialize);
        }
    }

    public GraphBufferId Data { get; }

    public GraphBufferId Metadata { get; }

    /// <summary>Stages a batch obtained from this store's current frame read view.</summary>
    public void Stage(ref RenderGraphFrame frame, RenderInstanceBatch batch, in RenderInstanceBatchView view)
    {
        if (!ReferenceEquals(batch.Owner, _storage))
            throw new ArgumentException("The batch belongs to another instance store.", nameof(batch));
        if (!_frameActive)
        {
            frame.ObserveExecution(this);
            _frameActive = true;
            _storage.BindDataSources(ref frame, _propertySources, _metadataSources);
            frame.SetPassEnabled(_initialize, !_initialized);
        }
        if (!_frameBatches.Add(batch)) return;
        var previous = _dataVersions[batch.Slot];
        if (ReferenceEquals(previous.Allocation, batch.Allocation) && previous.Revision == view.ContentRevision)
        {
            SetCopyPassesEnabled(ref frame);
            return;
        }
        int source = view.Generation;
        RenderInstancePropertyDataChange change = view.PropertyDataChange;
        bool adjacentRevision = ReferenceEquals(previous.Allocation, batch.Allocation) &&
            previous.Revision == change.FromRevision && change.ToRevision == view.ContentRevision;
        // Delta ownership is publication-local, not consumed by a graph. A lagging/new graph
        // retains the original full-copy path; only this graph's submitted cursor may advance.
        _ranges[source].AddRange(adjacentRevision ? change.Ranges.Span : view.PropertyDataRanges.Span);
        _metadataRanges[source].Add(view.MetadataRange);
        _changes[source].Add((batch, view.ContentRevision));
        // The pass retains these same range lists; recording reads their completed frame contents.
        SetCopyPassesEnabled(ref frame);
    }

    private void SetCopyPassesEnabled(ref RenderGraphFrame frame)
    {
        for (int generation = 0; generation < _copies.Length; generation++)
            frame.SetPassEnabled(_copies[generation], _changes[generation].Count != 0);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        try
        {
            if (result.TryGetPassCompletion(_initialize, out _)) _initialized = true;
            // Barriers may be submitted before the copy itself. Keep their actual states
            // even when a later recording or submission fails; data versions stay separate.
            for (int generation = 0; generation < _propertySources.Length; generation++)
            {
                ReadOnlySpan<BufferBoundaryState> states = result.GetBufferStates(_propertySources[generation]);
                if (!states.IsEmpty) _storage.StorePropertyReadStates(generation, states);
                ReadOnlySpan<BufferBoundaryState> metadataStates = result.GetBufferStates(_metadataSources[generation]);
                if (!metadataStates.IsEmpty) _storage.StoreMetadataReadStates(generation, metadataStates);
                if (!result.TryGetPassCompletion(_copies[generation], out QueueCompletion completion)) continue;
                _storage.RegisterCopyCompletion(generation, completion);
                foreach (var change in _changes[generation])
                    _dataVersions[change.Batch.Slot] = (change.Batch.Allocation, change.Revision);
            }
        }
        finally
        {
            foreach (var ranges in _ranges) ranges.Clear();
            foreach (var ranges in _metadataRanges) ranges.Clear();
            foreach (var changes in _changes) changes.Clear();
            _frameBatches.Clear();
            _frameActive = false;
        }
    }

    private readonly record struct DeviceBuffers(GraphBufferId Properties, GraphBufferId Metadata);

    private readonly record struct CopyData(GraphBufferId Properties, GraphBufferId Metadata,
        DeviceBuffers Destination, List<BufferRange> PropertyRanges, List<BufferRange> MetadataRanges)
    {
        internal static void Record(ref CopyPassCommandScope commands, in CopyData data)
        {
            var source = commands.GetBuffer(data.Properties);
            var destination = commands.GetBuffer(data.Destination.Properties);
            foreach (BufferRange range in data.PropertyRanges)
                commands.CopyBuffer(new BufferCopy(source, range.Offset, destination, range.Offset, range.Size));
            var metadata = commands.GetBuffer(data.Metadata);
            var destinationMetadata = commands.GetBuffer(data.Destination.Metadata);
            foreach (BufferRange range in data.MetadataRanges)
                commands.CopyBuffer(new BufferCopy(metadata, range.Offset, destinationMetadata, range.Offset, range.Size));
        }
    }
}
