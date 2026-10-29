using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using TextureViewDimension = SomeEngine.Graphics.TextureViewDimension;

namespace SomeEngine.Render.Features.GI;

/// <summary>Maintains screen-space directional radiance and the scene's reflected-radiance caches.</summary>
public sealed partial class ScreenProbeFeature : IGraphResourceProvider, IRenderGraphFrameExecutionObserver, IDisposable
{
    private const uint TrainingCount = 128, HashCount = 65_536, GridSide = 128;
    private readonly int _width, _height;
    private readonly uint _views, _probeCount, _rayCount, _candidateCapacity;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly Shader? _shader;
    private readonly IGraphResourceProvider? _shared;
    private readonly GraphRegionId _sceneRegion, _traceRegion;
    private readonly GraphBufferInput _captureSettings, _screenSettings, _cacheSettings;
    private readonly GraphBufferInput[] _sceneSettings;
    private readonly FeatureUniformBuffer _uniformData;
    private readonly GraphTextureId _normal, _diffuse, _reflectedRadiance;
    private readonly Dictionary<string, BufferData> _buffers = [];
    private readonly Dictionary<string, LinkedComputePipeline> _pipelines = [];
    private readonly Dictionary<uint, MaterialPasses> _materials = [];
    private readonly HashSet<GraphPassId> _geometryReaders = [];
    private readonly List<GraphPassId> _resets = [];
    private readonly List<GraphPassId> _fieldPasses = [];
    private readonly QueryPasses[] _queries = new QueryPasses[2];
    private readonly GiSceneData? _scene;
    private ClusterPipelineOutputs _surfaces;
    private ClusterPipelineSystem? _cluster;
    private GraphPassId _clearBuffers, _initializeProbes, _composeField, _complete;
    private uint _frame, _materialCapacity = 1;
    private ulong _fieldRevision;
    private bool _fieldChanged = true;
    private bool _resetBuffers = true;

    public ScreenProbeFeature(IGraphicsBackend backend, Device device,
        global::SomeEngine.RenderGraph.RenderGraph graph, int width, int height, uint viewCount,
        Shader? shader, bool enabled, RenderWorld? world = null, IGraphResourceProvider? shared = null,
        GraphRegionId sceneRegion = default, GraphRegionId traceRegion = default)
    {
        (_backend, _device, _graph, _shader, _shared) = (backend, device, graph, shader, shared);
        (_width, _height, _views, Enabled) = (width, height, viewCount, enabled);
        (_sceneRegion, _traceRegion) = (sceneRegion, traceRegion);
        _probeCount = ProbeWidth * ProbeHeight * viewCount;
        _rayCount = _probeCount + TrainingCount;
        _candidateCapacity = _probeCount * 64u + TrainingCount * 2048u;
        if (enabled) _scene = new GiSceneData(world!, graph);
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            _captureSettings = edit.CreateBufferInput(BufferUsages.Constant);
            _screenSettings = edit.CreateBufferInput(BufferUsages.Constant);
            _cacheSettings = edit.CreateBufferInput(BufferUsages.Constant);
            _sceneSettings = [edit.CreateBufferInput(BufferUsages.Constant), edit.CreateBufferInput(BufferUsages.Constant)];
            _uniformData = new FeatureUniformBuffer(ref edit,
                enabled ? [_captureSettings, _screenSettings, _cacheSettings, .. _sceneSettings] : [_captureSettings],
                device.Capabilities.Limits.ConstantBufferAlignment, "GI constants");
            if (enabled)
            {
                _normal = edit.CreatePersistentTexture(SurfaceTexture(Format.R16G16B16A16Float, "GI material normals"));
                _diffuse = edit.CreatePersistentTexture(SurfaceTexture(Format.R8G8B8A8UNorm, "GI material diffuse"));
                _reflectedRadiance = edit.CreatePersistentTexture(SurfaceTexture(Format.R11G11B10Float, "GI reflected radiance"));
                CreateBuffers(ref edit);
                CreateEmissiveResources(ref edit);
                InitializeBuffers(ref edit);
                var clear = new SurfaceClearData(_normal, _diffuse, _reflectedRadiance, viewCount);
                ClearSurfacesPass = edit.AddCopyFramePass("Clear GI material surfaces", PassQueueSelection.AnyOfType(QueueType.Graphics), clear,
                    new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                    SurfaceClearData.Declare, SurfaceClearData.Record, region: sceneRegion);
                InitializeEmissiveHistory(ref edit);
            }
            edit.Commit();
        }
        finally { edit.Dispose(); }
        TextureDesc SurfaceTexture(Format format, string label) => new(TextureDimension.Texture2D,
            (uint)width, (uint)height, 1, 1, viewCount, 1, format,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.CopyDestination | TextureUsages.ColorAttachment, label: label);
    }

    public bool Enabled { get; }
    public GraphPassId ClearSurfacesPass { get; }
    public GraphPassId CompletePass => _complete;
    private uint ProbeWidth => ((uint)_width + 7u) / 8u;
    private uint ProbeHeight => ((uint)_height + 7u) / 8u;

    /// <summary>Applies mesh and material changes at the existing graph-edit boundary.</summary>
    public void Reconcile(ClusterPipelineSystem cluster, in ClusterPipelineOutputs surfaces)
    {
        if (!Enabled) return;
        _cluster = cluster;
        _surfaces = surfaces;
        _fieldChanged |= _scene!.Reconcile();
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            if (_complete == default) Connect(ref edit);
            uint capacity = cluster.MaterialIndexCapacity;
            if (capacity != _materialCapacity)
            {
                ResizeMaterialBuffers(ref edit, capacity);
                _materialCapacity = capacity;
                _resetBuffers = true;
            }
            ReconcileEmissiveResources(ref edit, (uint)cluster.CurrentInstanceCount, capacity);
            HashSet<uint> active = [];
            foreach (ClusterMaterialEntry entry in cluster.EnumerateMaterials())
            {
                Shader shader = entry.Material.Shader!;
                int target = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
                _ = shader.RequireSlangEntry(target, "IntersectGiMaterial", SlangShaderSharp.SlangStage.Compute);
                _ = shader.RequireSlangEntry(target, "ShadeGiMaterial", SlangShaderSharp.SlangStage.Compute);
                active.Add(entry.Index);
                if (_materials.TryGetValue(entry.Index, out MaterialPasses? previous) &&
                    ReferenceEquals(previous.Material, entry.Material) && ReferenceEquals(previous.Shader, shader)) continue;
                if (previous is not null) RemoveMaterial(ref edit, previous);
                GraphBufferId constant = edit.DeclareExternalBuffer(new BufferDesc(256, BufferUsages.Constant, "GI material index"), MemoryType.Upload);
                byte[] bytes = new byte[256];
                uint index = entry.Index;
                MemoryMarshal.Write(bytes, in index);
                var resources = new MaterialResources(this, constant);
                var passes = new GraphPassId[7];
                for (int stage = 0; stage < 2; stage++)
                {
                    QueryPasses query = _queries[stage];
                    GraphPassId intersect = cluster.AddMaterialComputePass(ref edit, index, shader, "IntersectGiMaterial",
                        _buffers["GiMaterialDispatch"].Id, index * 12ul, resources, _traceRegion);
                    GraphPassId shade = cluster.AddMaterialComputePass(ref edit, index, shader, "ShadeGiMaterial",
                        _buffers["GiMaterialDispatch"].Id, index * 12ul, resources, _traceRegion);
                    edit.OrderAfter(intersect, query.TriangleWork);
                    edit.OrderAfter(query.Reduce, intersect);
                    edit.OrderAfter(shade, query.SurfaceWork);
                    edit.OrderAfter(query.Finish, shade);
                    passes[stage * 2] = intersect;
                    passes[stage * 2 + 1] = shade;
                }
                AddEmissiveMaterialPasses(ref edit, index, shader, resources).CopyTo(passes, 4);
                _materials[index] = new(entry.Material, shader, constant, bytes, passes);
            }
            foreach (uint index in _materials.Keys.Where(index => !active.Contains(index)).ToArray())
            {
                RemoveMaterial(ref edit, _materials[index]);
                _materials.Remove(index);
            }
            edit.Commit();
        }
        finally { edit.Dispose(); }
    }

    public void Record(ref RenderGraphFrame graph, bool cameraCut)
    {
        GiCaptureSettings capture = new(Enabled ? 1u : 0u, 0, 0, 0);
        _uniformData.Write(ref graph, _captureSettings, in capture);
        if (Enabled)
        {
            _scene!.Record(ref graph);
            RecordEmissive(ref graph, cameraCut);
            GiScreenSettings screen = new((uint)_width, (uint)_height, ProbeWidth, ProbeHeight,
                _frame, _frame & 1u, _frame != 0u && !cameraCut ? 1u : 0u, _views);
            _uniformData.Write(ref graph, _screenSettings, in screen);
            GiCacheSettings cache = new(_frame, TrainingCount * 2u, HashCount, 3, 0.25f, 0.002f, _resetBuffers ? 1u : 0u, 0);
            _uniformData.Write(ref graph, _cacheSettings, in cache);
            for (uint stage = 0; stage < 2; stage++)
            {
                GiSceneSettings scene = new((uint)_cluster!.CurrentInstanceCount, _materialCapacity, _candidateCapacity,
                    TrainingCount, GridSide, stage, _scene.MeshFieldsIndexCount, _surfaces.Geometry.UseInstanceGroups ? 1u : 0u,
                    2f, 24f, Vector2.Zero);
                _uniformData.Write(ref graph, _sceneSettings[stage], in scene);
                graph.SetPassData(_queries[stage].Begin, new DispatchArguments((Math.Max(_rayCount, _materialCapacity) + 63u) / 64u, 1, 1));
                graph.SetPassData(_queries[stage].Clear, new DispatchArguments((_materialCapacity + 63u) / 64u, 1, 1));
            }
            graph.SetPassData(_composeField, new DispatchArguments(Math.Max(1u, (uint)_cluster!.CurrentInstanceCount), 1, 1));
            bool updateField = _resetBuffers || _fieldChanged || _fieldRevision != _cluster.CurrentInstanceRevision;
            foreach (GraphPassId pass in _fieldPasses) graph.SetPassEnabled(pass, updateField);
            graph.SetPassEnabled(_clearBuffers, _resetBuffers);
            foreach (GraphPassId reset in _resets) graph.SetPassEnabled(reset, _resetBuffers);
            graph.SetPassEnabled(_initializeProbes, _resetBuffers || cameraCut);
            foreach (GraphPassId id in _geometryReaders)
            {
                PassDefinition pass = graph.GetPass(id);
                _surfaces.Geometry.ReadPageHeap(ref pass, PipelineSync.ComputeShading);
                _surfaces.Geometry.ReadBvh(ref pass, PipelineSync.ComputeShading);
            }
            foreach (MaterialPasses material in _materials.Values)
            {
                graph.SetBufferData(material.Constants, material.Bytes);
                foreach (GraphPassId pass in material.Passes) _cluster.ReadMaterialComputeResources(ref graph, pass);
            }
            graph.ObserveExecution(this);
        }
        _uniformData.Upload(ref graph);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (result.TryGetPassCompletion(_complete, out _))
        {
            _frame++;
            _resetBuffers = false;
            _fieldChanged = false;
            _fieldRevision = _cluster!.CurrentInstanceRevision;
        }
    }

    private static void RemoveMaterial(ref RenderGraphEdit edit, MaterialPasses material)
    {
        foreach (GraphPassId pass in material.Passes) edit.Remove(pass);
        edit.Remove(material.Constants);
    }

    public void Dispose()
    {
        _scene?.Dispose();
        foreach (LinkedComputePipeline pipeline in _pipelines.Values) pipeline.Dispose();
    }

    private sealed record MaterialPasses(Material Material, Shader Shader, GraphBufferId Constants, byte[] Bytes, GraphPassId[] Passes);
    private readonly record struct BufferData(GraphBufferId Id, uint Stride, BufferUsages Usages);
    private readonly record struct QueryPasses(GraphPassId Begin, GraphPassId Clear, GraphPassId TriangleWork, GraphPassId Reduce, GraphPassId SurfaceWork, GraphPassId Finish);
    private readonly record struct GiCaptureSettings(uint Enabled, uint Padding0, uint Padding1, uint Padding2);
    private readonly record struct GiScreenSettings(uint Width, uint Height, uint ProbeWidth, uint ProbeHeight, uint Frame, uint HistoryLayer, uint UseHistory, uint ViewCount);
    private readonly record struct GiCacheSettings(uint Frame, uint TrainingCount, uint HashCount, uint CacheMode, float CellSize, float LearningRate, uint Reset, uint Padding);
    private readonly record struct GiSceneSettings(uint InstanceCount, uint MaterialCount, uint CandidateCapacity, uint TrainingCount,
        uint GridSide, uint TrainingStage, uint MeshFieldsIndexCount, uint UseInstanceGroups, float NearDistance, float TraceDistance, Vector2 Padding0);
    private readonly record struct SurfaceClearData(GraphTextureId Normal, GraphTextureId Diffuse, GraphTextureId Radiance, uint Views)
    {
        internal static void Declare(ref PassDefinition pass, ref SurfaceClearData data)
        {
            var range = new TextureSubresourceRange(0, 1, 0, data.Views, TextureAspects.Color);
            foreach (GraphTextureId texture in new[] { data.Normal, data.Diffuse, data.Radiance })
                pass.Write(texture, range, PipelineSync.Copy, ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete);
        }
        internal static void Record(ref CopyPassCommandScope commands, in SurfaceClearData data)
        {
            var range = new TextureSubresourceRange(0, 1, 0, data.Views, TextureAspects.Color);
            commands.ClearTexture(commands.GetTexture(data.Normal), range, default);
            commands.ClearTexture(commands.GetTexture(data.Diffuse), range, default);
            commands.ClearTexture(commands.GetTexture(data.Radiance), range, default);
        }
    }
}
