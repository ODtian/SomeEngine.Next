using System.IO.Compression;
using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Features.Motion;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Features.Temporal;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using SlangShaderSharp;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class FrameStageRenderingTests
{
    [Theory]
    [InlineData("D3D12", false)]
    [InlineData("D3D12", true)]
    [InlineData("Vulkan", false)]
    [InlineData("Vulkan", true)]
    public async Task FrameStagesPreserveClusterColorWithShadowsEnabledOrDisabled(string backendName, bool shadows)
    {
        await using var scene = await FrameStageScene.CreateAsync(backendName, shadows);
        byte[] pixels = await scene.ReadClusterColorAsync();
        string path = Path.Combine(FrameStageScene.FindRoot(), "tests", "SomeEngine.Render.Cluster.Tests",
            "FrameStageImages", $"{backendName}-{shadows}.rgba16.gz");
        using var file = File.OpenRead(path);
        using var compressed = new GZipStream(file, CompressionMode.Decompress);
        using var expected = new MemoryStream();
        await compressed.CopyToAsync(expected);
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, expected.ToArray()),
            new(64, 64, Format.R16G16B16A16Float, pixels));
    }

    [Theory]
    [InlineData("D3D12", "Standard")]
    [InlineData("D3D12", "Unlit")]
    [InlineData("Vulkan", "Standard")]
    [InlineData("Vulkan", "Unlit")]
    public async Task MaterialReplacementKeepsPostProcessingEqualToFreshInstallation(string backendName, string material)
    {
        byte[] expected;
        await using (var fresh = await FrameStageScene.CreateAsync(backendName, true))
        {
            fresh.UseMaterial(material);
            expected = await fresh.ReadPostProcessedColorAsync();
        }
        await using var changed = await FrameStageScene.CreateAsync(backendName, true);
        changed.UseMaterial(material == "Standard" ? "Unlit" : "Standard");
        byte[] previous = await changed.ReadPostProcessedColorAsync();
        changed.UseMaterial(material);
        byte[] current = await changed.ReadPostProcessedColorAsync();
        Assert.NotEqual(previous, current);
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, expected),
            new(64, 64, Format.R16G16B16A16Float, current));
    }
}

// Full Cluster shading requires the hardware adapter's SM 6.6 and indirect command support.
// All observations use the public texture output, graph copy commands and buffer mapping.
internal sealed class FrameStageScene : IAsyncDisposable
{
    private const int Width = 64;
    private const int Height = 64;
    private const uint RowPitch = Width * 8;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly World _gameWorld = new();
    private readonly RenderWorld _world = new();
    private readonly RenderExtractionSystems _extraction;
    private readonly RenderDevice _renderDevice;
    private readonly RenderDataLock _dataLock;
    private readonly RenderInstanceStorageSystem _instances;
    private readonly ClusterRenderResources _cluster;
    private readonly RenderPrepareSystems _prepare;
    private readonly RenderFrameSystems _frames;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly LightingFeature _lighting;
    private readonly VirtualShadowFeature _shadows;
    private readonly ClusterPipelineSystem _pipeline;
    private readonly ClusterStreamingFeedbackConsumer _streaming;
    private readonly ClusterStreamingFeedbackChannel _feedback;
    private readonly TemporalFeature _temporal;
    private readonly Buffer _readback;
    private readonly Buffer _depthReadback;
    private readonly GraphTextureInput _readbackDepth;
    private readonly Entity _view;
    private readonly Entity[] _meshes;
    private readonly Entity _light;
    private readonly Mesh _mesh;
    private readonly string _backendName;
    private readonly List<Material> _extraMaterials = [];
    private readonly List<SomeEngine.Resources.Texture> _extraTextures = [];
    private readonly List<RenderGraphStatistics> _colorStatistics = [];
    private RenderGraphStatistics _statistics;
    private readonly Material _standard;
    private readonly Material _unlit;
    private readonly GraphTextureInput _readbackColor;
    private readonly QueueCompletion[] _completions;

    private FrameStageScene(IGraphicsBackend backend, Device device, Mesh mesh,
        Material standard, Material unlit, ClusterShaders clusterShaders,
        VirtualShadowMapShaders shadowShaders, Shader lightingShader, Shader temporalShader,
        ApplicationSettingsBlock settings, bool shadows, ClusterPipelineOptions? pipelineOptions, string backendName,
        VirtualShadowMapSettings? shadowSettings)
    {
        _backend = backend;
        _backendName = backendName;
        _mesh = mesh;
        _device = device;
        _standard = standard;
        _unlit = unlit;
        _meshes =
        [
            AddMesh(mesh, new TransformQvvs(new Vector3(0, 0, -8), Quaternion.Identity)
                { Stretch = new Vector3(4, 4, .1f) }),
            AddMesh(mesh, new TransformQvvs(new Vector3(0, 0, -6), Quaternion.Identity, .6f)),
        ];
        _light = _gameWorld.CreateEntity(new PointLight(new Vector3(-4, 2, -3), 20, Vector3.One, 30));
        _view = _world.CreateEntity(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), Width, Height));
        _extraction = new RenderExtractionSystems(_world, new MeshRenderExtractor(), new LightRenderExtractor());
        _renderDevice = new RenderDevice(backend, device);
        _dataLock = new RenderDataLock();
        _instances = new RenderInstanceStorageSystem(backend, device, _world,
            ClusterRenderFeature.InstanceLayout, new RenderInstanceOptions { RowCapacity = 16, BatchCapacity = 8 });
        _cluster = new ClusterRenderResources(backend, device, _world, _instances,
            new ClusterRenderOptions { Residency = new ClusterResidencyOptions
                { PageHeapCapacityBytes = 1048576, BvhCapacityBytes = 65536, PageFaultCapacity = 16 } });
        var assignments = new ClusterMaterialAssignments();
        var instanceSystem = new ClusterInstanceSystem<ClusterMaterialOffsetProducer>(_cluster,
            ClusterMaterialAssignmentSystem.EntityQuery(), ClusterRenderFeature.InstanceLayout, assignments.CreateProducer());
        _prepare = new RenderPrepareSystems(_world, _instances, _extraction);
        _prepare.Add(new ClusterResidencySystem(_cluster));
        _prepare.Add(new ClusterMaterialAssignmentSystem(assignments));
        _prepare.Add(instanceSystem);
        _graph = new global::SomeEngine.RenderGraph.RenderGraph(backend, device,
            [backend.GetQueue(device, QueueType.Graphics), backend.GetQueue(device, QueueType.Compute),
                backend.GetQueue(device, QueueType.Copy)]);
        GraphRegionId scene, primary, depthAfter, target, targetAfter, shading, post, feedbackRegion;
        using (RenderGraphEdit stages = _graph.BeginEdit())
        {
            stages.AddRegion("Uploads");
            scene = stages.AddRegion("Scene");
            primary = stages.AddRegion("Primary view geometry");
            depthAfter = stages.AddRegion("After primary view depth");
            target = stages.AddRegion("Target view geometry");
            targetAfter = stages.AddRegion("After target view geometry");
            shading = stages.AddRegion("Shading");
            post = stages.AddRegion("Post processing");
            feedbackRegion = stages.AddRegion("Feedback");
            stages.Commit();
        }
        _lighting = new LightingFeature(backend, device, _graph, Width, Height, 1,
            lightingShader, settings.LightingEntryPoint,
            new ClusteredLightGridSettings { MaxLights = 1, MaxLightsPerCell = 1 }, region: scene);
        var resources = new GraphResourceGroup();
        resources.Add(_lighting);
        var feedback = new ClusterStreamingFeedbackChannel();
        _feedback = feedback;
        _streaming = new ClusterStreamingFeedbackConsumer(_cluster, feedback);
        _pipeline = new ClusterPipelineSystem(backend, device, _renderDevice, _cluster, instanceSystem,
            ClusterRenderFeature.InstanceLayout, assignments, clusterShaders, _graph,
            new ClusterGraphRegions(primary, target, shading, feedbackRegion),
            new ClusterPipelineOutputDescription(Width, Height, 1), feedback, resources,
            pipelineOptions ?? new ClusterPipelineOptions { MaxCandidates = 4096 });
        _shadows = new VirtualShadowFeature(backend, device, shadowShaders,
            shadowSettings ?? new VirtualShadowMapSettings { Enabled = shadows, VirtualResolution = 4096,
                AtlasSize = 2048, MaxShadowLights = 1, DirectionalClipmapLevels = 1,
                DynamicResolutionEnabled = false }, _graph, _pipeline, _lighting,
            depthAfter, targetAfter, feedbackRegion);
        resources.Add(_shadows);
        var lightSystem = new RenderLightSetSystem();
        _frames = new RenderFrameSystems(_world, _instances);
        _frames.Add(lightSystem);
        _frames.Add(new LightingFeatureSystem(_lighting, lightSystem, 1));
        _frames.Add(new VirtualShadowFeatureSystem(_shadows, lightSystem));
        _frames.Add(_pipeline);
        _frames.Initialize();
        _temporal = new TemporalFeature(backend, device, _graph, Width, Height,
            temporalShader, settings.TemporalVertexEntry, settings.TemporalPixelEntry, region: post);
        _readback = backend.CreateBuffer(device,
            new BufferDesc(RowPitch * Height, BufferUsages.CopyDestination), MemoryType.Readback);
        _depthReadback = backend.CreateBuffer(device,
            new BufferDesc(Width * Height * 4, BufferUsages.CopyDestination), MemoryType.Readback);
        using RenderGraphEdit edit = _graph.BeginEdit();
        _readbackDepth = edit.CreateTextureInput();
        GraphBufferId depthDestination = edit.RegisterExternalBuffer(_depthReadback,
            [new BufferBoundaryState(BufferRange.Whole, _depthReadback.InitialSync, _depthReadback.InitialAccess,
                ResourceContentState.Undefined)]);
        edit.AddCopyFramePass("Read frame stage depth", PassQueueSelection.AnyOfType(QueueType.Copy),
            (_readbackDepth, depthDestination), new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref (GraphTextureInput Depth, GraphBufferId Destination) data) =>
            {
                pass.Read(data.Depth, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
                    PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in (GraphTextureInput Depth, GraphBufferId Destination) data) =>
                commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Destination), 0,
                    Width * 4, Height, commands.GetTexture(data.Depth), 0, 0, TextureAspects.Depth,
                    0, 0, 0, Width, Height, 1)), region: feedbackRegion);
        _readbackColor = edit.CreateTextureInput();
        GraphBufferId destination = edit.RegisterExternalBuffer(_readback,
            [new BufferBoundaryState(BufferRange.Whole, _readback.InitialSync, _readback.InitialAccess,
                ResourceContentState.Undefined)]);
        edit.AddCopyFramePass("Read frame stage color", PassQueueSelection.AnyOfType(QueueType.Copy),
            (_readbackColor, destination), new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref (GraphTextureInput Color, GraphBufferId Destination) data) =>
            {
                pass.Read(data.Color, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in (GraphTextureInput Color, GraphBufferId Destination) data) =>
                commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Destination), 0,
                    RowPitch, Height, commands.GetTexture(data.Color), 0, 0, TextureAspects.Color,
                    0, 0, 0, Width, Height, 1)), region: feedbackRegion);
        edit.Commit();
        _completions = new QueueCompletion[_graph.MaximumQueueCompletionCount];
    }

    internal static async Task<FrameStageScene> CreateAsync(string backendName, bool shadows,
        ClusterPipelineOptions? pipelineOptions = null, Mesh? geometryMesh = null,
        VirtualShadowMapSettings? shadowSettings = null)
    {
        string content = Path.Combine(FindRoot(), "templates", "DefaultProject");
        AssetManifest manifest = AssetManifest.Load(Path.Combine(content, "Library", "AssetManifest"));
        await using var assets = new AssetLoader(new LooseAssetStorage(content, manifest));
        Game.Assets.MaterialTypes.Register();
        SlangCompileTarget target = backendName == "Vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil;
        using ApplicationSettingsAsset settingsFile = await ApplicationSettingsAsset.OpenAsync(assets,
            new(manifest.List(ApplicationSettingsAsset.FileType).Single().AssetGuid));
        ApplicationSettingsBlock settings = settingsFile.GetBlock<ApplicationSettingsBlock>();
        using ClusterShadersAsset clusterFile = await ClusterShadersAsset.OpenAsync(assets, settings.Pipeline);
        ClusterShaders clusterShaders = await clusterFile.GetBlock<ClusterShadersBlock>().CreateAsync(
            clusterFile.Name, assets, TestShaderLoading.Slang, TestShaderLoading.CreateSessionDescription(target));
        using VirtualShadowMapShadersAsset shadowFile = await VirtualShadowMapShadersAsset.OpenAsync(assets, settings.VirtualShadowMap);
        VirtualShadowMapShaders shadowShaders = await shadowFile.GetBlock<VirtualShadowMapShadersBlock>().CreateAsync(
            shadowFile.Name, assets, TestShaderLoading.Slang, TestShaderLoading.CreateSessionDescription(target));
        using ShaderAsset lightingFile = await ShaderAsset.OpenAsync(assets, settings.LightingShader);
        Shader lightingShader = await TestShaderLoading.CreateAsync(lightingFile, target);
        using ShaderAsset temporalFile = await ShaderAsset.OpenAsync(assets, settings.TemporalShader);
        Shader temporalShader = await TestShaderLoading.CreateAsync(temporalFile, target);
        Mesh mesh;
        if (geometryMesh is not null) mesh = geometryMesh;
        else
        {
            using MeshAsset meshFile = await MeshAsset.OpenAsync(assets,
                new(manifest.List(MeshAsset.FileType).First().AssetGuid));
            ClusterMeshBlock geometry = meshFile.GetBlock<ClusterMeshBlock>();
            mesh = await geometry.CreateAsync(meshFile.GetBlock<MeshBlock>(), meshFile.Name);
            await geometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
        }
        using MaterialAsset standardFile = await MaterialAsset.OpenAsync(assets,
            new(manifest.List(MaterialAsset.FileType).First(item => item.Name == "Standard").AssetGuid));
        Material standard = await standardFile.CreateAsync(assets, TestShaderLoading.Slang,
            TestShaderLoading.CreateSessionDescription(target));
        using MaterialAsset unlitFile = await MaterialAsset.OpenAsync(assets,
            new(manifest.List(MaterialAsset.FileType).First(item => item.Name == "Unlit").AssetGuid));
        Material unlit = await unlitFile.CreateAsync(assets, TestShaderLoading.Slang,
            TestShaderLoading.CreateSessionDescription(target));
        IGraphicsBackend backend = new ValidationLayer(backendName == "Vulkan"
            ? VulkanGraphicsBackend.Create() : D3D12GraphicsBackend.Create(),
            new ValidationOptions(NativeValidationDisabled: backendName == "Vulkan"));
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance);
        backend.TryEnumerateAdapters(enumeration, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(enumeration, adapters, out _);
        Device device = backend.CreateDevice(new DeviceDesc(adapters.First(item => item.HardwareAccelerated).Id,
            [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)],
            requiredFeatures: DeviceFeatures.IndirectCommands));
        return new FrameStageScene(backend, device, mesh, standard, unlit, clusterShaders,
            shadowShaders, lightingShader, temporalShader, settings, shadows, pipelineOptions, backendName, shadowSettings);
    }

    internal void SetMeshTransform(int index, TransformQvvs transform) =>
        _gameWorld.Replace(_meshes[index], new WorldTransform { Qvvs = transform });

    internal Entity AddReceiver(TransformQvvs transform) => AddMesh(_mesh, transform);

    internal void UseDirectionalLight(Vector3 direction)
    {
        _gameWorld.Remove<PointLight>(_light);
        _gameWorld.Add(_light, new DirectionalLight(direction, Vector3.One, 3));
    }

    internal void UseMaterial(string material) => SetMaterial(material switch
    {
        "Standard" => _standard,
        "Unlit" => _unlit,
        _ => throw new ArgumentOutOfRangeException(nameof(material)),
    });

    private void SetMaterial(Material material)
    {
        for (int index = 0; index < _meshes.Length; index++) UseMaterial(index, material);
    }

    internal void UseMaterial(int index, Material material) => SetEntityMaterial(_meshes[index], material);

    private void SetEntityMaterial(Entity entity, Material material)
    {
        _gameWorld.ExecuteBufferWrite<MeshMaterialBinding, Material>(entity, ref material,
            static (DynamicBuffer<MeshMaterialBinding> buffer, ref Material value) =>
            {
                buffer.Clear();
                buffer.Add(new MeshMaterialBinding(value));
            });
    }

    internal Entity AddMaterialMesh(Material material)
    {
        Entity entity = AddMesh(_mesh, new TransformQvvs(new Vector3(1.3f, 0, -5), Quaternion.Identity, .5f));
        SetEntityMaterial(entity, material);
        return entity;
    }

    internal Entity AddGeometryMesh(Mesh mesh)
    {
        Entity entity = AddMesh(mesh, new TransformQvvs(new Vector3(1.3f, 0, -5), Quaternion.Identity, .5f));
        SetEntityMaterial(entity, _unlit);
        return entity;
    }

    internal void SetGeometryVisible(bool visible)
    {
        foreach (Entity mesh in _meshes)
            if (visible) _gameWorld.Add(mesh, new MeshInstance { Mesh = _mesh });
            else _gameWorld.Remove<MeshInstance>(mesh);
    }

    internal Material CreateShadeMaterial(Vector4 color)
    {
        byte[] pixel = [(byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255), 255];
        var texture = SomeEngine.Resources.Texture.Create(
            new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.Sampled | TextureUsages.CopyDestination),
            [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4 }, pixel)],
            TextureViewDimension.Texture2D);
        var material = new Game.Content.UnlitMaterial
            { Shader = _unlit.Shader, AlbedoMap = texture, BaseColorTint = Vector4.One };
        _extraTextures.Add(texture);
        _extraMaterials.Add(material);
        return material;
    }

    internal async Task<RasterImage> ReadNextRasterImageAsync()
    {
        await RenderAsync();
        return new RasterImage(ReadColor(), ReadDepth(), _pipeline.CaptureFrameMetrics());
    }

    internal void RemoveMaterialMesh(Entity entity) => _gameWorld.DestroyEntity(entity);

    internal Material CreateRasterMaterial(RasterMaterialKind kind, Vector4 color)
    {
        Shader shader = RasterTestShaders.Create(_backendName, kind);
        byte[] pixel = [(byte)(color.X * 255), (byte)(color.Y * 255), (byte)(color.Z * 255), (byte)(color.W * 255)];
        var texture = SomeEngine.Resources.Texture.Create(
            new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.Sampled | TextureUsages.CopyDestination),
            [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4 }, pixel)],
            TextureViewDimension.Texture2D);
        var material = new Game.Content.UnlitMaterial { Shader = shader, AlbedoMap = texture, BaseColorTint = Vector4.One };
        _extraTextures.Add(texture);
        _extraMaterials.Add(material);
        return material;
    }

    internal string[] PassLabels { get; private set; } = [];
    internal ClusterStreamingFeedbackStatistics FeedbackStatistics => _feedback.CaptureStatistics();
    internal void SetUnlitTint(Vector4 color) => Assert.IsType<UnlitMaterial>(_unlit).BaseColorTint = color;

    internal IReadOnlyList<RenderGraphStatistics> ColorStatistics => _colorStatistics;

    internal void AddIndependentWork(int count)
    {
        using RenderGraphEdit edit = _graph.BeginEdit();
        GraphRegionId region = edit.AddRegion("Independent buffer work");
        for (int index = 0; index < count; index++)
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(16, BufferUsages.CopyDestination));
            edit.AddCopyFramePass("Write independent buffer", PassQueueSelection.AnyOfType(QueueType.Graphics),
                buffer, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref GraphBufferId value) =>
                    pass.Write(value, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
                static (ref CopyPassCommandScope commands, in GraphBufferId value) =>
                    commands.ClearBuffer(commands.GetBuffer(value), new BufferRange(0, 16), 37), region: region);
        }
        edit.Commit();
    }

    internal async Task<byte[]> ReadClusterColorAsync()
    {
        _colorStatistics.Clear();
        for (int frame = 0; frame < 24; frame++)
        {
            await RenderAsync();
            _colorStatistics.Add(_statistics);
        }
        return ReadColor();
    }

    internal async Task<byte[]> ReadPostProcessedColorAsync()
    {
        for (int frame = 0; frame < 24; frame++) await RenderAsync(postProcessedColor: true);
        return ReadColor();
    }

    internal async Task DiscardNextFrameAsync()
    {
        _extraction.Extract(_gameWorld);
        Assert.True(_graph.TryOpenNextFrame(out var nextFrame, TimeSpan.FromSeconds(20)));
        using var resources = nextFrame;
        Assert.True(_dataLock.TryBeginPrepare(out var update));
        using (update)
        {
            _prepare.ApplyExtraction(update);
            await _cluster.PrepareMeshesAsync();
            _streaming.Pump();
            _prepare.Update(update, nextFrame);
        }
        _pipeline.ReconcileGraph();
        Assert.True(_dataLock.TryBeginRead(out var frame));
        using (frame)
        using (RenderGraphFrame commands = _graph.BeginFrame(nextFrame))
            _frames.Update(frame, commands);
    }

    private async Task RenderAsync(bool postProcessedColor = false)
    {
        _extraction.Extract(_gameWorld);
        Assert.True(_graph.TryOpenNextFrame(out var nextFrame, TimeSpan.FromSeconds(20)));
        using var nextFrameResources = nextFrame;
        Assert.True(_dataLock.TryBeginPrepare(out var update));
        using (update)
        {
            _prepare.ApplyExtraction(update);
            await _cluster.PrepareMeshesAsync();
            _streaming.Pump();
            _prepare.Update(update, nextFrame);
        }
        ClusterPipelineOutputs outputs = _pipeline.ReconcileGraph();
        Assert.True(_dataLock.TryBeginRead(out var frame));
        using (frame)
        using (RenderGraphFrame commands = _graph.BeginFrame(nextFrame, new RenderGraphFrameOptions(
            Diagnostics: (in RenderGraphDiagnosticsView diagnostics) =>
            {
                _statistics = diagnostics.Statistics;
                PassLabels = diagnostics.Passes.ToArray().Select(pass => pass.Label).ToArray();
            })))
        {
            _frames.Update(frame, commands);
            var temporalFrame = commands;
            _temporal.Record(ref temporalFrame, outputs.MaterialColor, outputs.Depth,
                new MotionFeatureOutput(outputs.MotionVectors, Width, Height, 1),
                TemporalResolveSettings.Default, cameraCut: true, view: _view);
            commands.SetTextureInput(_readbackDepth, outputs.Depth);
            commands.SetTextureInput(_readbackColor, postProcessedColor ? _temporal.Output.ResolvedColor : outputs.MaterialColor);
            commands.Execute(_completions);
            QueueCompletion[] accepted = _completions.Where(value => value != default).ToArray();
            _renderDevice.TrackSubmission(accepted);
            frame.Dispose();
            foreach (QueueCompletion completion in accepted)
                Assert.Equal(WaitStatus.Completed, _backend.WaitCpu(completion, TimeSpan.FromSeconds(20)));
        }
    }

    internal async Task<RasterImage> ReadRasterImageAsync() => new(
        await ReadClusterColorAsync(), ReadDepth(), _pipeline.CaptureFrameMetrics());

    internal byte[] ReadDepth()
    {
        using MappedBuffer mapping = _backend.Map(_depthReadback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        return mapping.Bytes.ToArray();
    }

    private byte[] ReadColor()
    {
        using MappedBuffer mapping = _backend.Map(_readback, MapType.Read, BufferRange.Whole);
        mapping.Invalidate(mapping.Range);
        return mapping.Bytes.ToArray();
    }

    private Entity AddMesh(Mesh mesh, TransformQvvs transform)
    {
        Entity entity = _gameWorld.CreateEntity(new MeshInstance { Mesh = mesh });
        _gameWorld.Add(entity, new WorldTransform { Qvvs = transform });
        _gameWorld.AddBuffer<MeshMaterialBinding>(entity);
        Material value = _standard;
        _gameWorld.ExecuteBufferWrite<MeshMaterialBinding, Material>(entity, ref value,
            static (DynamicBuffer<MeshMaterialBinding> buffer, ref Material item) => buffer.Add(new MeshMaterialBinding(item)));
        return entity;
    }

    internal static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }

    public ValueTask DisposeAsync()
    {
        _graph.Dispose();
        _frames.Dispose();
        _temporal.Dispose();
        _shadows.Dispose();
        _lighting.Dispose();
        _readback.Dispose();
        _depthReadback.Dispose();
        Assert.True(_dataLock.TryBeginPrepare(out var shutdown));
        using (shutdown)
        {
            _prepare.Shutdown(shutdown);
            _cluster.Shutdown(shutdown);
            _instances.Shutdown(shutdown);
        }
        _prepare.Dispose();
        _cluster.Dispose();
        _instances.Dispose();
        _extraction.Dispose();

        _renderDevice.Dispose();
        _world.Dispose();
        _gameWorld.Dispose();
        foreach (Material material in _extraMaterials) material.Dispose();
        foreach (SomeEngine.Resources.Texture texture in _extraTextures) texture.Dispose();
        _standard.Dispose();
        _unlit.Dispose();
        _device.Dispose();
        _backend.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed record RasterImage(byte[] Color, byte[] Depth, ClusterFrameMetrics Metrics)
{
    internal static void AssertEqual(RasterImage expected, RasterImage actual,
        [System.Runtime.CompilerServices.CallerMemberName] string test = "")
    {
        ImageArtifacts.AssertEqual(new(64, 64, Format.R16G16B16A16Float, expected.Color),
            new(64, 64, Format.R16G16B16A16Float, actual.Color), test + "-color");
        ImageArtifacts.AssertEqual(new(64, 64, Format.D32Float, expected.Depth),
            new(64, 64, Format.D32Float, actual.Depth), test + "-depth");
    }
}
