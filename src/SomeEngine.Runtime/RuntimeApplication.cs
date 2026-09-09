using System.Diagnostics;
using SomeEngine.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Graphics.Validation;
using SomeEngine.Render;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Features.Debugging;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Features.Motion;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Features.Temporal;
using SomeEngine.Render.Features.Translucency;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Runtime;

internal static class RuntimeApplication
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(60);
    private const Format PresentationFormat = Format.R8G8B8A8UNorm;

    internal static async Task RunAsync(RuntimeStartupOptions options, bool useWarp)
    {
        ArgumentNullException.ThrowIfNull(options);
        string contentRoot = FindContentRoot();
        string manifestDirectory = Path.Combine(contentRoot, "Library", "AssetManifest");
        AssetManifest manifest = AssetManifest.Load(manifestDirectory);
        IReadOnlyList<AssetManifestRecord> runtimeRecords =
            manifest.List(AssetType<RuntimeConfiguration>.Name);
        if (runtimeRecords.Count != 1)
        {
            throw new InvalidDataException(
                $"The runtime publication must contain exactly one {AssetType<RuntimeConfiguration>.Name} " +
                $"asset; found {runtimeRecords.Count}.");
        }

        await using var assets = new AssetLoader(new LooseAssetStorage(contentRoot, manifest));
        RuntimeConfiguration value = await assets
            .LoadAsync(new AssetId<RuntimeConfiguration>(runtimeRecords[0].Guid))
            .ConfigureAwait(false);
        (AssetGuid uiShader, string uiVertexEntry, string uiPixelEntry) =
            ParseRasterShaders(value.UiShaders, nameof(value.UiShaders));
        (AssetGuid presentationShader, string presentationVertexEntry, string presentationPixelEntry) =
            ParseRasterShaders(value.PresentationShaders, nameof(value.PresentationShaders));
        (AssetGuid lightingShader, string lightingEntryPoint) =
            ParseComputeShader(value.LightingShader, nameof(value.LightingShader));
        (AssetGuid temporalShader, string temporalVertexEntry, string temporalPixelEntry) =
            ParseRasterShaders(value.TemporalShaders, nameof(value.TemporalShaders));
        (AssetGuid translucencyShader, string translucencyVertexEntry, string translucencyPixelEntry) =
            ParseRasterShaders(value.TranslucencyShaders, nameof(value.TranslucencyShaders));
        (AssetGuid debugShader, string debugVertexEntry, string debugPixelEntry) =
            ParseRasterShaders(value.DebugShaders, nameof(value.DebugShaders));
        var boot = new BootConfiguration(
            ParseGuid(value.SceneGuid, nameof(value.SceneGuid)),
            ParseGuid(value.ClusterPipelineGuid, nameof(value.ClusterPipelineGuid)),
            ParseGuid(value.VirtualShadowMapGuid, nameof(value.VirtualShadowMapGuid)),
            uiShader,
            uiVertexEntry,
            uiPixelEntry,
            presentationShader,
            presentationVertexEntry,
            presentationPixelEntry,
            lightingShader,
            lightingEntryPoint,
            temporalShader,
            temporalVertexEntry,
            temporalPixelEntry,
            translucencyShader,
            translucencyVertexEntry,
            translucencyPixelEntry,
            debugShader,
            debugVertexEntry,
            debugPixelEntry,
            checked((int)value.WindowWidth),
            checked((int)value.WindowHeight),
            string.IsNullOrWhiteSpace(value.Name) ? "SomeEngine" : value.Name);

        Task<RenderScene> sceneTask = assets.LoadAsync(new AssetId<RenderScene>(boot.Scene)).AsTask();
        Task<ClusterShaders> pipelineTask = assets.LoadAsync(new AssetId<ClusterShaders>(boot.Pipeline)).AsTask();
        Task<VirtualShadowMapShaders> virtualShadowTask = assets
            .LoadAsync(new AssetId<VirtualShadowMapShaders>(boot.VirtualShadowMap))
            .AsTask();
        Task<Shader> uiShaderTask = assets.LoadAsync(new AssetId<Shader>(boot.UiShader)).AsTask();
        Task<Shader> presentationShaderTask = assets
            .LoadAsync(new AssetId<Shader>(boot.PresentationShader))
            .AsTask();
        Task<Shader> lightingShaderTask = assets
            .LoadAsync(new AssetId<Shader>(boot.LightingShader))
            .AsTask();
        Task<Shader> temporalShaderTask = assets
            .LoadAsync(new AssetId<Shader>(boot.TemporalShader))
            .AsTask();
        Task<Shader> translucencyShaderTask = assets
            .LoadAsync(new AssetId<Shader>(boot.TranslucencyShader))
            .AsTask();
        Task<Shader> debugShaderTask = assets
            .LoadAsync(new AssetId<Shader>(boot.DebugShader))
            .AsTask();
        await Task.WhenAll(
                sceneTask,
                pipelineTask,
                virtualShadowTask,
                uiShaderTask,
                presentationShaderTask,
                lightingShaderTask,
                temporalShaderTask,
                translucencyShaderTask,
                debugShaderTask)
            .ConfigureAwait(false);

        using var window = new NativeWindow(boot.Title, boot.Width, boot.Height, visible: !options.HideWindow);
        if (useWarp && options.GraphicsBackend != RuntimeGraphicsBackend.Direct3D12)
            throw new ArgumentException("The WARP adapter is available only with the D3D12 backend.");
        using IGraphicsBackend backend = CreateBackend(
            options.GraphicsBackend,
            options.DeviceValidation);
        AdapterInfo adapter = SelectAdapter(backend, useWarp);
        DeviceQueueDesc[] queues =
        [
            new(QueueType.Graphics),
            new(QueueType.Compute),
            new(QueueType.Copy),
        ];
        using Surface surface = backend.CreateSurface(new SurfaceDesc(
            NativeWindowType.Win32,
            window.Handle,
            Label: boot.Title));
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            queues,
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands,
            optionalFeatures: options.DisableMeshShaders ? DeviceFeatures.None : DeviceFeatures.MeshShaders,
            label: "SomeEngine Runtime Device"));
        using Swapchain swapchain = backend.CreateSwapchain(device, new SwapchainDesc(
            surface,
            3,
            TextureUsages.ColorAttachment | TextureUsages.CopySource,
            CreateSwapchainConfig(
                options,
                boot.Width,
                boot.Height,
                adapter.HardwareAccelerated),
            boot.Title));

        RunRenderer(
            options,
            backend,
            device,
            swapchain,
            window,
            assets,
            sceneTask.Result,
            pipelineTask.Result,
            virtualShadowTask.Result,
            uiShaderTask.Result,
            presentationShaderTask.Result,
            lightingShaderTask.Result,
            temporalShaderTask.Result,
            translucencyShaderTask.Result,
            debugShaderTask.Result,
            boot,
            forceHardwareRaster: useWarp || options.ForceHardwareRaster);
    }

    private static IGraphicsBackend CreateBackend(
        RuntimeGraphicsBackend graphicsBackend,
        bool validation)
    {
        IGraphicsBackend backend = graphicsBackend switch
        {
            RuntimeGraphicsBackend.Direct3D12 => D3D12GraphicsBackend.Create(),
            RuntimeGraphicsBackend.Vulkan => VulkanGraphicsBackend.Create(new VulkanBackendOptions(
                EnableValidation: validation,
                EnableDebugMessages: validation)),
            _ => throw new ArgumentOutOfRangeException(nameof(graphicsBackend)),
        };
        if (!validation)
            return backend;
        try
        {
            return new ValidationLayer(backend);
        }
        catch
        {
            backend.Dispose();
            throw;
        }
    }

    private static AdapterInfo SelectAdapter(IGraphicsBackend backend, bool useWarp)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: useWarp);
        _ = backend.TryEnumerateAdapters(options, [], out int requiredCount);
        if (requiredCount == 0)
            throw new NotSupportedException("No graphics adapter satisfies the Runtime request.");
        var adapters = new AdapterInfo[requiredCount];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmedCount) ||
            confirmedCount != adapters.Length)
        {
            throw new InvalidOperationException(
                "The graphics adapter set changed while Runtime selected a Device.");
        }

        if (useWarp)
        {
            foreach (AdapterInfo adapter in adapters)
                if (!adapter.HardwareAccelerated)
                    return adapter;
            throw new NotSupportedException("The Direct3D 12 WARP adapter is unavailable.");
        }
        foreach (AdapterInfo adapter in adapters)
            if (adapter.HardwareAccelerated)
                return adapter;
        throw new NotSupportedException("No hardware Direct3D 12 adapter is available.");
    }

    private static SwapchainConfig CreateSwapchainConfig(
        RuntimeStartupOptions options,
        int width,
        int height,
        bool hardwareAccelerated) => new(
            checked((uint)width),
            checked((uint)height),
            PresentationFormat,
            ColorSpace.Srgb,
            options.WindowVSync
                ? PresentType.Fifo
                : hardwareAccelerated
                    ? PresentType.Immediate
                    : PresentType.Mailbox,
            AllowTearing: !options.WindowVSync && hardwareAccelerated,
            MaximumFrameLatency: 2);

    private static void ReconfigureSwapchain(
        IGraphicsBackend backend,
        Swapchain swapchain,
        RuntimeStartupOptions options,
        int width,
        int height,
        bool hardwareAccelerated)
    {
        ReconfigureStatus status = backend.Reconfigure(
            swapchain,
            CreateSwapchainConfig(options, width, height, hardwareAccelerated));
        if (status != ReconfigureStatus.Success)
        {
            throw new InvalidOperationException(
                $"The Runtime swapchain could not be reconfigured: {status}.");
        }
    }

    private static SwapchainImage AcquireImage(
        IGraphicsBackend backend,
        Swapchain swapchain,
        NativeWindow window)
    {
        long started = Environment.TickCount64;
        while (true)
        {
            SwapchainAcquireStatus status = backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.Zero, PreserveContents: false),
                out SwapchainImage image);
            if (status == SwapchainAcquireStatus.Success)
                return image;
            if (status == SwapchainAcquireStatus.OutOfDate)
            {
                throw new InvalidOperationException(
                    "The Runtime swapchain became out of date before image acquisition.");
            }
            if (Environment.TickCount64 - started >= FrameTimeout.TotalMilliseconds)
                throw new TimeoutException("No presentation image became available before the frame timeout.");
            _ = window.PumpMessages();
            window.WaitForEvents(TimeSpan.FromMilliseconds(1));
        }
    }

    private static void RunRenderer(
        RuntimeStartupOptions options,
        IGraphicsBackend backend,
        Device device,
        Swapchain swapchain,
        NativeWindow window,
        AssetLoader assets,
        RenderScene sceneAsset,
        ClusterShaders pipelineConfiguration,
        VirtualShadowMapShaders virtualShadowConfiguration,
        Shader uiShader,
        Shader presentationShader,
        Shader lightingShader,
        Shader temporalShader,
        Shader translucencyShader,
        Shader debugShader,
        BootConfiguration boot,
        bool forceHardwareRaster)
    {
        using var mainWorld = new World(initialEntityCapacity: 2048);
        using var renderWorld = new RenderWorld(initialEntityCapacity: 2048);
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());

        RuntimeScene scene = RuntimeWait.Task(
            RuntimeScene.CreateAsync(mainWorld, assets, sceneAsset).AsTask(),
            window,
            FrameTimeout);

        if (!options.SynchronousFrames) extraction.Extract(mainWorld);
        RuntimeViewFrame initialView = RuntimeViewFrame.Create(
            scene.Camera.View,
            scene.Projection(boot.Width, boot.Height),
            boot.Width,
            boot.Height,
            temporalFrameIndex: 0);
        Entity viewEntity = renderWorld.CreateEntity(initialView.View);

        using var renderDevice = new RenderDevice(backend, device, FrameTimeout);
        using var coordinator = new RenderFrameCoordinator(renderDevice, FrameTimeout);
        var instances = new RenderInstanceStorageSystem(
            backend,
            device,
            coordinator,
            renderWorld,
            ClusterRenderFeature.InstanceLayout,
            new RenderInstanceOptions
            {
                RowCapacity = Math.Max(1, scene.MeshInstanceCount),
                BatchCapacity = 1,
            });
        var cluster = new ClusterRenderResources(
            backend,
            device,
            coordinator,
            renderWorld,
            instances);
        var materialAssignments = new ClusterMaterialAssignments();
        var materialSystem = new ClusterMaterialAssignmentSystem(materialAssignments);
        var instanceSystem = new ClusterInstanceSystem<ClusterMaterialOffsetProducer>(
            cluster,
            ClusterMaterialAssignmentSystem.EntityQuery(),
            ClusterRenderFeature.InstanceLayout,
            materialAssignments.CreateProducer());
        var prepareSystems = new RenderPrepareSystems(renderWorld, instances, extraction);
        var viewSystem = new RuntimeViewPrepareSystem(viewEntity);
        _ = prepareSystems.Add(viewSystem);
        _ = prepareSystems.Add(new ClusterResidencySystem(cluster));
        _ = prepareSystems.Add(materialSystem);
        _ = prepareSystems.Add(instanceSystem);

        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue computeQueue = backend.GetQueue(device, QueueType.Compute);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);
        using var renderGraph = new global::SomeEngine.RenderGraph.RenderGraph(
            backend,
            device,
            [graphicsQueue, computeQueue, copyQueue],
            new RenderGraphDesc(
                MaximumFramesInFlight: 3,
                Label: "SomeEngine Runtime Render Graph"));
        var outputDescription = new ClusterPipelineOutputDescription(boot.Width, boot.Height, 1);
        using var lighting = new LightingFeature(
            backend,
            device,
            renderGraph,
            outputDescription.Width,
            outputDescription.Height,
            outputDescription.ViewCount,
            lightingShader,
            boot.LightingEntryPoint);
        var stageResources = new GraphResourceGroup();
        stageResources.Add(lighting);
        var lightSystem = new RenderLightSetSystem();
        var streamingFeedback = new ClusterStreamingFeedbackChannel();
        var streamingConsumer = new ClusterStreamingFeedbackConsumer(cluster, streamingFeedback);
        var shadowSettings = new VirtualShadowMapSettings();
        ClusterPipelineSystem pipeline = CreatePipeline(
            outputDescription,
            metrics: options.VerifyFrameOutput);
        using var virtualShadows = new VirtualShadowFeature(
            backend,
            device,
            assets,
            virtualShadowConfiguration,
            shadowSettings,
            renderGraph,
            pipeline,
            lighting);
        stageResources.Add(virtualShadows);
        RenderFrameSystems frameSystems = CreateFrameSystems(
            pipeline,
            virtualShadows);
        var graphCompletionSlots = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        var graphCompletions = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        FrameOutputVerifier? outputVerifier = null;
        RuntimeUiRenderer? ui = null;
        RuntimePresentationPipeline? presentation = null;
        TemporalFeature? temporal = null;
        GraphPassId temporalProducer = default;
        TranslucencyFeature? translucency = null;
        DebugFeature? debug = null;
        Task? nextGameUpdate = null;
        var input = new RuntimeInput();
        int width = boot.Width;
        int height = boot.Height;
        RenderGraphStatistics latestGraphStatistics = default;
        bool hasGraphStatistics = false;
        int analyzedFrames = 0;
        int reusedAnalysisFrames = 0;

        ClusterPipelineSystem CreatePipeline(
            in ClusterPipelineOutputDescription description,
            bool metrics) => new(
            backend,
            device,
            renderDevice,
            cluster,
            instanceSystem,
            ClusterRenderFeature.InstanceLayout,
            materialAssignments,
            pipelineConfiguration,
            renderGraph,
            description,
            streamingFeedback,
            stageResources,
            new ClusterPipelineOptions
            {
                EnableAsyncCompute = options.AsyncCompute,
                ForceHardwareRaster = forceHardwareRaster,
                EnableFrameMetricsReadback = metrics,
            });

        void CaptureRenderGraphDiagnostics(in RenderGraphDiagnosticsView diagnostics)
        {
            latestGraphStatistics = diagnostics.Statistics;
            hasGraphStatistics = true;
            analyzedFrames++;
            if (latestGraphStatistics.AnalysisReused) reusedAnalysisFrames++;
        }

        RenderFrameSystems CreateFrameSystems(
            ClusterPipelineSystem receiver,
            VirtualShadowFeature virtualShadowFeature)
        {
            var systems = new RenderFrameSystems(renderWorld, instances);
            _ = systems.Add(lightSystem);
            _ = systems.Add(new LightingFeatureSystem(
                lighting,
                lightSystem,
                outputDescription.ViewCount));
            _ = systems.Add(new VirtualShadowFeatureSystem(
                virtualShadowFeature,
                lightSystem));
            _ = systems.Add(receiver);
            return systems;
        }

        try
        {
            frameSystems.Initialize();
            outputVerifier = options.VerifyFrameOutput
                ? new FrameOutputVerifier(backend, device, width, height, PresentationFormat)
                : null;
            ui = new RuntimeUiRenderer(
                backend,
                device,
                window,
                uiShader,
                boot.UiVertexEntry,
                boot.UiPixelEntry,
                PresentationFormat);
            Console.WriteLine(
                $"Runtime configuration '{boot.Title}' loaded from {contentRootLabel(FindContentRoot())}; " +
                $"sceneInstances={scene.MeshInstanceCount}, " +
                $"sceneBounds={scene.MeshPositionMin}..{scene.MeshPositionMax}, " +
                $"clusterPipeline={pipelineConfiguration.AssetGuid}.");
            long runtimeStarted = Stopwatch.GetTimestamp();
            long previousFrameTimestamp = Stopwatch.GetTimestamp();
            int frameIndex = 0;
            long traceFrameIndex = 0;
            bool animateScene = options.DynamicScene;
            bool debugUiOpen = true;
            FrameOutputMetrics? verifiedOutput = null;
            while (options.FrameLimit == 0 || frameIndex < options.FrameLimit)
            {
                using var frameTrace = EngineTrace.BeginFrame(++traceFrameIndex);
                bool instanceAdmitted = instances.TryAdmitFrameResources(
                    out _,
                    out QueueCompletion[] instanceRetirementFences);
                bool readbackAdmitted = pipeline.TryAdmitFrameResources(
                    out _,
                    out QueueCompletion[] readbackRetirementFences);
                bool uiAdmitted = ui.TryAdmitFrameResources(
                    out _,
                    out QueueCompletion[] uiRetirementFences);
                long admissionWaitStarted = Environment.TickCount64;
                while (!instanceAdmitted ||
                       !readbackAdmitted ||
                       !uiAdmitted)
                {
                    TimeSpan remaining = FrameTimeout -
                        TimeSpan.FromMilliseconds(
                            Environment.TickCount64 - admissionWaitStarted);
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new TimeoutException(
                            "Frame-resource admission did not become available before the runtime timeout.");
                    }
                    TimeSpan waitSlice = remaining > TimeSpan.FromMilliseconds(2)
                        ? TimeSpan.FromMilliseconds(2)
                        : remaining;

                    QueueCompletion[] retirementFences = !instanceAdmitted
                        ? instanceRetirementFences
                        : !readbackAdmitted
                            ? readbackRetirementFences
                            : uiRetirementFences;
                    _ = WaitForAll(backend, retirementFences, waitSlice);

                    _ = window.PumpMessages();
                    if (!instanceAdmitted)
                    {
                        instanceAdmitted = instances.TryAdmitFrameResources(
                            out _,
                            out instanceRetirementFences);
                    }
                    if (!readbackAdmitted)
                    {
                        readbackAdmitted = pipeline.TryAdmitFrameResources(
                            out _,
                            out readbackRetirementFences);
                    }
                    if (!uiAdmitted)
                    {
                        uiAdmitted = ui.TryAdmitFrameResources(
                            out _,
                            out uiRetirementFences);
                    }
                }
                backend.CollectCompleted(device);
                coordinator.AdmitFrameResources();
                // Shared asset content can change only after the Game World extraction reader exits.
                if (nextGameUpdate is not null)
                {
                    RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
                    nextGameUpdate = null;
                }
                RuntimeWait.Task(
                    assets.ApplyUpdatesAsync().AsTask(),
                    window,
                    FrameTimeout);
                input.BeginFrame();
                if (!window.PumpMessages())
                    break;
                while (window.TryReadEvent(out NativeWindowEvent windowEvent))
                {
                    input.Process(windowEvent);
                    ui.ProcessEvent(windowEvent, input);
                }
                if (input.WasKeyPressed(RuntimeInput.KeyEscape))
                {
                    window.RequestClose();
                    continue;
                }
                if (input.WasKeyPressed(RuntimeInput.KeyF1))
                    debugUiOpen = !debugUiOpen;
                if (window.IsMinimized)
                {
                    window.WaitForEvents(TimeSpan.FromMilliseconds(16));
                    previousFrameTimestamp = Stopwatch.GetTimestamp();
                    continue;
                }

                int clientWidth = window.ClientWidth;
                int clientHeight = window.ClientHeight;
                bool cameraCut = false;
                if (clientWidth > 0 && clientHeight > 0 &&
                    (clientWidth != width || clientHeight != height))
                {
                    outputVerifier?.Dispose();
                    outputVerifier = null;
                    ReconfigureSwapchain(
                        backend,
                        swapchain,
                        options,
                        clientWidth,
                        clientHeight,
                        device.Adapter.HardwareAccelerated);
                    width = clientWidth;
                    height = clientHeight;
                    cameraCut = true;
                    outputVerifier = options.VerifyFrameOutput
                        ? new FrameOutputVerifier(backend, device, width, height, PresentationFormat)
                        : null;
                }

                long currentFrameTimestamp = Stopwatch.GetTimestamp();
                float deltaSeconds = Math.Clamp(
                    (float)Stopwatch.GetElapsedTime(previousFrameTimestamp, currentFrameTimestamp).TotalSeconds,
                    1.0f / 1000.0f,
                    1.0f / 10.0f);
                if (options.FixedFrameSeconds > 0) deltaSeconds = options.FixedFrameSeconds;
                float sceneSeconds = options.FixedFrameSeconds > 0
                    ? frameIndex * options.FixedFrameSeconds
                    : (float)Stopwatch.GetElapsedTime(runtimeStarted).TotalSeconds;
                previousFrameTimestamp = currentFrameTimestamp;
                if (options.ScenePlayback is not null)
                    scene.Play(sceneSeconds, options.ScenePlayback == "materials");
                ui.BeginFrame(deltaSeconds, width, height, window.DpiScale);
                scene.Camera.Update(
                    input,
                    deltaSeconds,
                    ui.WantCaptureKeyboard,
                    ui.WantCaptureMouse);
                RuntimeViewFrame viewFrame = RuntimeViewFrame.Create(
                    scene.Camera.View,
                    scene.Projection(width, height),
                    width,
                    height,
                    checked((uint)frameIndex),
                    cameraCut: cameraCut);
                viewSystem.Stage(viewFrame.View);
                ui.DrawDebugWindow(
                    ref debugUiOpen,
                    ref animateScene,
                    new RuntimeUiMetrics(
                        frameIndex,
                        deltaSeconds,
                        width,
                        height,
                        window.DpiScale,
                        viewFrame.JitterPixels,
                        scene.Camera.Position,
                        window.HasFocus));
                bool reportMilestones = frameIndex == 0;
                if (reportMilestones)
                    Console.WriteLine("First frame: applying extracted scene.");
                if (options.SynchronousFrames)
                {
                    if (animateScene && options.ScenePlayback is null)
                        scene.Update(sceneSeconds);
                    extraction.Extract(mainWorld);
                }
                Prepare(
                    prepareSystems,
                    cluster,
                    streamingConsumer,
                    coordinator,
                    window,
                    reportMilestones);
                bool hasFutureFrame =
                    options.FrameLimit == 0 || frameIndex + 1 < options.FrameLimit;
                if (hasFutureFrame && !options.SynchronousFrames)
                {
                    bool updateScene = animateScene;
                    float nextSceneTime = options.FixedFrameSeconds > 0 ? (frameIndex + 1) * options.FixedFrameSeconds : (float)Stopwatch.GetElapsedTime(
                        runtimeStarted,
                        Stopwatch.GetTimestamp()).TotalSeconds;
                    TraceContext gameTrace = EngineTrace.Context with { Frame = traceFrameIndex + 1 };
                    nextGameUpdate = Task.Run(() =>
                    {
                        using var association = EngineTrace.Associate(gameTrace);
                        using var trace = EngineTrace.Begin(ApplicationTrace.GameUpdate);
                        if (updateScene)
                            scene.Update(nextSceneTime);
                        extraction.Extract(mainWorld);
                    });
                }
                ClusterPipelineOutputs clusterOutputs;
                using (EngineTrace.Begin(ApplicationTrace.Reconcile))
                    clusterOutputs = pipeline.ReconcileGraph();
                bool connectPostProcessing = temporal is null || translucency is null || debug is null;
                temporal ??= new TemporalFeature(
                    backend,
                    device,
                    renderGraph,
                    boot.Width,
                    boot.Height,
                    temporalShader,
                    boot.TemporalVertexEntry,
                    boot.TemporalPixelEntry);
                translucency ??= new TranslucencyFeature(
                    backend,
                    device,
                    renderGraph,
                    boot.Width,
                    boot.Height,
                    translucencyShader,
                    boot.TranslucencyVertexEntry,
                    boot.TranslucencyPixelEntry);
                debug ??= new DebugFeature(
                    backend,
                    device,
                    renderGraph,
                    boot.Width,
                    boot.Height,
                    debugShader,
                    boot.DebugVertexEntry,
                    boot.DebugPixelEntry);
                if (connectPostProcessing || temporalProducer != pipeline.ShadeCompletePass)
                {
                    RenderGraphEdit edit = renderGraph.BeginEdit();
                    try
                    {
                        temporal.OrderAfter(ref edit, pipeline.ShadeCompletePass);
                        translucency.OrderCompositionAfter(ref edit, temporal.Output.CompletePass);
                        debug.OrderAfter(ref edit, translucency.Output.CompletePass);
                        edit.Commit();
                        temporalProducer = pipeline.ShadeCompletePass;
                    }
                    finally
                    {
                        edit.Dispose();
                    }
                }
                presentation ??= new RuntimePresentationPipeline(
                    backend,
                    device,
                    renderGraph,
                    debug.Output.Color,
                    debug.Output.Format,
                    debug.Output.Width,
                    debug.Output.Height,
                    debug.Output.CompletePass,
                    presentationShader,
                    boot.PresentationVertexEntry,
                    boot.PresentationPixelEntry,
                    PresentationFormat);
                ui.InstallGraph(renderGraph, presentation.Target);
                if (reportMilestones)
                    Console.WriteLine("First frame: acquiring presentation image.");
                SwapchainImage image = AcquireImage(backend, swapchain, window);
                FrameOutputMetrics? frameOutput = ExecuteFrame(
                    backend,
                    renderGraph,
                    graphicsQueue,
                    graphCompletionSlots,
                    graphCompletions,
                    coordinator,
                    frameSystems,
                    pipeline,
                    virtualShadows,
                    clusterOutputs,
                    temporal,
                    translucency,
                    debug,
                    presentation,
                    ui,
                    image,
                    width,
                    height,
                    window,
                    reportMilestones,
                    cameraCut,
                    viewEntity,
                    outputVerifier,
                    options.VerifyFrameOutput ? CaptureRenderGraphDiagnostics : null,
                    options.SynchronousFrames);
                if (frameOutput is FrameOutputMetrics output)
                    verifiedOutput = output;

                if (reportMilestones)
                    Console.WriteLine("First frame: presenting.");
                PresentStatus present = backend.Present(
                    backend.GetQueue(device, QueueType.Graphics),
                    image);
                if (present == PresentStatus.Occluded)
                    window.WaitForEvents(TimeSpan.FromMilliseconds(16));
                else if (present == PresentStatus.OutOfDate)
                    ReconfigureSwapchain(
                        backend,
                        swapchain,
                        options,
                        width,
                        height,
                        device.Adapter.HardwareAccelerated);

                frameIndex++;
            }

            if (frameIndex > 0)
            {
                ClusterRenderDiagnostics diagnostics = cluster.CaptureDiagnostics();
                ClusterResidencyDiagnostics residency = diagnostics.Residency;
                Console.WriteLine(
                    $"Cluster residency: meshes={diagnostics.Meshes.PublishedMeshes}/" +
                    $"{diagnostics.Meshes.RegisteredMeshes}, pages={residency.ResidentPages}/" +
                    $"{residency.RegisteredPages}, missing={residency.MissingPages}, " +
                    $"queued={residency.QueuedPageLoads}, active={residency.ActivePageLoads}, " +
                    $"failures={residency.PageLoadFailures}.");
                if (diagnostics.Meshes.PublishedMeshes != diagnostics.Meshes.RegisteredMeshes ||
                    residency.PageLoadFailures != 0 ||
                    residency.LastPageLoadFailure is not null ||
                    residency.LastCleanupError is not null)
                {
                    throw new InvalidOperationException(
                        "Cluster streaming did not reach a healthy published state; see residency diagnostics.");
                }
                if (outputVerifier is not null)
                {
                    FrameOutputMetrics output = verifiedOutput ?? throw new InvalidOperationException(
                        "Frame-output verification was requested, but the runtime rendered no frames.");
                    Console.WriteLine(
                        $"Frame output: {output.Width}x{output.Height}, " +
                        $"rgb=({output.MinRed}-{output.MaxRed}," +
                        $"{output.MinGreen}-{output.MaxGreen}," +
                        $"{output.MinBlue}-{output.MaxBlue}), " +
                        $"colors>={output.ReportedDistinctColors}, " +
                        $"different={output.PixelsDifferentFromFirst}, " +
                        $"bounds=({output.MinDifferentX},{output.MinDifferentY})-" +
                        $"({output.MaxDifferentX},{output.MaxDifferentY}) " +
                        $"[{output.DifferentWidth}x{output.DifferentHeight}], " +
                        $"hash={output.Hash:X16}.");
                    ClusterFrameMetrics frameMetrics = pipeline.CaptureFrameMetrics();
                    Console.WriteLine(
                        $"Cluster frame {frameMetrics.FrameIndex}: " +
                        $"materials={materialAssignments.Current.MaterialCount}, " +
                        $"candidates={frameMetrics.CandidateCount}/" +
                        $"{frameMetrics.CandidateDispatchGroups} groups, " +
                        $"phase1={frameMetrics.PhaseOneSoftwareClusters} SW+" +
                        $"{frameMetrics.PhaseOneHardwareClusters} HW, " +
                        $"phase2Candidates={frameMetrics.PhaseTwoCandidateCount}/" +
                        $"{frameMetrics.PhaseTwoDispatchGroups} groups, " +
                        $"phase2={frameMetrics.PhaseTwoSoftwareClusters} SW+" +
                        $"{frameMetrics.PhaseTwoHardwareClusters} HW, " +
                        $"rasterBatches={frameMetrics.RasterBatches} total/" +
                        $"{frameMetrics.SoftwareRasterBatches} SW, " +
                        $"shadeBinTiles={frameMetrics.FirstShadeBinTiles}, " +
                        $"shadeItems={frameMetrics.ShadingWorkItems}, " +
                        $"tessPatches={frameMetrics.TessellationFinalPatches}, " +
                        $"tessDropped={frameMetrics.DroppedTessellationPatches}, " +
                        $"tessLimited={frameMetrics.QualityLimitedTessellationPatches}, " +
                        $"tessCulled={frameMetrics.CulledTessellationPatches}, " +
                        $"swDebug={frameMetrics.SoftwareRasterDebugRecords}, " +
                        $"visProbe={frameMetrics.VisibilityProbePixels}/64.");
                    if (!hasGraphStatistics)
                    {
                        throw new InvalidOperationException(
                            "Frame-output verification did not capture RenderGraph structural diagnostics.");
                    }
                    Console.WriteLine(
                        $"RenderGraph structure: declaredPasses={latestGraphStatistics.DeclaredPassCount}, " +
                        $"livePasses={latestGraphStatistics.LivePassCount}, " +
                        $"scheduledPasses={latestGraphStatistics.ScheduledPassCount}, " +
                        $"buffers={latestGraphStatistics.BufferCount}, " +
                        $"textures={latestGraphStatistics.TextureCount}, " +
                        $"accesses={latestGraphStatistics.AccessCount}, " +
                        $"dependencies={latestGraphStatistics.DependencyCount}, " +
                        $"barriers={latestGraphStatistics.BarrierCount}, " +
                        $"queues={latestGraphStatistics.QueueCount}, " +
                        $"analysisReused={reusedAnalysisFrames}/{analyzedFrames}, " +
                        $"transientResourceBytes={latestGraphStatistics.TransientResourceBytes}, " +
                        $"transientOccupiedBytes={latestGraphStatistics.TransientOccupiedBytes}, " +
                        $"transientHeapBytes={latestGraphStatistics.TransientHeapBytes}, " +
                        $"transientUnusedBytes={latestGraphStatistics.TransientUnusedBytes}, " +
                        $"graphHeapBytes={latestGraphStatistics.GraphHeapBytes}.");
                    uint visibleClusters = checked(
                        frameMetrics.PhaseOneSoftwareClusters +
                        frameMetrics.PhaseOneHardwareClusters +
                        frameMetrics.PhaseTwoSoftwareClusters +
                        frameMetrics.PhaseTwoHardwareClusters);
                    bool clusterOutputHealthy =
                        frameMetrics.CandidateCount != 0 &&
                        visibleClusters != 0 &&
                        frameMetrics.ShadingWorkItems != 0;
                    if (!output.HasSubstantialCoverage || !clusterOutputHealthy)
                    {
                        throw new InvalidOperationException(
                            "The Cluster frame did not produce substantial visible output together with " +
                            "candidates, visible clusters, and shaded pixels.");
                    }
                }
            }
            Console.WriteLine($"SomeEngine runtime completed {frameIndex} frame(s).");
        }
        finally
        {
            if (nextGameUpdate is not null)
                RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
            outputVerifier?.Dispose();
            coordinator.WaitForTrackedSubmissions();
            ui?.Dispose();
            presentation?.Dispose();
            debug?.Dispose();
            translucency?.Dispose();
            temporal?.Dispose();
            frameSystems.Dispose();
            Shutdown(prepareSystems, cluster, instances, coordinator);
            scene.Dispose();
            if (device.Status == DeviceStatus.Active)
                backend.CollectCompleted(device);
        }
    }

    private static void Prepare(
        RenderPrepareSystems systems,
        ClusterRenderResources cluster,
        ClusterStreamingFeedbackConsumer streamingFeedback,
        RenderFrameCoordinator coordinator,
        NativeWindow window,
        bool reportMilestones)
    {
        using var trace = EngineTrace.Begin(ApplicationTrace.Prepare);
        if (!coordinator.TryBeginPrepare(out RenderPrepareScope? scope))
            throw new InvalidOperationException("The render prepare boundary is not available.");
        using (scope)
        {
            _ = systems.ApplyExtraction(scope);
            if (reportMilestones)
                Console.WriteLine("First frame: preparing streamed meshes.");
            // Mesh discovery reads only the newly published RenderWorld. Asset I/O may complete
            // asynchronously; no renderer path reaches back into the authoritative GameWorld.
            ValueTask<ClusterMeshPrepareResult> meshPreparation =
                cluster.PrepareMeshesAsync();
            ClusterMeshPrepareResult meshPrepare =
                meshPreparation.IsCompletedSuccessfully
                    ? meshPreparation.Result
                    : RuntimeWait.Task(
                        meshPreparation.AsTask(),
                        window,
                        FrameTimeout);
            if (meshPrepare.UnresolvedMeshes != 0)
            {
                throw new InvalidOperationException(
                    $"{meshPrepare.UnresolvedMeshes} scene mesh assets were unresolved during Cluster preparation.");
            }
            streamingFeedback.Pump();
            if (reportMilestones)
                Console.WriteLine("First frame: publishing prepare systems.");
            systems.Update(scope);
            scope.Commit();
        }
    }

    private static FrameOutputMetrics? ExecuteFrame(
        IGraphicsBackend backend,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        Queue graphicsQueue,
        QueueCompletion[] completionSlots,
        QueueCompletion[] completions,
        RenderFrameCoordinator coordinator,
        RenderFrameSystems systems,
        ClusterPipelineSystem pipeline,
        VirtualShadowFeature virtualShadows,
        in ClusterPipelineOutputs clusterOutputs,
        TemporalFeature temporal,
        TranslucencyFeature translucency,
        DebugFeature debug,
        RuntimePresentationPipeline presentation,
        RuntimeUiRenderer ui,
        SwapchainImage image,
        int width,
        int height,
        NativeWindow window,
        bool reportMilestones,
        bool cameraCut,
        Entity viewEntity,
        FrameOutputVerifier? outputVerifier,
        RenderGraphDiagnosticsHandler? graphDiagnostics,
        bool synchronousFrames)
    {
        using var trace = EngineTrace.Begin(ApplicationTrace.Execute);
        if (!coordinator.TryBeginFrame(out RenderFrame? frame))
        {
            RenderFrameSynchronizationDiagnostics state = coordinator.CaptureDiagnostics();
            throw new InvalidOperationException(
                "The render frame boundary is not available: " +
                $"prepareOpen={state.PrepareOpen}, frameOpen={state.FrameOpen}, " +
                $"readers={state.OpenReaderCount}, retained={state.RetainedPositionCount}, " +
                $"pendingTimelines={state.PendingTimelineCount}, " +
                $"retryTimelines={state.RetryRequiredTimelineCount}.");
        }

        using (frame)
        {
            FrameOutputMetrics? frameOutput = null;
            completionSlots.AsSpan().Clear();
            try
            {
                RenderGraphFrame graphFrame = graph.BeginFrame(
                    new RenderGraphFrameOptions(Diagnostics: graphDiagnostics));
                try
                {
                    if (reportMilestones)
                        Console.WriteLine("First frame: recording render systems.");
                    presentation.Bind(ref graphFrame, image, graphicsQueue);
                    systems.Update(frame, graphFrame);
                    var motion = new MotionFeatureOutput(
                        clusterOutputs.MotionVectors,
                        clusterOutputs.Width,
                        clusterOutputs.Height,
                        clusterOutputs.ViewCount);
                    temporal.Record(
                        ref graphFrame,
                        clusterOutputs.MaterialColor,
                        clusterOutputs.Depth,
                        motion,
                        TemporalResolveSettings.Default,
                        enabled: true,
                        cameraCut: cameraCut,
                        view: viewEntity);
                    translucency.Record(
                        ref graphFrame,
                        temporal.Output.ResolvedColor,
                        enabled: false);
                    debug.Record(
                        ref graphFrame,
                        translucency.Output.Color,
                        clusterOutputs.Depth,
                        DebugVisualizationMode.None);
                    ui.Record(ref graphFrame, width, height);
                    outputVerifier?.Record(ref graphFrame, presentation.Target, graphicsQueue);
                    if (reportMilestones)
                        Console.WriteLine("First frame: analyzing and submitting render graph.");

                    int submittedQueueCount = graphFrame.Execute(completionSlots);
                    int completionCount = CompactCompletions(
                        completionSlots,
                        completions,
                        submittedQueueCount);
                    ReadOnlySpan<QueueCompletion> execution = completions.AsSpan(0, completionCount);
                    frame.Complete(execution);
                    if (synchronousFrames && outputVerifier is null)
                        Wait(backend, execution, window, synchronousFrames);
                    if (outputVerifier is not null)
                    {
                        Wait(backend, execution, window, synchronousFrames);
                        frameOutput = outputVerifier.Read();
                    }
                    return frameOutput;
                }
                finally
                {
                    graphFrame.Dispose();
                }
            }
            catch (Exception primary)
            {
                try
                {
                    int completionCount = CompactCompletions(
                        completionSlots,
                        completions,
                        expectedCount: null);
                    if (completionCount != 0)
                    {
                        ReadOnlySpan<QueueCompletion> accepted = completions.AsSpan(0, completionCount);
                        if (!frame.IsClosed) frame.Complete(accepted);
                    }
                }
                catch (Exception cleanup)
                {
                    throw new AggregateException(
                        "Frame execution failed and partial-submission cleanup also failed.",
                        primary,
                        cleanup);
                }
                throw;
            }
            finally
            {
                ui.Discard();
            }
        }
    }

    private static int CompactCompletions(
        ReadOnlySpan<QueueCompletion> source,
        Span<QueueCompletion> destination,
        int? expectedCount)
    {
        int count = 0;
        foreach (ref readonly QueueCompletion completion in source)
        {
            if (completion == default) continue;
            destination[count++] = completion;
        }
        if (expectedCount.HasValue && count != expectedCount.Value)
        {
            throw new InvalidOperationException(
                $"RenderGraph reported {expectedCount.Value} submitted Queues, but {count} completion slots were valid.");
        }
        return count;
    }

    private static void Wait(
        IGraphicsBackend backend,
        ReadOnlySpan<QueueCompletion> completions,
        NativeWindow window,
        bool eventDriven)
    {
        using var trace = EngineTrace.Begin(ApplicationTrace.GpuCompletion);
        foreach (QueueCompletion completion in completions)
        {
            if (eventDriven)
            {
                if (backend.WaitCpu(completion, FrameTimeout) != WaitStatus.Completed)
                    throw new TimeoutException("GPU work did not complete before the frame timeout.");
            }
            else RuntimeWait.Position(backend, completion, window, FrameTimeout);
        }
        if (eventDriven) _ = window.PumpMessages();
    }

    private static bool WaitForAll(
        IGraphicsBackend backend,
        ReadOnlySpan<QueueCompletion> completions,
        TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (timeout == TimeSpan.Zero)
        {
            foreach (ref readonly QueueCompletion completion in completions)
                if (!backend.IsComplete(completion))
                    return false;
            return true;
        }

        long started = Environment.TickCount64;
        foreach (ref readonly QueueCompletion completion in completions)
        {
            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            if (backend.WaitCpu(completion, remaining) != WaitStatus.Completed)
                return false;
        }
        return true;
    }

    private static void Shutdown(
        RenderPrepareSystems prepareSystems,
        ClusterRenderResources cluster,
        RenderInstanceStorageSystem instances,
        RenderFrameCoordinator coordinator)
    {
        if (!coordinator.TryBeginPrepare(out RenderPrepareScope? scope))
            throw new InvalidOperationException("The final render prepare boundary is not available.");
        using (scope)
        {
            prepareSystems.Shutdown(scope);
            cluster.Shutdown(scope);
            instances.Shutdown(scope);
            scope.Commit();
        }
        prepareSystems.Dispose();
        cluster.Dispose();
        instances.Dispose();
    }

    private static string FindContentRoot()
    {
        var starts = new[] { AppContext.BaseDirectory, Environment.CurrentDirectory };
        foreach (string start in starts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (DirectoryInfo? directory = new(Path.GetFullPath(start));
                 directory is not null;
                 directory = directory.Parent)
            {
                string manifest = Path.Combine(
                    directory.FullName,
                    "Library",
                    "AssetManifest",
                    AssetManifest.AssetIndexFileName);
                if (File.Exists(manifest))
                    return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException(
            "Could not locate a runtime publication containing Library/AssetManifest/asset_index.json.");
    }

    private static AssetGuid ParseGuid(string? value, string field)
    {
        if (!AssetGuid.TryParse(value, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Runtime configuration field '{field}' is invalid.");
        return guid;
    }

    private static (AssetGuid Shader, string VertexEntry, string PixelEntry) ParseRasterShaders(
        IList<ShaderRef>? shaders,
        string field)
    {
        if (shaders is not { Count: 2 })
            throw new InvalidDataException($"Runtime field '{field}' requires one vertex and one pixel shader entry.");

        ShaderRef? vertex = null;
        ShaderRef? pixel = null;
        foreach (ShaderRef shader in shaders)
        {
            if (shader is null)
                throw new InvalidDataException($"Runtime field '{field}' contains a null shader entry.");
            if (shader.Stage == ShaderStage.Vertex && vertex is null)
                vertex = shader;
            else if (shader.Stage == ShaderStage.Pixel && pixel is null)
                pixel = shader;
            else
                throw new InvalidDataException($"Runtime field '{field}' requires one vertex and one pixel shader entry.");
        }

        AssetGuid vertexGuid = ParseGuid(vertex!.AssetGuid, $"{field}.Vertex.AssetGuid");
        AssetGuid pixelGuid = ParseGuid(pixel!.AssetGuid, $"{field}.Pixel.AssetGuid");
        if (vertexGuid != pixelGuid)
            throw new InvalidDataException($"Runtime field '{field}' entries must use one shader asset.");
        if (string.IsNullOrWhiteSpace(vertex.EntryPoint) || string.IsNullOrWhiteSpace(pixel.EntryPoint))
            throw new InvalidDataException($"Runtime field '{field}' shader entry points are missing.");
        return (vertexGuid, vertex.EntryPoint, pixel.EntryPoint);
    }

    private static (AssetGuid Shader, string EntryPoint) ParseComputeShader(
        ShaderRef? shader,
        string field)
    {
        if (shader is null || shader.Stage != ShaderStage.Compute)
            throw new InvalidDataException($"Runtime field '{field}' must contain one compute shader.");
        AssetGuid guid = ParseGuid(shader.AssetGuid, $"{field}.AssetGuid");
        if (string.IsNullOrWhiteSpace(shader.EntryPoint))
            throw new InvalidDataException($"Runtime field '{field}' shader entry point is missing.");
        return (guid, shader.EntryPoint);
    }

    private static string contentRootLabel(string value) => Path.GetFullPath(value);

    private readonly record struct BootConfiguration(
        AssetGuid Scene,
        AssetGuid Pipeline,
        AssetGuid VirtualShadowMap,
        AssetGuid UiShader,
        string UiVertexEntry,
        string UiPixelEntry,
        AssetGuid PresentationShader,
        string PresentationVertexEntry,
        string PresentationPixelEntry,
        AssetGuid LightingShader,
        string LightingEntryPoint,
        AssetGuid TemporalShader,
        string TemporalVertexEntry,
        string TemporalPixelEntry,
        AssetGuid TranslucencyShader,
        string TranslucencyVertexEntry,
        string TranslucencyPixelEntry,
        AssetGuid DebugShader,
        string DebugVertexEntry,
        string DebugPixelEntry,
        int Width,
        int Height,
        string Title);
}
