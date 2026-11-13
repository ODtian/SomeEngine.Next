using System.Diagnostics;
using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;
using SomeEngine.Layout;
using SomeEngine.Views;
using SomeEngine.ECS.Entities;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Graphics.Validation;
using SomeEngine.Render;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Features.Decals;
using SomeEngine.Render.Features.Layers;
using SomeEngine.Render.Features.Motion;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Render.Features.Temporal;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

internal sealed partial class ApplicationRenderer : IDisposable
{
    private const Format PresentationFormat = Format.R8G8B8A8UNorm;
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(60);
    internal RenderWorld renderWorld;
    internal RenderExtractionSystems extraction;
    internal GlyphRenderExtractor? glyphExtractor;
    internal Entity viewEntity;
    internal ShaderLibrary ShaderLibrary { get; }
    private AssetLoader _shaderFiles;
    internal TemporalFeature? temporal;
    internal PresentationPipeline? presentation;
    internal DebugInterfaceRenderer? ui;
    internal IApplicationColorFeature? colorFeature;
    private ShaderSourceWatcher? _shaderWatcher;
    private ApplicationScene? _application;
    internal int FrameIndex;
    internal uint SubmittedFrames;
    internal bool SystemsInstalled;
    internal Task<ApplicationSceneContent>? LoadingScene;
    internal ApplicationSceneContent? NextScene;
    internal ApplicationGeometryContext? geometry;
    internal Queue graphicsQueue = default!;
    internal Queue computeQueue = default!;
    internal Queue copyQueue = default!;
    internal global::SomeEngine.RenderGraph.RenderGraph renderGraph = default!;
    internal RenderPipelines pipelines = default!;
    internal RenderDevice renderDevice = default!;
    internal RenderInstancePropertyLayout instanceLayout = default!;
    internal RenderInstanceStorageSystem instances = default!;
    internal ClusterRenderResources cluster = default!;
    internal ClusterMaterialAssignments materialAssignments = default!;
    internal ClusterMaterialAssignmentSystem materialSystem = default!;
    internal ClusterInstanceSystem<RenderInstanceProducerBundle<ClusterMaterialOffsetProducer, SkinInstanceProducer>> instanceSystem = default!;
    internal RenderPrepareSystems prepareSystems = default!;
    internal ViewPrepareSystem viewSystem = default!;
    internal SkinningFeature skinning = default!;
    internal SkinBoundsFeature skinBounds = default!;
    internal SurfaceMaterial? surfaceMaterial = default!;
    internal GlyphsMaterial? glyphMaterial = default!;
    internal CanvasRenderer? surfaces = default!;
    internal ClusterPipelineOutputDescription outputDescription = default!;
    internal LightingFeature lighting = default!;
    internal GraphResourceGroup stageResources = default!;
    internal RenderLightSetSystem lightSystem = default!;
    internal RenderFeedback<ClusterPageDemand> streamingFeedback = default!;
    internal ClusterStreamingFeedbackConsumer streamingConsumer = default!;
    internal VirtualShadowMapSettings shadowSettings = default!;
    internal ClusterGeometryPipeline pipeline = default!;
    internal VirtualShadowFeature virtualShadows = default!;
    internal MaterialDecalFeature materialDecals = default!;
    private IApplicationFrameFeature[] _frameFeatures = [];
    private bool _frameFeaturesInitialized;
    internal ClusterShadingPipeline shading = default!;
    internal ClusterGraphInstaller graphInstaller = default!;
    internal RenderFrameSystems frameSystems = default!;
    internal GraphRegionId sceneRegion;
    internal GraphRegionId primaryGeometryRegion;
    internal GraphRegionId primaryDepthAfterRegion;
    internal GraphRegionId targetGeometryRegion;
    internal GraphRegionId targetGeometryAfterRegion;
    internal GraphRegionId targetPhaseOneRegion, targetOcclusionRegion, targetPhaseTwoRegion;
    internal GraphRegionId shadingRegion;
    internal GraphRegionId layerVisibilityRegion, layerShadingRegion, layerCompositionRegion;
    internal GraphRegionId shadowReceiverRegion;
    internal GraphRegionId temporalRegion;
    internal GraphRegionId presentationRegion;
    internal GraphRegionId uiRegion;
    internal GraphRegionId feedbackRegion;
    internal GraphRegionId frameOutputRegion;
    internal GraphRegionId linearOutputRegion;
    internal GraphRegionId colorEffectsRegion;

    internal ApplicationRenderer(ApplicationOptions options, IGraphicsBackend backend, Device device,
        NativeWindow window, SceneWorldData scene, ClusterShaders pipelineConfiguration,
        VirtualShadowMapShaders virtualShadowConfiguration, Shader uiShader, Shader presentationShader,
        Shader lightingShader, Shader temporalShader, ShaderLibrary shaderLibrary, AssetLoader files,
        ApplicationSettingsBlock boot, RenderView initialView, bool includeInterface,
        bool forceHardwareRaster, ApplicationScene? application, SceneScript? script)
    {
        ShaderLibrary = shaderLibrary;
        _shaderFiles = files;
        renderWorld = new RenderWorld(initialEntityCapacity: 2048, execution: new WorldJobs(JobSystem.Default));
        extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor(),
            new MaterialDecalRenderExtractor(), new WorldOriginRenderExtractor(),
            new SurfaceRenderExtractor(), new SkinRenderExtractor(), new SkinBoundsRenderExtractor());
        viewEntity = renderWorld.CreateEntity(initialView);
        renderWorld.Add(viewEntity, new RenderViewTarget(ViewTargetKind.Screen));
        graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        computeQueue = backend.GetQueue(device, QueueType.Compute);
        copyQueue = backend.GetQueue(device, QueueType.Copy);
        renderGraph = new global::SomeEngine.RenderGraph.RenderGraph(
            backend,
            device,
            options.AsyncCompute ? [graphicsQueue, computeQueue, copyQueue] : [graphicsQueue, copyQueue],
            new RenderGraphDesc(
                // Instance and readback storage already limit this renderer to two in-flight
                // frames. Match that count instead of rotating a third copy of graph data.
                MaximumFramesInFlight: 2,
                Label: "SomeEngine Runtime Render Graph"));
        pipelines = new RenderPipelines(backend, device);
        renderDevice = new RenderDevice(renderGraph, pipelines);
        instanceLayout = RenderInstancePropertyLayout.Compose(
            ClusterRenderFeature.InstanceLayout, SkinInstanceProducer.Layout);
        instances = new RenderInstanceStorageSystem(
            renderGraph,
            renderWorld,
            instanceLayout,
            script is null && application is null ? new RenderInstanceOptions
            {
                RowCapacity = Math.Max(1, scene.MeshInstanceCount),
                BatchCapacity = 1,
            } : new RenderInstanceOptions());
        cluster = new ClusterRenderResources(
            renderGraph,
            renderWorld,
            instances,
            application is null && script is null ? null : new ClusterRenderOptions
            {
                Residency = new ClusterResidencyOptions
                {
                    PageHeapCapacityBytes = script?.GeometryMemoryBytes ?? application!.GeometryMemoryBytes,
                    BvhCapacityBytes = application?.GeometryBvhMemoryBytes ?? 64ul * 1024 * 1024,
                },
            });
        materialAssignments = new ClusterMaterialAssignments();
        materialSystem = new ClusterMaterialAssignmentSystem(materialAssignments);
        instanceSystem = new ClusterInstanceSystem<RenderInstanceProducerBundle<ClusterMaterialOffsetProducer, SkinInstanceProducer>>(
            cluster,
            new QueryDefinitionBuilder().Read<RenderInstance>().Read<RenderTransform>().Read<RenderPreviousTransform>()
                .Read<RenderClusterGeometry>().ReadBuffer<RenderMaterialBinding>().Optional<RenderSkinBinding>(QueryAccess.Read).Build(),
            instanceLayout,
            new(materialAssignments.CreateProducer(), new SkinInstanceProducer()));
        prepareSystems = new RenderPrepareSystems(renderWorld, instances, extraction);
        viewSystem = new ViewPrepareSystem(viewEntity);
        _ = prepareSystems.Add(viewSystem);

        skinning = new SkinningFeature(backend, device, pipelines, renderGraph, shaderLibrary, files);
        skinBounds = new SkinBoundsFeature(backend, device, pipelines, renderGraph, skinning, shaderLibrary, files);
        surfaceMaterial = includeInterface
            ? SurfaceMaterial.Create(shaderLibrary, files) : null;
        glyphMaterial = includeInterface
            ? GlyphsMaterial.Create(shaderLibrary, files) : null;
        surfaces = surfaceMaterial is null ? null
            : new CanvasRenderer(backend, device, pipelines, renderGraph, renderWorld, surfaceMaterial, PresentationFormat, glyphMaterial);
        using (RenderGraphEdit edit = renderGraph.BeginEdit())
        {
            _ = edit.AddRegion("Frame uploads");
            sceneRegion = edit.AddRegion("Scene");
            primaryGeometryRegion = edit.AddRegion("Primary view geometry");
            shadowReceiverRegion = edit.AddRegion("Shadow receiver preparation");
            layerVisibilityRegion = edit.AddRegion("Layer visibility");
            primaryDepthAfterRegion = edit.AddRegion("After primary view depth");
            targetGeometryRegion = edit.AddRegion("Target view geometry");
            targetPhaseOneRegion = edit.AddRegion("Target first geometry", targetGeometryRegion);
            targetOcclusionRegion = edit.AddRegion("Target intermediate occlusion", targetGeometryRegion);
            targetPhaseTwoRegion = edit.AddRegion("Target second geometry", targetGeometryRegion);
            targetGeometryAfterRegion = edit.AddRegion("After target view geometry");
            shadingRegion = edit.AddRegion("Shading");
            layerShadingRegion = edit.AddRegion("Layer shading");
            layerCompositionRegion = edit.AddRegion("Layer composition");
            linearOutputRegion = edit.AddFrameRegion("Linear frame output");
            GraphRegionId postProcessing = edit.AddRegion("Post processing");
            colorEffectsRegion = edit.AddRegion("Surface effects", postProcessing);
            temporalRegion = edit.AddRegion("Temporal", postProcessing);
            presentationRegion = edit.AddRegion("Presentation", postProcessing);
            uiRegion = edit.AddRegion("UI", postProcessing);
            frameOutputRegion = edit.AddFrameRegion("Frame output", postProcessing);
            feedbackRegion = edit.AddRegion("Feedback");
            edit.Commit();
        }
        outputDescription = new ClusterPipelineOutputDescription(boot.Width, boot.Height, 1);
        lighting = new LightingFeature(
            backend,
            device,
            pipelines,
            renderGraph,
            prepareSystems,
            outputDescription.Width,
            outputDescription.Height,
            outputDescription.ViewCount,
            lightingShader,
            region: sceneRegion);
        stageResources = new GraphResourceGroup();
        stageResources.Add(lighting);
        stageResources.Add(skinning);
        lightSystem = new RenderLightSetSystem();
        _ = prepareSystems.Add(lightSystem);
        streamingFeedback = new RenderFeedback<ClusterPageDemand>();
        streamingConsumer = new ClusterStreamingFeedbackConsumer(cluster, streamingFeedback, renderGraph);
        shadowSettings = CreateShadowSettings(options, script);
        shading = new ClusterShadingPipeline(renderDevice, materialAssignments, stageResources);
        materialDecals = new MaterialDecalFeature(renderDevice, shading, renderWorld, prepareSystems,
            pipelineConfiguration.Operations.Single(operation => operation.Role == ClusterShaderOperationRole.MaterialDecalBinning).Shaders[0],
            (uint)outputDescription.Width, (uint)outputDescription.Height, outputDescription.ViewCount,
            new MaterialDecalSettings(), sceneRegion);
        stageResources.Add(materialDecals);
        graphInstaller = new ClusterGraphInstaller(renderGraph);
        ClusterPipelineOptions clusterOptions = new()
        {
            ShadingPath = script?.ShadingPath ?? options.ShadingPath,
            VisibilityMode = script?.VisibilityMode,
            EnableAsyncCompute = options.AsyncCompute,
            ForceHardwareRaster = forceHardwareRaster,
            SoftwareRasterEdgeThreshold = script?.RasterEdgePixels ?? 32,
            UseInstanceGroups = options.UseInstanceGroups,
            EnableFrameMetricsReadback = true,
            LodThreshold = application?.LodThreshold ?? 0.25f,
            MaxCandidates = script?.ClusterWorkCapacity ?? 1_048_576,
            RasterBatchCapacity = script?.RasterBatchCapacity,
        };
        pipeline = new ClusterGeometryPipeline(
            backend,
            device,
            renderDevice,
            shaderLibrary,
            cluster,
            instanceSystem,
            instanceLayout,
            materialAssignments,
            pipelineConfiguration,
            renderGraph,
            new ClusterGraphRegions(primaryGeometryRegion, targetGeometryRegion, shadingRegion, feedbackRegion,
                targetOcclusionRegion, targetPhaseOneRegion, targetPhaseTwoRegion,
                layerVisibilityRegion, layerShadingRegion, layerCompositionRegion),
            outputDescription,
            streamingFeedback,
            shading,
            graphInstaller,
            stageResources,
            clusterOptions, deformBounds: skinBounds);
        virtualShadows = new VirtualShadowFeature(
            backend,
            device,
            pipelines,
            virtualShadowConfiguration,
            shadowSettings,
            renderGraph,
            pipeline,
            lighting,
            primaryDepthAfterRegion,
            targetOcclusionRegion,
            targetGeometryAfterRegion,
            feedbackRegion,
            renderDevice, shadowReceiverRegion);
        stageResources.Add(virtualShadows);
        InitializeLayers(pipelineConfiguration, clusterOptions);
        frameSystems = new RenderFrameSystems(renderWorld, instances);

    }

    internal void ApplySceneSettings(ApplicationOptions options, SceneScript? script)
    {
        shadowSettings = CreateShadowSettings(options, script);
        virtualShadows.SetSettings(shadowSettings);
    }

    private static VirtualShadowMapSettings CreateShadowSettings(ApplicationOptions options, SceneScript? script)
    {
        VirtualShadowMapSettings settings = (script?.ShadowSettings ?? new VirtualShadowMapSettings
        {
            DynamicResolutionEnabled = true,
        }) with { Enabled = options.VirtualShadows };
        if (script is null && options.DirectionalDynamicReceiverRefresh is { } refresh)
            settings = settings with { DirectionalDynamicReceiverRefresh = refresh };
        return options.ShadowRayCount is { } rayCount ? settings with
        {
            DirectionalRayCount = rayCount,
            LocalRayCount = rayCount,
            MaxFilterRadiusTexels = rayCount == 0 ? 0 : settings.MaxFilterRadiusTexels,
        } : settings;
    }

    internal void ReconcileFeatures(ClusterPipelineOutputs output, ApplicationOptions options, SceneScript? script)
    {
        virtualShadows.ReconcileOpaqueRequests(output.Geometry, output.Visibility, output.Depth,
            (uint)output.Width, (uint)output.Height, output.ViewCount);
        ReconcileLayers();
        foreach (IApplicationFrameFeature feature in _frameFeatures) feature.Reconcile();
    }

    internal bool InitializeFrameFeatures(ApplicationRendering rendering,
        Func<ApplicationRendering, IEnumerable<IApplicationFrameFeature>>? create)
    {
        if (_frameFeaturesInitialized) return false;
        _frameFeatures = create?.Invoke(rendering).ToArray() ?? [];
        _frameFeaturesInitialized = true;
        foreach (IApplicationFrameFeature feature in _frameFeatures) feature.Reconcile();
        return create is not null;
    }

    internal void RecordFeatures(ref RenderGraphFrame frame, in RenderView view, int simulationFrame)
    {
        materialDecals.Stage(ref frame, [view]);
        RecordLayers(ref frame, pipeline.Outputs);
        foreach (IApplicationFrameFeature feature in _frameFeatures) feature.Record(ref frame, simulationFrame);
    }

    internal void WatchScene(ApplicationSceneContent scene, ApplicationScene? application)
    {
        _shaderFiles = scene.Content.Files;
        _application = application;
        _shaderWatcher?.Dispose();
        ApplyShaderChanges(scene.Scene);
        var sources = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttributes(typeof(ShaderSourceDirectoryAttribute), false)
            .Cast<ShaderSourceDirectoryAttribute>().SingleOrDefault();
        string? directory = application?.ShaderSourceDirectory ?? sources?.Directory;
        _shaderWatcher = directory is null ? null : new ShaderSourceWatcher(scene.Content.Directory,
            directory, ShaderLibrary, renderGraph.Device.Capabilities.CompileTarget);
    }

    internal void ApplyShaderChanges(SceneWorldData scene) => _shaderWatcher?.Apply((previous, next) =>
    {
        foreach (Material material in scene.Materials)
            if (ReferenceEquals(material.Shader, previous)) material.Shader = next;
        pipeline.ReplaceShader(previous, next);
        lighting.ReplaceShader(previous, next);
        virtualShadows.ReplaceShader(previous, next);
        skinning.ReplaceShader(previous, next);
        skinBounds.ReplaceShader(previous, next);
        surfaces?.ReplaceShader(previous, next);
        materialDecals.ReplaceShader(previous, next);
        ReplaceLayerShaders(previous, next);
        colorFeature?.ReplaceShader(previous, next);
        temporal?.ReplaceShader(previous, next);
        ui?.ReplaceShader(previous, next);
        presentation?.ReplaceShader(previous, next);
    }, _application?.ShaderBuildFailed);

    internal void InstallSystems()
    {
        if (SystemsInstalled) return;
        prepareSystems.Add(new ClusterResidencySystem(cluster));
        prepareSystems.Add(skinning);
        prepareSystems.Add(skinBounds);
        prepareSystems.Add(materialSystem);
        prepareSystems.Add(instanceSystem);
        frameSystems.Add(skinning);
        frameSystems.Add(skinBounds);
        frameSystems.Add(new LightingFeatureSystem(lighting, lightSystem, outputDescription.ViewCount));
        frameSystems.Add(new VirtualShadowFeatureSystem(virtualShadows, lightSystem));
        frameSystems.Add(pipeline);
        frameSystems.Initialize();
        SystemsInstalled = true;
    }

    internal void Prepare(
        RenderGraphNextFrame frame,
        NativeWindow window,
        bool reportMilestones)
    {
        RenderPrepareSystems systems = prepareSystems;
        using var trace = EngineTrace.Begin(ApplicationTrace.Prepare);
        using (EngineTrace.Begin(ApplicationTrace.ExtractionApply))
            _ = systems.ApplyExtraction();
        if (reportMilestones) Console.WriteLine("First frame: preparing streamed meshes.");
        ValueTask<ClusterMeshPrepareResult> loading = cluster.PrepareMeshesAsync();
        ClusterMeshPrepareResult result = loading.IsCompletedSuccessfully
            ? loading.Result : ApplicationWait.Task(loading.AsTask(), window, FrameTimeout);
        if (result.UnresolvedMeshes != 0)
            throw new InvalidOperationException($"{result.UnresolvedMeshes} scene meshes were unresolved during Cluster preparation.");
        streamingConsumer.Pump();
        using (EngineTrace.Begin(ApplicationTrace.PrepareSystems))
            systems.Update();
        RenderLightSet lights = lightSystem.RequireCurrent();
        RenderView view = renderWorld.Read<RenderView>(viewEntity);
        Matrix4x4.Invert(view.View, out Matrix4x4 inverseView);
        lights.OrderForUpload(inverseView.Translation);
        virtualShadows.ReconcileLights(lights);
        virtualShadows.PrepareViews(frame, lights, view);
    }

    internal FrameOutputMetrics? RenderFrame(
        RenderView view,
        int simulationFrame,
        RenderGraphNextFrame nextGraphFrame,
        in ClusterPipelineOutputs clusterOutputs,
        SwapchainImage? image,
        int width,
        int height,
        NativeWindow window,
        bool reportMilestones,
        bool cameraCut,
        FrameOutputVerifier? outputVerifier,
        GraphRegionId frameOutputRegion,
        FrameOutputVerifier? linearVerifier,
        GraphRegionId linearOutputRegion,
        Action<VirtualShadowFrameQuality>? captureShadowQuality,
        RenderGraphDiagnosticsHandler? graphDiagnostics,
        bool synchronousFrames,
        bool temporalAntialiasing,
        Vector2 jitterPixels,
        TemporalResolveSettings temporalSettings,
        ApplicationFrameMeasurements? measurements,
        out ulong graphFrameIdentity,
        out PresentStatus present)
    {
        var backend = renderDevice.Backend;
        var graph = renderGraph;
        var systems = frameSystems;
        var geometry = this.geometry!;
        var temporal = this.temporal!;
        var presentation = this.presentation!;
        var ui = this.ui!;
        present = default;
        using var trace = EngineTrace.Begin(ApplicationTrace.Execute);
        FrameOutputMetrics? frameOutput = null;
        RenderGraphFrame graphFrame = graph.BeginFrame(
            nextGraphFrame,
            new RenderGraphFrameOptions(
                SubmissionMode: FrameSubmissionMode.RecordAhead,
                Diagnostics: graphDiagnostics,
                GpuTimes: measurements is null ? null : measurements.ReceiveGpu));
        graphFrameIdentity = graphFrame.FrameIdentity;
        try
        {
            geometry.Observe(ref graphFrame);
            if (reportMilestones)
                Console.WriteLine("First frame: recording render systems.");
            if (image is { } acquired) presentation.Bind(ref graphFrame, acquired, graphicsQueue);
            using (EngineTrace.Begin(ApplicationTrace.RenderSystems))
                systems.Update(graphFrame);
            RecordFeatures(ref graphFrame, view, simulationFrame);
            renderDevice.OrderTextureUploadsBefore(ref graphFrame, presentation.DrawPass);
            var motion = new MotionFeatureOutput(
                clusterOutputs.MotionVectors,
                clusterOutputs.Width,
                clusterOutputs.Height,
                clusterOutputs.ViewCount);
            if (image is not null)
                linearVerifier?.Record(ref graphFrame, layers.Color, graphicsQueue, linearOutputRegion);
            using (EngineTrace.Begin(ApplicationTrace.TemporalRecording))
                temporal.Record(
                    ref graphFrame,
                    colorFeature?.Record(ref graphFrame, layers.Color, layers.Depth) ?? layers.Color,
                    layers.Depth,
                    motion,
                    temporalSettings,
                    enabled: temporalAntialiasing,
                    cameraCut: cameraCut,
                    view: viewEntity,
                    jitterPixels: jitterPixels);
            surfaces?.UpdateFrame(ref graphFrame);
            ui.Record(ref graphFrame, width, height);
            if (image is null) return null;
            if (captureShadowQuality is not null) virtualShadows.CaptureQuality(ref graphFrame, captureShadowQuality);
            outputVerifier?.Record(ref graphFrame, presentation.Target, graphicsQueue, frameOutputRegion);
            if (reportMilestones)
                Console.WriteLine("First frame: analyzing and submitting render graph.");

            graphFrame.Execute();
            bool readOutput = outputVerifier is not null || linearVerifier is not null;
            if (synchronousFrames || readOutput)
            {
                if (graph.WaitForFrame(graphFrameIdentity, FrameTimeout) != WaitStatus.Completed)
                    throw new TimeoutException("GPU work did not complete before the frame timeout.");
                _ = window.PumpMessages();
            }
            if (outputVerifier is not null)
            {
                outputVerifier.ReadPixels();
                if (graphDiagnostics is not null) frameOutput = outputVerifier.Analyze();
            }
            linearVerifier?.ReadPixels();
            if (synchronousFrames || readOutput) graph.CollectCompleted();
            present = backend.Present(graphicsQueue, image.Value);
            return frameOutput;
        }
        finally
        {
            graphFrame.Dispose();
        }
    }

    public void Dispose()
    {
        List<Exception>? errors = null;
        Close(DisposeFrameConsumers);
        Close(renderGraph.Dispose);
        Close(DisposeProviders);
        Close(pipelines.Dispose);
        if (errors is not null)
            throw new AggregateException("Application renderer shutdown failed.", errors);

        void Close(Action dispose)
        {
            try { dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
    }

    private void DisposeFrameConsumers()
    {
        _shaderWatcher?.Dispose();
        if (LoadingScene is { } loading) loading.GetAwaiter().GetResult().Dispose();
        NextScene?.Dispose();
        if (renderDevice.Device.Status == DeviceStatus.Active)
        {
            renderGraph.WaitForFrame(renderGraph.LastSubmittedFrame, FrameTimeout);
            renderGraph.CollectCompleted();
        }
        ui?.Dispose();
        presentation?.Dispose();
        temporal?.Dispose();
        colorFeature?.Dispose();
        foreach (IApplicationFrameFeature feature in _frameFeatures) feature.Dispose();
    }

    private void DisposeProviders()
    {
        layers.Dispose();
        _layerDispatchLayout.Dispose();
        _layerDrawLayout.Dispose();
        materialDecals.Dispose();
        frameSystems.Dispose();
        geometry?.DisposeFeatures();
        virtualShadows.Dispose();
        lighting.Dispose();
        prepareSystems.Dispose();
        surfaces?.Dispose();
        skinBounds.Dispose();
        skinning.Dispose();
        cluster.Dispose();
        instances.Dispose();
        extraction.Dispose();
        renderWorld.Dispose();
        renderDevice.Dispose();
    }
}
