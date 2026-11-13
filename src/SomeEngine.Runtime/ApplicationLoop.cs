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

internal static class ApplicationLoop
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(60);
    private const Format PresentationFormat = Format.R8G8B8A8UNorm;

    internal static Task<int> RunAsync<TMaterials>(ApplicationOptions options, bool useWarp, ApplicationStartupTiming startup,
        Func<Material, float, bool>? animateMaterial = null, ApplicationScene? application = null, SceneScript? script = null)
        where TMaterials : ISceneMaterials => Task.Factory.StartNew(() =>
    {
        var display = new ApplicationDisplay();
        ApplicationContent? content = null;
        ApplicationRenderer? renderer = null;
        ApplicationSceneContent? readyScene = null;
        int frames = 0;
        Exception? runError = null;
        try
        {
            do
            {
                if (script is null && application is not null) options = application.Options;
                if (application?.ReloadRequested == true)
                {
                    renderer?.Dispose();
                    renderer = null;
                    application.ReloadRequested = false;
                }
                if (renderer is null) application?.TakeSceneRequest();
                readyScene = renderer?.NextScene;
                if (renderer is not null) renderer.NextScene = null;
                string contentRoot = Path.GetFullPath(script?.ContentDirectory ?? application?.ContentDirectory ?? FindContentRoot());
                SlangCompileTarget target = options.GraphicsBackend == ApplicationGraphicsBackend.Vulkan
                    ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil;
                ShaderLibrary shaderLibrary = ApplicationShaders.Get(target);
                if (readyScene is not null) content = readyScene.Content;
                else
                {
                    AssetManifest manifest = AssetManifest.Load(Path.Combine(contentRoot, "Library", "AssetManifest"));
                    var files = SomeEngine.Assets.Importers.EngineShaderContent.OpenFiles(contentRoot, manifest);
                    content = new ApplicationContent(contentRoot, files, manifest);
                }
                try
                {
                    application?.SceneStarted?.Invoke(Stopwatch.GetTimestamp());
                    int sceneFrames = RunScene<TMaterials>(options, useWarp, startup, animateMaterial, application, display, script, content, shaderLibrary, ref renderer, readyScene);
                    content = null;
                    readyScene = null;
                    frames += sceneFrames;
                    startup.FinishTrace();
                    application?.SceneCompleted?.Invoke(new(ApplicationFailure.None, sceneFrames) { Startup = startup.Result });
                }
                catch (Exception error)
                {
                    application?.SceneCompleted?.Invoke(new(ApplicationRunException.Classify(error, ApplicationFailure.ContentLoad),
                        error is ApplicationRunException completed ? completed.CompletedFrames : 0, error) { Startup = startup.Result });
                    throw new ApplicationRunException(ApplicationRunException.Classify(error, ApplicationFailure.ContentLoad),
                        frames + (error is ApplicationRunException run ? run.CompletedFrames : 0), error);
                }
            } while (application?.ReloadRequested == true || renderer?.NextScene is not null);
            return frames;
        }
        catch (Exception error)
        {
            runError = error;
            throw;
        }
        finally
        {
            List<Exception>? errors = null;
            CollectShutdownError(() => renderer?.Dispose(), ref errors);
            CollectShutdownError(() => content?.Files.DisposeAsync().AsTask().GetAwaiter().GetResult(), ref errors);
            CollectShutdownError(display.Dispose, ref errors);
            if (errors is not null)
            {
                if (runError is not null) errors.Insert(0, runError);
                var failure = new AggregateException("Application execution and shutdown failed.", errors);
                var run = runError as ApplicationRunException;
                throw new ApplicationRunException(
                    run?.Failure ?? ApplicationRunException.Classify(failure, ApplicationFailure.Startup),
                    run?.CompletedFrames ?? frames, failure);
            }
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void CollectShutdownError(Action shutdown, ref List<Exception>? errors)
    {
        try { shutdown(); }
        catch (Exception error) { (errors ??= []).Add(error); }
    }

    private static int RunScene<TMaterials>(ApplicationOptions options, bool useWarp, ApplicationStartupTiming startup,
        Func<Material, float, bool>? animateMaterial, ApplicationScene? application,
        ApplicationDisplay display, SceneScript? script, ApplicationContent content, ShaderLibrary shaderLibrary, ref ApplicationRenderer? renderer, ApplicationSceneContent? readyScene) where TMaterials : ISceneMaterials
    {
        ArgumentNullException.ThrowIfNull(options);
        AssetLoader assets = content.Files;
        var textureValues = new SomeEngine.Core.Collections.WeakValues<AssetResourceKey<TextureAsset>, SomeEngine.Resources.Texture>();
        AssetManifest manifest = content.Manifest;
        IReadOnlyList<AssetEntry> runtimeRecords =
            manifest.List(ApplicationSettingsAsset.FileType);
        if (runtimeRecords.Count != 1)
        {
            throw new InvalidDataException(
                $"The runtime publication must contain exactly one {ApplicationSettingsAsset.FileType} " +
                $"asset; found {runtimeRecords.Count}.");
        }

        using ApplicationSettingsAsset settings = display.Wait(ApplicationSettingsAsset.OpenAsync(assets,
            new AssetId<ApplicationSettingsAsset>(runtimeRecords[0].AssetGuid)).AsTask());
        ApplicationSettingsBlock boot = display.Wait(settings.GetBlockAsync<ApplicationSettingsBlock>().AsTask());
        string title = settings.Name;
        Task<WorldAsset> sceneTask = WorldAsset.OpenAsync(assets, boot.Scene).AsTask();
        Task<ClusterShaders> pipelineTask = CreateClusterShadersAsync().AsTask();
        Task<VirtualShadowMapShaders> virtualShadowTask = CreateShadowShadersAsync().AsTask();
        Task<Shader> uiShaderTask = shaderLibrary.GetAsync(assets, boot.UiShader).AsTask();
        Task<Shader> presentationShaderTask = shaderLibrary.GetAsync(assets, boot.PresentationShader).AsTask();
        Task<Shader> lightingShaderTask = shaderLibrary.GetAsync(assets, boot.LightingShader).AsTask();
        Task<Shader> temporalShaderTask = shaderLibrary.GetAsync(assets, boot.TemporalShader).AsTask();
        Task<World> worldTask = readyScene is not null ? Task.FromResult(readyScene.World) : Task.Run(async () =>
        {
            long started = Stopwatch.GetTimestamp();
            WorldAsset sceneAsset = await sceneTask.ConfigureAwait(false);
            World world = await sceneAsset.LoadAsync(SceneSerialization.CreateRegistry(),
                new(Execution: new WorldJobs(JobSystem.Default))).ConfigureAwait(false);
            startup.RecordWork("WorldLoad", started);
            return world;
        });
        Task<SceneWorldData> sceneResourcesTask = readyScene is not null ? Task.FromResult(readyScene.Scene) : Task.Run(async () =>
        {
            World world = await worldTask.ConfigureAwait(false);
            long started = Stopwatch.GetTimestamp();
            SceneWorldData scene = await SceneWorldData.CreateAsync<TMaterials>(world, content, shaderLibrary).ConfigureAwait(false);
            startup.RecordWork("SceneResources", started);
            return scene;
        });

        async ValueTask<ClusterShaders> CreateClusterShadersAsync()
        {
            await using ClusterShadersAsset file = await ClusterShadersAsset.OpenAsync(
                assets,
                boot.Pipeline).ConfigureAwait(false);
            ClusterShadersBlock shaders = await file.GetBlockAsync<ClusterShadersBlock>()
                .ConfigureAwait(false);
            Dictionary<AssetId<ShaderAsset>, Shader> references = await ShaderRef.LoadAsync(
                shaders.References(), assets, shaderLibrary).ConfigureAwait(false);
            return shaders.Create(file.Name, references);
        }
        async ValueTask<VirtualShadowMapShaders> CreateShadowShadersAsync()
        {
            await using VirtualShadowMapShadersAsset file = await VirtualShadowMapShadersAsset.OpenAsync(
                assets,
                boot.VirtualShadowMap).ConfigureAwait(false);
            VirtualShadowMapShadersBlock shaders = await file.GetBlockAsync<VirtualShadowMapShadersBlock>()
                .ConfigureAwait(false);
            Dictionary<AssetId<ShaderAsset>, Shader> references = await ShaderRef.LoadAsync(
                shaders.GetShaderAssetIds(), assets, shaderLibrary).ConfigureAwait(false);
            SomeEngine.Resources.Texture? blueNoise = shaders.BlueNoise is { } texture
                ? await TextureAsset.GetAsync(assets, texture, textureValues).ConfigureAwait(false) : null;
            return shaders.Create(file.Name, references, blueNoise);
        }
        Task contentReads = Task.WhenAll(
            sceneTask,
            pipelineTask,
            virtualShadowTask,
            uiShaderTask,
            presentationShaderTask,
            lightingShaderTask,
            temporalShaderTask,
            worldTask,
            sceneResourcesTask);
        try
        {
            startup.RecordStage("ContentReadsStarted");
            using (EngineTrace.Begin(ApplicationTrace.DeviceCreation))
                display.Initialize(options, useWarp, title, boot);
            startup.RecordStage("DeviceCreation");
            using (EngineTrace.Begin(ApplicationTrace.ContentWait))
                display.Wait(contentReads);
            startup.RecordStage("ContentWait");
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            try { contentReads.GetAwaiter().GetResult(); }
            catch (Exception readFailure)
            {
                if (!ReferenceEquals(readFailure, failure)) errors.Add(readFailure);
            }
            CloseCompleted(sceneTask, errors);
            CloseCompleted(sceneResourcesTask, errors);
            CloseCompleted(worldTask, errors);
            if (errors.Count != 1)
                throw new AggregateException("Application content creation and cleanup failed.", errors);
            throw;
        }

        static void CloseCompleted<T>(Task<T> task, List<Exception> errors) where T : IDisposable
        {
            if (!task.IsCompletedSuccessfully) return;
            try { task.Result.Dispose(); }
            catch (Exception cleanup) { errors.Add(cleanup); }
        }

        ClusterShaders clusterShaders = pipelineTask.Result;
        VirtualShadowMapShaders shadowShaders = virtualShadowTask.Result;
        Shader uiCode = uiShaderTask.Result;
        Shader presentationCode = presentationShaderTask.Result;
        Shader lightingCode = lightingShaderTask.Result;
        Shader temporalCode = temporalShaderTask.Result;
        using WorldAsset sceneFile = sceneTask.Result;

        if (display.Window is not null && !display.Window.PumpMessages())
        {
            sceneResourcesTask.Result.Dispose();
            worldTask.Result.Dispose();
            return 0;
        }
        return RunRenderer(
            ref renderer,
            options,
            startup,
            display.Backend,
            display.Device,
            display.Swapchain,
            display.Window!,
            worldTask.Result,
            sceneResourcesTask.Result,
            pipelineTask.Result,
            virtualShadowTask.Result,
            uiShaderTask.Result,
            presentationShaderTask.Result,
            lightingShaderTask.Result,
            temporalShaderTask.Result,
            shaderLibrary,
            boot,
            title,
            forceHardwareRaster: useWarp || options.ForceHardwareRaster, animateMaterial, application, script,
            readyScene ?? new ApplicationSceneContent(content, worldTask.Result, sceneResourcesTask.Result) { Script = script },
            directory => ApplicationSceneContent.LoadAsync<TMaterials>(directory, shaderLibrary));
    }

    private sealed class ApplicationDisplay : IDisposable
    {
        internal NativeWindow Window { get; private set; } = null!;
        internal IGraphicsBackend Backend { get; private set; } = null!;
        internal Surface Surface { get; private set; } = null!;
        internal Device Device { get; private set; } = null!;
        internal Swapchain Swapchain { get; private set; } = null!;
        private bool _windowVSync;
        private bool _meshShadersDisabled;
        private bool _validation;
        private ApplicationGraphicsBackend _graphicsBackend;

        internal void Initialize(ApplicationOptions options, bool useWarp, string title, ApplicationSettingsBlock boot)
        {
            if (Window is not null && _meshShadersDisabled == options.DisableMeshShaders &&
                _validation == options.DeviceValidation && _graphicsBackend == options.GraphicsBackend)
            {
                if (_windowVSync != options.WindowVSync)
                    ReconfigureSwapchain(Backend, Swapchain, options, Window.ClientWidth, Window.ClientHeight,
                        Device.Adapter.HardwareAccelerated);
                _windowVSync = options.WindowVSync;
                return;
            }
            _windowVSync = options.WindowVSync;
            _meshShadersDisabled = options.DisableMeshShaders;
            _validation = options.DeviceValidation;
            _graphicsBackend = options.GraphicsBackend;
            if (Window is not null)
            {
                Swapchain.Dispose();
                Swapchain = null!;
                Device.Dispose();
                Device = null!;
                Surface.Dispose();
                Surface = null!;
                Backend.Dispose();
                Backend = null!;
            }
            else Window = new NativeWindow(title, boot.Width, boot.Height, visible: !options.HideWindow);
            if (useWarp && options.GraphicsBackend != ApplicationGraphicsBackend.Direct3D12)
                throw new ArgumentException("The WARP adapter is available only with the D3D12 backend.");
            Backend = CreateBackend(
                options.GraphicsBackend,
                options.DeviceValidation);
            AdapterId adapterId = useWarp ? SelectAdapter(Backend, true).Id : default;
            DeviceQueueDesc[] queues =
            [
                new(QueueType.Graphics),
                new(QueueType.Compute),
                new(QueueType.Copy),
            ];
            Surface = Backend.CreateSurface(new SurfaceDesc(
                NativeWindowType.Win32,
                Window.Handle,
                Label: title));
            Device = Backend.CreateDevice(new DeviceDesc(
                adapterId,
                queues,
                requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands |
                    (options.MeasureBlending ? DeviceFeatures.CalibratedTimestamps : DeviceFeatures.None),
                optionalFeatures: DeviceFeatures.CalibratedTimestamps |
                    (options.DisableMeshShaders ? DeviceFeatures.None : DeviceFeatures.MeshShaders),
                label: "SomeEngine Runtime Device"));
            Swapchain = Backend.CreateSwapchain(Device, new SwapchainDesc(
                Surface,
                3,
                TextureUsages.ColorAttachment | TextureUsages.CopySource | TextureUsages.CopyDestination,
                CreateSwapchainConfig(
                    options,
                    boot.Width,
                    boot.Height,
                    Device.Adapter.HardwareAccelerated),
                title));

        }

        internal T Wait<T>(Task<T> task) => Window is null
            ? task.GetAwaiter().GetResult()
            : ApplicationWait.Task(task, Window, FrameTimeout);

        internal void Wait(Task task)
        {
            if (Window is null) task.GetAwaiter().GetResult();
            else ApplicationWait.Task(task, Window, FrameTimeout);
        }

        public void Dispose()
        {
            List<Exception>? errors = null;
            CollectShutdownError(() => Swapchain?.Dispose(), ref errors);
            CollectShutdownError(() => Device?.Dispose(), ref errors);
            CollectShutdownError(() => Surface?.Dispose(), ref errors);
            CollectShutdownError(() => Backend?.Dispose(), ref errors);
            CollectShutdownError(() => Window?.Dispose(), ref errors);
            if (errors is not null)
                throw new AggregateException("Application display shutdown failed.", errors);
        }
    }

    private static IGraphicsBackend CreateBackend(
        ApplicationGraphicsBackend graphicsBackend,
        bool validation)
    {
        IGraphicsBackend backend = graphicsBackend switch
        {
            ApplicationGraphicsBackend.Direct3D12 => D3D12GraphicsBackend.Create(),
            ApplicationGraphicsBackend.Vulkan => VulkanGraphicsBackend.Create(new VulkanBackendOptions(
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
        ApplicationOptions options,
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
        ApplicationOptions options,
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

    private static int RunRenderer(
        ref ApplicationRenderer? renderer,
        ApplicationOptions options,
        ApplicationStartupTiming startup,
        IGraphicsBackend backend,
        Device device,
        Swapchain swapchain,
        NativeWindow window,
        World loadedWorld,
        SceneWorldData scene,
        ClusterShaders pipelineConfiguration,
        VirtualShadowMapShaders virtualShadowConfiguration,
        Shader uiShader,
        Shader presentationShader,
        Shader lightingShader,
        Shader temporalShader,
        ShaderLibrary shaderLibrary,
        ApplicationSettingsBlock boot,
        string title,
        bool forceHardwareRaster, Func<Material, float, bool>? animateMaterial, ApplicationScene? application, SceneScript? script,
        ApplicationSceneContent sceneContent, Func<string, Task<ApplicationSceneContent>> loadScene)
    {
        World mainWorld = loadedWorld;
        ScreenText? screenText = options.SurfacePanel || options.HudOverlay ? new ScreenText(mainWorld) : null;
        SurfacePanelExample? surfacePanel = options.SurfacePanel
            ? new SurfacePanelExample(mainWorld, boot.Width, boot.Height, screenText!) : null;
        HudOverlay? hud = options.HudOverlay ? new HudOverlay(mainWorld, boot.Width, boot.Height, screenText!) : null;
        SphereNameplates? nameplates = options.WorldNameplates
            ? new SphereNameplates(mainWorld, scene.GetMovingMeshEntities()) : null;
        bool includeInterface = application?.InterfaceEnabled == true ||
            options.SurfacePanel || options.HudOverlay || options.WorldNameplates;
        var elementSystems = new GameFrameSystems(mainWorld, includeInterface);
        if (nameplates is not null) elementSystems.AddInterfaceSystem(nameplates);
        WindowInput? elementInput = includeInterface ? new WindowInput(mainWorld) : null;
        ApplicationFrameMeasurements? measurements = application?.FrameMeasured is { } receiveTiming
            ? new(receiveTiming) : null;
        bool measure = application?.MeasureInterface == true || measurements is not null;
        GameUpdateTiming gameTiming = default;
        long gameStarted = 0, nextGameStarted = 0;
        bool synchronousGame = options.SynchronousFrames || script is not null || application?.UpdateFrame is not null || application?.FrameCompleted is not null;

        ApplicationViewFrame initialView = ApplicationViewFrame.Create(
            scene.Camera.View,
            scene.Projection(boot.Width, boot.Height),
            boot.Width,
            boot.Height,
            temporalFrameIndex: 0);
        bool firstScene = renderer is null;
        renderer ??= new ApplicationRenderer(options, backend, device, window, scene, pipelineConfiguration,
            virtualShadowConfiguration, uiShader, presentationShader, lightingShader, temporalShader,
            shaderLibrary, sceneContent.Content.Files, boot, initialView.View, includeInterface, forceHardwareRaster, application, script);
        renderer.ApplySceneSettings(options, script);
        renderer.WatchScene(sceneContent, application);
        var renderWorld = renderer.renderWorld;
        var extraction = renderer.extraction;
        var viewEntity = renderer.viewEntity;
        var graphicsQueue = renderer.graphicsQueue;
        var computeQueue = renderer.computeQueue;
        var copyQueue = renderer.copyQueue;
        var renderGraph = renderer.renderGraph;
        var renderDevice = renderer.renderDevice;
        var pipelines = renderer.pipelines;
        var instanceLayout = renderer.instanceLayout;
        var instances = renderer.instances;
        var cluster = renderer.cluster;
        var materialAssignments = renderer.materialAssignments;
        var materialSystem = renderer.materialSystem;
        var instanceSystem = renderer.instanceSystem;
        var prepareSystems = renderer.prepareSystems;
        var viewSystem = renderer.viewSystem;
        var skinning = renderer.skinning;
        var poseFeedback = new GpuPoseFeedbackSystem(skinning.PoseFeedback, renderGraph.LastSubmittedFrame);
        var skinBounds = renderer.skinBounds;
        var surfaces = renderer.surfaces;
        var outputDescription = renderer.outputDescription;
        var lighting = renderer.lighting;
        var stageResources = renderer.stageResources;
        var shading = renderer.shading;
        var lightSystem = renderer.lightSystem;
        var streamingFeedback = renderer.streamingFeedback;
        var streamingConsumer = renderer.streamingConsumer;
        var pipeline = renderer.pipeline;
        var virtualShadows = renderer.virtualShadows;
        var frameSystems = renderer.frameSystems;
        var sceneRegion = renderer.sceneRegion;
        var primaryGeometryRegion = renderer.primaryGeometryRegion;
        var primaryDepthAfterRegion = renderer.primaryDepthAfterRegion;
        var targetGeometryRegion = renderer.targetGeometryRegion;
        var targetGeometryAfterRegion = renderer.targetGeometryAfterRegion;
        var shadingRegion = renderer.shadingRegion;
        var temporalRegion = renderer.temporalRegion;
        var presentationRegion = renderer.presentationRegion;
        var uiRegion = renderer.uiRegion;
        var feedbackRegion = renderer.feedbackRegion;
        var frameOutputRegion = renderer.frameOutputRegion;
        var linearOutputRegion = renderer.linearOutputRegion;
        var colorEffectsRegion = renderer.colorEffectsRegion;
        if (screenText is not null)
        {
            if (renderer.glyphExtractor is null)
            {
                renderer.glyphExtractor = new GlyphRenderExtractor(screenText.AtlasSource);
                extraction.Add(renderer.glyphExtractor);
            }
            else renderer.glyphExtractor.SetAtlasSource(screenText.AtlasSource);
        }
        renderer.geometry ??= new ApplicationGeometryContext(backend, device, renderDevice, cluster, renderGraph,
            mainWorld, renderWorld, extraction, prepareSystems, frameSystems, () => scene.Camera.Position);
        var geometry = renderer.geometry;
        if (!firstScene) geometry.ReplaceGameWorld(mainWorld, () => scene.Camera.Position);
        FrameOutputVerifier? outputVerifier = null;
        FrameOutputVerifier? linearVerifier = null;
        var ui = renderer.ui;
        var presentation = renderer.presentation;
        var temporal = renderer.temporal;
        var colorFeature = renderer.colorFeature;
        GraphReadbackId presentationReadback = default;
        Task<GameUpdateTiming>? nextGameUpdate = null;
        var input = new ApplicationInput(elementInput);
        int width = boot.Width;
        int height = boot.Height;
        RenderGraphStatistics latestGraphStatistics = default;
        bool hasGraphStatistics = false;
        int analyzedFrames = 0;
        int reusedAnalysisFrames = 0;
        ClusterShadingPath shadingPath = script?.ShadingPath ?? options.ShadingPath;

        GameUpdateTiming UpdateInterface(int frameNumber, int canvasWidth, int canvasHeight, double deltaSeconds, long gameUpdateStarted)
        {
            poseFeedback.Apply(mainWorld, renderGraph.LastCompletedFrame);
            geometry.UpdateGame(deltaSeconds);
            long allocated = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            long started = measure ? Stopwatch.GetTimestamp() : 0;
            surfacePanel?.Resize(canvasWidth, canvasHeight);
            hud?.Resize(canvasWidth, canvasHeight);
            if (options.HudNumbersChange) hud?.Update(frameNumber);
            if (!measure)
            {
                elementSystems.UpdateViews(deltaSeconds);
                extraction.Extract(mainWorld);
                return default;
            }
            ViewTiming views = elementSystems.MeasureViews(deltaSeconds);
            views = views with
            {
                TotalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
            };
            double gameMs = Stopwatch.GetElapsedTime(gameUpdateStarted).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            using var trace = EngineTrace.Begin(ApplicationTrace.Extraction);
            extraction.Extract(mainWorld);
            return new GameUpdateTiming(views, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocated, gameMs);
        }

        void CaptureRenderGraphDiagnostics(in RenderGraphDiagnosticsView diagnostics)
        {
            latestGraphStatistics = diagnostics.Statistics;
            hasGraphStatistics = true;
            analyzedFrames++;
            if (latestGraphStatistics.AnalysisReused) reusedAnalysisFrames++;
        }

        int frameIndex = renderer.FrameIndex;
        int sceneFirstFrame = frameIndex;
        uint submittedFrames = 0;
        var sceneEdit = new SceneEdit(scene, window);
        var applicationFrame = new ApplicationFrame(mainWorld, elementSystems, extraction, sceneEdit, input, cluster, startup, device.Adapter)
            { Index = frameIndex, Width = boot.Width, Height = boot.Height };
        int actionIndex = 0;
        ApplicationRunException? renderingError = null;
        try
        {
            if (firstScene) geometry.Install(application?.GeometryFeatures ?? []);
            renderer.InstallSystems();
            sceneEdit.Begin();
            try { application?.Initialize?.Invoke(applicationFrame); }
            finally { sceneEdit.End(); }
            if (synchronousGame) elementSystems.UpdateViews(0);
            else
            {
                gameStarted = Stopwatch.GetTimestamp();
                if (options.ScenePlayback is not null)
                    scene.Play(0, options.ScenePlayback is "materials" or "camera", animateMaterial,
                        orbitCamera: options.ScenePlayback == "camera");
                gameTiming = UpdateInterface(0, boot.Width, boot.Height, 0, gameStarted);
            }

            outputVerifier = options.VerifyFrameOutput || (options.FrameOutputPath is not null && options.CaptureFrame is null)
                ? new FrameOutputVerifier(backend, device, renderGraph, width, height, PresentationFormat)
                : null;
            ui ??= renderer.ui ??= new DebugInterfaceRenderer(
                backend,
                device,
                pipelines,
                window,
                uiShader,
                boot.UiVertexEntry,
                boot.UiPixelEntry,
                PresentationFormat);
            Console.WriteLine(
                $"Runtime configuration '{title}' loaded from {contentRootLabel(script?.ContentDirectory ?? application?.ContentDirectory ?? FindContentRoot())}; " +
                $"sceneInstances={scene.MeshInstanceCount}, " +
                $"sceneBounds={scene.MeshPositionMin}..{scene.MeshPositionMax}, " +
                    $"clusterPipeline={boot.Pipeline.Value}.");
            long runtimeStarted = Stopwatch.GetTimestamp();
            startup.RecordStage("RenderResources");
            long previousFrameTimestamp = Stopwatch.GetTimestamp();
            long traceFrameIndex = 0;
            bool animateScene = options.DynamicScene;
            bool debugUiOpen = !options.HideDebugUi && !options.VerifyFrameOutput;
            FrameOutputMetrics? verifiedOutput = null;
            uint maximumRecoveredClusters = 0;
            int FrameLimit() => script is null ? application?.Options.FrameLimit ?? options.FrameLimit : options.FrameLimit;
            while (FrameLimit() == 0 || frameIndex < FrameLimit())
            {
                if (application?.TakeSceneRequest() is { } directory)
                    renderer.LoadingScene = loadScene(directory);
                long frameStarted = Stopwatch.GetTimestamp();
                if (startup.Result is not null && startup.IsTracing)
                {
                    if (nextGameUpdate is not null)
                    {
                        ApplicationWait.Task(nextGameUpdate, window, FrameTimeout);
                        nextGameUpdate = null;
                    }
                    startup.FinishTrace();
                }
                using var frameTrace = EngineTrace.BeginFrame(++traceFrameIndex);
                renderDevice.CollectCompleted();
                if (!renderGraph.TryOpenNextFrame(out RenderGraphNextFrame nextGraphFrame, FrameTimeout))
                    throw new TimeoutException("RenderGraph frame resources did not become available before the runtime timeout.");
                using var graphFrameResources = nextGraphFrame;
                // Shared asset content can change only after the Game World extraction reader exits.
                if (nextGameUpdate is not null)
                {
                    gameTiming = ApplicationWait.Task(nextGameUpdate, window, FrameTimeout);
                    gameStarted = nextGameStarted;
                    nextGameUpdate = null;
                }
                renderer.ApplyShaderChanges(scene);
                input.BeginFrame();
                if (!window.PumpMessages())
                    break;
                while (window.TryReadEvent(out NativeWindowEvent windowEvent))
                {
                    input.Process(windowEvent);
                    ui.ProcessEvent(windowEvent, input);
                }
                elementSystems.UpdateInput();
                if (input.WasKeyPressed(ApplicationInput.KeyEscape))
                {
                    window.RequestClose();
                    continue;
                }
                if (input.WasKeyPressed(ApplicationInput.KeyF1))
                    debugUiOpen = !debugUiOpen;
                if (window.IsMinimized)
                {
                    window.WaitForEvents(TimeSpan.FromMilliseconds(16));
                    previousFrameTimestamp = Stopwatch.GetTimestamp();
                    continue;
                }

                int clientWidth = window.ClientWidth;
                int clientHeight = window.ClientHeight;
                bool cameraCut = script?.UseCameraHistory == false || frameIndex == sceneFirstFrame;
                if (clientWidth > 0 && clientHeight > 0 &&
                    (clientWidth != width || clientHeight != height))
                {
                    outputVerifier = null;
                    linearVerifier = null;
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
                    outputVerifier = options.VerifyFrameOutput || (options.FrameOutputPath is not null && options.CaptureFrame is null)
                        ? new FrameOutputVerifier(backend, device, renderGraph, width, height, PresentationFormat)
                        : null;
                }

                long currentFrameTimestamp = Stopwatch.GetTimestamp();
                float deltaSeconds = Math.Clamp(
                    (float)Stopwatch.GetElapsedTime(previousFrameTimestamp, currentFrameTimestamp).TotalSeconds,
                    1.0f / 1_000_000.0f,
                    1.0f / 10.0f);
                if (options.FixedFrameSeconds > 0) deltaSeconds = options.FixedFrameSeconds;
                float sceneSeconds = options.FixedFrameSeconds > 0
                    ? (frameIndex - sceneFirstFrame) * options.FixedFrameSeconds
                    : (float)Stopwatch.GetElapsedTime(runtimeStarted).TotalSeconds;
                previousFrameTimestamp = currentFrameTimestamp;
                if (synchronousGame) gameStarted = Stopwatch.GetTimestamp();
                sceneEdit.Begin();
                try
                {
                    while (script is not null && actionIndex < script.Actions.Count && script.Actions[actionIndex].Frame == frameIndex)
                        script.Actions[actionIndex++].Execute(sceneEdit);
                    applicationFrame.Index = frameIndex;
                    applicationFrame.Width = window.ClientWidth;
                    applicationFrame.Height = window.ClientHeight;
                    applicationFrame.DeltaSeconds = deltaSeconds;
                    application?.UpdateFrame?.Invoke(applicationFrame);
                }
                catch (Exception error)
                {
                    throw new ApplicationRunException(ApplicationFailure.SceneAction, checked((int)submittedFrames), error);
                }
                finally { sceneEdit.End(); }
                cameraCut |= sceneEdit.CameraCut;
                if (sceneEdit.ShadowMapRefreshRequested) virtualShadows.InvalidateAllCasters();

                if (options.ScenePlayback is not null && (synchronousGame))
                    scene.Play(sceneSeconds, options.ScenePlayback is "materials" or "camera", animateMaterial,
                        orbitCamera: options.ScenePlayback == "camera");
                ui.BeginFrame(deltaSeconds, width, height, window.DpiScale);
                SomeEngine.Elements.InputCapture elementCapture = elementInput?.InputCapture ?? default;
                scene.Camera.Update(
                    input,
                    deltaSeconds,
                    ui.WantCaptureKeyboard || elementCapture.Keyboard,
                    ui.WantCaptureMouse || elementCapture.Pointer);
                if (synchronousGame)
                {
                    if (animateScene && options.ScenePlayback is null)
                        scene.Update(sceneSeconds);
                    gameTiming = UpdateInterface(frameIndex, width, height, deltaSeconds, gameStarted);
                }
                ApplicationViewFrame viewFrame = ApplicationViewFrame.Create(
                    scene.Camera.View,
                    scene.Projection(width, height),
                    width,
                    height,
                    submittedFrames,
                    temporalResolveEnabled: options.TemporalAntialiasing,
                    cameraCut: cameraCut,
                    cameraTarget: scene.Camera.Target,
                    depthOfField: scene.DepthOfField,
                    playerId: scene.PlayerId,
                    firstPerson: scene.FirstPerson);
                viewSystem.Stage(viewFrame.View);
                if (application?.DebugUiEnabled != false) ui.DrawDebugWindow(
                    ref debugUiOpen,
                    ref animateScene,
                    new DebugInterfaceMetrics(
                        frameIndex,
                        deltaSeconds,
                        width,
                        height,
                        window.DpiScale,
                        viewFrame.JitterPixels,
                        scene.Camera.Position,
                        window.HasFocus,
                        options.TemporalAntialiasing), application?.DrawDebugUi);
                bool reportMilestones = frameIndex == 0;
                if (reportMilestones)
                    Console.WriteLine("First frame: applying extracted scene.");
                long prepareStarted = Stopwatch.GetTimestamp();
                startup.RecordStage("FrameSetup");
                bool selectedCapture = options.CaptureFrame == frameIndex;
                bool captureBeauty = options.VerifyFrameOutput || sceneEdit.Images.Count > 0 ||
                    (options.FrameOutputPath is not null && (selectedCapture || options.CaptureFrame is null));
                bool captureLinear = sceneEdit.LinearImages.Count > 0 ||
                    (selectedCapture && options.LinearFrameOutputPath is not null);
                if (captureBeauty)
                    outputVerifier ??= new FrameOutputVerifier(backend, device, renderGraph, width, height, PresentationFormat);
                if (captureLinear)
                {
                    linearVerifier ??= new FrameOutputVerifier(backend, device, renderGraph, width, height, Format.R16G16B16A16Float);
                    if (virtualShadows.Settings.Enabled) virtualShadows.EnableQualityCapture();
                }
                renderer.Prepare(
                    nextGraphFrame,
                    window,
                    reportMilestones);
                double prepareMs = Stopwatch.GetElapsedTime(prepareStarted).TotalMilliseconds;
                bool sceneGeometryReady = true;
                if (startup.Result is null)
                {
                    var meshes = cluster.CaptureDiagnostics().Meshes;
                    sceneGeometryReady = meshes.PublishedMeshes == meshes.RegisteredMeshes;
                }
                startup.RecordStage("Prepare");
                foreach (var request in sceneEdit.PoseReads)
                {
                    Entity target = extraction.RequireMirror(request.Target);
                    Entity section = renderWorld.Read<RenderGpuPoseTarget>(target).Section;
                    skinning.RequestReadback(renderWorld.Read<RenderPoseSection>(section), request.Receive);
                }
                long reconcileStarted = Stopwatch.GetTimestamp();
                if (sceneEdit.ShadingPath is { } selectedShadingPath) shadingPath = selectedShadingPath;
                pipeline.SetShadingPath(shadingPath);
                renderer.materialDecals.Prepare();
                ClusterPipelineOutputs clusterOutputs;
                using (EngineTrace.Begin(ApplicationTrace.Reconcile))
                    clusterOutputs = pipeline.ReconcileGraph();
                renderer.ReconcileFeatures(clusterOutputs, options, script);
                double reconcileMs = Stopwatch.GetElapsedTime(reconcileStarted).TotalMilliseconds;
                startup.RecordStage("ReconcileGraph");
                var rendering = new ApplicationRendering(backend, device, renderGraph, boot.Width, boot.Height,
                    viewFrame.View.Projection, colorEffectsRegion)
                {
                    Pipelines = pipelines, ShaderLibrary = shaderLibrary, Files = sceneContent.Content.Files,
                    Geometry = clusterOutputs.Geometry, Materials = renderer.shading,
                    ViewCount = clusterOutputs.ViewCount, ShadowRequestPass = () => virtualShadows.OpaqueRequestPass,
                    MaterialPasses = () => shading.MaterialPasses,
                    Layers = renderer.layers, Decals = renderer.materialDecals,
                    Shadows = virtualShadows,
                };
                bool connectPostProcessing = temporal is null;
                if (connectPostProcessing && (script?.CreateColorFeature ?? application?.CreateColorFeature) is { } createColorFeature)
                    colorFeature = renderer.colorFeature = createColorFeature(rendering);
                bool initializedFrameFeatures = renderer.InitializeFrameFeatures(rendering,
                    script?.CreateFrameFeatures ?? application?.CreateFrameFeatures);
                if (initializedFrameFeatures || connectPostProcessing && colorFeature is not null)
                {
                    extraction.Extract(mainWorld);
                    renderer.Prepare(nextGraphFrame, window, reportMilestones: false);
                    renderer.materialDecals.Prepare();
                    clusterOutputs = pipeline.ReconcileGraph();
                    renderer.ReconcileFeatures(clusterOutputs, options, script);
                }
                temporal ??= renderer.temporal ??= new TemporalFeature(
                    backend,
                    device,
                    pipelines,
                    renderGraph,
                    boot.Width,
                    boot.Height,
                    temporalShader,
                    boot.TemporalVertexEntry,
                    boot.TemporalPixelEntry,
                    region: temporalRegion, depthFormat: Format.R32Float);
                if (connectPostProcessing && colorFeature is not null)
                {
                    RenderGraphEdit edit = renderGraph.BeginEdit();
                    try
                    {
                        temporal.OrderAfter(ref edit, colorFeature.CompletePass);
                        edit.Commit();
                    }
                    finally
                    {
                        edit.Dispose();
                    }
                }
                presentation ??= renderer.presentation ??= new PresentationPipeline(
                    backend,
                    device,
                    pipelines,
                    renderGraph,
                    temporal.Output.ResolvedColor,
                    temporal.Output.Format,
                    temporal.Output.Width,
                    temporal.Output.Height,
                    pipeline.PageFaultReadback,
                    presentationShader,
                    boot.PresentationVertexEntry,
                    boot.PresentationPixelEntry,
                    PresentationFormat,
                    presentationRegion);
                if (presentationReadback != pipeline.PageFaultReadback)
                {
                    using RenderGraphEdit edit = renderGraph.BeginEdit();
                    presentation.OrderAfter(edit, pipeline.PageFaultReadback);
                    edit.Commit();
                    presentationReadback = pipeline.PageFaultReadback;
                }
                surfaces?.ReconcileGraph(presentation.Target, new WorldCanvasTarget(presentation.Target, clusterOutputs.Depth, viewEntity));
                ui.InstallGraph(renderGraph, presentation.Target, uiRegion);
                if (reportMilestones)
                    Console.WriteLine("First frame: acquiring presentation image.");
                SwapchainImage? image = sceneEdit.FrameCanceled
                    ? null : AcquireImage(backend, swapchain, window);
                SceneFrame? capturedFrame = captureLinear ? scene.CaptureFrame(new SceneFrame
                {
                    Frame = frameIndex, SubmittedFrame = submittedFrames, SceneSeconds = sceneSeconds,
                    Width = width, Height = height, CameraPosition = scene.Camera.Position,
                    WorldOrigin = renderWorld.WorldOrigin,
                    View = scene.Camera.View, Projection = viewFrame.View.Projection,
                    UnjitteredProjection = scene.Projection(width, height), JitterPixels = viewFrame.JitterPixels,
                    Options = options with { ShadingPath = shadingPath },
                    ShadowSettings = virtualShadows.Settings,
                    TemporalSettings = script?.TemporalSettings ?? TemporalResolveSettings.Default,
                    RasterEdgePixels = script?.RasterEdgePixels ?? 32,
                    ClusterWorkCapacity = script?.ClusterWorkCapacity ?? 1_048_576,
                    RasterBatchCapacity = script?.RasterBatchCapacity,
                    VisibilityMode = script?.VisibilityMode,
                    Adapter = device.Adapter.Name, DriverVersion = device.Adapter.DriverVersion,
                }) : null;
                VirtualShadowFrameQuality? shadowQuality = null;
                long executeStarted = Stopwatch.GetTimestamp();
                startup.RecordStage("PostProcessing");
                FrameOutputMetrics? frameOutput = renderer.RenderFrame(
                    viewFrame.View,
                    frameIndex,
                    nextGraphFrame,
                    clusterOutputs,
                    image,
                    width,
                    height,
                    window,
                    reportMilestones,
                    cameraCut,
                    captureBeauty ? outputVerifier : null,
                    frameOutputRegion,
                    captureLinear ? linearVerifier : null,
                    linearOutputRegion,
                    captureLinear && virtualShadows.Settings.Enabled ? value => shadowQuality = value : null,
                    options.VerifyFrameOutput ? CaptureRenderGraphDiagnostics : null,
                    options.SynchronousFrames || application?.FrameRendered is not null,
                    options.TemporalAntialiasing,
                    viewFrame.JitterPixels,
                    script?.TemporalSettings ?? TemporalResolveSettings.Default,
                    measurements,
                    out ulong graphFrameIdentity,
                    out PresentStatus present);
                if (image is null)
                {
                    frameIndex++;
                    continue;
                }
                submittedFrames++;
                double executeMs = Stopwatch.GetElapsedTime(executeStarted).TotalMilliseconds;
                skinning.CompleteReadbacks();
                application?.FrameRendered?.Invoke(pipeline.CaptureFrameMetrics());
                startup.RecordStage("ExecuteFrame");
                if (selectedCapture && options.FrameOutputPath is { } framePath)
                    outputVerifier!.SaveImage(framePath);
                if (capturedFrame is not null)
                {
                    ClusterRenderDiagnostics diagnostics = cluster.CaptureDiagnostics();
                    ClusterResidencyDiagnostics residency = diagnostics.Residency;
                    capturedFrame = capturedFrame with
                    {
                        Quality = new(shadowQuality, pipeline.CaptureFrameMetrics(), new(
                            diagnostics.Meshes.RegisteredMeshes, diagnostics.Meshes.PublishedMeshes,
                            residency.RegisteredPages, residency.ResidentPages, residency.MissingPages,
                            residency.QueuedPageLoads, residency.ActivePageLoads, residency.PageLoadFailures)),
                    };
                    SceneLinearImage captured = linearVerifier!.CopyLinearImage(capturedFrame);
                    try
                    {
                        foreach (Action<SceneLinearImage> receive in sceneEdit.LinearImages) receive(captured);
                    }
                    catch (Exception error)
                    {
                        throw new ApplicationRunException(ApplicationFailure.ImageCallback, checked((int)submittedFrames), error);
                    }
                    if (selectedCapture && options.LinearFrameOutputPath is { } linearPath) captured.Save(linearPath);
                }
                if (sceneEdit.Images.Count > 0)
                {
                    SceneImage captured = outputVerifier!.CopyImage(frameIndex);
                    try
                    {
                        foreach (Action<SceneImage> receive in sceneEdit.Images) receive(captured);
                    }
                    catch (Exception error)
                    {
                        throw new ApplicationRunException(ApplicationFailure.ImageCallback, checked((int)submittedFrames), error);
                    }
                    if (!options.VerifyFrameOutput && options.FrameOutputPath is null)
                    {
                        outputVerifier = null;
                    }
                }
                if (frameOutput is FrameOutputMetrics output)
                    verifiedOutput = output;
                if (options.VerifyFrameOutput)
                {
                    ClusterFrameMetrics metrics = pipeline.CaptureFrameMetrics();
                    maximumRecoveredClusters = Math.Max(maximumRecoveredClusters,
                        metrics.PhaseTwoSoftwareClusters + metrics.PhaseTwoHardwareClusters);
                }

                bool hasFutureFrame =
                    FrameLimit() == 0 || frameIndex + 1 < FrameLimit();
                if (hasFutureFrame && !synchronousGame)
                {
                    bool updateScene = animateScene;
                    float nextSceneTime = options.FixedFrameSeconds > 0 ? (frameIndex + 1) * options.FixedFrameSeconds : (float)Stopwatch.GetElapsedTime(
                        runtimeStarted,
                        Stopwatch.GetTimestamp()).TotalSeconds;
                    TraceContext gameTrace = EngineTrace.Context with { Frame = traceFrameIndex + 1 };
                    int nextCanvasWidth = width;
                    int nextCanvasHeight = height;
                    int nextFrameNumber = frameIndex + 1;
                    nextGameStarted = Stopwatch.GetTimestamp();
                    nextGameUpdate = Task.Run(() =>
                    {
                        long updateStarted = Stopwatch.GetTimestamp();
                        using var association = EngineTrace.Associate(gameTrace);
                        using var trace = EngineTrace.Begin(ApplicationTrace.GameUpdate);
                        // CPU rendering has finished reading shared resources. Apply next-frame
                        // resource edits here while submitted GPU work retains its device data.
                        if (options.ScenePlayback is not null)
                            scene.Play(nextSceneTime, options.ScenePlayback is "materials" or "camera", animateMaterial,
                                orbitCamera: options.ScenePlayback == "camera");
                        else if (updateScene)
                            scene.Update(nextSceneTime);
                        return UpdateInterface(nextFrameNumber, nextCanvasWidth, nextCanvasHeight, deltaSeconds, updateStarted);
                    });
                }

                if (reportMilestones)
                    Console.WriteLine("First frame: presenting.");
                double cpuMs = measurements is null ? 0
                    : Stopwatch.GetElapsedTime(Math.Min(frameStarted, gameStarted)).TotalMilliseconds;
                if (sceneGeometryReady && startup.Result is null)
                {
                    startup.RecordPresent(window, present, frameIndex);
                }
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

                applicationFrame.Timing = new(Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds,
                    prepareMs, reconcileMs, executeMs, gameTiming.Views, gameTiming.ExtractionMs,
                    gameTiming.ExtractionAllocatedBytes, frameIndex);
                application?.FrameCompleted?.Invoke(applicationFrame);
                measurements?.RecordCpu(graphFrameIdentity, frameIndex,
                    cpuMs,
                    gameTiming.GameMs, gameTiming.Views.AnimationMs, gameTiming.ExtractionMs,
                    prepareMs, reconcileMs, executeMs);
                frameIndex++;
                if (application?.ReloadRequested == true) break;
                if (renderer.LoadingScene is { IsCompleted: true } loading)
                {
                    renderer.NextScene = loading.GetAwaiter().GetResult();
                    renderer.LoadingScene = null;
                    break;
                }
            }

            if (frameIndex > 0)
            {
                if (options.FrameOutputPath is not null && options.CaptureFrame is null)
                    outputVerifier!.SaveImage(options.FrameOutputPath);
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
                if (options.VerifyFrameOutput)
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
                        $"maximumRecovered={maximumRecoveredClusters}, " +
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
                    if (!output.HasVisibleVariation || !clusterOutputHealthy)
                    {
                        throw new InvalidOperationException(
                            "The Cluster frame did not produce visible output together with " +
                            "candidates, visible clusters, and shaded pixels.");
                    }
                }
            }
            Console.WriteLine($"SomeEngine runtime completed {submittedFrames} frame(s).");
            return checked((int)submittedFrames);
        }
        catch (Exception error)
        {
            if (error is ApplicationRunException run)
            {
                renderingError = run;
                throw;
            }
            renderingError = new ApplicationRunException(ApplicationRunException.Classify(error, ApplicationFailure.Rendering), checked((int)submittedFrames), error);
            throw renderingError;
        }
        finally
        {
            List<Exception>? errors = null;
            if (nextGameUpdate is not null)
                CollectShutdownError(() => ApplicationWait.Task(nextGameUpdate, window, FrameTimeout), ref errors);
            if (device.Status == DeviceStatus.Active)
            {
                if (renderer.NextScene is null)
                    CollectShutdownError(() => renderGraph.WaitForFrame(renderGraph.LastSubmittedFrame, FrameTimeout), ref errors);
                if (device.Status == DeviceStatus.Active)
                    CollectShutdownError(renderGraph.CollectCompleted, ref errors);
            }
            CollectShutdownError(() => application?.Shutdown?.Invoke(applicationFrame), ref errors);
            CollectShutdownError(() => sceneContent.GameSystems = geometry.TakeGameSystems(), ref errors);
            renderer.FrameIndex = frameIndex;
            renderer.SubmittedFrames += submittedFrames;
            renderer.ui = ui;
            renderer.presentation = presentation;
            renderer.temporal = temporal;
            renderer.colorFeature = colorFeature;
            CollectShutdownError(() => extraction.ReplaceSource(), ref errors);
            CollectShutdownError(() => elementInput?.Dispose(), ref errors);
            CollectShutdownError(elementSystems.Dispose, ref errors);
            CollectShutdownError(() => nameplates?.Dispose(), ref errors);
            CollectShutdownError(() => hud?.Dispose(), ref errors);
            CollectShutdownError(() => surfacePanel?.Dispose(), ref errors);
            CollectShutdownError(() => screenText?.Dispose(), ref errors);
            CollectShutdownError(() => renderGraph.RetireAfterSubmittedFrames(sceneContent), ref errors);
            if (device.Status == DeviceStatus.Active)
                CollectShutdownError(() => backend.CollectCompleted(device), ref errors);
            if (errors is not null)
            {
                if (renderingError is not null) errors.Insert(0, renderingError);
                var failure = new AggregateException("Scene execution and shutdown failed.", errors);
                throw new ApplicationRunException(
                    renderingError?.Failure ?? ApplicationRunException.Classify(failure, ApplicationFailure.Rendering),
                    renderingError?.CompletedFrames ?? checked((int)submittedFrames), failure);
            }
        }
    }

    private readonly record struct GameUpdateTiming(ViewTiming Views, double ExtractionMs, long ExtractionAllocatedBytes, double GameMs = 0);

    private static string FindContentRoot()
    {
        string root = AppContext.BaseDirectory;
        string manifest = Path.Combine(root, "Library", "AssetManifest", AssetManifest.CatalogFileName);
        if (File.Exists(manifest)) return root;
        throw new DirectoryNotFoundException(
            "The application directory has no Library/AssetManifest/catalog.json. Build this project's content before starting the application.");
    }

    private static string contentRootLabel(string value) => Path.GetFullPath(value);
}
