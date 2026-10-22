using System.Diagnostics;
using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
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
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Features.Motion;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Features.Temporal;
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

    internal static Task<int> RunAsync(RuntimeStartupOptions options, bool useWarp, ApplicationStartupTiming startup,
        Func<Material, float, bool>? animateMaterial = null, ApplicationScene? application = null, SceneScript? script = null) => Task.Factory.StartNew(() =>
    {
        var shaderResources = new RuntimeShaderResources();
        using var display = new ApplicationDisplay();
        int frames = 0;
        do
        {
            if (application is not null) options = application.Options;
            if (application is not null) application.ReloadRequested = false;
            try
            {
                application?.SceneStarted?.Invoke(Stopwatch.GetTimestamp());
                int sceneFrames = RunScene(options, useWarp, startup, animateMaterial, application, display, shaderResources, script);
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
        } while (application?.ReloadRequested == true);
        return frames;
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static int RunScene(RuntimeStartupOptions options, bool useWarp, ApplicationStartupTiming startup,
        Func<Material, float, bool>? animateMaterial, ApplicationScene? application,
        ApplicationDisplay display, RuntimeShaderResources shaderResources, SceneScript? script)
    {
        ArgumentNullException.ThrowIfNull(options);
        string contentRoot = script?.ContentDirectory ?? application?.ContentDirectory ?? FindContentRoot();
        string manifestDirectory = Path.Combine(contentRoot, "Library", "AssetManifest");
        AssetManifest manifest = AssetManifest.Load(manifestDirectory);
        IReadOnlyList<AssetEntry> runtimeRecords =
            manifest.List(ApplicationSettingsAsset.FileType);
        if (runtimeRecords.Count != 1)
        {
            throw new InvalidDataException(
                $"The runtime publication must contain exactly one {ApplicationSettingsAsset.FileType} " +
                $"asset; found {runtimeRecords.Count}.");
        }

        var assets = new AssetLoader(new LooseAssetStorage(contentRoot, manifest));
        try
        {
            using ApplicationSettingsAsset settings = display.Wait(ApplicationSettingsAsset.OpenAsync(assets,
                new AssetId<ApplicationSettingsAsset>(runtimeRecords[0].AssetGuid)).AsTask());
            ApplicationSettingsBlock boot = display.Wait(settings.GetBlockAsync<ApplicationSettingsBlock>().AsTask());
            string title = settings.Name;
            SlangCompileTarget target = options.GraphicsBackend == RuntimeGraphicsBackend.Vulkan
                ? SlangCompileTarget.Spirv
                : SlangCompileTarget.Dxil;
            // Slang front-end operations are serialized per global session. Keeping Cluster
            // configuration work separate lets independent Slang object graphs progress together.
            IGlobalSession clusterSlang = shaderResources.GetSlang(target, 0);
            IGlobalSession slang = shaderResources.GetSlang(target, 1);
            SessionDesc clusterSession = CreateShaderSessionDescription(
                clusterSlang,
                target);
            SessionDesc shaderSession = CreateShaderSessionDescription(
                slang,
                target);

            Task<WorldAsset> sceneTask = WorldAsset.OpenAsync(assets, boot.Scene).AsTask();
            Task<ClusterShaders> pipelineTask = shaderResources.LoadClusterAsync(
                manifest,
                boot.Pipeline,
                CreateClusterShadersAsync).AsTask();
            Task<VirtualShadowMapShaders> virtualShadowTask = shaderResources.LoadShadowsAsync(
                manifest,
                boot.VirtualShadowMap,
                CreateShadowShadersAsync).AsTask();
            Task<Shader> uiShaderTask = LoadShaderAsync(boot.UiShader).AsTask();
            Task<Shader> presentationShaderTask = LoadShaderAsync(boot.PresentationShader).AsTask();
            Task<Shader> lightingShaderTask = LoadShaderAsync(boot.LightingShader).AsTask();
            Task<Shader> temporalShaderTask = LoadShaderAsync(boot.TemporalShader).AsTask();
            var deviceCreated = new TaskCompletionSource<Device>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shaderOptions = new ClusterPipelineOptions { ForceHardwareRaster = useWarp || options.ForceHardwareRaster };
            Task<World> worldTask = Task.Run(async () =>
            {
                long started = Stopwatch.GetTimestamp();
                WorldAsset sceneAsset = await sceneTask.ConfigureAwait(false);
                World world = await sceneAsset.LoadAsync(SceneSerialization.CreateRegistry(),
                    new(Execution: new WorldJobs(JobSystem.Default))).ConfigureAwait(false);
                startup.RecordWork("WorldLoad", started);
                return world;
            });
            Task<RuntimeScene> sceneResourcesTask = Task.Run(async () =>
            {
                World world = await worldTask.ConfigureAwait(false);
                long started = Stopwatch.GetTimestamp();
                RuntimeScene scene = await RuntimeScene.CreateAsync(world, assets, LoadShaderAsync,
                    PrepareMaterialShaderAsync).ConfigureAwait(false);
                startup.RecordWork("SceneResources", started);
                return scene;
            });

            ValueTask<Shader> LoadShaderAsync(
                AssetId<ShaderAsset> id,
                CancellationToken cancellationToken = default) => shaderResources.LoadShaderAsync(
                    manifest,
                    assets,
                    slang,
                    shaderSession,
                    id,
                    cancellationToken);
            async ValueTask PrepareMaterialShaderAsync(Shader shader, CancellationToken cancellationToken)
            {
                ClusterPipelineSystem.PrepareMaterialShader(shader, target);
                Device device = await deviceCreated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                ClusterPipelineSystem.PrepareMaterialShader(display.Backend, device, shader, shaderOptions);
            }
            async ValueTask<ClusterShaders> CreateClusterShadersAsync(CancellationToken cancellationToken)
            {
                await using ClusterShadersAsset file = await ClusterShadersAsset.OpenAsync(
                    assets,
                    boot.Pipeline,
                    cancellationToken).ConfigureAwait(false);
                ClusterShadersBlock shaders = await file.GetBlockAsync<ClusterShadersBlock>(cancellationToken)
                    .ConfigureAwait(false);
                return await shaders.CreateAsync(
                    file.Name,
                    assets,
                    clusterSlang,
                    clusterSession,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            async ValueTask<VirtualShadowMapShaders> CreateShadowShadersAsync(CancellationToken cancellationToken)
            {
                await using VirtualShadowMapShadersAsset file = await VirtualShadowMapShadersAsset.OpenAsync(
                    assets,
                    boot.VirtualShadowMap,
                    cancellationToken).ConfigureAwait(false);
                VirtualShadowMapShadersBlock shaders = await file.GetBlockAsync<VirtualShadowMapShadersBlock>(cancellationToken)
                    .ConfigureAwait(false);
                return await shaders.CreateAsync(
                    file.Name,
                    assets,
                    slang,
                    shaderSession,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
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
                deviceCreated.SetResult(display.Device);
                startup.RecordStage("DeviceCreation");
                using (EngineTrace.Begin(ApplicationTrace.ContentWait))
                    display.Wait(contentReads);
                startup.RecordStage("ContentWait");
            }
            catch (Exception failure)
            {
                deviceCreated.TrySetException(failure);
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
                options,
                startup,
                display.Backend,
                display.Device,
                display.Swapchain,
                display.Window!,
                assets,
                worldTask.Result,
                sceneResourcesTask.Result,
                pipelineTask.Result,
                virtualShadowTask.Result,
                uiShaderTask.Result,
                presentationShaderTask.Result,
                lightingShaderTask.Result,
                temporalShaderTask.Result,
                LoadShaderAsync,
                boot,
                title,
                forceHardwareRaster: useWarp || options.ForceHardwareRaster, animateMaterial, application, script);
        }
        finally
        {
            assets.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static SessionDesc CreateShaderSessionDescription(
        IGlobalSession slang,
        SlangCompileTarget target)
    {
        string profile = target == SlangCompileTarget.Dxil ? "sm_6_6" : "glsl_460";
        return new SessionDesc
        {
            Targets =
            [
                new TargetDesc
                {
                    Format = target,
                    Profile = slang.FindProfile(profile),
                },
            ],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries =
            [
                new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
                new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
                new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
            ],
        };
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

        internal void Initialize(RuntimeStartupOptions options, bool useWarp, string title, ApplicationSettingsBlock boot)
        {
            if (Window is not null && _meshShadersDisabled == options.DisableMeshShaders &&
                _validation == options.DeviceValidation)
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
            if (useWarp && options.GraphicsBackend != RuntimeGraphicsBackend.Direct3D12)
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
                requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands,
                optionalFeatures: options.DisableMeshShaders ? DeviceFeatures.None : DeviceFeatures.MeshShaders,
                label: "SomeEngine Runtime Device"));
            Swapchain = Backend.CreateSwapchain(Device, new SwapchainDesc(
                Surface,
                3,
                TextureUsages.ColorAttachment | TextureUsages.CopySource,
                CreateSwapchainConfig(
                    options,
                    boot.Width,
                    boot.Height,
                    Device.Adapter.HardwareAccelerated),
                title));

        }

        internal T Wait<T>(Task<T> task) => Window is null
            ? task.GetAwaiter().GetResult()
            : RuntimeWait.Task(task, Window, FrameTimeout);

        internal void Wait(Task task)
        {
            if (Window is null) task.GetAwaiter().GetResult();
            else RuntimeWait.Task(task, Window, FrameTimeout);
        }

        public void Dispose()
        {
            Swapchain?.Dispose();
            Device?.Dispose();
            Surface?.Dispose();
            Backend?.Dispose();
            Window?.Dispose();
        }
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

    private static int RunRenderer(
        RuntimeStartupOptions options,
        ApplicationStartupTiming startup,
        IGraphicsBackend backend,
        Device device,
        Swapchain swapchain,
        NativeWindow window,
        AssetLoader assets,
        World loadedWorld,
        RuntimeScene scene,
        ClusterShaders pipelineConfiguration,
        VirtualShadowMapShaders virtualShadowConfiguration,
        Shader uiShader,
        Shader presentationShader,
        Shader lightingShader,
        Shader temporalShader,
        Func<AssetId<ShaderAsset>, CancellationToken, ValueTask<Shader>> loadShader,
        ApplicationSettingsBlock boot,
        string title,
        bool forceHardwareRaster, Func<Material, float, bool>? animateMaterial, ApplicationScene? application, SceneScript? script)
    {
        using World mainWorld = loadedWorld;
        using var renderWorld = new RenderWorld(initialEntityCapacity: 2048, execution: new WorldJobs(JobSystem.Default));
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor());


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
            script is null && application is null ? new RenderInstanceOptions
            {
                RowCapacity = Math.Max(1, scene.MeshInstanceCount),
                BatchCapacity = 1,
            } : new RenderInstanceOptions());
        var cluster = new ClusterRenderResources(
            backend,
            device,
            coordinator,
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

        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue computeQueue = backend.GetQueue(device, QueueType.Compute);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);
        using var renderGraph = new global::SomeEngine.RenderGraph.RenderGraph(
            backend,
            device,
            options.AsyncCompute ? [graphicsQueue, computeQueue, copyQueue] : [graphicsQueue, copyQueue],
            new RenderGraphDesc(
                // Instance and readback storage already limit this renderer to two in-flight
                // frames. Match that count instead of rotating a third copy of graph data.
                MaximumFramesInFlight: 2,
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
        var shadowSettings = (script?.ShadowSettings ?? new VirtualShadowMapSettings()) with { Enabled = options.VirtualShadows };
        ClusterPipelineSystem pipeline = CreatePipeline(
            outputDescription,
            metrics: options.VerifyFrameOutput);
        using var virtualShadows = new VirtualShadowFeature(
            backend,
            device,
            virtualShadowConfiguration,
            shadowSettings,
            renderGraph,
            pipeline,
            lighting);
        stageResources.Add(virtualShadows);
        var frameSystems = new RenderFrameSystems(renderWorld, instances);
        var geometry = new ApplicationGeometryContext(backend, device, renderDevice, cluster, renderGraph,
            mainWorld, renderWorld, extraction, prepareSystems, frameSystems);
        var graphCompletionSlots = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        var graphCompletions = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        FrameOutputVerifier? outputVerifier = null;
        RuntimeUiRenderer? ui = null;
        RuntimePresentationPipeline? presentation = null;
        TemporalFeature? temporal = null;
        GraphPassId temporalProducer = default;
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
                SoftwareRasterEdgeThreshold = script?.RasterEdgePixels ?? 4,
                UseInstanceGroups = options.UseInstanceGroups,
                EnableFrameMetricsReadback = metrics,
            });

        void CaptureRenderGraphDiagnostics(in RenderGraphDiagnosticsView diagnostics)
        {
            latestGraphStatistics = diagnostics.Statistics;
            hasGraphStatistics = true;
            analyzedFrames++;
            if (latestGraphStatistics.AnalysisReused) reusedAnalysisFrames++;
        }

        void AddFrameSystems(
            RenderFrameSystems systems,
            ClusterPipelineSystem receiver,
            VirtualShadowFeature virtualShadowFeature)
        {
            _ = systems.Add(lightSystem);
            _ = systems.Add(new LightingFeatureSystem(
                lighting,
                lightSystem,
                outputDescription.ViewCount));
            _ = systems.Add(new VirtualShadowFeatureSystem(
                virtualShadowFeature,
                lightSystem));
            _ = systems.Add(receiver);
        }

        int frameIndex = 0;
        uint submittedFrames = 0;
        var sceneEdit = script is null && application is null ? null : new SceneEdit(scene, window);
        var applicationFrame = application is null ? null : new ApplicationFrame(sceneEdit!, input, cluster, startup, device.Adapter);
        int actionIndex = 0;
        try
        {
            geometry.Install(application?.GeometryFeatures ?? []);
            _ = prepareSystems.Add(new ClusterResidencySystem(cluster));
            _ = prepareSystems.Add(materialSystem);
            _ = prepareSystems.Add(instanceSystem);
            AddFrameSystems(frameSystems, pipeline, virtualShadows);
            frameSystems.Initialize();
            if (!options.SynchronousFrames && script is null)
            {
                if (options.ScenePlayback is not null)
                    scene.Play(0, options.ScenePlayback is "materials" or "camera", animateMaterial,
                        orbitCamera: options.ScenePlayback == "camera");
                geometry.UpdateGame(0);
                extraction.Extract(mainWorld);
            }
            outputVerifier = options.VerifyFrameOutput || options.FrameOutputPath is not null
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
            while (options.FrameLimit == 0 || frameIndex < options.FrameLimit)
            {
                long frameStarted = Stopwatch.GetTimestamp();
                if (startup.Result is not null && startup.IsTracing)
                {
                    if (nextGameUpdate is not null)
                    {
                        RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
                        nextGameUpdate = null;
                    }
                    startup.FinishTrace();
                }
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

                if (sceneEdit is not null)
                {
                    sceneEdit.Begin();
                    try
                    {
                        while (script is not null && actionIndex < script.Actions.Count && script.Actions[actionIndex].Frame == frameIndex)
                            script.Actions[actionIndex++].Execute(sceneEdit);
                        if (applicationFrame is not null)
                        {
                            applicationFrame.Index = frameIndex;
                            applicationFrame.Width = window.ClientWidth;
                            applicationFrame.Height = window.ClientHeight;
                            application!.UpdateFrame?.Invoke(applicationFrame);
                        }
                    }
                    catch (Exception error)
                    {
                        throw new ApplicationRunException(ApplicationFailure.SceneAction, checked((int)submittedFrames), error);
                    }
                    finally { sceneEdit.End(); }
                }

                int clientWidth = window.ClientWidth;
                int clientHeight = window.ClientHeight;
                bool cameraCut = sceneEdit?.CameraCut == true || script?.UseCameraHistory == false;
                if (sceneEdit?.ShadowMapRefreshRequested == true) virtualShadows.InvalidateAllCasters();
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
                    outputVerifier = options.VerifyFrameOutput || options.FrameOutputPath is not null
                        ? new FrameOutputVerifier(backend, device, width, height, PresentationFormat)
                        : null;
                }

                long currentFrameTimestamp = Stopwatch.GetTimestamp();
                float deltaSeconds = Math.Clamp(
                    (float)Stopwatch.GetElapsedTime(previousFrameTimestamp, currentFrameTimestamp).TotalSeconds,
                    1.0f / 1_000_000.0f,
                    1.0f / 10.0f);
                if (options.FixedFrameSeconds > 0) deltaSeconds = options.FixedFrameSeconds;
                float sceneSeconds = options.FixedFrameSeconds > 0
                    ? frameIndex * options.FixedFrameSeconds
                    : (float)Stopwatch.GetElapsedTime(runtimeStarted).TotalSeconds;
                previousFrameTimestamp = currentFrameTimestamp;
                if (options.ScenePlayback is not null && options.SynchronousFrames)
                    scene.Play(sceneSeconds, options.ScenePlayback is "materials" or "camera", animateMaterial,
                        orbitCamera: options.ScenePlayback == "camera");
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
                    submittedFrames,
                    temporalResolveEnabled: options.TemporalAntialiasing,
                    cameraCut: cameraCut);
                viewSystem.Stage(viewFrame.View);
                if (application?.DebugUiEnabled != false) ui.DrawDebugWindow(
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
                        window.HasFocus,
                        options.TemporalAntialiasing), application?.DrawDebugUi);
                bool reportMilestones = frameIndex == 0;
                if (reportMilestones)
                    Console.WriteLine("First frame: applying extracted scene.");
                if (options.SynchronousFrames || script is not null)
                {
                    if (animateScene && options.ScenePlayback is null)
                        scene.Update(sceneSeconds);
                    geometry.UpdateGame(deltaSeconds);
                    extraction.Extract(mainWorld);
                }
                long prepareStarted = Stopwatch.GetTimestamp();
                startup.RecordStage("FrameSetup");
                if (sceneEdit?.Images.Count > 0)
                    outputVerifier ??= new FrameOutputVerifier(backend, device, width, height, PresentationFormat);
                Prepare(
                    prepareSystems,
                    cluster,
                    streamingConsumer,
                    coordinator,
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
                long reconcileStarted = Stopwatch.GetTimestamp();
                ClusterPipelineOutputs clusterOutputs;
                using (EngineTrace.Begin(ApplicationTrace.Reconcile))
                    clusterOutputs = pipeline.ReconcileGraph();
                double reconcileMs = Stopwatch.GetElapsedTime(reconcileStarted).TotalMilliseconds;
                startup.RecordStage("ReconcileGraph");
                bool connectPostProcessing = temporal is null;
                temporal ??= new TemporalFeature(
                    backend,
                    device,
                    renderGraph,
                    boot.Width,
                    boot.Height,
                    temporalShader,
                    boot.TemporalVertexEntry,
                    boot.TemporalPixelEntry);
                if (connectPostProcessing || temporalProducer != pipeline.ShadeCompletePass)
                {
                    RenderGraphEdit edit = renderGraph.BeginEdit();
                    try
                    {
                        temporal.OrderAfter(ref edit, pipeline.ShadeCompletePass);
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
                    temporal.Output.ResolvedColor,
                    temporal.Output.Format,
                    temporal.Output.Width,
                    temporal.Output.Height,
                    temporal.Output.CompletePass,
                    pipeline.PageFaultReadbackPass,
                    presentationShader,
                    boot.PresentationVertexEntry,
                    boot.PresentationPixelEntry,
                    PresentationFormat);
                ui.InstallGraph(renderGraph, presentation.Target);
                if (reportMilestones)
                    Console.WriteLine("First frame: acquiring presentation image.");
                SwapchainImage? image = sceneEdit?.FrameCanceled == true
                    ? null : AcquireImage(backend, swapchain, window);
                long executeStarted = Stopwatch.GetTimestamp();
                startup.RecordStage("PostProcessing");
                FrameOutputMetrics? frameOutput = ExecuteFrame(
                    backend,
                    renderGraph,
                    renderDevice,
                    graphicsQueue,
                    graphCompletionSlots,
                    graphCompletions,
                    coordinator,
                    frameSystems,
                    geometry,
                    pipeline,
                    streamingConsumer,
                    virtualShadows,
                    clusterOutputs,
                    temporal,
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
                    options.SynchronousFrames,
                    options.TemporalAntialiasing,
                    viewFrame.JitterPixels,
                    script?.TemporalSettings ?? TemporalResolveSettings.Default);
                if (image is null)
                {
                    frameIndex++;
                    continue;
                }
                submittedFrames++;
                double executeMs = Stopwatch.GetElapsedTime(executeStarted).TotalMilliseconds;
                startup.RecordStage("ExecuteFrame");
                if (sceneEdit?.Images.Count > 0)
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
                        outputVerifier.Dispose();
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
                        // CPU rendering has finished reading shared resources. Apply next-frame
                        // resource edits here while submitted GPU work retains its device data.
                        if (options.ScenePlayback is not null)
                            scene.Play(nextSceneTime, options.ScenePlayback is "materials" or "camera", animateMaterial,
                                orbitCamera: options.ScenePlayback == "camera");
                        else if (updateScene)
                            scene.Update(nextSceneTime);
                        if (script is null)
                        {
                            geometry.UpdateGame(deltaSeconds);
                            extraction.Extract(mainWorld);
                        }
                    });
                }

                if (reportMilestones)
                    Console.WriteLine("First frame: presenting.");
                PresentStatus present = backend.Present(
                    backend.GetQueue(device, QueueType.Graphics),
                    image.Value);
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

                if (applicationFrame is not null)
                {
                    applicationFrame.Timing = new(Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds,
                        prepareMs, reconcileMs, executeMs);
                    application!.FrameCompleted?.Invoke(applicationFrame);
                }
                frameIndex++;
                if (application?.ReloadRequested == true) break;
            }

            if (frameIndex > 0)
            {
                if (options.FrameOutputPath is not null)
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
            if (error is ApplicationRunException) throw;
            throw new ApplicationRunException(ApplicationRunException.Classify(error, ApplicationFailure.Rendering), checked((int)submittedFrames), error);
        }
        finally
        {
            if (nextGameUpdate is not null)
                RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
            outputVerifier?.Dispose();
            coordinator.WaitForTrackedSubmissions();
            ui?.Dispose();
            presentation?.Dispose();
            temporal?.Dispose();
            frameSystems.Dispose();
            Shutdown(prepareSystems, cluster, instances, coordinator);
            extraction.Dispose();
            geometry.DisposeFeatures();
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
            using (EngineTrace.Begin(ApplicationTrace.ExtractionApply))
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
            using (EngineTrace.Begin(ApplicationTrace.PrepareSystems))
                systems.Update(scope);
            using (EngineTrace.Begin(ApplicationTrace.PrepareCommit))
                scope.Commit();
        }
    }

    private static FrameOutputMetrics? ExecuteFrame(
        IGraphicsBackend backend,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        RenderDevice renderDevice,
        Queue graphicsQueue,
        QueueCompletion[] completionSlots,
        QueueCompletion[] completions,
        RenderFrameCoordinator coordinator,
        RenderFrameSystems systems,
        ApplicationGeometryContext geometry,
        ClusterPipelineSystem pipeline,
        ClusterStreamingFeedbackConsumer streamingConsumer,
        VirtualShadowFeature virtualShadows,
        in ClusterPipelineOutputs clusterOutputs,
        TemporalFeature temporal,
        RuntimePresentationPipeline presentation,
        RuntimeUiRenderer ui,
        SwapchainImage? image,
        int width,
        int height,
        NativeWindow window,
        bool reportMilestones,
        bool cameraCut,
        Entity viewEntity,
        FrameOutputVerifier? outputVerifier,
        RenderGraphDiagnosticsHandler? graphDiagnostics,
        bool synchronousFrames,
        bool temporalAntialiasing,
        Vector2 jitterPixels,
        TemporalResolveSettings temporalSettings)
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
                    new RenderGraphFrameOptions(
                        SubmissionMode: FrameSubmissionMode.RecordAhead,
                        Diagnostics: graphDiagnostics));
                try
                {
                    geometry.Observe(ref graphFrame);
                    if (reportMilestones)
                        Console.WriteLine("First frame: recording render systems.");
                    if (image is { } acquired) presentation.Bind(ref graphFrame, acquired, graphicsQueue);
                    using (EngineTrace.Begin(ApplicationTrace.RenderSystems))
                        systems.Update(frame, graphFrame);
                    renderDevice.OrderTextureUploadsBefore(ref graphFrame, presentation.DrawPass);
                    graphFrame.ObserveExecution(streamingConsumer);
                    var motion = new MotionFeatureOutput(
                        clusterOutputs.MotionVectors,
                        clusterOutputs.Width,
                        clusterOutputs.Height,
                        clusterOutputs.ViewCount);
                    using (EngineTrace.Begin(ApplicationTrace.TemporalRecording))
                        temporal.Record(
                            ref graphFrame,
                            clusterOutputs.MaterialColor,
                            clusterOutputs.Depth,
                            motion,
                            temporalSettings,
                            enabled: temporalAntialiasing,
                            cameraCut: cameraCut,
                            view: viewEntity,
                            jitterPixels: jitterPixels);
                    ui.Record(ref graphFrame, width, height);
                    if (image is null) return null;
                    outputVerifier?.Record(ref graphFrame, presentation.Target, graphicsQueue);
                    if (reportMilestones)
                        Console.WriteLine("First frame: analyzing and submitting render graph.");

                    int submittedQueueCount = graphFrame.Execute(completionSlots);
                    int completionCount = CompactCompletions(
                        completionSlots,
                        completions,
                        submittedQueueCount);
                    ReadOnlySpan<QueueCompletion> execution = completions.AsSpan(0, completionCount);
                    using (EngineTrace.Begin(ApplicationTrace.FrameCompletion))
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
        string root = AppContext.BaseDirectory;
        string manifest = Path.Combine(root, "Library", "AssetManifest", AssetManifest.CatalogFileName);
        if (File.Exists(manifest)) return root;
        throw new DirectoryNotFoundException(
            "The application directory has no Library/AssetManifest/catalog.json. Build this project's content before starting the application.");
    }

    private static string contentRootLabel(string value) => Path.GetFullPath(value);
}
