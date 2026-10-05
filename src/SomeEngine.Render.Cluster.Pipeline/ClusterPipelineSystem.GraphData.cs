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
        internal ImmutableDictionary<GraphBufferId, (BufferDesc Description, GraphPassId UploadPass)> BufferDescriptions =
            ImmutableDictionary<GraphBufferId, (BufferDesc, GraphPassId)>.Empty;
        internal ImmutableDictionary<GraphBufferId, ImmutableList<BufferWrite>> BufferWrites = ImmutableDictionary<GraphBufferId, ImmutableList<BufferWrite>>.Empty;
        internal ImmutableDictionary<GraphBufferInput, ShaderParameters> Parameters = ImmutableDictionary<GraphBufferInput, ShaderParameters>.Empty;
        internal GraphBufferId ShaderParameterBytes;
        internal int ParameterByteCount;
        internal ImmutableDictionary<int, ImmutableStack<int>> ParameterFreeOffsets = ImmutableDictionary<int, ImmutableStack<int>>.Empty;
        internal GraphReadbackId PageFaultReadback;
        internal GraphReadbackId FrameMetricsReadback;
        internal GraphPassId PageUpload;
        internal GraphPassId BvhUpload;
        internal GraphReadbackId WorkStatusReadback;
        internal GraphRegionId TargetFirstReference;
        internal GraphRegionId TargetSecondReference;
        internal GraphRegionId PrimaryGeometry;
        internal GraphRegionId HistoryInitialization;
        internal GraphRegionId SceneInitialization;
        internal GraphRegionId SceneBoundsRegion;
        internal GraphRegionId ShadeBinning;
        internal GraphRegionId ShadeScatter;
        internal FrameResources Resources;
        internal ClusterPipelineOutputs Outputs;
        internal ClusterLayerDispatch SceneBounds;
        internal ClusterLayerDispatch PrimarySeed;
        internal ClusterLayerDispatch[] ShadeBinDispatches = [];
        internal ImmutableDictionary<int, GraphRegionMap<int, bool>> TraversalLevels = ImmutableDictionary<int, GraphRegionMap<int, bool>>.Empty;
        internal ImmutableDictionary<FeedbackKind, GraphPassId> Feedback = ImmutableDictionary<FeedbackKind, GraphPassId>.Empty;
        internal ImmutableDictionary<int, RasterPhase> RasterPhases = ImmutableDictionary<int, RasterPhase>.Empty;
        internal RasterBinning?[] RasterBinning = new RasterBinning?[RasterPhaseCount];
        internal GraphRegionMap<uint, ShadeGroup> ShadeGroups = null!;
        internal List<ClusterLayerDispatch> LayerDispatches = [];
        internal List<ClusterLayerDispatch> WinnerDispatches = [];
        internal GraphBufferInput ShadeBinUniforms = null!;
        internal ImmutableDictionary<(Material Material, uint Property), MaterialInput> MaterialInputs = ImmutableDictionary<(Material Material, uint Property), MaterialInput>.Empty;
        internal GraphBufferInput ShadeScalarOffsets = null!;
        internal GraphBufferInput ShadeScalars = null!;
        internal GraphBufferId ScalarBytes;
        internal GraphBufferId ScalarOffsets;
        internal GraphBufferInput PrimarySeedUniforms = null!;
        internal GraphBufferInput TargetSeedUniforms = null!;
        internal GraphBufferId SceneChunks;
        internal GraphBufferId InstanceWorldBounds;
        internal GraphBufferId SceneInstanceData;
        internal GraphBufferInput SceneInstances = null!;
        internal GraphBufferInput SceneUniforms = null!;
        internal ImmutableDictionary<SomeEngine.Graphics.Buffer, MaterialBuffer> MaterialBuffers =
            ImmutableDictionary<SomeEngine.Graphics.Buffer, MaterialBuffer>.Empty;
        internal ImmutableDictionary<SomeEngine.Resources.Texture, MaterialTexture> MaterialTextures =
            ImmutableDictionary<SomeEngine.Resources.Texture, MaterialTexture>.Empty;

        internal ClusterGraphData Copy()
        {
            var data = (ClusterGraphData)MemberwiseClone();
            data.LayerDispatches = new(LayerDispatches);
            data.WinnerDispatches = new(WinnerDispatches);
            data.RasterBinning = (RasterBinning?[])RasterBinning.Clone();
            return data;
        }
    }

    private delegate void GraphEditWork(ref RenderGraphEdit edit);

    private void EditGraph(GraphEditWork update)
    {
        RenderGraphEdit edit = _graph.BeginEdit();
        _editingGraphData = _graphData.Copy();
        try
        {
            update(ref edit);
            edit.Commit();
            AcceptMaterialTextures(_editingGraphData.MaterialTextures);
            _graphData = _editingGraphData;
        }
        finally { edit.Dispose(); _editingGraphData = null; }
    }
}
