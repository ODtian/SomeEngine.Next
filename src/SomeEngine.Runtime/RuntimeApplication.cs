using System.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Graphics.Validation;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using Texture = SomeEngine.Graphics.Texture;

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
        AssetHandle<RuntimeConfiguration> configurationHandle = assets.Load(
            new AssetId<RuntimeConfiguration>(runtimeRecords[0].Guid));
        _ = await assets.WaitAsync(configurationHandle).ConfigureAwait(false);

        BootConfiguration boot;
        using (AssetRead<RuntimeConfiguration> read = assets.Read(configurationHandle))
        {
            RuntimeConfiguration value = read.Value;
            (AssetGuid uiShader, string uiVertexEntry, string uiPixelEntry) =
                ParseUiShaders(value.UiShaders);
            boot = new BootConfiguration(
                ParseGuid(value.SceneGuid, nameof(value.SceneGuid)),
                ParseGuid(value.ClusterRendererGuid, nameof(value.ClusterRendererGuid)),
                uiShader,
                uiVertexEntry,
                uiPixelEntry,
                checked((int)value.WindowWidth),
                checked((int)value.WindowHeight),
                string.IsNullOrWhiteSpace(value.Name) ? "SomeEngine" : value.Name);
        }

        AssetHandle<RenderScene> sceneHandle = assets.Load(new AssetId<RenderScene>(boot.Scene));
        AssetHandle<ClusterShaders> rendererHandle = assets.Load(
            new AssetId<ClusterShaders>(boot.Renderer));
        AssetHandle<Shader> uiShaderHandle = assets.Load(new AssetId<Shader>(boot.UiShader));
        await Task.WhenAll(
            assets.WaitAsync(sceneHandle).AsTask(),
            assets.WaitAsync(rendererHandle).AsTask(),
            assets.WaitAsync(uiShaderHandle).AsTask()).ConfigureAwait(false);

        using var window = new NativeWindow(boot.Title, boot.Width, boot.Height);
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
            label: "SomeEngine Runtime Device"));
        using Swapchain swapchain = backend.CreateSwapchain(device, new SwapchainDesc(
            surface,
            3,
            TextureUsages.ColorAttachment | TextureUsages.CopySource,
            new SwapchainConfig(
                checked((uint)boot.Width),
                checked((uint)boot.Height),
                PresentationFormat,
                ColorSpace.Srgb,
                options.WindowVSync ? PresentType.Fifo : PresentType.Immediate,
                AllowTearing: !options.WindowVSync,
                MaximumFrameLatency: 2),
            boot.Title));

        RunRenderer(
            options,
            backend,
            device,
            swapchain,
            window,
            assets,
            sceneHandle,
            rendererHandle,
            uiShaderHandle,
            boot,
            forceHardwareRaster: useWarp);
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
        int height) => new(
            checked((uint)width),
            checked((uint)height),
            PresentationFormat,
            ColorSpace.Srgb,
            options.WindowVSync ? PresentType.Fifo : PresentType.Immediate,
            AllowTearing: !options.WindowVSync,
            MaximumFrameLatency: 2);

    private static void ReconfigureSwapchain(
        IGraphicsBackend backend,
        Swapchain swapchain,
        RuntimeStartupOptions options,
        int width,
        int height)
    {
        ReconfigureStatus status = backend.Reconfigure(
            swapchain,
            CreateSwapchainConfig(options, width, height));
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
        AssetHandle<RenderScene> sceneHandle,
        AssetHandle<ClusterShaders> rendererHandle,
        AssetHandle<Shader> uiShaderHandle,
        BootConfiguration boot,
        bool forceHardwareRaster)
    {
        using var mainWorld = new World(initialEntityCapacity: 2048);
        using var renderWorld = new RenderWorld(initialEntityCapacity: 2048);
        using var extraction = new RenderExtractionSystems(renderWorld);

        RuntimeScene scene;
        using (AssetRead<RenderScene> read = assets.Read(sceneHandle))
        {
            scene = RuntimeWait.Task(
                RuntimeScene.CreateAsync(
                    mainWorld,
                    assets,
                    read.Value).AsTask(),
                window,
                FrameTimeout);
        }

        extraction.Extract(mainWorld);
        RuntimeViewFrame initialView = RuntimeViewFrame.Create(
            scene.Camera.View,
            scene.Projection(boot.Width, boot.Height),
            boot.Width,
            boot.Height,
            temporalFrameIndex: 0);
        Entity viewEntity = renderWorld.CreateEntity(initialView.View);

        using var coordinator = new RenderFrameCoordinator(backend, device, FrameTimeout);
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
        var materialTable = new ClusterMaterialTable();
        var materialSystem = new ClusterMaterialSystem(materialTable);
        var instanceSystem = new ClusterInstanceSystem<ClusterMaterialProducer>(
            cluster,
            ClusterMaterialSystem.EntityQuery(),
            ClusterRenderFeature.InstanceLayout,
            materialTable.CreateProducer());
        var prepareSystems = new RenderPrepareSystems(renderWorld, instances);
        _ = prepareSystems.Add(new ClusterResidencySystem(cluster));
        _ = prepareSystems.Add(materialSystem);
        _ = prepareSystems.Add(instanceSystem);

        var targetMailbox = new ClusterRenderTargetMailbox();
        ClusterRendererSystem renderer = CreateRenderer();
        RenderFrameSystems frameSystems = CreateFrameSystems(renderer);
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
        var graphCompletionSlots = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        var graphCompletions = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        FrameOutputVerifier? outputVerifier = null;
        RuntimeUiRenderer? ui = null;
        var input = new RuntimeInput();
        int width = boot.Width;
        int height = boot.Height;

        ClusterRendererSystem CreateRenderer() => new(
            backend,
            device,
            assets,
            cluster,
            instanceSystem,
            ClusterRenderFeature.InstanceLayout,
            materialTable,
            rendererHandle,
            targetMailbox,
            new ClusterPipelineOptions
            {
                EnableAsyncCompute = options.AsyncCompute,
                ForceHardwareRaster = forceHardwareRaster,
                EnableFrameMetricsReadback = options.VerifyFrameOutput,
            });

        RenderFrameSystems CreateFrameSystems(ClusterRendererSystem system)
        {
            var systems = new RenderFrameSystems(renderWorld, instances);
            _ = systems.Add(system);
            return systems;
        }

        try
        {
            outputVerifier = options.VerifyFrameOutput
                ? new FrameOutputVerifier(backend, device, width, height, PresentationFormat)
                : null;
            ui = new RuntimeUiRenderer(
                backend,
                device,
                window,
                assets,
                uiShaderHandle,
                boot.UiVertexEntry,
                boot.UiPixelEntry,
                PresentationFormat);
            Console.WriteLine(
                $"Runtime configuration '{boot.Title}' loaded from {contentRootLabel(FindContentRoot())}; " +
                $"sceneInstances={scene.MeshInstanceCount}, " +
                $"sceneBounds={scene.MeshPositionMin}..{scene.MeshPositionMax}, " +
                $"renderer={rendererHandle.AssetId}.");
            long runtimeStarted = Stopwatch.GetTimestamp();
            long previousFrameTimestamp = Stopwatch.GetTimestamp();
            int frameIndex = 0;
            bool animateScene = options.DynamicScene;
            bool debugUiOpen = true;
            FrameOutputMetrics? verifiedOutput = null;
            while (options.FrameLimit == 0 || frameIndex < options.FrameLimit)
            {
                bool instanceAdmitted = instances.TryAdmitFrameResources(
                    out _,
                    out QueueCompletion[] instanceRetirementFences);
                bool readbackAdmitted = renderer.TryAdmitFrameResources(
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
                        readbackAdmitted = renderer.TryAdmitFrameResources(
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
                    ReconfigureSwapchain(backend, swapchain, options, clientWidth, clientHeight);
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
                previousFrameTimestamp = currentFrameTimestamp;
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
                renderWorld.Replace(viewEntity, viewFrame.View);
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
                    Console.WriteLine("First frame: extracting scene.");
                if (animateScene)
                {
                    scene.Update(
                        (float)Stopwatch.GetElapsedTime(
                            runtimeStarted,
                            currentFrameTimestamp).TotalSeconds);
                }
                extraction.Extract(mainWorld);
                if (reportMilestones)
                    Console.WriteLine("First frame: preparing streamed meshes.");
                // The receiver and RenderGraph have one coordinator-thread owner. Asset I/O
                // may run asynchronously, but its result is joined here before publishing GPU work.
                ValueTask<ClusterMeshPrepareResult> meshPreparation =
                    cluster.PrepareMeshesAsync(assets);
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
                cluster.PumpStreaming();
                if (reportMilestones)
                    Console.WriteLine("First frame: publishing prepare systems.");
                Prepare(prepareSystems, coordinator);
                if (reportMilestones)
                    Console.WriteLine("First frame: acquiring presentation image.");
                SwapchainImage image = AcquireImage(backend, swapchain, window);
                Texture imageTexture = image.Texture;
                targetMailbox.Publish(new ClusterRenderTarget(imageTexture));
                FrameOutputMetrics? frameOutput = ExecuteFrame(
                    backend,
                    renderGraph,
                    graphicsQueue,
                    graphCompletionSlots,
                    graphCompletions,
                    coordinator,
                    frameSystems,
                    renderer,
                    ui,
                    image,
                    width,
                    height,
                    window,
                    reportMilestones,
                    outputVerifier);
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
                    ReconfigureSwapchain(backend, swapchain, options, width, height);

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
                    ClusterFrameMetrics frameMetrics = renderer.CaptureFrameMetrics();
                    Console.WriteLine(
                        $"Cluster frame {frameMetrics.FrameIndex}: " +
                        $"materials={materialTable.Current.MaterialCount}, " +
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
                        $"shadeBin={frameMetrics.ShadeBinPixels}, " +
                        $"shadePixels={frameMetrics.ShadedPixels}, " +
                        $"deformBins={frameMetrics.BinnedDeformClusters}, " +
                        $"cachedClusters={frameMetrics.CachedDeformClusters}, " +
                        $"swDebug={frameMetrics.SoftwareRasterDebugRecords}, " +
                        $"visProbe={frameMetrics.VisibilityProbePixels}/64, " +
                        $"deformCache={frameMetrics.DeformCacheBytes}/" +
                        $"{frameMetrics.DeformCacheCapacityBytes} bytes.");
                    uint visibleClusters = checked(
                        frameMetrics.PhaseOneSoftwareClusters +
                        frameMetrics.PhaseOneHardwareClusters +
                        frameMetrics.PhaseTwoSoftwareClusters +
                        frameMetrics.PhaseTwoHardwareClusters);
                    bool clusterOutputHealthy =
                        frameMetrics.CandidateCount != 0 &&
                        visibleClusters != 0 &&
                        frameMetrics.ShadedPixels != 0 &&
                        frameMetrics.BinnedDeformClusters != 0 &&
                        frameMetrics.CachedDeformClusters != 0 &&
                        frameMetrics.DeformCacheBytes is > 0 &&
                        frameMetrics.DeformCacheBytes <= frameMetrics.DeformCacheCapacityBytes;
                    if (!output.HasSubstantialCoverage || !clusterOutputHealthy)
                    {
                        throw new InvalidOperationException(
                            "The Cluster frame did not produce substantial visible output together with " +
                            "candidates, visible clusters, deform-cache writes, and shaded pixels.");
                    }
                }
            }
            Console.WriteLine($"SomeEngine runtime completed {frameIndex} frame(s).");
        }
        finally
        {
            outputVerifier?.Dispose();
            coordinator.WaitForTrackedSubmissions();
            ui?.Dispose();
            frameSystems.Dispose();
            Shutdown(prepareSystems, cluster, instances, coordinator);
            scene.Dispose();
            backend.CollectCompleted(device);
        }
    }

    private static void Prepare(
        RenderPrepareSystems systems,
        RenderFrameCoordinator coordinator)
    {
        if (!coordinator.TryBeginPrepare(out RenderPrepareScope? scope))
            throw new InvalidOperationException("The render prepare boundary is not available.");
        using (scope)
        {
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
        ClusterRendererSystem renderer,
        RuntimeUiRenderer ui,
        SwapchainImage image,
        int width,
        int height,
        NativeWindow window,
        bool reportMilestones,
        FrameOutputVerifier? outputVerifier)
    {
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
            bool rendererCommitted = false;
            bool uiCommitted = false;
            completionSlots.AsSpan().Clear();
            try
            {
                RenderGraphFrame graphFrame = graph.BeginFrame();
                try
                {
                    if (reportMilestones)
                        Console.WriteLine("First frame: recording render systems.");
                    GraphTextureId presentation = graphFrame.Import(image, graphicsQueue);
                    systems.Update(frame, graphFrame);
                    ui.Record(ref graphFrame, presentation, width, height);
                    outputVerifier?.Record(ref graphFrame, presentation, graphicsQueue);
                    if (reportMilestones)
                        Console.WriteLine("First frame: analyzing and submitting render graph.");

                    int submittedQueueCount = graphFrame.Execute(completionSlots);
                    int completionCount = CompactCompletions(
                        completionSlots,
                        completions,
                        submittedQueueCount);
                    ReadOnlySpan<QueueCompletion> execution = completions.AsSpan(0, completionCount);
                    renderer.Commit(execution);
                    rendererCommitted = true;
                    ui.Commit(execution);
                    uiCommitted = true;
                    frame.Complete(execution);
                    if (outputVerifier is not null)
                    {
                        Wait(backend, execution, window);
                        frameOutput = outputVerifier.Read();
                    }
                    return frameOutput;
                }
                finally
                {
                    graphFrame.Dispose();
                }
            }
            catch
            {
                int completionCount = CompactCompletions(
                    completionSlots,
                    completions,
                    expectedCount: null);
                if (completionCount != 0)
                {
                    ReadOnlySpan<QueueCompletion> accepted = completions.AsSpan(0, completionCount);
                    if (!rendererCommitted)
                    {
                        renderer.Commit(accepted);
                        rendererCommitted = true;
                    }
                    if (!uiCommitted)
                    {
                        ui.Commit(accepted);
                        uiCommitted = true;
                    }
                    if (!frame.IsClosed) frame.Complete(accepted);
                }
                throw;
            }
            finally
            {
                if (!rendererCommitted) renderer.Discard();
                if (!uiCommitted) ui.Discard();
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
        NativeWindow window)
    {
        foreach (QueueCompletion completion in completions)
            RuntimeWait.Position(backend, completion, window, FrameTimeout);
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

    private static (AssetGuid Shader, string VertexEntry, string PixelEntry) ParseUiShaders(
        IList<ShaderRef>? shaders)
    {
        if (shaders is not { Count: 2 })
            throw new InvalidDataException("Runtime UI requires one vertex and one pixel shader entry.");

        ShaderRef? vertex = null;
        ShaderRef? pixel = null;
        foreach (ShaderRef shader in shaders)
        {
            if (shader is null)
                throw new InvalidDataException("Runtime UI contains a null shader entry.");
            if (shader.Stage == ShaderStage.Vertex && vertex is null)
                vertex = shader;
            else if (shader.Stage == ShaderStage.Pixel && pixel is null)
                pixel = shader;
            else
                throw new InvalidDataException("Runtime UI requires one vertex and one pixel shader entry.");
        }

        AssetGuid vertexGuid = ParseGuid(vertex!.AssetGuid, "UiShaders.Vertex.AssetGuid");
        AssetGuid pixelGuid = ParseGuid(pixel!.AssetGuid, "UiShaders.Pixel.AssetGuid");
        if (vertexGuid != pixelGuid)
            throw new InvalidDataException("Runtime UI shader entries must use one shader asset.");
        if (string.IsNullOrWhiteSpace(vertex.EntryPoint) || string.IsNullOrWhiteSpace(pixel.EntryPoint))
            throw new InvalidDataException("Runtime UI shader entry points are missing.");
        return (vertexGuid, vertex.EntryPoint, pixel.EntryPoint);
    }

    private static string contentRootLabel(string value) => Path.GetFullPath(value);

    private readonly record struct BootConfiguration(
        AssetGuid Scene,
        AssetGuid Renderer,
        AssetGuid UiShader,
        string UiVertexEntry,
        string UiPixelEntry,
        int Width,
        int Height,
        string Title);
}
