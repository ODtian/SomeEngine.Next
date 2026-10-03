using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Instances;

/// <summary>
/// One GPU copy of an instance store in one render graph. Consumers stage their batches
/// before execution and share Data; only submitted copies advance the stored versions.
/// The graph owns the GPU buffer and its passes.
/// </summary>
public sealed class RenderInstanceDataTransfer : IRenderGraphFrameExecutionObserver
{
    private readonly RenderInstanceResources _storage;
    private readonly GraphBufferId[] _sources = new GraphBufferId[2];
    private readonly GraphPassId _copy;
    private readonly List<BufferRange>[] _ranges = [[], []];
    private readonly GraphPassId _initialize;
    // Retiring a batch must release its CPU change history even while the graph lives.
    private readonly (object? Allocation, ulong Revision)[] _dataVersions;
    private readonly HashSet<RenderInstanceBatch> _frameBatches = [];
    private readonly List<(RenderInstanceBatch Batch, ulong Revision)> _changes = [];
    private bool _initialized;
    private bool _frameActive;

    internal RenderInstanceDataTransfer(RenderInstanceResources storage, ref RenderGraphEdit edit,
        GraphRegionId region)
    {
        _storage = storage;
        ulong bytes = checked((ulong)storage.PropertyDataBytes);
        _dataVersions = new (object?, ulong)[storage.Options.BatchCapacity];
        Data = edit.CreatePersistentBuffer(new BufferDesc((bytes + 3ul) & ~3ul,
            BufferUsages.ShaderRead | BufferUsages.CopySource | BufferUsages.CopyDestination, "Render instance data"));
        _initialize = edit.AddCopyFramePass("Initialize render instance data",
            PassQueueSelection.AnyOfType(QueueType.Graphics), Data, default,
            static (ref PassDefinition pass, ref GraphBufferId buffer) =>
                pass.Write(buffer, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete),
            static (ref CopyPassCommandScope commands, in GraphBufferId buffer) =>
                commands.ClearBuffer(commands.GetBuffer(buffer), BufferRange.Whole), region: region);
        for (int generation = 0; generation < _sources.Length; generation++)
            _sources[generation] = edit.DeclareExternalBuffer(new BufferDesc(bytes,
                BufferUsages.CopySource | BufferUsages.ShaderRead, $"Render instance upload data {generation}"), MemoryType.Upload);
        var data = new CopyData(_sources, Data, _ranges);
        _copy = edit.AddCopyFramePass("Copy render instance data",
            PassQueueSelection.AnyOfType(QueueType.Graphics), data, default,
            static (ref PassDefinition pass, ref CopyData values) =>
            {
                foreach (GraphBufferId source in values.Sources)
                    pass.Read(source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                pass.Write(values.Destination, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Partial);
            }, CopyData.Record, region: region);
        edit.OrderAfter(_copy, _initialize);
    }

    public GraphBufferId Data { get; }

    /// <summary>Stages a batch obtained from this store's current frame read view.</summary>
    public void Stage(ref RenderGraphFrame frame, RenderInstanceBatch batch, in RenderInstanceBatchView view)
    {
        if (!ReferenceEquals(batch.Owner, _storage))
            throw new ArgumentException("The batch belongs to another instance store.", nameof(batch));
        if (!_frameActive)
        {
            frame.ObserveExecution(this);
            _frameActive = true;
            _storage.BindDataSources(ref frame, _sources);
            frame.SetPassEnabled(_initialize, !_initialized);
        }
        if (!_frameBatches.Add(batch)) return;
        var previous = _dataVersions[batch.Slot];
        if (ReferenceEquals(previous.Allocation, batch.Allocation) && previous.Revision == view.ContentRevision)
        {
            frame.SetPassEnabled(_copy, _changes.Count != 0);
            return;
        }
        int source = view.Generation;
        _ranges[source].AddRange(view.PropertyDataRanges.Span);
        _changes.Add((batch, view.ContentRevision));
        frame.SetPassData(_copy, new CopyData(_sources, Data, _ranges));
        frame.SetPassEnabled(_copy, true);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        try
        {
            if (result.TryGetPassCompletion(_initialize, out _)) _initialized = true;
            // Barriers may be submitted before the copy itself. Keep their actual states
            // even when a later recording or submission fails; data versions stay separate.
            for (int generation = 0; generation < _sources.Length; generation++)
            {
                ReadOnlySpan<BufferBoundaryState> states = result.GetBufferStates(_sources[generation]);
                if (!states.IsEmpty) _storage.StorePropertyReadStates(generation, states);
            }
            if (result.TryGetPassCompletion(_copy, out _))
            {
                foreach (var change in _changes)
                    _dataVersions[change.Batch.Slot] = (change.Batch.Allocation, change.Revision);
            }
        }
        finally
        {
            foreach (var ranges in _ranges) ranges.Clear();
            _changes.Clear();
            _frameBatches.Clear();
            _frameActive = false;
        }
    }

    private readonly record struct CopyData(GraphBufferId[] Sources, GraphBufferId Destination, List<BufferRange>[] Ranges)
    {
        internal static void Record(ref CopyPassCommandScope commands, in CopyData data)
        {
            var destination = commands.GetBuffer(data.Destination);
            for (int generation = 0; generation < data.Sources.Length; generation++)
            {
                if (data.Ranges[generation].Count == 0) continue;
                var source = commands.GetBuffer(data.Sources[generation]);
                foreach (BufferRange range in data.Ranges[generation])
                    commands.CopyBuffer(new BufferCopy(source, range.Offset, destination, range.Offset, range.Size));
            }
        }
    }
}
