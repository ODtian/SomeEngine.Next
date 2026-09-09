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
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterRenderingTests
{
    [Theory]
    [InlineData(1, 1, false, false, false)]
    [InlineData(3, 2, true, false, false)]
    [InlineData(32, 32, true, true, false)]
    [InlineData(3, 2, true, false, true)]
    [InlineData(32, 32, true, true, true)]
    public async Task GeometryChangesAndRemovalPreserveOutputAfterCancellation(
        int width, int height, bool asyncCompute, bool changeGeometry, bool vulkan)
    {
        string root = FindRoot();
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest"));
        await using var assets = new AssetLoader(new LooseAssetStorage(root, manifest));
        AssetManifestRecord record = Assert.Single(manifest.List(AssetType<ClusterShaders>.Name));
        ClusterShaders configuration = await assets.LoadAsync(new AssetId<ClusterShaders>(record.Guid));
        using IGraphicsBackend backend = vulkan ? VulkanGraphicsBackend.Create() : new ValidationLayer(D3D12GraphicsBackend.Create());
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(enumeration, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(enumeration, adapters, out _);
        AdapterInfo selected = vulkan ? adapters[0] : adapters.First(adapter => !adapter.HardwareAccelerated);
        using Device device = backend.CreateDevice(new DeviceDesc(selected.Id,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute), new DeviceQueueDesc(QueueType.Copy)], requiredFeatures: DeviceFeatures.IndirectCommands));
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        Queue compute = backend.GetQueue(device, QueueType.Compute);
        Queue copy = backend.GetQueue(device, QueueType.Copy);
        using var mainWorld = new World();
        Entity source = Entity.Null;
        Mesh? mesh = changeGeometry ? await assets.LoadAsync(new AssetId<Mesh>(
            manifest.List(AssetType<Mesh>.Name).First(value => value.Name == "IcoSphere").Guid)) : null;
        Material? material = changeGeometry ? await assets.LoadAsync(new AssetId<Material>(
            manifest.List(AssetType<Material>.Name).First(value => value.Name == "TestUnlit_1").Guid)) : null;
        using var world = new RenderWorld();
        world.CreateEntity(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, (float)width / height, 0.1f, 100), checked((uint)width), checked((uint)height)));
        using var extraction = new RenderExtractionSystems(world, new MeshRenderExtractor());
        using var renderDevice = new RenderDevice(backend, device);
        using var coordinator = new RenderFrameCoordinator(renderDevice);
        var instances = new RenderInstanceStorageSystem(backend, device, coordinator, world,
            ClusterRenderFeature.InstanceLayout, new RenderInstanceOptions { RowCapacity = 2, BatchCapacity = 2 });
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
        var graph = new global::SomeEngine.RenderGraph.RenderGraph(backend, device, [graphics, compute, copy]);
        var frameSystems = new RenderFrameSystems(world, instances);
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(checked((ulong)height * 256), BufferUsages.CopyDestination), MemoryType.Readback);
        var feedback = new ClusterStreamingFeedbackChannel();
        var streaming = new ClusterStreamingFeedbackConsumer(cluster, feedback);
        var pipeline = new ClusterPipelineSystem(backend, device, renderDevice, cluster, instanceSystem,
            ClusterRenderFeature.InstanceLayout, assignments, configuration, graph,
            new ClusterPipelineOutputDescription(width, height, 1), feedback,
            options: new ClusterPipelineOptions { MaxCandidates = changeGeometry ? 1024u : 65u, ForceHardwareRaster = true,
                EnableAsyncCompute = asyncCompute, EnableFrameMetricsReadback = true });
        frameSystems.Add(pipeline);
        try
        {
            Assert.True(coordinator.TryBeginPrepare(out var preparation));
            using (preparation) { prepare.Update(preparation); preparation.Commit(); }
            frameSystems.Initialize();
            ClusterPipelineOutputs outputs = pipeline.ReconcileGraph();
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                GraphBufferId result = edit.RegisterExternalBuffer(readback,
                    [new BufferBoundaryState(BufferRange.Whole, readback.InitialSync, readback.InitialAccess,
                        ResourceContentState.Undefined)]);
                GraphPassId readbackPass = edit.AddCopyFramePass("Read empty visibility", PassQueueSelection.Exact(copy),
                    (outputs.Visibility, result, width, height), new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref (GraphTextureId Texture, GraphBufferId Buffer, int Width, int Height) data) =>
                    {
                        pass.Read(data.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                            PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                        pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                            WriteCoverage.Partial);
                    },
                    static (ref CopyPassCommandScope commands, in (GraphTextureId Texture, GraphBufferId Buffer, int Width, int Height) data) =>
                        commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), 0, 256,
                            checked((uint)data.Height), commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color,
                            0, 0, 0, checked((uint)data.Width), checked((uint)data.Height), 1)));
                edit.OrderAfter(readbackPass, outputs.Geometry.CompletePass);
                edit.Commit();
            }
            Assert.True(coordinator.TryBeginFrame(out var canceled));
            using (canceled)
            using (RenderGraphFrame candidate = graph.BeginFrame())
                frameSystems.Update(canceled, candidate);

            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            bool sawVisibility = false;
            for (int iteration = 0; iteration < (changeGeometry ? 18 : 3); iteration++)
            {
                if (changeGeometry && iteration == 1)
                {
                    source = mainWorld.CreateEntity(new MeshInstance { Mesh = mesh! });
                    mainWorld.Add(source, new WorldTransform
                    { Qvvs = new TransformQvvs(new Vector3(0, 0, -3), Quaternion.Identity) });
                    mainWorld.AddBuffer<MeshMaterialBinding>(source);
                    Material value = material!;
                    mainWorld.ExecuteBufferWrite<MeshMaterialBinding, Material>(source, ref value,
                        static (SomeEngine.ECS.DynamicBuffer<MeshMaterialBinding> buffer, ref Material data) => buffer.Add(new MeshMaterialBinding(data)));
                }
                if (changeGeometry && iteration == 8)
                    Assert.IsType<UnlitMaterial>(material).BaseColorTint = new Vector4(0.1f, 0.8f, 0.2f, 1);
                if (changeGeometry && iteration == 16) mainWorld.DestroyEntity(source);
                extraction.Extract(mainWorld);
                Assert.True(coordinator.TryBeginPrepare(out var update));
                using (update)
                {
                    prepare.ApplyExtraction(update);
                    await cluster.PrepareMeshesAsync();
                    streaming.Pump();
                    prepare.Update(update);
                    update.Commit();
                }
                ulong structureVersion = graph.StructureVersion;
                pipeline.ReconcileGraph();
                if (changeGeometry && iteration == 8) Assert.Equal(structureVersion, graph.StructureVersion);
                Assert.True(coordinator.TryBeginFrame(out var frame));
                using (frame)
                using (RenderGraphFrame commands = graph.BeginFrame())
                {
                    frameSystems.Update(frame, commands);
                    int submitted = commands.Execute(completions);
                    QueueCompletion[] accepted = completions.Where(value => value != default).ToArray();
                    Assert.Equal(submitted, accepted.Length);
                    frame.Complete(accepted);
                    foreach (QueueCompletion completion in accepted)
                        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(20)));
                }
                using MappedBuffer mapping = backend.Map(readback, MapType.Read, BufferRange.Whole);
                mapping.Invalidate(mapping.Range);
                bool populated = changeGeometry && iteration is >= 1 and < 16;
                if (populated)
                    for (int y = 0; y < height; y++)
                        sawVisibility |= mapping.Bytes.Slice(y * 256, width * 4).ContainsAnyExcept((byte)0);
                if (!populated)
                    for (int y = 0; y < height; y++)
                        Assert.All(mapping.Bytes.Slice(y * 256, width * 4).ToArray(), value => Assert.Equal(0, value));
                Assert.Equal(populated ? 1 : 0, pipeline.CurrentInstanceCount);
            }
            if (changeGeometry) Assert.True(sawVisibility, "The populated scene must produce visible geometry before removal.");
        }
        finally
        {
            graph.Dispose();
            frameSystems.Dispose();
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
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("The test content root was not found.");
    }
}
