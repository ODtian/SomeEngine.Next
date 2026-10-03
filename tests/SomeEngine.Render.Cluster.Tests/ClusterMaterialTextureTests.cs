using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterMaterialTextureTests
{
    [Fact]
    public async Task ReplacingAMaterialTextureUpdatesItsNextFrameAndPreservesOtherMaterials()
    {
        const int width = 32, height = 32;
        string content = FindContent();
        AssetManifest manifest = AssetManifest.Load(Path.Combine(content, "Library", "AssetManifest"));
        await using var assets = new AssetLoader(new LooseAssetStorage(content, manifest));
        Game.Assets.MaterialTypes.Register();
        using ClusterShadersAsset configurationFile = await ClusterShadersAsset.OpenAsync(assets,
            new(Assert.Single(manifest.List(ClusterShadersAsset.FileType)).AssetGuid));
        ClusterShaders configuration = await configurationFile.GetBlock<ClusterShadersBlock>().CreateAsync(
            configurationFile.Name, assets, TestShaderLoading.Slang, TestShaderLoading.CreateSessionDescription());
        using MeshAsset meshFile = await MeshAsset.OpenAsync(assets,
            new(manifest.List(MeshAsset.FileType).First().AssetGuid));
        ClusterMeshBlock geometry = meshFile.GetBlock<ClusterMeshBlock>();
        Mesh mesh = await geometry.CreateAsync(meshFile.GetBlock<MeshBlock>(), meshFile.Name);
        await geometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
        using MaterialAsset materialFile = await MaterialAsset.OpenAsync(assets,
            new(manifest.List(MaterialAsset.FileType).First(item => item.Name == "Unlit").AssetGuid));
        var shaders = new Dictionary<Guid, Shader>();
        using Material first = await materialFile.CreateAsync(assets, LoadShader, null);
        using Material second = await materialFile.CreateAsync(assets, LoadShader, null);
        using Texture white = ColorTexture(255, 255, 255);
        using Texture blue = ColorTexture(0, 0, 255);
        UnlitMaterial firstMaterial = Assert.IsType<UnlitMaterial>(first);
        UnlitMaterial secondMaterial = Assert.IsType<UnlitMaterial>(second);
        firstMaterial.AlbedoMap = secondMaterial.AlbedoMap = white;
        firstMaterial.BaseColorTint = new Vector4(.8f, .4f, .2f, 1);
        secondMaterial.BaseColorTint = new Vector4(.2f, .3f, .9f, 1);

        using IGraphicsBackend backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance);
        backend.TryEnumerateAdapters(enumeration, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(enumeration, adapters, out _);
        // Full Cluster composition uses the SM 6.6 stage set and a hardware adapter.
        AdapterInfo adapter = adapters.First(static item => item.HardwareAccelerated);
        using Device device = backend.CreateDevice(new DeviceDesc(adapter.Id,
            [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)],
            requiredFeatures: DeviceFeatures.IndirectCommands));
        Queue[] queues = [backend.GetQueue(device, QueueType.Graphics),
            backend.GetQueue(device, QueueType.Compute), backend.GetQueue(device, QueueType.Copy)];
        using var game = new World();
        AddMesh(first, -.7f);
        AddMesh(second, .7f);
        using var world = new RenderWorld();
        world.CreateEntity(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), width, height));
        using var extraction = new RenderExtractionSystems(world, new MeshRenderExtractor());
        using var renderDevice = new RenderDevice(backend, device);
        using var coordinator = new RenderFrameCoordinator(renderDevice);
        var instances = new RenderInstanceStorageSystem(backend, device, coordinator, world,
            ClusterRenderFeature.InstanceLayout, new RenderInstanceOptions { RowCapacity = 4, BatchCapacity = 4 });
        var cluster = new ClusterRenderResources(backend, device, coordinator, world, instances,
            new ClusterRenderOptions { Residency = new ClusterResidencyOptions
                { PageHeapCapacityBytes = 1048576, BvhCapacityBytes = 65536, PageFaultCapacity = 16 } });
        var assignments = new ClusterMaterialAssignments();
        var instanceSystem = new ClusterInstanceSystem<ClusterMaterialOffsetProducer>(cluster,
            ClusterMaterialAssignmentSystem.EntityQuery(), ClusterRenderFeature.InstanceLayout,
            assignments.CreateProducer());
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        prepare.Add(new ClusterResidencySystem(cluster));
        prepare.Add(new ClusterMaterialAssignmentSystem(assignments));
        prepare.Add(instanceSystem);
        var graph = new global::SomeEngine.RenderGraph.RenderGraph(backend, device, queues);
        ClusterGraphRegions regions;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            regions = new ClusterGraphRegions(edit.AddRegion("Primary geometry"), edit.AddRegion("Target geometry"),
                edit.AddRegion("Shading"), edit.AddRegion("Feedback"));
            edit.Commit();
        }
        var frameSystems = new RenderFrameSystems(world, instances);
        var feedback = new ClusterStreamingFeedbackChannel();
        var streaming = new ClusterStreamingFeedbackConsumer(cluster, feedback);
        var pipeline = new ClusterPipelineSystem(backend, device, renderDevice, cluster, instanceSystem,
            ClusterRenderFeature.InstanceLayout, assignments, configuration, graph,
            regions,
            new ClusterPipelineOutputDescription(width, height, 1), feedback,
            options: new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true });
        frameSystems.Add(pipeline);
        using Buffer result = backend.CreateBuffer(device,
            new BufferDesc(height * 256, BufferUsages.CopyDestination), MemoryType.Readback);
        try
        {
            Assert.True(coordinator.TryBeginPrepare(out var initialize));
            using (initialize) { prepare.Update(initialize); initialize.Commit(); }
            frameSystems.Initialize();
            GraphTextureInput output;
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                output = edit.CreateTextureInput();
                GraphBufferId resultId = edit.RegisterExternalBuffer(result,
                    [new BufferBoundaryState(BufferRange.Whole, result.InitialSync,
                        result.InitialAccess, ResourceContentState.Undefined)]);
                _ = edit.AddCopyFramePass("read material colors", PassQueueSelection.AnyOfType(QueueType.Copy),
                    (output, resultId), new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref (GraphTextureInput Texture, GraphBufferId Buffer) data) =>
                    {
                        pass.Read(data.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                            PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                        pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                            ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    }, static (ref CopyPassCommandScope commands,
                        in (GraphTextureInput Texture, GraphBufferId Buffer) data) =>
                        commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), 0, 256,
                            height, commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color,
                            0, 0, 0, width, height, 1)), region: regions.Feedback);
                edit.Commit();
            }
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            for (int frame = 0; frame < 4; frame++) await Render();
            Vector4[] before = ReadColors();
            Assert.Contains(before, color => Near(color, new Vector4(.8f, .4f, .2f, 1)));
            Assert.Contains(before, color => Near(color, new Vector4(.2f, .3f, .9f, 1)));

            firstMaterial.AlbedoMap = blue;
            await Render();
            Vector4[] after = ReadColors();
            Assert.Contains(after, color => Near(color, new Vector4(0, 0, .2f, 1)));
            Assert.Contains(after, color => Near(color, new Vector4(.2f, .3f, .9f, 1)));

            async Task Render()
            {
                extraction.Extract(game);
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
                Assert.True(coordinator.TryBeginFrame(out var frame));
                using (frame)
                using (RenderGraphFrame commands = graph.BeginFrame())
                {
                    frameSystems.Update(frame, commands);
                    commands.ObserveExecution(streaming);
                    commands.SetTextureInput(output, outputs.MaterialColor);
                    commands.Execute(completions);
                    QueueCompletion[] used = completions.Where(static completion => completion != default).ToArray();
                    frame.Complete(used);
                    foreach (QueueCompletion completion in used)
                        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(20)));
                }
            }
            Vector4[] ReadColors()
            {
                using MappedBuffer bytes = backend.Map(result, MapType.Read, BufferRange.Whole);
                ReadOnlySpan<Half> channels = MemoryMarshal.Cast<byte, Half>(bytes.Bytes);
                var colors = new Vector4[width * height];
                for (int index = 0; index < colors.Length; index++)
                    colors[index] = new Vector4((float)channels[index * 4], (float)channels[index * 4 + 1],
                        (float)channels[index * 4 + 2], (float)channels[index * 4 + 3]);
                return colors;
            }
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

        void AddMesh(Material material, float x)
        {
            var entity = game.CreateEntity(new MeshInstance { Mesh = mesh });
            game.Add(entity, new WorldTransform
                { Qvvs = new TransformQvvs(new Vector3(x, 0, -3), Quaternion.Identity, .7f) });
            game.AddBuffer<MeshMaterialBinding>(entity);
            game.ExecuteBufferWrite<MeshMaterialBinding, Material>(entity, ref material,
                static (DynamicBuffer<MeshMaterialBinding> buffer, ref Material value) => buffer.Add(new(value)));
        }
        async ValueTask<Shader> LoadShader(AssetId<ShaderAsset> id, CancellationToken cancellationToken)
        {
            if (shaders.TryGetValue(id.Value, out Shader? shared)) return shared;
            using ShaderAsset shader = await ShaderAsset.OpenAsync(assets, id, cancellationToken);
            Shader value = await TestShaderLoading.CreateAsync(shader, cancellationToken: cancellationToken);
            shaders.Add(id.Value, value);
            return value;
        }
    }

    private static bool Near(Vector4 value, Vector4 expected) => Vector4.Distance(value, expected) < .01f;

    private static Texture ColorTexture(byte red, byte green, byte blue) => Texture.Create(
        new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination),
        [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4 },
            new byte[] { red, green, blue, 255 })], TextureViewDimension.Texture2D);

    private static string FindContent()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx")))
                return Path.Combine(directory.FullName, "templates", "DefaultProject");
        throw new DirectoryNotFoundException("Default project test content was not found.");
    }
}
