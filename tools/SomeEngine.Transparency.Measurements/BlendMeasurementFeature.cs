using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Features.Layers;
using SomeEngine.RenderGraph;
using SomeEngine.Resources.Layers;
using SomeEngine.Runtime;

namespace SomeEngine.Transparency.Measurements;

/// <summary>Collects actual layer-stage times and fragment storage through the application's frame hook.</summary>
public sealed class BlendMeasurementFeature : IApplicationFrameFeature
{
    private readonly ApplicationRendering _rendering;
    private readonly Action<BlendFrameMetrics> _completed;
    private BlendGpuMeasurements? _measurements;
    private BlendMeasurementPasses _passes;
    private GraphRegionId _feedback;
    private bool _disposed;

    public BlendMeasurementFeature(ApplicationRendering rendering, Action<BlendFrameMetrics> completed)
    {
        (_rendering, _completed) = (rendering, completed);
        Reconcile();
    }

    public void Reconcile()
    {
        RenderGraphEdit edit = _rendering.Graph.BeginEdit();
        try
        {
            GraphRegionId feedback = _feedback == default
                ? edit.AddRegion("Layer effect measurements", _rendering.Region) : _feedback;
            LayerComposition layers = _rendering.Layers!;
            var decals = _rendering.Decals;
            GraphPassId requests = _rendering.ShadowRequestPass();
            var passes = new BlendMeasurementPasses(
                PipelinePasses(ref edit, layers.VisibilityRegion),
                PipelinePasses(ref edit, layers.ShadingBins.Region),
                PipelinePasses(ref edit, layers.MaterialShading.Region),
                PipelinePasses(ref edit, layers.CompositeRegion), feedback,
                decals is null ? [] : PipelinePasses(ref edit, decals.Region),
                requests == default ? [] : [requests], _rendering.MaterialPasses().ToArray());
            BlendGpuMeasurements? measurements = _measurements;
            if (measurements is not null && !measurements.HasPasses(passes))
            {
                measurements.Remove(ref edit);
                measurements = null;
            }
            measurements ??= new BlendGpuMeasurements(_rendering.Backend, _rendering.Device,
                _rendering.Graph, ref edit, passes, decals?.WorkCounters ?? default, _completed);
            edit.Commit();
            (_feedback, _passes, _measurements) = (feedback, passes, measurements);
        }
        finally { edit.Dispose(); }
    }

    public void Record(ref RenderGraphFrame frame, int simulationFrame)
    {
        bool reusedDecals = true;
        foreach (GraphPassId pass in _passes.Decals) reusedDecals &= !frame.IsPassEnabled(pass);
        _measurements!.Stage(ref frame, simulationFrame, reusedDecals, CaptureBuffers(), CaptureDecals());
    }

    private static GraphPassId[] PipelinePasses(ref RenderGraphEdit edit, GraphRegionId region)
    {
        if (region == default) return [];
        var passes = new List<GraphPassId>();
        foreach (GraphPassId pass in edit.GetPasses(region))
            if (edit.GetPass(pass).Pipeline is not null) passes.Add(pass);
        return passes.ToArray();
    }

    private BlendMeasurementBuffers CaptureBuffers()
    {
        LayerComposition layers = _rendering.Layers!;
        LayerFragmentBuffers fragments = layers.Fragments;
        BufferDesc blocks = _rendering.Graph.GetBufferDescription(fragments.Blocks);
        BufferDesc payloads = _rendering.Graph.GetBufferDescription(fragments.Payloads);
        BufferDesc heads = _rendering.Graph.GetBufferDescription(fragments.TileHeads);
        BufferDesc counters = _rendering.Graph.GetBufferDescription(fragments.FrameCounters);
        ulong allocation = 0;
        foreach (BufferDesc description in new[] { blocks, payloads, heads, counters })
            allocation += _rendering.Backend.GetBufferMemoryRequirements(_rendering.Device, description).Size;
        return new(fragments.FrameCounters, (ulong)Marshal.SizeOf<LayerFragment>(),
            payloads.Size / layers.FragmentCapacity, blocks.Size / layers.BlockCapacity,
            heads.Size + counters.Size, blocks.Size,
            blocks.Size + payloads.Size + heads.Size + counters.Size, allocation);
    }

    private BlendDecalBuffers CaptureDecals()
    {
        if (_rendering.Decals is not { } feature) return default;
        ulong capacity = 0, allocation = 0;
        foreach (BufferDesc description in feature.ResourceDescriptions)
        {
            capacity += description.Size;
            allocation += _rendering.Backend.GetBufferMemoryRequirements(_rendering.Device, description).Size;
        }
        ulong properties = _rendering.Graph.GetBufferDescription(feature.PropertyData).Size;
        ulong cells = _rendering.Graph.GetBufferDescription(feature.CellRanges).Size;
        ulong variableBytes = properties + _rendering.Graph.GetBufferDescription(feature.VolumeData).Size + cells +
            _rendering.Graph.GetBufferDescription(feature.References).Size;
        return new(feature.VolumeCount, capacity - variableBytes, cells, properties, capacity, allocation);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_rendering.Graph.IsLost || _rendering.Graph.IsDisposed) _measurements?.ReleaseQueries();
        else
        {
            RenderGraphEdit edit = _rendering.Graph.BeginEdit();
            try
            {
                _measurements?.Remove(ref edit);
                edit.Remove(_feedback);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
        _disposed = true;
    }
}
