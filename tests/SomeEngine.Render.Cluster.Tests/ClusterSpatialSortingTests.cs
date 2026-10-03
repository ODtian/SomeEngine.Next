using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Xunit;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterSpatialSortingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task SpatialGroupsPlaceInstancesAtTheirRenderWorldTransforms()
    {
        byte[] pixels = await RenderSceneAsync();
        output.WriteLine($"RGBA16F bytes: {pixels.Length}; SHA256: {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels))}");
        Assert.True(HasColor(pixels, 7, 16));
        Assert.True(HasColor(pixels, 24, 16));

        static bool HasColor(byte[] bytes, int x, int y)
        {
            ReadOnlySpan<Half> color = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan(y * 256 + x * 8, 8));
            return (float)color[0] + (float)color[1] + (float)color[2] > .1f;
        }
    }

    internal static async Task<byte[]> RenderSceneAsync()
    {
        string root = ContentRoot();
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest"));
        await using var assets = new AssetLoader(new LooseAssetStorage(root, manifest));
        Game.Assets.MaterialTypes.Register();
        IGlobalSession slang = TestShaderLoading.Slang;
        SessionDesc session = TestShaderLoading.CreateSessionDescription();
        using var shaderFile = await ClusterShadersAsset.OpenAsync(assets,
            new(manifest.List(ClusterShadersAsset.FileType).Single().AssetGuid));
        ClusterShaders shaders = await shaderFile.GetBlock<ClusterShadersBlock>().CreateAsync(
            shaderFile.Name, assets, slang, session);
        using var meshFile = await MeshAsset.OpenAsync(assets,
            new(manifest.List(MeshAsset.FileType).First().AssetGuid));
        var geometry = meshFile.GetBlock<ClusterMeshBlock>();
        Mesh mesh = await geometry.CreateAsync(meshFile.GetBlock<MeshBlock>(), meshFile.Name);
        await geometry.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
        using var materialFile = await MaterialAsset.OpenAsync(assets,
            new(manifest.List(MaterialAsset.FileType).First(value => value.Name == "Unlit").AssetGuid));
        using Material material = await materialFile.CreateAsync(assets,
            async (id, cancellation) =>
            {
                using var value = await ShaderAsset.OpenAsync(assets, id, cancellation);
                Shader shader = await value.GetBlock<ShaderBlock>().CreateAsync(
                    value.GetBlock<ShaderSourceBlock>(), slang, session, value.Name, cancellationToken: cancellation);
                return shader;
            }, null);
        ((Game.Content.UnlitMaterial)material).BaseColorTint = new Vector4(.7f, .2f, .5f, 1);

        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(enumeration, [], out int adapterCount);
        var adapters = new AdapterInfo[adapterCount];
        backend.TryEnumerateAdapters(enumeration, adapters, out _);
        // The complete Cluster SM 6.6 shader composition is exercised on hardware.
        AdapterInfo adapter = adapters.First(value => value.HardwareAccelerated);
        using Device device = backend.CreateDevice(new DeviceDesc(adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute), new DeviceQueueDesc(QueueType.Copy)],
            requiredFeatures: DeviceFeatures.IndirectCommands));
        using var game = new World();
        Entity left = AddMesh(new Vector3(-1.4f, 0, -5));
        AddMesh(new Vector3(1.4f, 0, -5));
        AddMesh(new Vector3(0, 1.4f, -6));
        AddMesh(new Vector3(0, -1.4f, -6));
        using var world = new RenderWorld();
        world.CreateEntity(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 32, 32));
        using var extraction = new RenderExtractionSystems(world, new MeshRenderExtractor());
        using var renderDevice = new RenderDevice(backend, device);
        var dataLock = new RenderDataLock();
        using var graph = new global::SomeEngine.RenderGraph.RenderGraph(backend, device,
            [backend.GetQueue(device, QueueType.Graphics), backend.GetQueue(device, QueueType.Compute),
                backend.GetQueue(device, QueueType.Copy)]);
        var instances = new RenderInstanceStorageSystem(backend, device, world,
            ClusterRenderFeature.InstanceLayout, new RenderInstanceOptions { RowCapacity = 8, BatchCapacity = 2 });
        var cluster = new ClusterRenderResources(backend, device, world, instances,
            new ClusterRenderOptions { Residency = new ClusterResidencyOptions { PageHeapCapacityBytes = 1048576 } });
        var assignments = new ClusterMaterialAssignments();
        var composition = new ClusterInstanceSystem<ClusterMaterialOffsetProducer>(cluster,
            ClusterMaterialAssignmentSystem.EntityQuery(), ClusterRenderFeature.InstanceLayout, assignments.CreateProducer());
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        prepare.Add(new ClusterResidencySystem(cluster));
        prepare.Add(new ClusterMaterialAssignmentSystem(assignments));
        prepare.Add(composition);
        var feedback = new ClusterStreamingFeedbackChannel();
        var streaming = new ClusterStreamingFeedbackConsumer(cluster, feedback);
        GraphRegionId primary, target, shading, feedbackRegion, readbackRegion;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            primary = edit.AddRegion("Primary geometry");
            target = edit.AddRegion("Target geometry");
            shading = edit.AddRegion("Shading");
            feedbackRegion = edit.AddRegion("Feedback");
            readbackRegion = edit.AddRegion("Output readback");
            edit.Commit();
        }
        var pipeline = new ClusterPipelineSystem(backend, device, renderDevice, cluster, composition,
            ClusterRenderFeature.InstanceLayout, assignments, shaders, graph,
            new ClusterGraphRegions(primary, target, shading, feedbackRegion),
            new ClusterPipelineOutputDescription(32, 32, 1), feedback,
            options: new ClusterPipelineOptions { UseInstanceGroups = true, ForceHardwareRaster = true });
        var frames = new RenderFrameSystems(world, instances);
        frames.Add(pipeline);
        frames.Initialize();
        var receiver = new PixelsReceiver();
        try
        {
            for (int frameIndex = 0; frameIndex < 8; frameIndex++)
            {
                extraction.Extract(game);
                if (!graph.TryOpenNextFrame(out var next, TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("Scene upload pages exceeded the wait time.");
                using (next)
                {
                    if (!dataLock.TryBeginPrepare(out var scope))
                        throw new InvalidOperationException("Scene preparation requires the write boundary.");
                    using (scope)
                    {
                        prepare.ApplyExtraction(scope);
                        if (frameIndex == 0)
                        {
                            Entity mirror = world.GetByIndex<RenderSource, Entity>(left)[0];
                            world.Add(mirror, new PointLight(Vector3.Zero, 1, Vector3.One, 1));
                        }
                        await cluster.PrepareMeshesAsync();
                        streaming.Pump();
                        prepare.Update(scope, next);
                    }
                    ClusterPipelineOutputs output = pipeline.ReconcileGraph();
                    if (frameIndex == 0)
                    {
                        using var edit = graph.BeginEdit();
                        GraphBufferId pixels = edit.CreatePersistentBuffer(new BufferDesc(32 * 256,
                            BufferUsages.CopyDestination | BufferUsages.CopySource));
                        GraphPassId copy = edit.AddCopyFramePass("Read spatially grouped color",
                            PassQueueSelection.AnyOfType(QueueType.Copy), (output.MaterialColor, pixels), default,
                            static (ref PassDefinition pass, ref (GraphTextureId Color, GraphBufferId Pixels) data) =>
                            {
                                pass.Read(data.Color, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                                    PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                                pass.Write(data.Pixels, BufferRange.Whole, PipelineSync.Copy,
                                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
                            },
                            static (ref CopyPassCommandScope commands, in (GraphTextureId Color, GraphBufferId Pixels) data) =>
                                commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Pixels), 0, 256, 32,
                                    commands.GetTexture(data.Color), 0, 0, TextureAspects.Color, 0, 0, 0, 32, 32, 1)), region: readbackRegion);
                        edit.AddReadback(pixels, BufferRange.Whole, receiver, "Spatially grouped pixels");
                        edit.Commit();
                    }
                    if (!dataLock.TryBeginRead(out var renderFrame))
                        throw new InvalidOperationException("Scene recording requires the read boundary.");
                    using (renderFrame)
                    using (var commands = graph.BeginFrame(next))
                    {
                        frames.Update(renderFrame, commands);
                        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                        int count = commands.Execute(completions);
                        renderDevice.TrackSubmission(completions.AsSpan(0, count));
                        renderFrame.Dispose();
                        foreach (QueueCompletion completion in completions.AsSpan(0, count))
                            if (backend.WaitCpu(completion, TimeSpan.FromSeconds(20)) != WaitStatus.Completed)
                                throw new TimeoutException("Scene output exceeded the GPU wait time.");
                    }
                    graph.CollectCompleted();
                }
            }
            return receiver.Pixels;
        }
        finally
        {
            frames.Dispose();
            if (!dataLock.TryBeginPrepare(out var shutdown))
                throw new InvalidOperationException("Scene shutdown requires the write boundary.");
            using (shutdown)
            {
                prepare.Shutdown(shutdown);
                cluster.Shutdown(shutdown);
                instances.Shutdown(shutdown);
            }
            prepare.Dispose();
            cluster.Dispose();
            instances.Dispose();
        }

        Entity AddMesh(Vector3 position)
        {
            Entity entity = game.CreateEntity(new MeshInstance { Mesh = mesh });
            game.Add(entity, new WorldTransform { Qvvs = new TransformQvvs(position, Quaternion.Identity, .5f) });
            game.AddBuffer<MeshMaterialBinding>(entity);
            Material value = material;
            game.ExecuteBufferWrite<MeshMaterialBinding, Material>(entity, ref value,
                static (DynamicBuffer<MeshMaterialBinding> buffer, ref Material item) => buffer.Add(new MeshMaterialBinding(item)));
            return entity;
        }
    }

    private static string ContentRoot()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "Library", "AssetManifest", AssetManifest.CatalogFileName)))
            return AppContext.BaseDirectory;
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx")))
                return Path.Combine(directory.FullName, "templates", "DefaultProject");
        throw new DirectoryNotFoundException("Build the DefaultProject shader content for the complete Cluster test.");
    }

    private sealed class PixelsReceiver : IGraphReadbackReceiver
    {
        internal byte[] Pixels = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Pixels = bytes.ToArray();
    }
}
