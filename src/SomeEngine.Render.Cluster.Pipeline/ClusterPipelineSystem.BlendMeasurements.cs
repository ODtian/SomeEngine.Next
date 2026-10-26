using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private int _measurementSimulationFrame;
    private (ulong Volumes, ulong Properties, ulong Ranges) _decalBufferSizes;
    private BlendDecalBuffers _decalMeasurementBuffers;

    /// <summary>Completed blend GPU measurements, identified by the caller's simulation frame.</summary>
    public event Action<ClusterBlendMetrics>? BlendFrameCompleted;

    /// <summary>Associates the next graph submission with the application's simulation frame.</summary>
    public void SetMeasurementFrame(int simulationFrame) => _measurementSimulationFrame = simulationFrame;

    private void ReceiveBlendFrame(ClusterBlendMetrics frame) => BlendFrameCompleted?.Invoke(frame);

    private void ReconcileBlendMeasurements(ref RenderGraphEdit edit)
    {
        if (!_options.MeasureBlending)
        {
            if (GraphData.Stencil is { } sourceStencil)
                sourceStencil.SetSourceReady(ref edit, GraphData.Transparency?.ColorPass ?? sourceStencil.ClearPass, null);
            return;
        }
        TransparencyData? transparent = GraphData.Transparency;
        MaterialDecalData decals = GraphData.Decals!;
        GraphPassId[] colors = GraphData.Stencil is { } colorStencil ? [colorStencil.ClearPass] : [];
        if (transparent is not null) colors = [.. colors, .. transparent.ColorPasses];
        var passes = new BlendMeasurementPasses(
            transparent?.Builds ?? [], transparent?.RequestPasses ?? [], colors, _regions.Feedback, decals.Builds,
            GraphData.ShadowRequestGroups.Values.OrderBy(group => group.Stage.BinIndex).Select(group => group.Pass).ToArray(),
            GraphData.ShadeGroups.Values.OrderBy(group => group.Stage.BinIndex).Select(group => group.Pass).ToArray());
        if (GraphData.BlendMeasurements is { } previous && !previous.HasPasses(passes)) RemoveBlendMeasurements(ref edit);
        GraphData.BlendMeasurements ??= new(_backend, _device, _graph, ref edit,
            passes, CreateBlendMeasurementBuffers(), decals.Work, ReceiveBlendFrame);
        ClusterBlendMeasurements measurements = GraphData.BlendMeasurements;
        if (GraphData.Stencil is { } stencil)
            stencil.SetSourceReady(ref edit, transparent?.ColorPass ?? stencil.ClearPass, measurements);
    }

    private void RemoveBlendMeasurements(ref RenderGraphEdit edit)
    {
        GraphData.BlendMeasurements?.Remove(ref edit);
        GraphData.BlendMeasurements = null;
    }

    private BlendMeasurementBuffers CreateBlendMeasurementBuffers()
    {
        ulong tileViewBytes = _options.BlendIndex == ClusterBlendIndex.Xy ? (ulong)_viewGrid.ViewCount * 192u : 0;
        ulong capacityBytes = (GraphData.Stencil?.CapacityBytes ?? 0) + tileViewBytes;
        ulong allocationBytes = GraphData.Stencil?.AllocationBytes ?? 0;
        if (GraphData.Transparency is not { } data) return new(default, 0, 0, capacityBytes, capacityBytes, allocationBytes, 0);
        FrameResources frame = GraphData.Resources;
        BufferUsages storage = BufferUsages.ShaderRead | BufferUsages.ShaderWrite;
        BufferDesc triangles = new((ulong)data.TriangleCapacity * data.Shaders.TriangleStride, storage);
        BufferDesc counts = new((ulong)_viewGrid.CellCountPerView * frame.ViewCount * 4u, storage | BufferUsages.CopySource);
        BufferDesc triangleCount = new(20, storage | BufferUsages.CopySource);
        BufferDesc arguments = new(60, storage | BufferUsages.Indirect);
        BufferDesc states = GraphData.BufferDescriptions[data.States].Description;
        BufferDesc orderedStates = GraphData.BufferDescriptions[data.OrderedStates].Description;
        BufferDesc[] descriptions = [triangles, counts, counts, counts, counts,
            new((ulong)_options.BlendReferenceCapacity * 4u, storage), triangleCount, arguments, states, orderedStates,
            new((ulong)_options.MaxCandidates * 12u, storage), new(16, storage),
            new((ulong)_options.MaxCandidates * 4u, storage), new((ulong)data.TriangleCapacity * data.Shaders.SourceBoundsStride, storage)];
        ulong fixedBytes = capacityBytes + counts.Size * 4u + triangleCount.Size + arguments.Size + states.Size + orderedStates.Size + 16u;
        foreach (BufferDesc description in descriptions)
        {
            capacityBytes += description.Size;
            allocationBytes += _backend.GetBufferMemoryRequirements(_device, description).Size;
        }
        return new(data.TriangleCount, data.Shaders.TriangleStride, data.Shaders.SourceBoundsStride,
            fixedBytes, capacityBytes, allocationBytes, data.TriangleCapacity);
    }

    private BlendDecalBuffers CaptureDecalBuffers()
    {
        MaterialDecalData data = GraphData.Decals!;
        BufferDesc volumes = GraphData.BufferDescriptions[data.Volumes].Description;
        BufferDesc properties = GraphData.BufferDescriptions[data.Properties].Description;
        ulong rangeBytes = Math.Max(32ul, (ulong)data.Sources.Length * _outputDescription.ViewCount * 32u);
        var sizes = (volumes.Size, properties.Size, rangeBytes);
        if (_decalBufferSizes == sizes) return _decalMeasurementBuffers;
        BufferUsages storage = BufferUsages.ShaderRead | BufferUsages.ShaderWrite;
        BufferDesc counts = new(Math.Max(4ul, (ulong)_viewGrid.CellCountPerView * _outputDescription.ViewCount * 4u), storage);
        BufferDesc work = new(16, storage | BufferUsages.CopySource);
        BufferDesc[] descriptions = [volumes, properties, counts, counts, counts,
            new((ulong)_options.BlendReferenceCapacity * 4u, storage), new(rangeBytes, storage), work];
        ulong fixedBytes = counts.Size * 3u + work.Size + data.Parameters.Range.Size;
        ulong capacityBytes = data.Parameters.Range.Size, allocationBytes = 0;
        foreach (BufferDesc description in descriptions)
        {
            capacityBytes += description.Size;
            allocationBytes += _backend.GetBufferMemoryRequirements(_device, description).Size;
        }
        if (data.EmptyViews != default)
        {
            BufferDesc emptyViews = GraphData.BufferDescriptions[data.EmptyViews].Description;
            fixedBytes += emptyViews.Size; capacityBytes += emptyViews.Size;
            allocationBytes += _backend.GetBufferMemoryRequirements(_device, emptyViews).Size;
        }
        _decalBufferSizes = sizes;
        return _decalMeasurementBuffers = new(_outputDescription.ViewCount,
            fixedBytes, properties.Size, capacityBytes, allocationBytes);
    }
}
