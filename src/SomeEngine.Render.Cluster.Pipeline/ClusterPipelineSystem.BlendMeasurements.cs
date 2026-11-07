using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private int _measurementSimulationFrame;

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
                sourceStencil.SetSourceReady(ref edit, GraphData.Transparency?.Tiles.ColorPass ?? sourceStencil.ClearPass, null);
            return;
        }
        TransparencyData? transparent = GraphData.Transparency;
        MaterialDecalData decals = GraphData.Decals!;
        GraphPassId[] colors = GraphData.Stencil is { } colorStencil ? [colorStencil.ClearPass] : [];
        if (transparent is not null) colors = [.. colors, .. transparent.Tiles.ColorPasses];
        var passes = new BlendMeasurementPasses(
            transparent?.Tiles.Builds ?? [], transparent is null ? [] : [.. transparent.Tiles.CoveragePasses, .. transparent.Tiles.RequestPasses], colors, _regions.Feedback, decals.Builds,
            GraphData.ShadowRequestGroups.Values.OrderBy(group => group.Stage.BinIndex).Select(group => group.Pass).ToArray(),
            GraphData.ShadeGroups.Values.OrderBy(group => group.Stage.BinIndex).Select(group => group.Pass).ToArray());
        if (GraphData.BlendMeasurements is { } previous && !previous.HasPasses(passes)) RemoveBlendMeasurements(ref edit);
        GraphData.BlendMeasurements ??= new(_backend, _device, _graph, ref edit,
            passes, CreateBlendMeasurementBuffers(ref edit), decals.Work, ReceiveBlendFrame);
        ClusterBlendMeasurements measurements = GraphData.BlendMeasurements;
        if (GraphData.Stencil is { } stencil)
            stencil.SetSourceReady(ref edit, transparent?.Tiles.ColorPass ?? stencil.ClearPass, measurements);
    }

    private void RemoveBlendMeasurements(ref RenderGraphEdit edit)
    {
        GraphData.BlendMeasurements?.Remove(ref edit);
        GraphData.BlendMeasurements = null;
    }

    private BlendMeasurementBuffers CreateBlendMeasurementBuffers(ref RenderGraphEdit edit)
    {
        ulong tileViewBytes = _options.BlendIndex is ClusterBlendIndex.Xy or ClusterBlendIndex.Adaptive ? (ulong)_viewGrid.ViewCount * 192u : 0;
        ulong capacityBytes = (GraphData.Stencil?.CapacityBytes ?? 0) + tileViewBytes;
        ulong allocationBytes = GraphData.Stencil?.AllocationBytes ?? 0;
        if (GraphData.Transparency is not { } data) return new(default, 0, 0, capacityBytes, capacityBytes, allocationBytes, 0);
        var tiles = data.Tiles.GetMemoryUsage(ref edit, _backend, _device);
        ulong fixedBytes = capacityBytes + tiles.FixedBytes;
        capacityBytes += tiles.CapacityBytes;
        allocationBytes += tiles.AllocationBytes;
        GraphBufferId[] buffers = [data.States, data.OrderedStates,
            data.Sources, data.SourceCounts, data.ActiveSources];
        foreach (GraphBufferId buffer in buffers)
        {
            BufferDesc description = edit.GetBufferDescription(buffer);
            capacityBytes += description.Size;
            allocationBytes += _backend.GetBufferMemoryRequirements(_device, description).Size;
            if (buffer != data.Sources && buffer != data.ActiveSources)
                fixedBytes += description.Size;
        }
        return new(data.Tiles.TriangleCount, data.Shaders.TriangleStride, data.Shaders.SourceBoundsStride,
            fixedBytes, capacityBytes, allocationBytes, data.Tiles.TriangleCapacity);
    }

    private BlendDecalBuffers CaptureDecalBuffers(ref RenderGraphEdit edit)
    {
        MaterialDecalData data = GraphData.Decals!;
        BufferDesc volumes = edit.GetBufferDescription(data.Volumes);
        BufferDesc properties = edit.GetBufferDescription(data.Properties);
        ulong rangeBytes = edit.GetBufferDescription(data.Ranges).Size;
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
            BufferDesc emptyViews = edit.GetBufferDescription(data.EmptyViews);
            fixedBytes += emptyViews.Size; capacityBytes += emptyViews.Size;
            allocationBytes += _backend.GetBufferMemoryRequirements(_device, emptyViews).Size;
        }
        return new(_outputDescription.ViewCount,
            fixedBytes, properties.Size, capacityBytes, allocationBytes);
    }
}
