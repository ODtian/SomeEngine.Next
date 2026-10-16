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
        Func<Material, float, bool>? animateMaterial = null, SceneTestPlatform? platform = null, SceneScript? script = null) => Task.Factory.StartNew(() =>
    {
        using var shaderResources = new RuntimeShaderResources();
        using var display = new ApplicationDisplay();
        int frames = 0;
        do
        {
            if (platform is not null) options = platform.Options with { MeasureBlending = platform.MeasureBlending };
            platform?.BeginScene();
            try
            {
                frames += RunScene(options, useWarp, startup, animateMaterial, platform, display, shaderResources, script);
                startup.FinishTrace();
                platform?.EndScene();
            }
            catch (Exception error)
            {
                platform?.EndScene(error.Message);
                throw new ApplicationRunException(ApplicationRunException.Classify(error, ApplicationFailure.ContentLoad),
                    frames + (error is ApplicationRunException run ? run.CompletedFrames : 0), error);
            }
        } while (platform?.ReloadRequested == true);
        return frames;
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static int RunScene(RuntimeStartupOptions options, bool useWarp, ApplicationStartupTiming startup,
        Func<Material, float, bool>? animateMaterial, SceneTestPlatform? platform,
        ApplicationDisplay display, RuntimeShaderResources shaderResources, SceneScript? script)
    {
        ArgumentNullException.ThrowIfNull(options);
        string contentRoot = script?.ContentDirectory ?? platform?.Current.ContentDirectory ?? FindContentRoot();
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
                RuntimeScene scene = await RuntimeScene.CreateAsync(world, assets, LoadShaderAsync).ConfigureAwait(false);
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
            platform?.SetRenderingInfo(boot.Width, boot.Height, display.Device.Adapter.Name, display.Device.Adapter.DriverVersion);
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
                forceHardwareRaster: useWarp || options.ForceHardwareRaster, animateMaterial, platform, script);
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
        string profile = target == SlangCompileTarget.Dxil ? "sm_6_6" : "spirv_1_5";
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
                new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(0, 0)),
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
                        Device.Adapter.HardwareAccelerated, Window);
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
                requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands |
                    (options.MeasureBlending ? DeviceFeatures.CalibratedTimestamps : DeviceFeatures.None),
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

    private static bool ReconfigureSwapchain(
        IGraphicsBackend backend,
        Swapchain swapchain,
        RuntimeStartupOptions options,
        int width,
        int height,
        bool hardwareAccelerated,
        NativeWindow window) =>
        ReconfigureSwapchain(backend, swapchain, window,
            CreateSwapchainConfig(options, width, height, hardwareAccelerated));

    private static bool ReconfigureSwapchain(
        IGraphicsBackend backend,
        Swapchain swapchain,
        NativeWindow window,
        SwapchainConfig config)
    {
        long started = Stopwatch.GetTimestamp();
        while (window.PumpMessages())
        {
            if (window.IsMinimized || window.ClientWidth <= 0 || window.ClientHeight <= 0)
            {
                window.WaitForEvents(Timeout.InfiniteTimeSpan);
                started = Stopwatch.GetTimestamp();
                continue;
            }
            config = config with
            {
                Width = checked((uint)window.ClientWidth),
                Height = checked((uint)window.ClientHeight),
            };
            ReconfigureStatus status = backend.Reconfigure(swapchain, config);
            if (status == ReconfigureStatus.Success)
                return true;
            if (status == ReconfigureStatus.Unsupported)
                throw new NotSupportedException("The Runtime swapchain configuration is unsupported.");
            if (Stopwatch.GetElapsedTime(started) >= FrameTimeout)
                throw new TimeoutException("Presentation work did not finish before the swapchain reconfiguration timeout.");
            // Busy is an outstanding image/present fence, not an occlusion pacing policy.
            window.WaitForEvents(TimeSpan.FromMilliseconds(1));
        }
        return false;
    }

    private static SwapchainImage? AcquireImage(
        IGraphicsBackend backend,
        Swapchain swapchain,
        NativeWindow window)
    {
        long started = Environment.TickCount64;
        while (true)
        {
            if (window.CloseRequested || window.IsMinimized)
                return null;
            if (Environment.TickCount64 - started >= FrameTimeout.TotalMilliseconds)
                throw new TimeoutException("No presentation image became available before the frame timeout.");
            SwapchainAcquireStatus status = backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.Zero, PreserveContents: false),
                out SwapchainImage image);
            if (status == SwapchainAcquireStatus.Success)
                return image;
            if (status == SwapchainAcquireStatus.OutOfDate)
            {
                SwapchainConfig previous = swapchain.Info.Config;
                if (!ReconfigureSwapchain(backend, swapchain, window, previous))
                    return null;
                if (previous.Width != swapchain.Info.Config.Width || previous.Height != swapchain.Info.Config.Height)
                    return null;
                continue;
            }
            if (!window.PumpMessages())
                return null;
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
        bool forceHardwareRaster, Func<Material, float, bool>? animateMaterial, SceneTestPlatform? platform, SceneScript? script)
    {
        using World mainWorld = loadedWorld;
        using var renderWorld = new RenderWorld(initialEntityCapacity: 2048, execution: new WorldJobs(JobSystem.Default));
        using var extraction = new RenderExtractionSystems(renderWorld, new WorldOriginRenderExtractor(), new MeshRenderExtractor(), new LightRenderExtractor(), new XrayRenderExtractor(), new MaterialDecalRenderExtractor());


        if (!options.SynchronousFrames && script is null)
        {
            if (options.ScenePlayback is not null)
                scene.Play(0, options.ScenePlayback is "materials" or "camera", animateMaterial,
                    orbitCamera: options.ScenePlayback == "camera");
            if (platform?.InitializeScene is null && platform?.UpdateScene is null) extraction.Extract(mainWorld);
        }
        RuntimeViewFrame initialView = RuntimeViewFrame.Create(
            scene.Camera.View,
            scene.Projection(boot.Width, boot.Height),
            boot.Width,
            boot.Height,
            temporalFrameIndex: 0,
            cameraTarget: scene.Camera.Target,
            depthOfField: scene.DepthOfField, playerId: scene.PlayerId, firstPerson: scene.FirstPerson);
        Entity viewEntity = renderWorld.CreateEntity(initialView.View);

        using var renderDevice = new RenderDevice(backend, device, FrameTimeout);
        var dataLock = new RenderDataLock();
        var instances = new RenderInstanceStorageSystem(
            backend,
            device,
            renderWorld,
            ClusterRenderFeature.InstanceLayout,
            script is null && platform?.InitializeScene is null && platform?.UpdateScene is null ? new RenderInstanceOptions
            {
                RowCapacity = Math.Max(1, scene.MeshInstanceCount),
                BatchCapacity = 1,
            } : new RenderInstanceOptions());
        var cluster = new ClusterRenderResources(
            backend,
            device,
            renderWorld,
            instances,
            platform is null && script is null ? null : new ClusterRenderOptions
            {
                Residency = new ClusterResidencyOptions
                {
                    PageHeapCapacityBytes = script?.GeometryMemoryBytes ?? checked((uint)platform!.GeometryMemoryMegabytes * 1024 * 1024),
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
        _ = prepareSystems.Add(new ClusterResidencySystem(cluster));
        _ = prepareSystems.Add(materialSystem);
        _ = prepareSystems.Add(instanceSystem);

        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue computeQueue = backend.GetQueue(device, QueueType.Compute);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);
        using var renderGraph = new global::SomeEngine.RenderGraph.RenderGraph(
            backend,
            device,
            options.AsyncCompute ? [graphicsQueue, computeQueue, copyQueue] : [graphicsQueue, copyQueue],
            new RenderGraphDesc(
                MaximumFramesInFlight: 2,
                Label: "SomeEngine Runtime Render Graph"));
        GraphRegionId sceneRegion, primaryGeometryRegion, primaryDepthAfterRegion, targetGeometryRegion;
        GraphRegionId targetGeometryAfterRegion, shadingRegion, temporalRegion, presentationRegion, uiRegion, feedbackRegion;
        GraphRegionId frameOutputRegion, linearOutputRegion, debugViewsRegion, colorEffectsRegion;
        using (RenderGraphEdit edit = renderGraph.BeginEdit())
        {
            _ = edit.AddRegion("Frame uploads");
            sceneRegion = edit.AddRegion("Scene");
            primaryGeometryRegion = edit.AddRegion("Primary view geometry");
            primaryDepthAfterRegion = edit.AddRegion("After primary view depth");
            targetGeometryRegion = edit.AddRegion("Target view geometry");
            targetGeometryAfterRegion = edit.AddRegion("After target view geometry");
            shadingRegion = edit.AddRegion("Shading");
            debugViewsRegion = edit.AddRegion("Independent debug views");
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
        var outputDescription = new ClusterPipelineOutputDescription(boot.Width, boot.Height, 1);
        float rasterEdgePixels = script?.RasterEdgePixels ?? 32;
        using var lighting = new LightingFeature(
            backend,
            device,
            renderGraph,
            outputDescription.Width,
            outputDescription.Height,
            outputDescription.ViewCount,
            lightingShader,
            boot.LightingEntryPoint,
            region: sceneRegion);
        var stageResources = new GraphResourceGroup();
        stageResources.Add(lighting);
        var lightSystem = new RenderLightSetSystem();
        _ = prepareSystems.Add(lightSystem);
        var streamingFeedback = new ClusterStreamingFeedbackChannel();
        var streamingConsumer = new ClusterStreamingFeedbackConsumer(cluster, streamingFeedback);
        var shadowSettings = (script?.ShadowSettings ?? new VirtualShadowMapSettings
        {
            DynamicResolutionEnabled = true,
        }) with { Enabled = options.VirtualShadows };
        if (options.ShadowRayCount is { } rayCount)
            shadowSettings = shadowSettings with
            {
                DirectionalRayCount = rayCount,
                LocalRayCount = rayCount,
                MaxFilterRadiusTexels = rayCount == 0 ? 0 : shadowSettings.MaxFilterRadiusTexels,
            };
        ClusterShadingPath shadingPath = script?.ShadingPath ?? options.ShadingPath;
        DebugViewSettings[] debugViewSettings = (script?.DebugViews ?? options.DebugViews).ToArray();
        ClusterPipelineSystem pipeline = CreatePipeline(
            outputDescription,
            metrics: options.VerifyFrameOutput || options.LinearFrameOutputPath is not null || script is not null);
        if (platform is not null && options.MeasureBlending)
            pipeline.BlendFrameCompleted += platform.RecordBlendFrame;
        if (script?.BlendFrameCompleted is { } blendCompleted)
            pipeline.BlendFrameCompleted += blendCompleted;
        using var virtualShadows = new VirtualShadowFeature(
            backend,
            device,
            virtualShadowConfiguration,
            shadowSettings,
            renderGraph,
            pipeline,
            lighting,
            primaryDepthAfterRegion,
            targetGeometryAfterRegion,
            feedbackRegion,
            renderDevice);
        if (platform?.IsMeasuring == true)
            virtualShadows.ResolutionBiasCompleted += platform.RecordShadowResolutionBias;
        stageResources.Add(virtualShadows);
        RenderFrameSystems frameSystems = CreateFrameSystems(
            pipeline,
            virtualShadows);
        var graphCompletionSlots = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        var graphCompletions = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        FrameOutputVerifier? outputVerifier = null;
        FrameOutputVerifier? linearVerifier = null;
        RuntimeUiRenderer? ui = null;
        RuntimePresentationPipeline? presentation = null;
        TemporalFeature? temporal = null;
        IApplicationColorFeature? colorFeature = null;
        var debugViews = new DebugViews(renderGraph, debugViewsRegion,
            (settings, regions) => CreatePipeline(new ClusterPipelineOutputDescription(settings.Width, settings.Height, 1),
                metrics: false, preview: settings, previewRegions: regions),
            receiver =>
            {
                var systems = new RenderFrameSystems(renderWorld, instances);
                _ = systems.Add(receiver);
                return systems;
            });
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
            bool metrics,
            DebugViewSettings? preview = null,
            ClusterGraphRegions? previewRegions = null) => new(
            backend,
            device,
            renderDevice,
            cluster,
            instanceSystem,
            ClusterRenderFeature.InstanceLayout,
            materialAssignments,
            pipelineConfiguration,
            renderGraph,
            previewRegions ?? new ClusterGraphRegions(primaryGeometryRegion, targetGeometryRegion, shadingRegion, feedbackRegion),
            description,
            streamingFeedback,
            preview is null ? stageResources : null,
            new ClusterPipelineOptions
            {
                ShadingPath = shadingPath,
                DebugMode = preview?.Mode ?? 0,
                EnableAsyncCompute = options.AsyncCompute,
                ForceHardwareRaster = forceHardwareRaster,
                SoftwareRasterEdgeThreshold = rasterEdgePixels,
                MaxCandidates = script?.ClusterWorkCapacity ?? 1_048_576,
                UseInstanceGroups = options.UseInstanceGroups,
                EnableFrameMetricsReadback = metrics,
                MeasureBlending = preview is null && options.MeasureBlending,
                BlendIndex = options.BlendIndex,
                BlendTriangleCapacity = script?.BlendTriangleCapacity ?? 262_144,
                BlendReferenceCapacity = script?.BlendReferenceCapacity ?? 1_048_576,
            }, viewGrid: lighting.ViewGrid);

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

        int frameIndex = 0;
        uint submittedFrames = 0;
        var sceneEdit = script is not null || platform?.InitializeScene is not null || platform?.UpdateScene is not null
            ? new SceneEdit(scene, window) : null;
        int actionIndex = 0;
        int? captureFrame = options.CaptureFrame ?? (options.FrameLimit > 0 ? options.FrameLimit - 1 : null);
        bool outputSaved = false, linearOutputSaved = false;
        try
        {
            frameSystems.Initialize();
            outputVerifier = options.VerifyFrameOutput || (options.FrameOutputPath is not null && captureFrame is null)
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
                $"Runtime configuration '{title}' loaded from {contentRootLabel(script?.ContentDirectory ?? platform?.Current.ContentDirectory ?? FindContentRoot())}; " +
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
                double gameUpdateWaitMs = 0;
                if (startup.Result is not null && startup.IsTracing)
                {
                    if (nextGameUpdate is not null)
                    {
                        long gameUpdateWaitStarted = Stopwatch.GetTimestamp();
                        RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
                        gameUpdateWaitMs += Stopwatch.GetElapsedTime(gameUpdateWaitStarted).TotalMilliseconds;
                        nextGameUpdate = null;
                    }
                    startup.FinishTrace();
                }
                using var frameTrace = EngineTrace.BeginFrame(++traceFrameIndex);
                // Shared asset content can change only after the Game World extraction reader exits.
                if (nextGameUpdate is not null)
                {
                    long gameUpdateWaitStarted = Stopwatch.GetTimestamp();
                    RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
                    gameUpdateWaitMs += Stopwatch.GetElapsedTime(gameUpdateWaitStarted).TotalMilliseconds;
                    nextGameUpdate = null;
                }
                if (sceneEdit is not null)
                {
                    sceneEdit.Begin();
                    try
                    {
                        if (frameIndex == 0 && platform?.InitializeScene is { } initializeScene)
                            initializeScene(sceneEdit, platform.Current);
                        if (platform?.UpdateScene is { } updateScene)
                            updateScene(sceneEdit, platform.Current, frameIndex);
                        if (script is not null)
                            while (actionIndex < script.Actions.Count && script.Actions[actionIndex].Frame == frameIndex)
                                script.Actions[actionIndex++].Execute(sceneEdit);
                    }
                    catch (Exception error)
                    {
                        throw new ApplicationRunException(ApplicationFailure.SceneAction, checked((int)submittedFrames), error);
                    }
                    finally { sceneEdit.End(); }
                }
                if (sceneEdit?.ShadingPath is { } requestedPath) shadingPath = requestedPath;
                if (sceneEdit?.DebugViews is { } requestedViews) debugViewSettings = requestedViews;
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
                if (platform is not null && !platform.IsMeasuring)
                {
                    for (int index = 0; index < Math.Min(platform.Scenes.Count, 3); index++)
                        if (input.WasKeyPressed(0x71 + index)) platform.SelectScene(index);
                    if (input.WasKeyPressed(0x77)) platform.CaptureImage();
                    if (input.WasKeyPressed(0x78)) platform.StartMeasurements();
                    if (input.WasKeyPressed(0x79)) platform.StartMeasurements(recordTrace: true);
                }
                if (input.WasKeyPressed(RuntimeInput.KeyF1))
                    debugUiOpen = !debugUiOpen;
                if (window.IsMinimized)
                {
                    platform?.InvalidateMeasurements("The window was minimized during measurement.");
                    window.WaitForEvents(TimeSpan.FromMilliseconds(16));
                    previousFrameTimestamp = Stopwatch.GetTimestamp();
                    continue;
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
                    linearVerifier?.Dispose();
                    linearVerifier = null;
                    if (!ReconfigureSwapchain(
                        backend,
                        swapchain,
                        options,
                        clientWidth,
                        clientHeight,
                        device.Adapter.HardwareAccelerated,
                        window))
                        break;
                    width = checked((int)swapchain.Info.Config.Width);
                    height = checked((int)swapchain.Info.Config.Height);
                    cameraCut = true;
                    outputVerifier = options.VerifyFrameOutput || (options.FrameOutputPath is not null && captureFrame is null)
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
                    cameraCut: cameraCut,
                    cameraTarget: scene.Camera.Target,
                    depthOfField: scene.DepthOfField, playerId: scene.PlayerId, firstPerson: scene.FirstPerson);
                viewSystem.Stage(viewFrame.View);
                ClusterShadingPath previousShadingPath = shadingPath;
                DebugViewSettings[] previousDebugViews = debugViewSettings;
                if (platform?.IsMeasuring != true) ui.DrawDebugWindow(
                    ref debugUiOpen,
                    ref animateScene,
                    ref shadingPath,
                    ref debugViewSettings,
                    new RuntimeUiMetrics(
                        frameIndex,
                        deltaSeconds,
                        width,
                        height,
                        window.DpiScale,
                        viewFrame.JitterPixels,
                        scene.Camera.Position,
                        window.HasFocus,
                        options.TemporalAntialiasing), platform);
                if (shadingPath != previousShadingPath || !ReferenceEquals(debugViewSettings, previousDebugViews))
                    platform?.SetViewOptions(shadingPath, debugViewSettings);
                DebugViewSettings[] activeDebugViews = platform?.IsMeasuring == true ? [] : debugViewSettings;
                bool reportMilestones = frameIndex == 0;
                if (reportMilestones)
                    Console.WriteLine("First frame: applying extracted scene.");
                if (options.SynchronousFrames || script is not null || platform?.UpdateScene is not null ||
                    (frameIndex == 0 && platform?.InitializeScene is not null))
                {
                    if (animateScene && options.ScenePlayback is null)
                        scene.Update(sceneSeconds);
                    extraction.Extract(mainWorld);
                }
                long frameSlotAcquireStarted = Stopwatch.GetTimestamp();
                if (!renderGraph.TryOpenNextFrame(out RenderGraphNextFrame nextFrame, FrameTimeout))
                    throw new TimeoutException("The next rendering frame exceeded its resource wait timeout.");
                double frameSlotAcquireMs = Stopwatch.GetElapsedTime(frameSlotAcquireStarted).TotalMilliseconds;
                using var nextFrameResources = nextFrame;
                renderDevice.CollectCompleted();
                long prepareStarted = Stopwatch.GetTimestamp();
                startup.RecordStage("FrameSetup");
                bool selectedCapture = captureFrame == frameIndex;
                bool captureBeauty = options.VerifyFrameOutput || platform?.ImageRequested == true || sceneEdit?.Images.Count > 0 ||
                    (options.FrameOutputPath is not null && (selectedCapture || captureFrame is null));
                bool captureLinear = sceneEdit?.LinearImages.Count > 0 ||
                    (selectedCapture && options.LinearFrameOutputPath is not null);
                if (captureBeauty)
                    outputVerifier ??= new FrameOutputVerifier(backend, device, width, height, PresentationFormat);
                if (captureLinear)
                {
                    linearVerifier ??= new FrameOutputVerifier(backend, device, width, height, Format.R16G16B16A16Float);
                    if (shadowSettings.Enabled) virtualShadows.EnableQualityCapture();
                }
                Prepare(
                    prepareSystems,
                    cluster,
                    streamingConsumer,
                    dataLock,
                    nextFrame,
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
                {
                    pipeline.SetShadingPath(shadingPath);
                    if (!debugViews.Matches(activeDebugViews))
                    {
                        if (presentation is not null && debugViews.ChangesOutputs(activeDebugViews))
                            ui.InstallGraph(renderGraph, presentation.Target, uiRegion, debugViews.RetainedImages(activeDebugViews));
                        debugViews.ReconcileSettings(activeDebugViews);
                    }
                    virtualShadows.ReconcileLights(lightSystem.RequireCurrent());
                    virtualShadows.SetPrimaryDepthReceiverRequests(true);
                    clusterOutputs = pipeline.ReconcileGraph();
                    debugViews.ReconcileGraph(shadingPath);
                    platform?.SetShadingInfo(pipeline.InstalledShadingPath, pipeline.DebugMode, debugViews.Images.Length);
                }
                double reconcileMs = Stopwatch.GetElapsedTime(reconcileStarted).TotalMilliseconds;
                startup.RecordStage("ReconcileGraph");
                bool connectColorFeature = temporal is null;
                if (connectColorFeature && platform?.CreateColorFeature is { } createColorFeature)
                    colorFeature = createColorFeature(new(backend, device, renderGraph, boot.Width, boot.Height,
                        viewFrame.View.Projection, colorEffectsRegion) { StencilSource = pipeline.GetStencil }, platform.Current);
                else if (connectColorFeature && script?.CreateColorFeature is { } createScriptColorFeature)
                    colorFeature = createScriptColorFeature(new(backend, device, renderGraph, boot.Width, boot.Height,
                        viewFrame.View.Projection, colorEffectsRegion) { StencilSource = pipeline.GetStencil });
                temporal ??= new TemporalFeature(
                    backend,
                    device,
                    renderGraph,
                    boot.Width,
                    boot.Height,
                    temporalShader,
                    boot.TemporalVertexEntry,
                    boot.TemporalPixelEntry,
                    region: temporalRegion);
                if (connectColorFeature && colorFeature is not null)
                {
                    RenderGraphEdit edit = renderGraph.BeginEdit();
                    try
                    {
                        temporal.OrderAfter(ref edit, colorFeature.CompletePass);
                        edit.Commit();
                    }
                    finally { edit.Dispose(); }
                }
                presentation ??= new RuntimePresentationPipeline(
                    backend,
                    device,
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
                ui.InstallGraph(renderGraph, presentation.Target, uiRegion, debugViews.Images);
                ScissorRect? presentationRect = ui.DrawDebugViews(debugViews, width, height);
                if (reportMilestones)
                    Console.WriteLine("First frame: acquiring presentation image.");
                long acquireImageStarted = Stopwatch.GetTimestamp();
                SwapchainImage? image = sceneEdit?.FrameCanceled == true
                    ? null : AcquireImage(backend, swapchain, window);
                double acquireImageMs = Stopwatch.GetElapsedTime(acquireImageStarted).TotalMilliseconds;
                SceneFrame? capturedFrame = captureLinear ? scene.CaptureFrame(new SceneFrame
                {
                    Frame = frameIndex, SubmittedFrame = submittedFrames, SceneSeconds = sceneSeconds,
                    Width = width, Height = height, CameraPosition = scene.Camera.Position,
                    WorldOrigin = renderWorld.WorldOrigin,
                    View = scene.Camera.View, Projection = viewFrame.View.Projection,
                    UnjitteredProjection = scene.Projection(width, height), JitterPixels = viewFrame.JitterPixels,
                    Options = options with { ShadingPath = shadingPath, DebugViews = activeDebugViews.ToArray() },
                    ShadowSettings = shadowSettings,
                    TemporalSettings = script?.TemporalSettings ?? TemporalResolveSettings.Default,
                    RasterEdgePixels = rasterEdgePixels,
                    ClusterWorkCapacity = script?.ClusterWorkCapacity ?? 1_048_576,
                    Adapter = device.Adapter.Name, DriverVersion = device.Adapter.DriverVersion,
                }) : null;
                VirtualShadowFrameQuality? shadowQuality = null;
                long executeStarted = Stopwatch.GetTimestamp();
                pipeline.SetMeasurementFrame(frameIndex);
                startup.RecordStage("PostProcessing");
                FrameOutputMetrics? frameOutput = ExecuteFrame(
                    backend,
                    renderGraph,
                    nextFrame,
                    graphicsQueue,
                    graphCompletionSlots,
                    graphCompletions,
                    renderDevice,
                    dataLock,
                    frameSystems,
                    pipeline,
                    streamingConsumer,
                    virtualShadows,
                    clusterOutputs,
                    colorFeature,
                    temporal,
                    presentation,
                    ui,
                    debugViews,
                    renderWorld.Read<RenderView>(viewEntity),
                    scene.Projection(width, height),
                    presentationRect,
                    image,
                    width,
                    height,
                    window,
                    reportMilestones,
                    cameraCut,
                    viewEntity,
                    captureBeauty ? outputVerifier : null,
                    frameOutputRegion,
                    captureLinear ? linearVerifier : null,
                    linearOutputRegion,
                    captureLinear && shadowSettings.Enabled ? value => shadowQuality = value : null,
                    options.VerifyFrameOutput ? CaptureRenderGraphDiagnostics : null,
                    options.SynchronousFrames,
                    options.TemporalAntialiasing,
                    viewFrame.JitterPixels,
                    script?.TemporalSettings ?? TemporalResolveSettings.Default,
                    out double graphExecuteMs, out double completionWaitMs);
                if (image is null)
                {
                    platform?.InvalidateMeasurements("No swapchain image was rendered during measurement.");
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
                    if (!options.VerifyFrameOutput && options.FrameOutputPath is null && platform?.ImageRequested != true)
                    {
                        outputVerifier.Dispose();
                        outputVerifier = null;
                    }
                }
                if (captureLinear)
                {
                    ClusterRenderDiagnostics diagnostics = cluster.CaptureDiagnostics();
                    ClusterResidencyDiagnostics residency = diagnostics.Residency;
                    capturedFrame = capturedFrame! with { Quality = new SceneRenderingQuality(shadowQuality,
                        pipeline.CaptureFrameMetrics(), new(diagnostics.Meshes.RegisteredMeshes, diagnostics.Meshes.PublishedMeshes,
                            residency.RegisteredPages, residency.ResidentPages, residency.MissingPages,
                            residency.QueuedPageLoads, residency.ActivePageLoads, residency.PageLoadFailures)) };
                    SceneLinearImage captured = linearVerifier!.CopyLinearImage(capturedFrame);
                    if (selectedCapture && options.LinearFrameOutputPath is not null)
                    {
                        captured.Save(options.LinearFrameOutputPath);
                        linearOutputSaved = true;
                    }
                    try
                    {
                        if (sceneEdit is not null)
                            foreach (Action<SceneLinearImage> receive in sceneEdit.LinearImages) receive(captured);
                    }
                    catch (Exception error)
                    {
                        throw new ApplicationRunException(ApplicationFailure.ImageCallback, checked((int)submittedFrames), error);
                    }
                    linearVerifier.Dispose();
                    linearVerifier = null;
                }
                if (selectedCapture && options.FrameOutputPath is not null)
                {
                    outputVerifier!.SaveImage(options.FrameOutputPath);
                    outputSaved = true;
                    if (!options.VerifyFrameOutput && platform?.ImageRequested != true)
                    {
                        outputVerifier.Dispose();
                        outputVerifier = null;
                    }
                }
                if (frameOutput is FrameOutputMetrics output)
                    verifiedOutput = output;
                if (platform?.ImageRequested == true)
                {
                    string path = platform.ImageDestination();
                    outputVerifier!.SaveImage(path);
                    platform.ImageSaved(path);
                    outputVerifier.Dispose();
                    outputVerifier = null;
                }
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
                        if (script is null && platform?.UpdateScene is null) extraction.Extract(mainWorld);
                    });
                }

                if (reportMilestones)
                    Console.WriteLine("First frame: presenting.");
                long presentStarted = Stopwatch.GetTimestamp();
                PresentStatus present = backend.Present(
                    backend.GetQueue(device, QueueType.Graphics),
                    image.Value);
                double presentMs = Stopwatch.GetElapsedTime(presentStarted).TotalMilliseconds;
                if (present is not (PresentStatus.Success or PresentStatus.Suboptimal))
                    platform?.InvalidateMeasurements($"Presentation returned {present}.");
                if (sceneGeometryReady && startup.Result is null)
                {
                    startup.RecordPresent(window, present, frameIndex);
                    if (startup.Result is { } times) platform?.RecordStartup(times);
                }
                if (present == PresentStatus.OutOfDate)
                {
                    if (!ReconfigureSwapchain(
                        backend,
                        swapchain,
                        options,
                        width,
                        height,
                        device.Adapter.HardwareAccelerated,
                        window))
                        break;
                }

                if (platform?.IsMeasuring == true && !platform.ReloadRequested)
                    platform.RecordFrame(new(Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds,
                        prepareMs, reconcileMs, executeMs, frameIndex, gameUpdateWaitMs, frameSlotAcquireMs,
                        acquireImageMs, presentMs, graphExecuteMs, completionWaitMs), cluster.CaptureDiagnostics(), instances,
                        shadowSettings.Enabled && shadowSettings.DynamicResolutionEnabled);
                frameIndex++;
                if (platform?.ReloadRequested == true) break;
            }

            if (frameIndex > 0)
            {
                if (options.FrameOutputPath is not null && captureFrame is null)
                {
                    outputVerifier!.SaveImage(options.FrameOutputPath);
                    outputSaved = true;
                }
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
            renderDevice.WaitForTrackedSubmissions(FrameTimeout);
            renderGraph.CollectCompleted();
            if ((options.FrameOutputPath is not null && !outputSaved) ||
                (options.LinearFrameOutputPath is not null && !linearOutputSaved))
                throw new InvalidOperationException("The requested capture frame was not submitted.");
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
            if (platform is not null)
            {
                virtualShadows.ResolutionBiasCompleted -= platform.RecordShadowResolutionBias;
                pipeline.BlendFrameCompleted -= platform.RecordBlendFrame;
            }
            if (nextGameUpdate is not null)
                RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
            renderDevice.WaitForTrackedSubmissions(FrameTimeout);
            outputVerifier?.Dispose();
            linearVerifier?.Dispose();
            if (ui is not null && presentation is not null) ui.InstallGraph(renderGraph, presentation.Target, uiRegion);
            debugViews.Dispose();
            ui?.Dispose();
            presentation?.Dispose();
            temporal?.Dispose();
            colorFeature?.Dispose();
            frameSystems.Dispose();
            Shutdown(prepareSystems, cluster, instances, dataLock);
            scene.Dispose();
            if (device.Status == DeviceStatus.Active)
                backend.CollectCompleted(device);
        }
    }

    private static void Prepare(
        RenderPrepareSystems systems,
        ClusterRenderResources cluster,
        ClusterStreamingFeedbackConsumer streamingFeedback,
        RenderDataLock dataLock,
        RenderGraphNextFrame nextFrame,
        NativeWindow window,
        bool reportMilestones)
    {
        using var trace = EngineTrace.Begin(ApplicationTrace.Prepare);
        if (!dataLock.TryBeginPrepare(out RenderPrepareScope? scope))
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
                systems.Update(scope, nextFrame);
        }
    }

    private static FrameOutputMetrics? ExecuteFrame(
        IGraphicsBackend backend,
        global::SomeEngine.RenderGraph.RenderGraph graph,
        RenderGraphNextFrame nextFrame,
        Queue graphicsQueue,
        QueueCompletion[] completionSlots,
        QueueCompletion[] completions,
        RenderDevice renderDevice,
        RenderDataLock dataLock,
        RenderFrameSystems systems,
        ClusterPipelineSystem pipeline,
        ClusterStreamingFeedbackConsumer streamingConsumer,
        VirtualShadowFeature virtualShadows,
        in ClusterPipelineOutputs clusterOutputs,
        IApplicationColorFeature? colorFeature,
        TemporalFeature temporal,
        RuntimePresentationPipeline presentation,
        RuntimeUiRenderer ui,
        DebugViews debugViews,
        in RenderView mainView,
        in Matrix4x4 unjitteredProjection,
        ScissorRect? presentationRect,
        SwapchainImage? image,
        int width,
        int height,
        NativeWindow window,
        bool reportMilestones,
        bool cameraCut,
        Entity viewEntity,
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
        out double graphExecuteMs,
        out double completionWaitMs)
    {
        graphExecuteMs = 0;
        completionWaitMs = 0;
        using var trace = EngineTrace.Begin(ApplicationTrace.Execute);
        if (!dataLock.TryBeginRead(out RenderReadScope? readScope))
        {
            RenderDataAccessDiagnostics state = dataLock.CaptureDiagnostics();
            throw new InvalidOperationException(
                "Render-data reading conflicted with prepare: " +
                $"prepareOpen={state.PrepareOpen}, readers={state.OpenReaderCount}.");
        }

        using (readScope)
        {
            FrameOutputMetrics? frameOutput = null;
            completionSlots.AsSpan().Clear();
            try
            {
                RenderGraphFrame graphFrame = graph.BeginFrame(nextFrame,
                    new RenderGraphFrameOptions(
                        SubmissionMode: FrameSubmissionMode.RecordAhead,
                        Diagnostics: graphDiagnostics));
                try
                {
                    if (reportMilestones)
                        Console.WriteLine("First frame: recording render systems.");
                    if (image is { } acquired) presentation.Bind(ref graphFrame, acquired, graphicsQueue, presentationRect);
                    using (EngineTrace.Begin(ApplicationTrace.RenderSystems))
                    {
                        systems.Update(readScope, graphFrame);
                        debugViews.Record(readScope, graphFrame, mainView, unjitteredProjection);
                    }
                    renderDevice.OrderTextureUploadsBefore(ref graphFrame, presentation.DrawPass);
                    var motion = new MotionFeatureOutput(
                        clusterOutputs.MotionVectors,
                        clusterOutputs.Width,
                        clusterOutputs.Height,
                        clusterOutputs.ViewCount);
                    if (image is not null)
                        linearVerifier?.Record(ref graphFrame, clusterOutputs.MaterialColor, graphicsQueue, linearOutputRegion);
                    using (EngineTrace.Begin(ApplicationTrace.TemporalRecording))
                        temporal.Record(
                            ref graphFrame,
                            colorFeature?.Record(ref graphFrame, clusterOutputs.MaterialColor, clusterOutputs.Depth)
                                ?? clusterOutputs.MaterialColor,
                            clusterOutputs.Depth,
                            motion,
                            temporalSettings,
                            enabled: temporalAntialiasing,
                            cameraCut: cameraCut,
                            view: viewEntity,
                            jitterPixels: jitterPixels);
                    ui.Record(ref graphFrame, width, height);
                    if (image is null) return null;
                    if (captureShadowQuality is not null) virtualShadows.CaptureQuality(ref graphFrame, captureShadowQuality);
                    outputVerifier?.Record(ref graphFrame, presentation.Target, graphicsQueue, frameOutputRegion);
                    if (reportMilestones)
                        Console.WriteLine("First frame: analyzing and submitting render graph.");

                    long graphExecuteStarted = Stopwatch.GetTimestamp();
                    int submittedQueueCount = graphFrame.Execute(completionSlots);
                    graphExecuteMs = Stopwatch.GetElapsedTime(graphExecuteStarted).TotalMilliseconds;
                    int completionCount = CompactCompletions(
                        completionSlots,
                        completions,
                        submittedQueueCount);
                    ReadOnlySpan<QueueCompletion> execution = completions.AsSpan(0, completionCount);
                    renderDevice.TrackSubmission(execution);
                    readScope.Dispose();
                    bool readOutput = outputVerifier is not null || linearVerifier is not null;
                    if (synchronousFrames || readOutput)
                    {
                        long completionWaitStarted = Stopwatch.GetTimestamp();
                        Wait(backend, execution, window, synchronousFrames);
                        completionWaitMs = Stopwatch.GetElapsedTime(completionWaitStarted).TotalMilliseconds;
                    }
                    if (outputVerifier is not null) frameOutput = outputVerifier.Read();
                    linearVerifier?.ReadPixels();
                    if (synchronousFrames || readOutput)
                    {
                        graph.CollectCompleted();
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
                        renderDevice.TrackSubmission(accepted);
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

    private static void Shutdown(
        RenderPrepareSystems prepareSystems,
        ClusterRenderResources cluster,
        RenderInstanceStorageSystem instances,
        RenderDataLock dataLock)
    {
        if (!dataLock.TryBeginPrepare(out RenderPrepareScope? scope))
            throw new InvalidOperationException("The final render prepare boundary is not available.");
        using (scope)
        {
            prepareSystems.Shutdown(scope);
            cluster.Shutdown(scope);
            instances.Shutdown(scope);
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
