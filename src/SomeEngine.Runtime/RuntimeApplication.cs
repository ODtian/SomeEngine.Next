using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.RenderGraph.Diagnostics;
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
            boot = new BootConfiguration(
                ParseGuid(value.SceneGuid, nameof(value.SceneGuid)),
                ParseGuid(value.ClusterRendererGuid, nameof(value.ClusterRendererGuid)),
                ParseGuid(value.UiShaderGuid, nameof(value.UiShaderGuid)),
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
        using IGraphicsBackend backend = CreateBackend(options.DeviceValidation);
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

    private static IGraphicsBackend CreateBackend(bool validation)
    {
        IGraphicsBackend backend = D3D12GraphicsBackend.Create();
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
            throw new NotSupportedException("No Direct3D 12 adapter satisfies the Runtime request.");
        var adapters = new AdapterInfo[requiredCount];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmedCount) ||
            confirmedCount != adapters.Length)
        {
            throw new InvalidOperationException(
                "The Direct3D 12 adapter set changed while Runtime selected a Device.");
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

        var targets = new ClusterRenderTargetSource();
        ClusterRendererSystem renderer = CreateRenderer();
        RenderFrameSystems frameSystems = CreateFrameSystems(renderer);
        FrameOutputVerifier? outputVerifier = null;
        RuntimeUiRenderer? ui = null;
        bool admittedCpuIntervalActive = false;
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
            targets,
            new ClusterPipelineOptions
            {
                EnableAsyncCompute = options.AsyncCompute,
                ForceHardwareRaster = forceHardwareRaster,
                EnableDiagnosticsReadback = options.VerifyFrameOutput,
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
                PresentationFormat);
            Console.WriteLine(
                $"Runtime configuration '{boot.Title}' loaded from {contentRootLabel(FindContentRoot())}; " +
                $"sceneInstances={scene.MeshInstanceCount}, " +
                $"sceneBounds={scene.MeshPositionMin}..{scene.MeshPositionMax}, " +
                $"renderer={rendererHandle.AssetId}.");
            if (options.BenchmarkEnabled)
                ValidateBenchmarkConfiguration(options, device, scene);
            long[] benchmarkCpuTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkDxgiAdmissionTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkResourceAdmissionTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAllocatedBytes = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            int[] benchmarkGen0Collections = options.BenchmarkEnabled
                ? new int[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkDeviceWaitCalls = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkDeviceWaitTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkTaskWaitCalls = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCommandAllocatorCreations = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCommandAllocatorResets = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAdmissionFenceQueries = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAdmissionWaitCalls = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAdmissionBlockingWaitCalls = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAdmissionWaitTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            int[] benchmarkInstanceAvailableGenerations = options.BenchmarkEnabled
                ? new int[options.BenchmarkSampleFrames]
                : [];
            int[] benchmarkReadbackAvailableGenerations = options.BenchmarkEnabled
                ? new int[options.BenchmarkSampleFrames]
                : [];
            int[] benchmarkUiAvailableGenerations = options.BenchmarkEnabled
                ? new int[options.BenchmarkSampleFrames]
                : [];
            int[] benchmarkGraphicsCommandAllocators = options.BenchmarkEnabled
                ? new int[options.BenchmarkSampleFrames]
                : [];
            int[] benchmarkComputeCommandAllocators = options.BenchmarkEnabled
                ? new int[options.BenchmarkSampleFrames]
                : [];
            int[] benchmarkCopyCommandAllocators = options.BenchmarkEnabled
                ? new int[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkFrontendTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkSceneExtractTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkPrepareTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkTargetPublishTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkGraphFrameTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkGraphAuthorTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkGraphCloseTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCompilerContentsTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCompilerLivenessTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCompilerValidationTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCompilerDependencyTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCompilerBarrierTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCompilerPlacementTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCompilerExecutionTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAcquisitionSetupTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAcquisitionHeapTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAcquisitionResourceTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAcquisitionViewTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkAcquisitionBindlessTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCommandEncodingTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCommandSubmitTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkCommandCleanupTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkDiagnosticsTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            long[] benchmarkPresentTicks = options.BenchmarkEnabled
                ? new long[options.BenchmarkSampleFrames]
                : [];
            int benchmarkSampleIndex = 0;
            long runtimeStarted = Stopwatch.GetTimestamp();
            long previousFrameTimestamp = Stopwatch.GetTimestamp();
            int frameIndex = 0;
            bool animateScene = options.DynamicScene;
            bool debugUiOpen = true;
            FrameOutputMetrics? verifiedOutput = null;
            RenderGraphSnapshot? benchmarkGraphBefore = null;
            RenderGraphSnapshot? benchmarkGraphAfter = null;
            int benchmarkSampleEnd = options.BenchmarkEnabled
                ? checked(options.BenchmarkWarmupFrames + options.BenchmarkSampleFrames)
                : 0;
            int benchmarkVerificationFrame = options.BenchmarkEnabled
                ? checked(
                    benchmarkSampleEnd +
                    ((options.BenchmarkSampleFrames & 1) == 0 ? 1 : 0))
                : 0;
            while (options.FrameLimit == 0 || frameIndex < options.FrameLimit)
            {
                long admissionStarted = options.BenchmarkEnabled
                    ? Stopwatch.GetTimestamp()
                    : 0;
                bool measureFrame = options.BenchmarkEnabled &&
                    frameIndex >= options.BenchmarkWarmupFrames &&
                    frameIndex < benchmarkSampleEnd;
                bool collectBenchmarkBreakdown =
                    measureFrame && !options.BenchmarkOuterOnly;
                bool instanceAdmitted = instances.TryAdmitFrameResources(
                    out int instanceAvailableGenerations,
                    out QueueCompletion[] instanceRetirementFences);
                bool readbackAdmitted = renderer.TryAdmitFrameResources(
                    out int readbackAvailableGenerations,
                    out QueueCompletion[] readbackRetirementFences);
                bool uiAdmitted = ui.TryAdmitFrameResources(
                    out int uiAvailableGenerations,
                    out QueueCompletion[] uiRetirementFences);
                long dxgiAdmitted = 0;
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
                            out instanceAvailableGenerations,
                            out instanceRetirementFences);
                    }
                    if (!readbackAdmitted)
                    {
                        readbackAdmitted = renderer.TryAdmitFrameResources(
                            out readbackAvailableGenerations,
                            out readbackRetirementFences);
                    }
                    if (!uiAdmitted)
                    {
                        uiAdmitted = ui.TryAdmitFrameResources(
                            out uiAvailableGenerations,
                            out uiRetirementFences);
                    }
                }
                backend.CollectCompleted(device);
                coordinator.AdmitFrameResources();
                int graphicsCommandAllocators = 0;
                int computeCommandAllocators = 0;
                int copyCommandAllocators = 0;
                long admissionFenceQueries = 0;
                long admissionWaitCalls = 0;
                long admissionBlockingWaitCalls = 0;
                long admissionWaitTicks = 0;
                long resourcesAdmitted = options.BenchmarkEnabled
                    ? Stopwatch.GetTimestamp()
                    : 0;
                long allocatedBefore = 0;
                int gen0Before = 0;
                long cpuStarted = 0;
                if (measureFrame)
                {
                    RuntimeWait.BeginAdmittedCpuInterval();
                    admittedCpuIntervalActive = true;
                    allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
                    gen0Before = GC.CollectionCount(0);
                    cpuStarted = Stopwatch.GetTimestamp();
                }
                input.BeginFrame();
                if (!window.PumpMessages())
                {
                    if (measureFrame)
                    {
                        throw new InvalidOperationException(
                            "The benchmark window closed inside an admitted CPU sample.");
                    }
                    break;
                }
                while (window.TryReadEvent(out NativeWindowEvent windowEvent))
                {
                    input.Process(windowEvent);
                    ui.ProcessEvent(windowEvent, input);
                }
                if (input.WasKeyPressed(RuntimeInput.KeyEscape))
                {
                    if (measureFrame)
                    {
                        throw new InvalidOperationException(
                            "The benchmark received an escape request inside an admitted CPU sample.");
                    }
                    window.RequestClose();
                    continue;
                }
                if (input.WasKeyPressed(RuntimeInput.KeyF1))
                    debugUiOpen = !debugUiOpen;
                if (window.IsMinimized)
                {
                    if (measureFrame)
                    {
                        throw new InvalidOperationException(
                            "The benchmark window became minimized inside an admitted CPU sample.");
                    }
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

                long currentFrameTimestamp =
                    measureFrame && options.BenchmarkOuterOnly
                        ? cpuStarted
                        : Stopwatch.GetTimestamp();
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
                long frontendCompleted = collectBenchmarkBreakdown
                    ? Stopwatch.GetTimestamp()
                    : 0;
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
                long sceneExtractCompleted = collectBenchmarkBreakdown
                    ? Stopwatch.GetTimestamp()
                    : 0;
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
                long prepareCompleted = collectBenchmarkBreakdown
                    ? Stopwatch.GetTimestamp()
                    : 0;
                if (reportMilestones)
                    Console.WriteLine("First frame: acquiring presentation image.");
                SwapchainImage image = AcquireImage(backend, swapchain, window);
                Texture imageTexture = image.Texture;
                if (options.BenchmarkEnabled)
                    dxgiAdmitted = Stopwatch.GetTimestamp();
                targets.Publish(new ClusterRenderTarget(
                    imageTexture,
                    width,
                    height,
                    PresentationFormat));
                long targetPublished = collectBenchmarkBreakdown
                    ? Stopwatch.GetTimestamp()
                    : 0;
                bool captureBenchmarkGraph = options.BenchmarkEnabled &&
                    (frameIndex == options.BenchmarkWarmupFrames - 1 ||
                     frameIndex == benchmarkVerificationFrame);
                long graphStarted = targetPublished;
                RenderGraphSnapshot? benchmarkGraph = ExecuteFrame(
                    backend,
                    device,
                    coordinator,
                    frameSystems,
                    renderer,
                    ui,
                    image,
                    width,
                    height,
                    window,
                    reportMilestones,
                    captureBenchmarkGraph,
                    collectBenchmarkBreakdown,
                    outputVerifier,
                    out FrameOutputMetrics? frameOutput,
                    out long graphAuthorTicks,
                    out InvocationCpuTimings graphTimings);
                long graphCompleted = collectBenchmarkBreakdown
                    ? Stopwatch.GetTimestamp()
                    : 0;
                if (frameIndex == options.BenchmarkWarmupFrames - 1)
                    benchmarkGraphBefore = benchmarkGraph;
                else if (frameIndex == benchmarkVerificationFrame)
                    benchmarkGraphAfter = benchmarkGraph;
                if (frameOutput is FrameOutputMetrics output)
                    verifiedOutput = output;

                if (reportMilestones)
                    Console.WriteLine("First frame: presenting.");
                long diagnosticsCompleted = collectBenchmarkBreakdown
                    ? Stopwatch.GetTimestamp()
                    : 0;
                PresentStatus present = backend.Present(
                    backend.GetQueue(device, QueueType.Graphics),
                    image);
                if (present == PresentStatus.Occluded && measureFrame)
                {
                    throw new InvalidOperationException(
                        "The benchmark swapchain became occluded inside an admitted CPU sample.");
                }
                if (present == PresentStatus.Occluded)
                    window.WaitForEvents(TimeSpan.FromMilliseconds(16));
                else if (present == PresentStatus.OutOfDate)
                    ReconfigureSwapchain(backend, swapchain, options, width, height);

                if (measureFrame)
                {
                    long cpuEnded = Stopwatch.GetTimestamp();
                    long allocatedAfter = GC.GetTotalAllocatedBytes(precise: false);
                    int gen0After = GC.CollectionCount(0);
                    long taskWaitCalls = RuntimeWait.EndAdmittedCpuInterval();
                    long deviceWaitCalls = 0;
                    long blockingDeviceWaitCalls = 0;
                    long deviceWaitTicks = 0;
                    long commandAllocatorCreations = 0;
                    long commandAllocatorResets = 0;
                    admittedCpuIntervalActive = false;
                    if (deviceWaitCalls != 0 ||
                        blockingDeviceWaitCalls != 0 ||
                        taskWaitCalls != 0 ||
                        commandAllocatorCreations != 0 ||
                        commandAllocatorResets != 0)
                    {
                        throw new InvalidOperationException(
                            "The admitted CPU benchmark interval performed a forbidden wait or " +
                            "command-allocator lifecycle operation: " +
                            $"deviceWaitCalls={deviceWaitCalls}, " +
                            $"blockingDeviceWaitCalls={blockingDeviceWaitCalls}, " +
                            $"taskWaitCalls={taskWaitCalls}, " +
                            $"commandAllocatorCreations={commandAllocatorCreations}, " +
                            $"commandAllocatorResets={commandAllocatorResets}.");
                    }

                    int sample = benchmarkSampleIndex++;
                    benchmarkCpuTicks[sample] = cpuEnded - cpuStarted;
                    benchmarkDxgiAdmissionTicks[sample] = dxgiAdmitted == 0
                        ? 0
                        : dxgiAdmitted - prepareCompleted;
                    benchmarkResourceAdmissionTicks[sample] = resourcesAdmitted - admissionStarted;
                    benchmarkAllocatedBytes[sample] = allocatedAfter - allocatedBefore;
                    benchmarkGen0Collections[sample] = gen0After - gen0Before;
                    benchmarkDeviceWaitCalls[sample] = deviceWaitCalls;
                    benchmarkDeviceWaitTicks[sample] = deviceWaitTicks;
                    benchmarkTaskWaitCalls[sample] = taskWaitCalls;
                    benchmarkCommandAllocatorCreations[sample] =
                        commandAllocatorCreations;
                    benchmarkCommandAllocatorResets[sample] =
                        commandAllocatorResets;
                    benchmarkAdmissionFenceQueries[sample] =
                        admissionFenceQueries;
                    benchmarkAdmissionWaitCalls[sample] = admissionWaitCalls;
                    benchmarkAdmissionBlockingWaitCalls[sample] =
                        admissionBlockingWaitCalls;
                    benchmarkAdmissionWaitTicks[sample] = admissionWaitTicks;
                    benchmarkInstanceAvailableGenerations[sample] =
                        instanceAvailableGenerations;
                    benchmarkReadbackAvailableGenerations[sample] =
                        readbackAvailableGenerations;
                    benchmarkUiAvailableGenerations[sample] =
                        uiAvailableGenerations;
                    benchmarkGraphicsCommandAllocators[sample] =
                        graphicsCommandAllocators;
                    benchmarkComputeCommandAllocators[sample] =
                        computeCommandAllocators;
                    benchmarkCopyCommandAllocators[sample] =
                        copyCommandAllocators;
                    if (collectBenchmarkBreakdown)
                    {
                        benchmarkFrontendTicks[sample] =
                            frontendCompleted - cpuStarted;
                        benchmarkSceneExtractTicks[sample] =
                            sceneExtractCompleted - frontendCompleted;
                        benchmarkPrepareTicks[sample] =
                            prepareCompleted - sceneExtractCompleted;
                        benchmarkTargetPublishTicks[sample] =
                            targetPublished - prepareCompleted;
                        benchmarkGraphFrameTicks[sample] =
                            graphCompleted - graphStarted;
                        benchmarkGraphAuthorTicks[sample] = graphAuthorTicks;
                        benchmarkGraphCloseTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Close);
                        benchmarkCompilerContentsTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Compiler.Contents);
                        benchmarkCompilerLivenessTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Compiler.Liveness);
                        benchmarkCompilerValidationTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Compiler.Validation);
                        benchmarkCompilerDependencyTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Compiler.Dependencies);
                        benchmarkCompilerBarrierTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Compiler.Barrier);
                        benchmarkCompilerPlacementTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Compiler.Placement);
                        benchmarkCompilerExecutionTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Compiler.Execution);
                        benchmarkAcquisitionSetupTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Acquisition.Setup);
                        benchmarkAcquisitionHeapTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Acquisition.Heaps);
                        benchmarkAcquisitionResourceTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Acquisition.Resources);
                        benchmarkAcquisitionViewTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Acquisition.Views);
                        benchmarkAcquisitionBindlessTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Acquisition.Bindless);
                        benchmarkCommandEncodingTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Commands.Encoding);
                        benchmarkCommandSubmitTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Commands.Submit);
                        benchmarkCommandCleanupTicks[sample] =
                            DurationToStopwatchTicks(graphTimings.Commands.Cleanup);
                        benchmarkDiagnosticsTicks[sample] =
                            diagnosticsCompleted - graphCompleted;
                        benchmarkPresentTicks[sample] =
                            cpuEnded - diagnosticsCompleted;
                    }
                }
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
                    ClusterFrameDiagnostics frameDiagnostics = renderer.CaptureFrameDiagnostics();
                    Console.WriteLine(
                        $"Cluster frame {frameDiagnostics.FrameIndex}: " +
                        $"materials={materialTable.Current.MaterialCount}, " +
                        $"candidates={frameDiagnostics.CandidateCount}/" +
                        $"{frameDiagnostics.CandidateDispatchGroups} groups, " +
                        $"phase1={frameDiagnostics.PhaseOneSoftwareClusters} SW+" +
                        $"{frameDiagnostics.PhaseOneHardwareClusters} HW, " +
                        $"phase2Candidates={frameDiagnostics.PhaseTwoCandidateCount}/" +
                        $"{frameDiagnostics.PhaseTwoDispatchGroups} groups, " +
                        $"phase2={frameDiagnostics.PhaseTwoSoftwareClusters} SW+" +
                        $"{frameDiagnostics.PhaseTwoHardwareClusters} HW, " +
                        $"rasterBatches={frameDiagnostics.RasterBatches} total/" +
                        $"{frameDiagnostics.SoftwareRasterBatches} SW, " +
                        $"shadePixels={frameDiagnostics.ShadedPixels}, " +
                        $"deformBins={frameDiagnostics.BinnedDeformClusters}, " +
                        $"cachedClusters={frameDiagnostics.CachedDeformClusters}, " +
                        $"deformCache={frameDiagnostics.DeformCacheBytes}/" +
                        $"{frameDiagnostics.DeformCacheCapacityBytes} bytes.");
                    uint visibleClusters = checked(
                        frameDiagnostics.PhaseOneSoftwareClusters +
                        frameDiagnostics.PhaseOneHardwareClusters +
                        frameDiagnostics.PhaseTwoSoftwareClusters +
                        frameDiagnostics.PhaseTwoHardwareClusters);
                    bool clusterOutputHealthy =
                        frameDiagnostics.CandidateCount != 0 &&
                        visibleClusters != 0 &&
                        frameDiagnostics.ShadedPixels != 0 &&
                        frameDiagnostics.BinnedDeformClusters != 0 &&
                        frameDiagnostics.CachedDeformClusters != 0 &&
                        frameDiagnostics.DeformCacheBytes is > 0 &&
                        frameDiagnostics.DeformCacheBytes <= frameDiagnostics.DeformCacheCapacityBytes;
                    if (!output.HasSubstantialCoverage || !clusterOutputHealthy)
                    {
                        throw new InvalidOperationException(
                            "The Cluster frame did not produce substantial visible output together with " +
                            "candidates, visible clusters, deform-cache writes, and shaded pixels.");
                    }
                }
            }
            if (options.BenchmarkEnabled)
            {
                if (benchmarkSampleIndex != options.BenchmarkSampleFrames)
                {
                    throw new InvalidOperationException(
                        $"The benchmark captured {benchmarkSampleIndex} samples; " +
                        $"{options.BenchmarkSampleFrames} were required.");
                }
                WriteAndValidateBenchmarkGraphs(
                    options,
                    benchmarkGraphBefore,
                    benchmarkGraphAfter);
                WriteBenchmark(
                    options,
                    device,
                    scene,
                    benchmarkCpuTicks,
                    benchmarkDxgiAdmissionTicks,
                    benchmarkResourceAdmissionTicks,
                    benchmarkAllocatedBytes,
                    benchmarkGen0Collections,
                    benchmarkDeviceWaitCalls,
                    benchmarkDeviceWaitTicks,
                    benchmarkTaskWaitCalls,
                    benchmarkCommandAllocatorCreations,
                    benchmarkCommandAllocatorResets,
                    benchmarkAdmissionFenceQueries,
                    benchmarkAdmissionWaitCalls,
                    benchmarkAdmissionBlockingWaitCalls,
                    benchmarkAdmissionWaitTicks,
                    benchmarkInstanceAvailableGenerations,
                    benchmarkReadbackAvailableGenerations,
                    benchmarkUiAvailableGenerations,
                    benchmarkGraphicsCommandAllocators,
                    benchmarkComputeCommandAllocators,
                    benchmarkCopyCommandAllocators,
                    benchmarkFrontendTicks,
                    benchmarkSceneExtractTicks,
                    benchmarkPrepareTicks,
                    benchmarkTargetPublishTicks,
                    benchmarkGraphFrameTicks,
                    benchmarkGraphAuthorTicks,
                    benchmarkGraphCloseTicks,
                    benchmarkCompilerContentsTicks,
                    benchmarkCompilerLivenessTicks,
                    benchmarkCompilerValidationTicks,
                    benchmarkCompilerDependencyTicks,
                    benchmarkCompilerBarrierTicks,
                    benchmarkCompilerPlacementTicks,
                    benchmarkCompilerExecutionTicks,
                    benchmarkAcquisitionSetupTicks,
                    benchmarkAcquisitionHeapTicks,
                    benchmarkAcquisitionResourceTicks,
                    benchmarkAcquisitionViewTicks,
                    benchmarkAcquisitionBindlessTicks,
                    benchmarkCommandEncodingTicks,
                    benchmarkCommandSubmitTicks,
                    benchmarkCommandCleanupTicks,
                    benchmarkDiagnosticsTicks,
                    benchmarkPresentTicks);
            }
            Console.WriteLine($"SomeEngine runtime completed {frameIndex} frame(s).");
        }
        finally
        {
            if (admittedCpuIntervalActive)
                _ = RuntimeWait.EndAdmittedCpuInterval();
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

    private static RenderGraphSnapshot? ExecuteFrame(
        IGraphicsBackend backend,
        Device device,
        RenderFrameCoordinator coordinator,
        RenderFrameSystems systems,
        ClusterRendererSystem renderer,
        RuntimeUiRenderer ui,
        SwapchainImage image,
        int width,
        int height,
        NativeWindow window,
        bool reportMilestones,
        bool captureGraphSnapshot,
        bool collectBenchmarkTimings,
        FrameOutputVerifier? outputVerifier,
        out FrameOutputMetrics? frameOutput,
        out long graphAuthorTicks,
        out InvocationCpuTimings graphTimings)
    {
        graphAuthorTicks = 0;
        graphTimings = default;
        frameOutput = null;
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
            using global::SomeEngine.RenderGraph.RenderGraph graph = new(backend, device);
            bool rendererCommitted = false;
            bool uiCommitted = false;
            try
            {
                long authorStarted = collectBenchmarkTimings
                    ? Stopwatch.GetTimestamp()
                    : 0;
                if (reportMilestones)
                    Console.WriteLine("First frame: recording render systems.");
                TextureHandle presentation = graph.Import(image);
                systems.Update(frame, graph);
                ui.Record(graph, image.Texture, width, height);
                outputVerifier?.Record(graph, presentation);
                if (collectBenchmarkTimings)
                    graphAuthorTicks = Stopwatch.GetTimestamp() - authorStarted;
                if (reportMilestones)
                    Console.WriteLine("First frame: compiling and submitting render graph.");
                RenderGraphSnapshot? snapshot = null;
                QueueCompletion[] execution;
                if (captureGraphSnapshot)
                {
                    execution = graph.ExecuteWithSnapshot(out snapshot);
                }
                else if (collectBenchmarkTimings)
                {
                    execution = graph.ExecuteForBenchmark(out graphTimings);
                }
                else
                {
                    execution = graph.Execute();
                }
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
                return snapshot;
            }
            catch (RenderGraphExecutionException failure)
            {
                if (!frame.IsClosed && failure.PublishedFences.Length != 0)
                {
                    frame.Complete(failure.PublishedFences);
                    Wait(backend, failure.PublishedFences, window);
                }
                throw;
            }
            finally
            {
                if (!rendererCommitted)
                    renderer.Discard();
                if (!uiCommitted)
                    ui.Discard();
            }
        }
    }

    private static void WriteAndValidateBenchmarkGraphs(
        RuntimeStartupOptions options,
        RenderGraphSnapshot? before,
        RenderGraphSnapshot? after)
    {
        if (before is null || after is null)
        {
            throw new InvalidOperationException(
                "The benchmark did not capture both excluded boundary graph snapshots.");
        }
        string output = Path.GetFullPath(options.BenchmarkOutput!);
        string? directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(
            output + ".graph-before.json",
            RenderGraphSnapshotJson.Serialize(before));
        File.WriteAllText(
            output + ".graph-after.json",
            RenderGraphSnapshotJson.Serialize(after));
        IReadOnlyList<string> differences = RenderGraphSnapshotDiff.Compare(
            before,
            after,
            compareQueuePositionValues: false);
        File.WriteAllLines(output + ".graph-diff.txt", differences);
        if (differences.Count != 0)
        {
            throw new InvalidOperationException(
                "The canonical Runtime graph changed across the measured interval: " +
                string.Join("; ", differences));
        }
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

    private static void ValidateBenchmarkConfiguration(
        RuntimeStartupOptions options,
        Device device,
        RuntimeScene scene)
    {
        RequireDisabledOptimizationsForLoadedEngineAssemblies();

        RequireDisabledRuntimeFeature(
            "tiered compilation",
            "DOTNET_TieredCompilation",
            "COMPlus_TieredCompilation");
        RequireDisabledRuntimeFeature(
            "ReadyToRun",
            "DOTNET_ReadyToRun",
            "COMPlus_ReadyToRun");

        if (!device.Adapter.HardwareAccelerated ||
            !device.Adapter.Name.Contains("RTX 3080", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The benchmark requires a hardware RTX 3080; the active adapter is '{device.Adapter.Name}'.");
        }
        if (options.DeviceValidation)
            throw new InvalidOperationException("The benchmark requires D3D12 validation to be disabled.");
        if (scene.MeshInstanceCount != 1_024)
        {
            throw new InvalidOperationException(
                $"The benchmark requires exactly 1,024 scene instances; found {scene.MeshInstanceCount}.");
        }
        if (!options.DynamicScene)
            throw new InvalidOperationException("The benchmark requires the dynamic Default Runtime scene.");
        if (!options.AsyncCompute)
            throw new InvalidOperationException("The benchmark requires async compute to remain enabled.");
        if (!options.WindowVSync || options.PresentSyncInterval != 1)
            throw new InvalidOperationException("The benchmark requires FIFO Present(1).");
        if (options.SkipSwapchainPresent)
            throw new InvalidOperationException("The benchmark cannot skip swapchain presentation.");
        if (options.VerifyFrameOutput)
        {
            throw new InvalidOperationException(
                "Per-frame output readback waits are excluded from the performance process; " +
                "use the benchmark's boundary output verification instead.");
        }
        if (options.RenderDocCapture is not null)
            throw new InvalidOperationException("RenderDoc capture must be disabled during benchmark sampling.");
        if (options.Profiler.EnableTracy)
            throw new InvalidOperationException("The benchmark requires the external profiler to be disabled.");
    }

    private static void RequireDisabledOptimizationsForLoadedEngineAssemblies()
    {
        var invalid = new List<string>();
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name = assembly.GetName().Name;
            if (name is null ||
                !name.StartsWith("SomeEngine.", StringComparison.Ordinal))
            {
                continue;
            }
            DebuggableAttribute? debugging =
                assembly.GetCustomAttribute<DebuggableAttribute>();
            if (debugging is not null &&
                (debugging.DebuggingFlags &
                 DebuggableAttribute.DebuggingModes.DisableOptimizations) != 0)
            {
                continue;
            }
            invalid.Add(name);
        }
        if (invalid.Count == 0)
            return;
        invalid.Sort(StringComparer.Ordinal);
        throw new InvalidOperationException(
            "The benchmark requires every loaded SomeEngine assembly to be built with " +
            $"Optimize=false; invalid assemblies: {string.Join(", ", invalid)}.");
    }

    private static void RequireDisabledRuntimeFeature(
        string feature,
        string dotnetVariable,
        string compatibilityVariable)
    {
        string? dotnet = Environment.GetEnvironmentVariable(dotnetVariable);
        string? compatibility = Environment.GetEnvironmentVariable(compatibilityVariable);
        if ((dotnet is null && compatibility is null) ||
            (dotnet is not null && !string.Equals(dotnet, "0", StringComparison.Ordinal)) ||
            (compatibility is not null &&
                !string.Equals(compatibility, "0", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The benchmark requires {feature} to be explicitly disabled with " +
                $"{dotnetVariable}=0 or {compatibilityVariable}=0.");
        }
    }

    private static void WriteBenchmark(
        RuntimeStartupOptions options,
        Device device,
        RuntimeScene scene,
        long[] cpuTicks,
        long[] dxgiAdmissionTicks,
        long[] resourceAdmissionTicks,
        long[] allocatedBytes,
        int[] gen0Collections,
        long[] deviceWaitCalls,
        long[] deviceWaitTicks,
        long[] taskWaitCalls,
        long[] commandAllocatorCreations,
        long[] commandAllocatorResets,
        long[] admissionFenceQueries,
        long[] admissionWaitCalls,
        long[] admissionBlockingWaitCalls,
        long[] admissionWaitTicks,
        int[] instanceAvailableGenerations,
        int[] readbackAvailableGenerations,
        int[] uiAvailableGenerations,
        int[] graphicsCommandAllocators,
        int[] computeCommandAllocators,
        int[] copyCommandAllocators,
        long[] frontendTicks,
        long[] sceneExtractTicks,
        long[] prepareTicks,
        long[] targetPublishTicks,
        long[] graphFrameTicks,
        long[] graphAuthorTicks,
        long[] graphCloseTicks,
        long[] compilerContentsTicks,
        long[] compilerLivenessTicks,
        long[] compilerValidationTicks,
        long[] compilerDependencyTicks,
        long[] compilerBarrierTicks,
        long[] compilerPlacementTicks,
        long[] compilerExecutionTicks,
        long[] acquisitionSetupTicks,
        long[] acquisitionHeapTicks,
        long[] acquisitionResourceTicks,
        long[] acquisitionViewTicks,
        long[] acquisitionBindlessTicks,
        long[] commandEncodingTicks,
        long[] commandSubmitTicks,
        long[] commandCleanupTicks,
        long[] diagnosticsTicks,
        long[] presentTicks)
    {
        string[] criticalPathNames =
        [
            "frontend_ticks",
            "scene_extract_ticks",
            "prepare_ticks",
            "target_publish_ticks",
            "graph_frame_ticks",
            "graph_author_ticks",
            "graph_close_ticks",
            "compiler_contents_ticks",
            "compiler_liveness_ticks",
            "compiler_validation_ticks",
            "compiler_dependencies_ticks",
            "compiler_barriers_ticks",
            "compiler_placement_ticks",
            "compiler_execution_ticks",
            "acquisition_setup_ticks",
            "acquisition_heaps_ticks",
            "acquisition_resources_ticks",
            "acquisition_views_ticks",
            "acquisition_bindless_ticks",
            "command_encoding_ticks",
            "command_submit_ticks",
            "command_cleanup_ticks",
            "diagnostics_ticks",
            "present_ticks",
        ];
        long[][] criticalPathColumns =
        [
            frontendTicks,
            sceneExtractTicks,
            prepareTicks,
            targetPublishTicks,
            graphFrameTicks,
            graphAuthorTicks,
            graphCloseTicks,
            compilerContentsTicks,
            compilerLivenessTicks,
            compilerValidationTicks,
            compilerDependencyTicks,
            compilerBarrierTicks,
            compilerPlacementTicks,
            compilerExecutionTicks,
            acquisitionSetupTicks,
            acquisitionHeapTicks,
            acquisitionResourceTicks,
            acquisitionViewTicks,
            acquisitionBindlessTicks,
            commandEncodingTicks,
            commandSubmitTicks,
            commandCleanupTicks,
            diagnosticsTicks,
            presentTicks,
        ];
        string path = Path.GetFullPath(options.BenchmarkOutput!);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using (var writer = new StreamWriter(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.WriteLine("# schema=SomeEngine.Runtime.CpuEndToEnd.v2");
            writer.WriteLine($"# timestamp_utc={DateTimeOffset.UtcNow:O}");
            writer.WriteLine($"# stopwatch_frequency={Stopwatch.Frequency}");
            writer.WriteLine($"# process_id={Environment.ProcessId}");
            writer.WriteLine($"# adapter={device.Adapter.Name}");
            writer.WriteLine($"# driver={device.Adapter.DriverVersion}");
            writer.WriteLine("# api=Direct3D12");
            writer.WriteLine($"# scene_instances={scene.MeshInstanceCount}");
            writer.WriteLine($"# warmup_frames={options.BenchmarkWarmupFrames}");
            writer.WriteLine($"# sample_frames={options.BenchmarkSampleFrames}");
            writer.WriteLine(
                $"# timing_mode={(options.BenchmarkOuterOnly ? "outer-only" : "breakdown")}");
            writer.WriteLine("# configuration=Debug;Optimize=false;TieredCompilation=0;" +
                "ReadyToRun=0;FIFO Present(1);buffers=3;maximum_frame_latency=2;" +
                "dynamic_scene=true;async_compute=true");
            writer.Write(
                "sample,cpu_ticks,cpu_ms,dxgi_admission_ticks,dxgi_admission_ms," +
                "resource_admission_ticks,resource_admission_ms,allocated_bytes,gen0_collections," +
                "device_wait_calls,device_wait_ticks,task_wait_calls," +
                "command_allocator_creations,command_allocator_resets," +
                "admission_fence_queries,admission_wait_calls," +
                "admission_blocking_wait_calls,admission_wait_ticks," +
                "instance_available_generations,readback_available_generations," +
                "ui_available_generations,graphics_available_command_allocators," +
                "compute_available_command_allocators,copy_available_command_allocators");
            foreach (string name in criticalPathNames)
            {
                writer.Write(',');
                writer.Write(name);
            }
            writer.WriteLine();
            for (int index = 0; index < cpuTicks.Length; index++)
            {
                writer.Write(index.ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(cpuTicks[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(TicksToMilliseconds(cpuTicks[index]).ToString("R", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(dxgiAdmissionTicks[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(TicksToMilliseconds(dxgiAdmissionTicks[index]).ToString("R", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(resourceAdmissionTicks[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(TicksToMilliseconds(resourceAdmissionTicks[index]).ToString("R", CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(allocatedBytes[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(gen0Collections[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(deviceWaitCalls[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(deviceWaitTicks[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(taskWaitCalls[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(commandAllocatorCreations[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(commandAllocatorResets[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(admissionFenceQueries[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(admissionWaitCalls[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(admissionBlockingWaitCalls[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(admissionWaitTicks[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(instanceAvailableGenerations[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(readbackAvailableGenerations[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(uiAvailableGenerations[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(graphicsCommandAllocators[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(computeCommandAllocators[index].ToString(CultureInfo.InvariantCulture));
                writer.Write(',');
                writer.Write(copyCommandAllocators[index].ToString(CultureInfo.InvariantCulture));
                foreach (long[] column in criticalPathColumns)
                {
                    writer.Write(',');
                    writer.Write(column[index].ToString(CultureInfo.InvariantCulture));
                }
                writer.WriteLine();
            }
        }

        long[] ordered = (long[])cpuTicks.Clone();
        Array.Sort(ordered);
        double p50 = TicksToMilliseconds(Percentile(ordered, 0.50));
        double p95 = TicksToMilliseconds(Percentile(ordered, 0.95));
        double p99 = TicksToMilliseconds(Percentile(ordered, 0.99));
        double maximum = TicksToMilliseconds(ordered[^1]);
        long thresholdTicks = checked((Stopwatch.Frequency + 999L) / 1_000L);
        int failures = 0;
        foreach (long sample in cpuTicks)
        {
            if (sample >= thresholdTicks)
                failures++;
        }
        Console.WriteLine(
            $"CPU E2E raw benchmark: samples={cpuTicks.Length}, " +
            $"p50={p50:F4} ms, p95={p95:F4} ms, p99={p99:F4} ms, " +
            $"max={maximum:F4} ms, >=1ms={failures}, output={path}.");
    }

    private static long Percentile(long[] ordered, double percentile)
    {
        int index = checked((int)Math.Ceiling(percentile * ordered.Length) - 1);
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static double TicksToMilliseconds(long ticks) =>
        ticks * (1_000.0 / Stopwatch.Frequency);

    private static long DurationToStopwatchTicks(TimeSpan elapsed) =>
        checked((long)Math.Round(
            elapsed.TotalSeconds * Stopwatch.Frequency,
            MidpointRounding.AwayFromZero));

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

    private static string contentRootLabel(string value) => Path.GetFullPath(value);

    private readonly record struct BootConfiguration(
        AssetGuid Scene,
        AssetGuid Renderer,
        AssetGuid UiShader,
        int Width,
        int Height,
        string Title);
}
