using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Graphics.Validation;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Xunit.Abstractions;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class VirtualShadowRenderingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task MovingLightUsesCurrentReceiverPagesInItsFirstFrame(
        bool receiverMask, bool vulkan, bool softwareRaster)
    {
        var shadowed = await RenderLightMovement(true, receiverMask, vulkan, softwareRaster);
        var unshadowed = await RenderLightMovement(false, receiverMask, vulkan, softwareRaster);
        Assert.True(MaximumRelativeDifference(shadowed.Settled, unshadowed.Settled) > .05f,
            "The occluder must cast a visible shadow on the receiver.");
        float firstFrameError = MaximumRelativeDifference(shadowed.First, shadowed.Settled);
        output.WriteLine($"First-frame relative color error: {firstFrameError}.");
        Assert.True(firstFrameError < .01f,
            $"The moved light's first frame must match its settled shadow; relative color error was {firstFrameError}.");
    }

    private static float MaximumRelativeDifference(float[] first, float[] second)
    {
        float maximum = 0;
        for (int index = 0; index < first.Length; index++)
            maximum = Math.Max(maximum, Math.Abs(first[index] - second[index]) / Math.Max(1f, Math.Abs(second[index])));
        return maximum;
    }

    private static async Task<(float[] First, float[] Settled)> RenderLightMovement(
        bool shadows, bool receiverMask, bool vulkan, bool softwareRaster)
    {
        const int width = 256, height = 256, rowPitch = width * 8;
        string root = FindContentRoot();
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest"));
        await using var assets = new AssetLoader(new LooseAssetStorage(root, manifest));
        Game.Assets.MaterialTypes.Register();
        using ApplicationSettingsAsset settingsFile = await ApplicationSettingsAsset.OpenAsync(assets,
            new(manifest.List(ApplicationSettingsAsset.FileType).Single().AssetGuid));
        ApplicationSettingsBlock settings = settingsFile.GetBlock<ApplicationSettingsBlock>();
        using ClusterShadersAsset clusterFile = await ClusterShadersAsset.OpenAsync(assets, settings.Pipeline);
        using ClusterShaders clusterShaders = await clusterFile.GetBlock<ClusterShadersBlock>().CreateAsync(clusterFile.Name, assets);
        using VirtualShadowMapShadersAsset shadowFile = await VirtualShadowMapShadersAsset.OpenAsync(assets, settings.VirtualShadowMap);
        using VirtualShadowMapShaders shadowShaders = await shadowFile.GetBlock<VirtualShadowMapShadersBlock>().CreateAsync(shadowFile.Name, assets);
        using ShaderAsset lightFile = await ShaderAsset.OpenAsync(assets, settings.LightingShader);
        using Shader lightShader = await lightFile.GetBlock<ShaderBlock>().CreateAsync(lightFile.Name);
        using IGraphicsBackend backend = new ValidationLayer(vulkan
            ? VulkanGraphicsBackend.Create()
            : D3D12GraphicsBackend.Create(), new ValidationOptions(NativeValidationDisabled: vulkan));
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance);
        backend.TryEnumerateAdapters(enumeration, [], out int adapterCount);
        var adapters = new AdapterInfo[adapterCount];
        backend.TryEnumerateAdapters(enumeration, adapters, out _);
        using Device device = backend.CreateDevice(new DeviceDesc(adapters.First(item => item.HardwareAccelerated).Id,
            [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)],
            requiredFeatures: DeviceFeatures.IndirectCommands));
        using MeshAsset meshFile = await MeshAsset.OpenAsync(assets, new(manifest.List(MeshAsset.FileType).First().AssetGuid));
        ClusterMeshBlock geometry = meshFile.GetBlock<ClusterMeshBlock>();
        using Mesh mesh = await geometry.CreateAsync(meshFile.GetBlock<MeshBlock>(), meshFile.Name);
        await geometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
        using MaterialAsset materialFile = await MaterialAsset.OpenAsync(assets,
            new(manifest.List(MaterialAsset.FileType).First(item => item.Name == "Standard").AssetGuid));
        using Material material = await materialFile.CreateAsync(assets);
        using var gameWorld = new World();
        using var world = new RenderWorld();
        AddMesh(new TransformQvvs(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new Vector3(4, 4, .1f) });
        AddMesh(new TransformQvvs(new Vector3(0, 0, -6), Quaternion.Identity, .6f));
        Entity light = gameWorld.CreateEntity(new PointLight(new Vector3(4, 2, -3), 20, Vector3.One, 30));
        world.CreateEntity(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), width, height));
        using var extraction = new RenderExtractionSystems(world, new MeshRenderExtractor(), new LightRenderExtractor());
        using var renderDevice = new RenderDevice(backend, device);
        using var coordinator = new RenderFrameCoordinator(renderDevice);
        var instances = new RenderInstanceStorageSystem(backend, device, coordinator, world,
            ClusterRenderFeature.InstanceLayout, new RenderInstanceOptions { RowCapacity = 4, BatchCapacity = 2 });
        var cluster = new ClusterRenderResources(backend, device, coordinator, world, instances,
            new ClusterRenderOptions { Residency = new ClusterResidencyOptions
            { PageHeapCapacityBytes = 1048576, BvhCapacityBytes = 65536, PageFaultCapacity = 16 } });
        var assignments = new ClusterMaterialAssignments();
        var instanceSystem = new ClusterInstanceSystem<ClusterMaterialOffsetProducer>(cluster,
            ClusterMaterialAssignmentSystem.EntityQuery(), ClusterRenderFeature.InstanceLayout, assignments.CreateProducer());
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        prepare.Add(new ClusterResidencySystem(cluster));
        prepare.Add(new ClusterMaterialAssignmentSystem(assignments));
        prepare.Add(instanceSystem);
        var graph = new global::SomeEngine.RenderGraph.RenderGraph(backend, device,
            [backend.GetQueue(device, QueueType.Graphics), backend.GetQueue(device, QueueType.Compute), backend.GetQueue(device, QueueType.Copy)]);
        using var lighting = new LightingFeature(backend, device, graph, width, height, 1,
            lightShader, settings.LightingEntryPoint, new ClusteredLightGridSettings { MaxLights = 1, MaxLightsPerCell = 1 });
        var resources = new GraphResourceGroup();
        resources.Add(lighting);
        var feedback = new ClusterStreamingFeedbackChannel();
        var streaming = new ClusterStreamingFeedbackConsumer(cluster, feedback);
        var pipeline = new ClusterPipelineSystem(backend, device, renderDevice, cluster, instanceSystem,
            ClusterRenderFeature.InstanceLayout, assignments, clusterShaders, graph,
            new ClusterPipelineOutputDescription(width, height, 1), feedback, resources,
            new ClusterPipelineOptions
            {
                MaxCandidates = 4096,
                SoftwareRasterEdgeThreshold = softwareRaster ? 64 : 4,
            });
        using var shadow = new VirtualShadowFeature(backend, device, shadowShaders,
            new VirtualShadowMapSettings
            {
                Enabled = shadows, VirtualResolution = 4096, AtlasSize = 2048,
                MaxShadowLights = 1, DirectionalClipmapLevels = 1,
                DynamicResolutionEnabled = false, LocalReceiverMask = receiverMask,
            }, graph, pipeline, lighting);
        resources.Add(shadow);
        var lightSystem = new RenderLightSetSystem();
        var frames = new RenderFrameSystems(world, instances);
        frames.Add(lightSystem);
        frames.Add(new LightingFeatureSystem(lighting, lightSystem, 1));
        frames.Add(new VirtualShadowFeatureSystem(shadow, lightSystem));
        frames.Add(pipeline);
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(rowPitch * height, BufferUsages.CopyDestination), MemoryType.Readback);
        float[] first = [], settled = [];
        try
        {
            frames.Initialize();
            GraphTextureInput colorInput = null!;
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            for (int iteration = 0; iteration < 24; iteration++)
            {
                if (iteration == 16)
                    gameWorld.Replace(light, new PointLight(new Vector3(-4, 2, -3), 20, Vector3.One, 30));
                extraction.Extract(gameWorld);
                Assert.True(coordinator.TryBeginPrepare(out var update));
                using (update)
                {
                    prepare.ApplyExtraction(update);
                    await cluster.PrepareMeshesAsync();
                    streaming.Pump();
                    prepare.Update(update);
                    update.Commit();
                }
                ClusterPipelineOutputs outputs = pipeline.ReconcileGraph();
                if (iteration == 0)
                {
                    using RenderGraphEdit edit = graph.BeginEdit();
                    colorInput = edit.CreateTextureInput();
                    GraphBufferId buffer = edit.RegisterExternalBuffer(readback,
                        [new BufferBoundaryState(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
                    GraphPassId copy = edit.AddCopyFramePass("Read shadowed receiver", PassQueueSelection.AnyOfType(QueueType.Copy),
                        (Color: colorInput, Buffer: buffer), new PassOptions(Culling: PassCullingMode.NeverCull),
                        static (ref PassDefinition pass, ref (GraphTextureInput Color, GraphBufferId Buffer) data) =>
                        {
                            pass.Read(data.Color, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                                PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                            pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                        },
                        static (ref CopyPassCommandScope commands, in (GraphTextureInput Color, GraphBufferId Buffer) data) =>
                            commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), 0, rowPitch, height,
                                commands.GetTexture(data.Color), 0, 0, TextureAspects.Color, 0, 0, 0, width, height, 1)));
                    edit.OrderAfter(copy, pipeline.ShadeCompletePass);
                    edit.Commit();
                }
                Assert.True(coordinator.TryBeginFrame(out var frame));
                using (frame)
                using (RenderGraphFrame commands = graph.BeginFrame())
                {
                    frames.Update(frame, commands);
                    commands.SetTextureInput(colorInput, outputs.MaterialColor);
                    commands.Execute(completions);
                    QueueCompletion[] accepted = completions.Where(value => value != default).ToArray();
                    frame.Complete(accepted);
                    foreach (QueueCompletion completion in accepted)
                        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(20)));
                }
                if (iteration is 16 or 23)
                {
                    using MappedBuffer mapping = backend.Map(readback, MapType.Read, BufferRange.Whole);
                    mapping.Invalidate(mapping.Range);
                    ReadOnlySpan<Half> pixels = MemoryMarshal.Cast<byte, Half>(mapping.Bytes);
                    var color = new float[width * height * 3];
                    for (int pixel = 0; pixel < width * height; pixel++)
                        for (int channel = 0; channel < 3; channel++)
                            color[pixel * 3 + channel] = (float)pixels[pixel * 4 + channel];
                    if (iteration == 16) first = color;
                    else settled = color;
                }
            }
            return (first, settled);
        }
        finally
        {
            graph.Dispose();
            frames.Dispose();
            Assert.True(coordinator.TryBeginPrepare(out var shutdown));
            using (shutdown)
            {
                prepare.Shutdown(shutdown);
                cluster.Shutdown(shutdown);
                instances.Shutdown(shutdown);
                shutdown.Commit();
            }
            prepare.Dispose();
            cluster.Dispose();
            instances.Dispose();
        }

        void AddMesh(TransformQvvs transform)
        {
            Entity entity = gameWorld.CreateEntity(new MeshInstance { Mesh = mesh });
            gameWorld.Add(entity, new WorldTransform { Qvvs = transform });
            gameWorld.AddBuffer<MeshMaterialBinding>(entity);
            Material value = material;
            gameWorld.ExecuteBufferWrite<MeshMaterialBinding, Material>(entity, ref value,
                static (DynamicBuffer<MeshMaterialBinding> buffer, ref Material item) => buffer.Add(new MeshMaterialBinding(item)));
        }
    }

    private static string FindContentRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return Path.Combine(directory.FullName, "templates", "DefaultProject");
        throw new DirectoryNotFoundException("The test content root was not found.");
    }
}
