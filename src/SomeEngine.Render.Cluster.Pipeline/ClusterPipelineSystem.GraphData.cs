using System.Collections.Immutable;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private ClusterGraphData _graphData = new();
    private ClusterGraphData? _editingGraphData;
    private ClusterGraphData GraphData => _editingGraphData ?? _graphData;

    private enum FeedbackKind { Candidates, FrameMetrics }

    // These identities address frame command data and completion delivery. Region maps hold
    // business keys; the RenderGraph tree supplies stage order and subtree membership.
    private sealed class ClusterGraphData
    {
        internal ImmutableDictionary<GraphBufferId, ImmutableList<BufferWrite>> BufferWrites = ImmutableDictionary<GraphBufferId, ImmutableList<BufferWrite>>.Empty;
        internal ClusterBytes ParameterData = ClusterBytes.Zero(0);
        internal GraphBufferId ShaderParameterBytes;
        internal int ParameterByteCount;
        internal ImmutableDictionary<int, ImmutableStack<int>> ParameterFreeOffsets = ImmutableDictionary<int, ImmutableStack<int>>.Empty;
        internal GraphPassId[] Passes = [];
        internal GraphReadbackId PageFaultReadback;
        internal GraphReadbackId FrameMetricsReadback;
        internal GraphPassId PageUpload;
        internal GraphPassId BvhUpload;
        internal GraphRegionId LeafFlagRegion;
        internal GraphPassId LeafFlagPass;
        internal GraphBufferInput LeafFlagParameters = null!;
        internal GraphBufferInput LeafFlagData = null!;
        internal GraphBufferId LeafFlagEmptyData;
        internal GraphBufferId LeafFlagEmptyParameters;
        internal GraphRegionId TargetFirstReference;
        internal GraphRegionId TargetSecondReference;
        internal GraphRegionId PrimaryGeometry;
        internal GraphRegionId HistoryInitialization;
        internal GraphRegionId SceneInitialization;
        internal GraphRegionId SceneBoundsRegion;
        internal GraphRegionId ShadeBinning;
        internal GraphRegionId ShadeCountRegion;
        internal GraphPassId ShadeCountPass;
        internal GraphRegionId ShadeScatter;
        internal FrameResources Resources;
        internal TransparencyData? Transparency;
        internal MaterialDecalData? Decals;
        internal BlendDecalBuffers DecalMeasurementBuffers;
        internal ClusterStencil? Stencil;
        internal GraphRegionId BlendColors;
        internal ClusterBlendMeasurements? BlendMeasurements;
        internal ClusterPipelineOutputs Outputs;
        internal GraphPassId SceneBounds;
        internal GraphPassId PrimarySeed;
        internal GraphPassId[] ShadeBinDispatches = [];
        internal ImmutableDictionary<FeedbackKind, GraphPassId> Feedback = ImmutableDictionary<FeedbackKind, GraphPassId>.Empty;
        internal ImmutableDictionary<int, RasterPhase> RasterPhases = ImmutableDictionary<int, RasterPhase>.Empty;
        internal RasterBinning?[] RasterBinning = new RasterBinning?[RasterPhaseCount];
        internal GraphRegionMap<uint, ShadeGroup> ShadeGroups = null!;
        internal GraphRegionMap<uint, ShadeGroup> ShadowRequestGroups = null!;
        internal List<ClusterLayerDispatch> LayerDispatches = [];
        internal List<GraphPassId> WinnerDispatches = [];
        internal GraphBufferSlice ShadeBinUniforms;
        internal ImmutableDictionary<(Material Material, uint Property, ResourceBindingType Type), GraphParameterInput> MaterialInputs = ImmutableDictionary<(Material Material, uint Property, ResourceBindingType Type), GraphParameterInput>.Empty;
        internal ImmutableHashSet<(Material Material, uint Property, ResourceBindingType Type)> MaterialInputChanges = ImmutableHashSet<(Material Material, uint Property, ResourceBindingType Type)>.Empty;
        internal MaterialTexture? MissingMaterialTexture;
        internal uint MaterialTextureDecalVersion;
        internal GraphBufferInput ShadeScalarOffsets = null!;
        internal GraphBufferInput ShadeScalars = null!;
        internal GraphBufferId ScalarBytes;
        internal GraphBufferId ScalarOffsets;
        internal GraphBufferSlice PrimarySeedUniforms;
        internal GraphBufferSlice TargetSeedUniforms;
        internal GraphBufferId SceneChunks;
        internal GraphBufferId InstanceWorldBounds;
        internal GraphBufferId SceneInstanceData;
        internal GraphBufferInput SceneInstances = null!;
        internal GraphBufferSlice SceneUniforms;
        internal ImmutableDictionary<SomeEngine.Graphics.Buffer, MaterialBuffer> MaterialBuffers =
            ImmutableDictionary<SomeEngine.Graphics.Buffer, MaterialBuffer>.Empty;
        internal ImmutableDictionary<SomeEngine.Resources.Texture, MaterialTexture> MaterialTextures =
            ImmutableDictionary<SomeEngine.Resources.Texture, MaterialTexture>.Empty;

        internal ClusterGraphData Copy() => (ClusterGraphData)MemberwiseClone();
    }

    // An edit borrows published containers until its first write. The published data
    // stays unchanged if any later work in the edit fails.
    private List<ClusterLayerDispatch> GetWritableLayerDispatches()
    {
        if (_editingGraphData is not { } data) return _graphData.LayerDispatches;
        if (ReferenceEquals(data.LayerDispatches, _graphData.LayerDispatches))
            data.LayerDispatches = new(data.LayerDispatches);
        return data.LayerDispatches;
    }

    private List<GraphPassId> GetWritableWinnerDispatches()
    {
        if (_editingGraphData is not { } data) return _graphData.WinnerDispatches;
        if (ReferenceEquals(data.WinnerDispatches, _graphData.WinnerDispatches))
            data.WinnerDispatches = new(data.WinnerDispatches);
        return data.WinnerDispatches;
    }

    private RasterBinning?[] GetWritableRasterBinning()
    {
        if (_editingGraphData is not { } data) return _graphData.RasterBinning;
        if (ReferenceEquals(data.RasterBinning, _graphData.RasterBinning))
            data.RasterBinning = (RasterBinning?[])data.RasterBinning.Clone();
        return data.RasterBinning;
    }

    private delegate void GraphEditWork(ref RenderGraphEdit edit);

    private void EditGraph(GraphEditWork update, Action? accept = null)
    {
        RenderGraphEdit edit = _graph.BeginEdit();
        List<Exception>? failures = null;
        try
        {
            _editingGraphData = _graphData.Copy();
            update(ref edit);
            List<GraphPassId> passes = [];
            foreach (GraphRegionId region in new[] { _regions.PrimaryGeometry, _regions.TargetGeometry,
                _regions.Shading, _regions.Feedback, _shadowRequestRegion, _transparencyRequestRegion })
                if (region != default) passes.AddRange(edit.GetPasses(region));
            _editingGraphData.Passes = passes.ToArray();
            edit.Commit();
        }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        finally
        {
            if (edit.IsAccepted)
            {
                try { AcceptMaterialTextures(_editingGraphData!.MaterialTextures, _editingGraphData.MissingMaterialTexture); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
                _graphData = _editingGraphData!;
                try { accept?.Invoke(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            try { edit.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            _editingGraphData = null;
        }
        if (failures is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures.Count == 1
                ? failures[0] : new AggregateException("Cluster graph edit and publication reported failures.", failures)).Throw();
    }
}
