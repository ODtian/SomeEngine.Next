using SomeEngine.Resources;
using Game.Content;
﻿using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
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
using SlangShaderSharp;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ClusterRenderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StreamedGeometryKeepsVisibleCoverageWhenTheDetailDoesNotFit(bool softwareRaster) =>
        RenderGeometryChanges(32, 32, true, true, false, true, softwareRaster, false,
            streamedGeometry: true);

    [Theory]
    [InlineData(1, 1, false, false, false, false, false)]
    [InlineData(3, 2, true, false, false, false, false)]
    [InlineData(32, 32, true, true, false, false, false)]
    [InlineData(3, 2, true, false, true, false, false)]
    [InlineData(32, 32, true, true, true, false, false)]
    [InlineData(32, 32, true, true, false, true, false)]
    [InlineData(32, 32, false, true, false, true, false)]
    [InlineData(32, 32, false, true, true, true, false)]
    [InlineData(32, 32, true, true, true, true, false)]
    [InlineData(31, 29, true, true, false, true, false)]
    [InlineData(31, 29, true, true, true, true, false)]
    [InlineData(32, 32, true, true, false, true, true)]
    [InlineData(32, 32, true, true, true, true, true)]
    public Task GeometryChangesAndRemovalPreserveOutputAfterCancellation(
        int width, int height, bool asyncCompute, bool changeGeometry, bool vulkan, bool shadeColor, bool softwareRaster) =>
        RenderGeometryChanges(width, height, asyncCompute, changeGeometry, vulkan, shadeColor, softwareRaster, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MaterialAdditionsAndRemovalPreserveVisibleOutput(bool vulkan) =>
        RenderGeometryChanges(31, 29, true, true, vulkan, true, false, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ManyShaderMaterialsPreserveVisibleOutputWithSharedCandidateCapacity(bool vulkan) =>
        RenderGeometryChanges(31, 29, true, true, vulkan, true, false, true,
            candidateCapacity: 1024, extraMaterialCount: 64, separateShaders: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShaderContentReplacementPreservesMaterialAndTextureOutput(bool vulkan) =>
        RenderGeometryChanges(31, 29, true, true, vulkan, true, false, true, replaceShader: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CameraMovementRecoversHiddenGeometryWithoutChangingTheScene(bool vulkan)
    {
        List<byte[]> withHistory = [], withoutHistory = [];
        await RenderGeometryChanges(32, 32, true, true, vulkan, true, true, false,
            cameraMovement: true, discardCameraHistory: true, cameraImages: withoutHistory);
        await RenderGeometryChanges(32, 32, true, true, vulkan, true, true, false,
            cameraMovement: true, cameraImages: withHistory);
        Assert.Equal(withoutHistory.Count, withHistory.Count);
        for (int frame = 24; frame < withHistory.Count; frame++)
            Assert.True(withHistory[frame].AsSpan().SequenceEqual(withoutHistory[frame]),
                $"Camera frame {frame} must match rendering without previous-frame occlusion.");
    }

    private async Task RenderGeometryChanges(
        int width, int height, bool asyncCompute, bool changeGeometry, bool vulkan, bool shadeColor,
        bool softwareRaster, bool addMaterialVariants, bool cameraMovement = false,
        bool discardCameraHistory = false, List<byte[]>? cameraImages = null,
        uint? candidateCapacity = null, int extraMaterialCount = 6, bool separateShaders = false,
        bool streamedGeometry = false, bool replaceShader = false)
    {
        string root = FindRoot();
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest"));
        await using var assets = new AssetLoader(new LooseAssetStorage(root, manifest));
        Game.Assets.MaterialTypes.Register();
        SlangCompileTarget shaderTarget = vulkan
            ? SlangCompileTarget.Spirv
            : SlangCompileTarget.Dxil;
        AssetEntry record = Assert.Single(manifest.List(ClusterShadersAsset.FileType));
        using ClusterShadersAsset file = await ClusterShadersAsset.OpenAsync(assets, new(record.AssetGuid));
        ClusterShaders configuration = await file.GetBlock<ClusterShadersBlock>().CreateAsync(
            file.Name,
            assets,
            TestShaderLoading.Slang,
            TestShaderLoading.CreateSessionDescription(shaderTarget));
        using IGraphicsBackend backend = new ValidationLayer(vulkan
            ? VulkanGraphicsBackend.Create()
            : D3D12GraphicsBackend.Create(), new ValidationOptions(NativeValidationDisabled: vulkan));
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(enumeration, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(enumeration, adapters, out _);
        // The direct Slang path uses the SM 6.6 shader set for every Cluster stage.
        // Smaller D3D12 tests retain WARP coverage; this full stage composition uses hardware.
        AdapterInfo selected = vulkan
            ? adapters[0]
            : adapters.First(adapter => adapter.HardwareAccelerated);
        using Device device = backend.CreateDevice(new DeviceDesc(selected.Id,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute), new DeviceQueueDesc(QueueType.Copy)], requiredFeatures: DeviceFeatures.IndirectCommands));
        Queue graphics = backend.GetQueue(device, QueueType.Graphics);
        Queue compute = backend.GetQueue(device, QueueType.Compute);
        Queue copy = backend.GetQueue(device, QueueType.Copy);
        using var mainWorld = new World();
        Entity source = Entity.Null;
        var sharedMaterialShaders = new Dictionary<Guid, Shader>();
        MeshPayloadSource? streamedPages = null;
        Mesh? mesh = changeGeometry ? await ReadMesh() : null;
        using MeshPayloadSource? pageSource = streamedPages;
        using Material? material = changeGeometry ? await ReadMaterial() : null;
        using Material? secondMaterial = shadeColor ? await ReadMaterial() : null;

        using SomeEngine.Resources.Texture? sharedTexture = shadeColor ? CreateColorTexture(255) : null;
        Task? textureUpdate = null;

        static SomeEngine.Resources.Texture CreateColorTexture(byte value) => SomeEngine.Resources.Texture.Create(
            new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
                TextureUsages.Sampled | TextureUsages.CopyDestination),
            [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4 },
                new byte[] { value, value, value, 255 })], SomeEngine.Graphics.TextureViewDimension.Texture2D);

        async Task<Mesh> ReadMesh()
        {
            using MeshAsset value = await MeshAsset.OpenAsync(assets, new(manifest.List(MeshAsset.FileType).First().AssetGuid));
            ClusterMeshBlock geometry = value.GetBlock<ClusterMeshBlock>();
            Mesh result = await geometry.CreateAsync(value.GetBlock<MeshBlock>(), value.Name);
            if (streamedGeometry) streamedPages = await geometry.OpenPayloadSourceAsync();
            else await geometry.LoadPagesAsync(result, Enumerable.Range(0, result.PageCount));
            return result;
        }
        async Task<Material> ReadMaterial()
        {
            using MaterialAsset value = await MaterialAsset.OpenAsync(assets,
                new(manifest.List(MaterialAsset.FileType).First(item => item.Name == "Unlit").AssetGuid));
            return await value.CreateAsync(assets, LoadMaterialShader, null);
        }
        async ValueTask<Shader> LoadMaterialShader(
            AssetId<ShaderAsset> id,
            CancellationToken cancellationToken)
        {
            if (!separateShaders && sharedMaterialShaders.TryGetValue(id.Value, out Shader? shared))
                return shared;
            using ShaderAsset shaderFile = await ShaderAsset.OpenAsync(assets, id, cancellationToken);
            Shader shader = await TestShaderLoading.CreateAsync(
                shaderFile,
                shaderTarget,
                cancellationToken);
            if (!separateShaders)
                sharedMaterialShaders.Add(id.Value, shader);
            return shader;
        }
        Entity AddCameraMesh(Vector3 position, float scale, Material value)
        {
            Entity entity = mainWorld.CreateEntity(new MeshInstance { Mesh = mesh!, PageSource = pageSource });
            mainWorld.Add(entity, new WorldTransform { Qvvs = new TransformQvvs(position, Quaternion.Identity, scale) });
            mainWorld.AddBuffer<MeshMaterialBinding>(entity);
            mainWorld.ExecuteBufferWrite<MeshMaterialBinding, Material>(entity, ref value,
                static (DynamicBuffer<MeshMaterialBinding> buffer, ref Material item) => buffer.Add(new MeshMaterialBinding(item)));
            return entity;
        }
        Entity secondSource = Entity.Null;
        List<Material> extraMaterials = [];
        List<Entity> extraSources = [];
        if (shadeColor)
        {
            Assert.IsType<UnlitMaterial>(material).AlbedoMap = sharedTexture;
            Assert.IsType<UnlitMaterial>(secondMaterial).AlbedoMap = sharedTexture;
            Assert.IsType<UnlitMaterial>(material).BaseColorTint = new Vector4(.8f, .1f, .2f, 1);
            Assert.IsType<UnlitMaterial>(secondMaterial).BaseColorTint = new Vector4(.05f, .1f, .9f, 1);
            Assert.IsType<UnlitMaterial>(secondMaterial).MaterialSampler = new SamplerDesc(
                FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);
        }
        using var world = new RenderWorld();
        Entity viewEntity = world.CreateEntity(new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(1, (float)width / height, 0.1f, 100), checked((uint)width), checked((uint)height)));
        using var extraction = new RenderExtractionSystems(world, new MeshRenderExtractor());
        using var renderDevice = new RenderDevice(backend, device);
        using var coordinator = new RenderFrameCoordinator(renderDevice);
        var instances = new RenderInstanceStorageSystem(backend, device, coordinator, world,
            ClusterRenderFeature.InstanceLayout, new RenderInstanceOptions
            { RowCapacity = shadeColor ? Math.Max(16, (extraMaterialCount + 2) * 3) : 2,
                BatchCapacity = shadeColor ? 16 : 2 });
        var cluster = new ClusterRenderResources(backend, device, coordinator, world, instances,
            new ClusterRenderOptions { Residency = new ClusterResidencyOptions
            { PageHeapCapacityBytes = streamedGeometry ? 262144u : 1048576u,
                BvhCapacityBytes = 65536, PageFaultCapacity = 16 } });
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
            options: new ClusterPipelineOptions { MaxCandidates = candidateCapacity ?? (changeGeometry ? 1024u : 65u),
                ForceHardwareRaster = !softwareRaster, SoftwareRasterEdgeThreshold = softwareRaster ? 64f : 4f,
                EnableAsyncCompute = asyncCompute, EnableFrameMetricsReadback = true });
        frameSystems.Add(pipeline);
        try
        {
            Assert.True(coordinator.TryBeginPrepare(out var preparation));
            using (preparation) { prepare.Update(preparation); preparation.Commit(); }
            frameSystems.Initialize();
            ClusterPipelineOutputs outputs = shadeColor ? default : pipeline.ReconcileGraph();
            GraphPassId readbackPass;
            GraphTextureInput outputInput;
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                outputInput = edit.CreateTextureInput();
                GraphBufferId result = edit.RegisterExternalBuffer(readback,
                    [new BufferBoundaryState(BufferRange.Whole, readback.InitialSync, readback.InitialAccess,
                        ResourceContentState.Undefined)]);
                readbackPass = edit.AddCopyFramePass("Read Cluster output", PassQueueSelection.AnyOfType(QueueType.Copy),
                    (outputInput, result, width, height), new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref (GraphTextureInput Texture, GraphBufferId Buffer, int Width, int Height) data) =>
                    {
                        pass.Read(data.Texture, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                            PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                        pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination,
                            WriteCoverage.Partial);
                    },
                    static (ref CopyPassCommandScope commands, in (GraphTextureInput Texture, GraphBufferId Buffer, int Width, int Height) data) =>
                        commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Buffer), 0, 256,
                            checked((uint)data.Height), commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color,
                            0, 0, 0, checked((uint)data.Width), checked((uint)data.Height), 1)));
                edit.Commit();
            }
            if (shadeColor)
            {
                pipeline.ConfigureShadeCompleteBefore(readbackPass);
                outputs = pipeline.ReconcileGraph();
            }
            if (!shadeColor)
            {
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.OrderAfter(readbackPass, outputs.Geometry.CompletePass);
                edit.Commit();
            }
            Assert.True(coordinator.TryBeginFrame(out var canceled));
            using (canceled)
            using (RenderGraphFrame candidate = graph.BeginFrame())
                frameSystems.Update(canceled, candidate);

            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            bool sawVisibility = false;
            bool sawCameraRecovery = false;
            // Streaming feedback and material textures arrive through completed frames.
            // Keep each color-test edit in place for six frames before reading its result.
            for (int renderIndex = 0; renderIndex < (shadeColor ? 108 : changeGeometry ? 18 : 3); renderIndex++)
            {
                int iteration = shadeColor ? renderIndex / 6 : renderIndex;
                bool applyChanges = !shadeColor || renderIndex % 6 == 0;
                if (cameraMovement)
                {
                    if (renderIndex == 0)
                    {
                        source = AddCameraMesh(new Vector3(0, 0, -5), 1f, material!);
                        secondSource = AddCameraMesh(new Vector3(0, 0, -2), 1.2f, secondMaterial!);
                    }
                    float horizontal = Math.Clamp((renderIndex - 24) * 0.1f, 0, 4);
                    world.Replace(viewEntity, world.Read<RenderView>(viewEntity) with
                    {
                        View = Matrix4x4.CreateLookAt(new Vector3(horizontal, 0, 0),
                            new Vector3(0, 0, -5), Vector3.UnitY),
                        CameraCut = discardCameraHistory,
                    });
                }
                else
                {
                    if (shadeColor && renderIndex is 42 or 43)
                        world.Replace(viewEntity, world.Read<RenderView>(viewEntity) with { CameraCut = renderIndex == 42 });
                    if (applyChanges && changeGeometry && iteration == 1)
                    {
                        source = mainWorld.CreateEntity(new MeshInstance { Mesh = mesh!, PageSource = pageSource });
                        mainWorld.Add(source, new WorldTransform
                        { Qvvs = new TransformQvvs(new Vector3(0, 0, -3), Quaternion.Identity) });
                        mainWorld.AddBuffer<MeshMaterialBinding>(source);
                        Material value = material!;
                        mainWorld.ExecuteBufferWrite<MeshMaterialBinding, Material>(source, ref value,
                            static (SomeEngine.ECS.DynamicBuffer<MeshMaterialBinding> buffer, ref Material data) => buffer.Add(new MeshMaterialBinding(data)));
                    }
                    if (applyChanges && addMaterialVariants && iteration == 6)
                    {
                        // Distinct samplers add shading work while the visible materials stay the same.
                        // Removing these off-screen meshes later must preserve the visible image too.
                        for (int index = 0; index < extraMaterialCount; index++)
                        {
                            Material extra = await ReadMaterial();
                            extraMaterials.Add(extra);
                            UnlitMaterial unlit = Assert.IsType<UnlitMaterial>(extra);
                            unlit.AlbedoMap = sharedTexture;
                            unlit.MaterialSampler = new SamplerDesc(
                                FilterType.Linear, FilterType.Linear, FilterType.Linear,
                                AddressType.Repeat, AddressType.Repeat, AddressType.Repeat,
                                MipLodBias: (index + 1) * 0.125f);
                            Entity entity = mainWorld.CreateEntity(new MeshInstance { Mesh = mesh! });
                            extraSources.Add(entity);
                            mainWorld.Add(entity, new WorldTransform
                            { Qvvs = new TransformQvvs(new Vector3(100 + index, 0, -3), Quaternion.Identity) });
                            mainWorld.AddBuffer<MeshMaterialBinding>(entity);
                            mainWorld.ExecuteBufferWrite<MeshMaterialBinding, Material>(entity, ref extra,
                                static (SomeEngine.ECS.DynamicBuffer<MeshMaterialBinding> buffer, ref Material data) =>
                                    buffer.Add(new MeshMaterialBinding(data)));
                        }
                    }
                    if (applyChanges && addMaterialVariants && iteration == 13)
                    {
                        foreach (Entity entity in extraSources) mainWorld.DestroyEntity(entity);
                        extraSources.Clear();
                    }
                    if (applyChanges && shadeColor && iteration == 7)
                        textureUpdate = sharedTexture!.UpdateAsync(CreateColorTexture(128)).AsTask();
                    if (applyChanges && changeGeometry && iteration == 8)
                        Assert.IsType<UnlitMaterial>(material).BaseColorTint = new Vector4(0.1f, 0.8f, 0.2f, 1);
                    if (applyChanges && replaceShader && iteration is 9 or 13)
                    {
                        Shader current = material!.Shader!;
                        Guid shaderId = manifest.List(ShaderAsset.FileType)
                            .First(item => item.Name == current.Name).AssetGuid;
                        using ShaderAsset shaderFile = await ShaderAsset.OpenAsync(assets, new(shaderId));
                        Shader replacement = await TestShaderLoading.CreateAsync(shaderFile, shaderTarget);
                        foreach (Material? item in new[] { material, secondMaterial }.Concat(extraMaterials))
                            if (item is not null && ReferenceEquals(item.Shader, current))
                                item.Shader = replacement;
                    }
                    // The center crosses the side plane while part of the rotated, stretched mesh
                    // remains visible. A conservative box test must keep those pixels.
                    if (applyChanges && shadeColor && iteration is 11 or 14)
                        mainWorld.Replace(source, new WorldTransform
                        {
                            Qvvs = new TransformQvvs(iteration == 11 ? new Vector3(1.7f, 0, -3) : new Vector3(0, 0, -3),
                                Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.47f))
                            { Stretch = new Vector3(0.75f, 1.25f, 0.5f) },
                        });
                    if (applyChanges && shadeColor && iteration is 9 or 13)
                        Assert.IsType<UnlitMaterial>(material).MaterialSampler = iteration == 9
                            ? Assert.IsType<UnlitMaterial>(secondMaterial).MaterialSampler
                            : new SamplerDesc(FilterType.Linear, FilterType.Linear, FilterType.Linear,
                                AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);
                    if (applyChanges && shadeColor && iteration is 2 or 6 or 12)
                    {
                        secondSource = mainWorld.CreateEntity(new MeshInstance { Mesh = mesh!, PageSource = pageSource });
                        mainWorld.Add(secondSource, new WorldTransform
                        { Qvvs = iteration == 2
                            ? new TransformQvvs(new Vector3(0, 0, -2), Quaternion.Identity, 1.9f)
                            : new TransformQvvs(new Vector3(2, 0, -4), Quaternion.Identity) });
                        mainWorld.AddBuffer<MeshMaterialBinding>(secondSource);
                        Material value = secondMaterial!;
                        mainWorld.ExecuteBufferWrite<MeshMaterialBinding, Material>(secondSource, ref value,
                            static (SomeEngine.ECS.DynamicBuffer<MeshMaterialBinding> buffer, ref Material data) => buffer.Add(new MeshMaterialBinding(data)));
                    }
                    // Keep the hidden mesh and its identity unchanged while moving the
                    // occluder away, so old HiZ must be corrected in this same frame.
                    if (applyChanges && shadeColor && iteration == 4)
                        mainWorld.Replace(secondSource, new WorldTransform
                        { Qvvs = new TransformQvvs(new Vector3(100, 0, -2), Quaternion.Identity, 1.9f) });
                    if (applyChanges && shadeColor && iteration is 5 or 10 or 16)
                    {
                        mainWorld.DestroyEntity(secondSource);
                        secondSource = Entity.Null;
                    }
                    if (applyChanges && changeGeometry && iteration == 16) mainWorld.DestroyEntity(source);
                }
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
                if (applyChanges && shadeColor && iteration is 6 or 12)
                {
                    Assert.True(coordinator.TryBeginFrame(out var discarded));
                    using (discarded)
                    using (RenderGraphFrame candidate = graph.BeginFrame())
                        frameSystems.Update(discarded, candidate);
                }
                Assert.True(coordinator.TryBeginFrame(out var frame));
                using (frame)
                using (RenderGraphFrame commands = graph.BeginFrame())
                {
                    frameSystems.Update(frame, commands);
                    commands.ObserveExecution(streaming);
                    commands.SetTextureInput(outputInput, shadeColor ? outputs.MaterialColor : outputs.Visibility);
                    int submitted = commands.Execute(completions);
                    QueueCompletion[] accepted = completions.Where(value => value != default).ToArray();
                    Assert.Equal(submitted, accepted.Length);
                    frame.Complete(accepted);
                    foreach (QueueCompletion completion in accepted)
                    {
                        WaitStatus status = backend.WaitCpu(completion, TimeSpan.FromSeconds(20));
                        Assert.True(
                            status == WaitStatus.Completed,
                            $"Frame {renderIndex}, iteration {iteration}, queue {completion.Queue.Type} " +
                            $"finished with {status}.");
                    }
                }
                if (textureUpdate is not null)
                {
                    await textureUpdate.WaitAsync(TimeSpan.FromSeconds(20));
                    textureUpdate = null;
                    Assert.Same(sharedTexture, Assert.IsType<UnlitMaterial>(material).AlbedoMap);
                    Assert.Same(sharedTexture, Assert.IsType<UnlitMaterial>(secondMaterial).AlbedoMap);
                }
                using MappedBuffer mapping = backend.Map(readback, MapType.Read, BufferRange.Whole);
                mapping.Invalidate(mapping.Range);
                if (streamedGeometry && renderIndex is >= 6 and < 18)
                {
                    bool visible = false;
                    for (int y = 0; y < height; y++)
                    {
                        ReadOnlySpan<Half> row = MemoryMarshal.Cast<byte, Half>(mapping.Bytes.Slice(y * 256, width * 8));
                        for (int x = 0; x < width; x++) visible |= (float)row[x * 4] > .01f;
                    }
                    Assert.True(visible, "The first rendered mesh frame must keep coarse coverage while detail is missing.");
                }
                if (cameraMovement)
                {
                    cameraImages!.Add(mapping.Bytes.ToArray());
                    int rearPixels = 0;
                    for (int y = 0; y < height; y++)
                    {
                        ReadOnlySpan<Half> row = MemoryMarshal.Cast<byte, Half>(mapping.Bytes.Slice(y * 256, width * 8));
                        for (int x = 0; x < width; x++)
                            if (Math.Abs((float)row[x * 4] - .8f) < .01f &&
                                Math.Abs((float)row[x * 4 + 1] - .1f) < .01f) rearPixels++;
                    }
                    if (renderIndex == 23) Assert.Equal(0, rearPixels);
                    if (renderIndex >= 80) Assert.True(rearPixels > 0, "Sideways camera movement must reveal the rear mesh.");
                    if (renderIndex >= 24)
                    {
                        ClusterFrameMetrics metrics = pipeline.CaptureFrameMetrics();
                        sawCameraRecovery |= metrics.PhaseTwoHardwareClusters + metrics.PhaseTwoSoftwareClusters > 0;
                    }
                    continue;
                }
                bool populated = changeGeometry && iteration is >= 1 and < 16;
                if (shadeColor && populated && iteration >= 3 &&
                    (renderIndex % 6 == 5 || iteration is 4 or 7 && applyChanges))
                {
                    Vector4 expected = Assert.IsType<UnlitMaterial>(material).BaseColorTint;
                    if (iteration >= 7) expected *= 128f / 255f;
                    int matching = 0;
                    int occluderPixels = 0;
                    float maximum = 0;
                    Vector4 maximumColor = Vector4.Zero;
                    for (int y = 0; y < height; y++)
                    {
                        ReadOnlySpan<Half> row = MemoryMarshal.Cast<byte, Half>(mapping.Bytes.Slice(y * 256, width * 8));
                        for (int x = 0; x < width; x++)
                        {
                            maximum = Math.Max(maximum, (float)row[x * 4]);
                            maximumColor = Vector4.Max(maximumColor, new Vector4(
                                (float)row[x * 4], (float)row[x * 4 + 1],
                                (float)row[x * 4 + 2], (float)row[x * 4 + 3]));
                            if (Math.Abs((float)row[x * 4] - expected.X) < .01f &&
                                Math.Abs((float)row[x * 4 + 1] - expected.Y) < .01f &&
                                Math.Abs((float)row[x * 4 + 2] - expected.Z) < .01f) matching++;
                            if (Math.Abs((float)row[x * 4] - .05f) < .01f &&
                                Math.Abs((float)row[x * 4 + 2] - .9f) < .01f) occluderPixels++;
                        }
                    }
                    if (iteration == 3)
                    {
                        Assert.Equal(0, matching);
                        Assert.True(occluderPixels > 0,
                            $"The front mesh must hide the rear mesh before movement. Maximum color: {maximumColor}; {pipeline.CaptureFrameMetrics()}");
                    }
                    else
                    {
                        Assert.True(matching > 0, $"Frame {iteration} must show the current unlit color; maximum red was {maximum}.");
                        sawVisibility = true;
                    }
                    if (iteration == 4 && applyChanges)
                    {
                        ClusterFrameMetrics metrics = pipeline.CaptureFrameMetrics();
                        Assert.True(metrics.PhaseTwoHardwareClusters + metrics.PhaseTwoSoftwareClusters > 0,
                            "Moving the occluder must recover the hidden mesh during the same frame.");
                    }
                }
                else if (populated && !shadeColor)
                    for (int y = 0; y < height; y++)
                        sawVisibility |= mapping.Bytes.Slice(y * 256, width * 4).ContainsAnyExcept((byte)0);
                if (!populated && !shadeColor)
                    for (int y = 0; y < height; y++)
                        Assert.All(mapping.Bytes.Slice(y * 256, width * 4).ToArray(), value => Assert.Equal(0, value));
                if (!populated && shadeColor)
                    for (int y = 0; y < height; y++)
                    {
                        ReadOnlySpan<Half> row = MemoryMarshal.Cast<byte, Half>(mapping.Bytes.Slice(y * 256, width * 8));
                        for (int x = 0; x < width; x++)
                        {
                            Assert.Equal((Half)0, row[x * 4]);
                            Assert.Equal((Half)0, row[x * 4 + 1]);
                            Assert.Equal((Half)0, row[x * 4 + 2]);
                            Assert.Equal((Half)1, row[x * 4 + 3]);
                        }
                    }
                Assert.Equal((populated ? 1 : 0) + (secondSource != Entity.Null ? 1 : 0) + extraSources.Count, pipeline.CurrentInstanceCount);
            }
            if (cameraMovement && !discardCameraHistory)
                Assert.True(sawCameraRecovery, "Camera movement must exercise second-phase recovery.");
            if (changeGeometry && !cameraMovement) Assert.True(sawVisibility, "The populated scene must produce visible geometry before removal.");
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
            foreach (Material extra in extraMaterials) extra.Dispose();
        }
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return Path.Combine(directory.FullName, "templates", "DefaultProject");
        throw new DirectoryNotFoundException("The test content root was not found.");
    }
}
