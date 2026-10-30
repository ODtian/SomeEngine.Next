using System.Diagnostics;
using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Diagnostics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
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

internal static class RuntimeApplication
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(60);

    internal static Task<int> RunAsync<TMaterials>(RuntimeStartupOptions options, bool useWarp, ApplicationStartupTiming startup,
        ApplicationScene? application = null, SceneScript? script = null)
        where TMaterials : ISceneMaterials => Task.Factory.StartNew(() =>
    {
        using var display = new ApplicationDisplay();
        ApplicationContent? content = null;
        try
        {
            if (application is not null) application.ExitRequested = false;
            int frames = 0;
            do
            {
                if (script is null && application is not null) options = application.Options;
                if (application is not null) application.ReloadRequested = false;
                string contentRoot = Path.GetFullPath(script?.ContentDirectory ?? application?.ContentDirectory ?? FindContentRoot());
                IReadOnlyList<string> selectedPacks = (script?.Application ?? application)?.AssetPacks ?? [];
                if (content is null || !content.Assets.MatchesSource(contentRoot, selectedPacks) ||
                    content.GraphicsBackend != options.GraphicsBackend || content.Assets.HasChanges)
                {
                    content?.Assets.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    content = null;
                    SlangCompileTarget target = options.GraphicsBackend == RuntimeGraphicsBackend.Vulkan
                        ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil;
                    IGlobalSession slang = Shader.CreateSlangGlobalSession();
                    SessionDesc shaderSettings = CreateShaderSessionDescription(slang, target);
                    AssetContent assets = display.Wait(AssetContent.OpenAsync(contentRoot, selectedPacks, CancellationToken.None).AsTask());
                    content = new ApplicationContent(contentRoot, options.GraphicsBackend,
                        assets, slang, shaderSettings);
                }
                try
                {
                    application?.SceneStarted?.Invoke(Stopwatch.GetTimestamp());
                    int sceneFrames = RunScene<TMaterials>(options, useWarp, startup, application, display, script, content);
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
            } while (application is { ReloadRequested: true, ExitRequested: false });
            return frames;
        }
        finally { content?.Assets.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static int RunScene<TMaterials>(RuntimeStartupOptions options, bool useWarp, ApplicationStartupTiming startup,
        ApplicationScene? application,
        ApplicationDisplay display, SceneScript? script, ApplicationContent content) where TMaterials : ISceneMaterials
    {
        ArgumentNullException.ThrowIfNull(options);
        SerializationRegistry sceneTypes = SceneSerialization.CreateRegistry();
        (script?.Application ?? application)?.RegisterSceneComponents?.Invoke(sceneTypes);
        AssetLoader assets = content.Files;
        AssetManifest manifest = content.Manifest;
        IReadOnlyList<AssetEntry> runtimeRecords =
            manifest.List(ApplicationSettingsAsset.FileType);
        if ((script?.Application ?? application)?.ApplicationAsset is { } applicationId)
            runtimeRecords = runtimeRecords.Where(record => record.AssetGuid == applicationId.Value).ToArray();
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
        Task<Shader> uiShaderTask = ShaderAsset.GetAsync(assets, boot.UiShader, content.ShaderValues, content.Slang, content.ShaderSettings).AsTask();
        Task<Shader> presentationShaderTask = ShaderAsset.GetAsync(assets, boot.PresentationShader, content.ShaderValues, content.Slang, content.ShaderSettings).AsTask();
        Task<Shader> lightingShaderTask = ShaderAsset.GetAsync(assets, boot.LightingShader, content.ShaderValues, content.Slang, content.ShaderSettings).AsTask();
        Task<Shader> temporalShaderTask = ShaderAsset.GetAsync(assets, boot.TemporalShader, content.ShaderValues, content.Slang, content.ShaderSettings).AsTask();
        Task<World> worldTask = Task.Run(async () =>
        {
            long started = Stopwatch.GetTimestamp();
            WorldAsset sceneAsset = await sceneTask.ConfigureAwait(false);
            World world = await sceneAsset.LoadAsync(sceneTypes,
                new(Execution: new WorldJobs(JobSystem.Default))).ConfigureAwait(false);
            startup.RecordWork("WorldLoad", started);
            return world;
        });
        Task<RuntimeScene> sceneResourcesTask = Task.Run(async () =>
        {
            World world = await worldTask.ConfigureAwait(false);
            long started = Stopwatch.GetTimestamp();
            RuntimeScene scene = await RuntimeScene.CreateAsync<TMaterials>(world, content,
                (script?.Application ?? application)?.InitializeMesh).ConfigureAwait(false);
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
                shaders.References(), assets, content.ShaderValues, content.Slang, content.ShaderSettings).ConfigureAwait(false);
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
                shaders.GetShaderAssetIds(), assets, content.ShaderValues, content.Slang, content.ShaderSettings).ConfigureAwait(false);
            SomeEngine.Resources.Texture? blueNoise = shaders.BlueNoise is { } texture
                ? await TextureAsset.GetAsync(assets, texture, content.TextureValues).ConfigureAwait(false) : null;
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
                display.Initialize(options, useWarp, title, boot, (script?.Application ?? application)?.MeasureShadowInvalidation == true);
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

        ClusterShaders clusterShaders = content.ReuseClusterShaders(pipelineTask.Result);
        VirtualShadowMapShaders shadowShaders = virtualShadowTask.Result;
        Shader uiCode = uiShaderTask.Result;
        Shader presentationCode = presentationShaderTask.Result;
        Shader lightingCode = lightingShaderTask.Result;
        Shader temporalCode = temporalShaderTask.Result;
        content.Shadows = shadowShaders;
        content.SharedShaders = [uiCode, presentationCode, lightingCode, temporalCode];
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
            display,
            display.Backend,
            display.Device,
            display.Swapchain,
            display.Window!,
            worldTask.Result,
            sceneResourcesTask.Result,
            clusterShaders,
            virtualShadowTask.Result,
            uiShaderTask.Result,
            presentationShaderTask.Result,
            lightingShaderTask.Result,
            temporalShaderTask.Result,
            boot,
            title,
            forceHardwareRaster: useWarp || options.ForceHardwareRaster, application, script);
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
        private bool _calibratedTimestampsRequired;
        private ClusterPipelineSet? _clusterPipelines;

        internal void Initialize(RuntimeStartupOptions options, bool useWarp, string title, ApplicationSettingsBlock boot,
            bool measureShadowInvalidation = false)
        {
            bool calibratedTimestampsRequired = options.MeasureBlending || measureShadowInvalidation;
            if (Window is not null && _meshShadersDisabled == options.DisableMeshShaders &&
                _validation == options.DeviceValidation && _calibratedTimestampsRequired == calibratedTimestampsRequired)
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
            _calibratedTimestampsRequired = calibratedTimestampsRequired;
            IGraphicsBackend? previousBackend = Backend;
            if (Window is not null)
            {
                DisposeClusterPipelines();
                Swapchain.Dispose();
                Swapchain = null!;
                Device.Dispose();
                Device = null!;
                Surface.Dispose();
                Surface = null!;
            }
            else Window = NativeWindow.Create(title, boot.Width, boot.Height, visible: !options.HideWindow);
            if (useWarp && options.GraphicsBackend != RuntimeGraphicsBackend.Direct3D12)
                throw new ArgumentException("The WARP adapter is available only with the D3D12 backend.");
            Backend = CreateBackend(
                options.GraphicsBackend,
                options.DeviceValidation);
            previousBackend?.Dispose();
            AdapterId adapterId = useWarp ? SelectAdapter(Backend, true).Id : default;
            DeviceQueueDesc[] queues =
            [
                new(QueueType.Graphics),
                new(QueueType.Compute),
                new(QueueType.Copy),
            ];
            Surface = Backend.CreateSurface(new SurfaceDesc(
                Window.Type,
                Window.Handle,
                Window.DisplayHandle,
                Label: title));
            Device = Backend.CreateDevice(new DeviceDesc(
                adapterId,
                queues,
                requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands |
                    (calibratedTimestampsRequired ? DeviceFeatures.CalibratedTimestamps : DeviceFeatures.None),
                optionalFeatures: DeviceFeatures.CalibratedTimestamps |
                    (options.DisableMeshShaders ? DeviceFeatures.None : DeviceFeatures.MeshShaders),
                label: "SomeEngine Runtime Device"));
            Swapchain = Backend.CreateSwapchain(Device, new SwapchainDesc(
                Surface,
                3,
                TextureUsages.ColorAttachment | TextureUsages.CopySource,
                SelectDefaultSwapchainConfig(
                    CreateSwapchainConfig(options, boot.Width, boot.Height,
                        Device.Adapter.HardwareAccelerated, Format.R8G8B8A8UNorm),
                    Backend.GetSwapchainSupport(Device, Surface)),
                title));
            Console.WriteLine($"Display: backend={options.GraphicsBackend}, adapter={Device.Adapter.Name}, " +
                $"adapterId={Device.Adapter.Id.Low:x16}:{Device.Adapter.Id.High:x16}, " +
                $"surface={Window.Type}, format={Swapchain.Info.Config.Format}.");
        }

        internal ClusterPipelineSet BorrowClusterPipelines(ClusterShaders shaders, ClusterVisibilityMode visibilityMode)
        {
            // All views share these inputs; replacement follows the previous scene's shutdown.
            if (_clusterPipelines is { } pipelines && ReferenceEquals(pipelines.Shaders, shaders) &&
                pipelines.VisibilityMode == visibilityMode)
                return pipelines;
            DisposeClusterPipelines();
            return _clusterPipelines = new ClusterPipelineSet(Backend, Device, shaders, visibilityMode);
        }

        private void DisposeClusterPipelines()
        {
            ClusterPipelineSet? pipelines = _clusterPipelines;
            _clusterPipelines = null;
            pipelines?.Dispose();
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
            // Device completes native teardown, including a scene that failed during shutdown.
            _clusterPipelines = null;
            Surface?.Dispose();
            Window?.Dispose();
            Backend?.Dispose();
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

    private static SwapchainConfig SelectDefaultSwapchainConfig(
        in SwapchainConfig defaults,
        ReadOnlySpan<SwapchainSupport> support)
    {
        ReadOnlySpan<Format> formats = [Format.R8G8B8A8UNorm, Format.B8G8R8A8UNorm];
        foreach (Format format in formats)
        {
            foreach (ref readonly SwapchainSupport value in support)
            {
                if (value.Format == format && value.ColorSpace == defaults.ColorSpace &&
                    value.PresentType == defaults.PresentType &&
                    (value.TearingSupported || !defaults.AllowTearing))
                    return defaults with { Format = format };
            }
        }
        throw new NotSupportedException("The application display requires an RGBA8 or BGRA8 swapchain for its presentation settings.");
    }

    private static SwapchainConfig CreateSwapchainConfig(
        RuntimeStartupOptions options,
        int width,
        int height,
        bool hardwareAccelerated,
        Format format) => new(
            checked((uint)width),
            checked((uint)height),
            format,
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
            CreateSwapchainConfig(options, width, height, hardwareAccelerated, swapchain.Info.Config.Format));
        if (status != ReconfigureStatus.Success)
        {
            throw new InvalidOperationException(
                $"The Runtime swapchain could not be reconfigured: {status}.");
        }
    }

    private static void ReconfigureSwapchain(
        IGraphicsBackend backend,
        Swapchain swapchain,
        RuntimeStartupOptions options,
        int width,
        int height,
        bool hardwareAccelerated,
        RenderFrameCoordinator coordinator,
        global::SomeEngine.RenderGraph.RenderGraph graph)
    {
        // Resize retires this renderer's accepted work, not unrelated device work.
        // Completed graph slots still own cached back-buffer views until compacted.
        coordinator.WaitForTrackedSubmissions();
        graph.CollectCompleted();
        graph.CompactTransientResources();
        ReconfigureSwapchain(backend, swapchain, options, width, height, hardwareAccelerated);
    }

    private static RenderGraphNextFrame OpenNextGraphFrame(global::SomeEngine.RenderGraph.RenderGraph graph)
    {
        if (!graph.TryOpenNextFrame(out RenderGraphNextFrame next, FrameTimeout))
            throw new TimeoutException("RenderGraph frame resources did not become available before the runtime timeout.");
        return next;
    }

    private static SwapchainImage AcquireImage(
        IGraphicsBackend backend,
        Swapchain swapchain,
        NativeWindow window)
    {
        using var trace = EngineTrace.Begin(ApplicationTrace.AcquireImage);
        long started = Environment.TickCount64;
        while (true)
        {
            SwapchainAcquireStatus status = backend.Acquire(
                swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromMilliseconds(1), PreserveContents: false),
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
        ApplicationDisplay display,
        IGraphicsBackend backend,
        Device device,
        Swapchain swapchain,
        NativeWindow window,
        World loadedWorld,
        RuntimeScene scene,
        ClusterShaders pipelineConfiguration,
        VirtualShadowMapShaders virtualShadowConfiguration,
        Shader uiShader,
        Shader presentationShader,
        Shader lightingShader,
        Shader temporalShader,
        ApplicationSettingsBlock boot,
        string title,
        bool forceHardwareRaster, ApplicationScene? application, SceneScript? script)
    {
        Format presentationFormat = swapchain.Info.Config.Format;
        using World mainWorld = loadedWorld;
        using RuntimeScene loadedSceneResources = scene;
        var sceneEdit = new SceneEdit(scene, window);
        IDisposable? createdResources = null;
        if (application?.CreateSceneResources is { } createSceneResources)
            sceneEdit.Invoke(() => createdResources = createSceneResources(mainWorld, sceneEdit));
        using IDisposable? sceneResources = createdResources;
        using var renderWorld = new RenderWorld(initialEntityCapacity: 2048, execution: new WorldJobs(JobSystem.Default));
        using var extraction = new RenderExtractionSystems(renderWorld, new MeshRenderExtractor(), new LightRenderExtractor(),
            new MaterialDecalRenderExtractor(), new XrayRenderExtractor(), new WorldOriginRenderExtractor(),
            new SurfaceRenderExtractor(), new SkinRenderExtractor(), new SkinBoundsRenderExtractor());

        bool includeInterface = application?.InterfaceEnabled == true;
        using var elementSystems = new GameFrameSystems(mainWorld, includeInterface);
        using WindowInput? elementInput = includeInterface ? new WindowInput(mainWorld) : null;
        ApplicationFrameMeasurements? measurements = application?.FrameMeasured is { } receiveTiming
            ? new(receiveTiming) : null;
        bool measure = application?.MeasureInterface == true || measurements is not null;
        GameUpdateTiming gameTiming = default;
        long gameStarted = 0, nextGameStarted = 0;
        bool synchronousGame = options.SynchronousFrames || script is not null;
        bool updateGameAfterInput = synchronousGame || application?.UpdateFrame is not null || application?.FrameCompleted is not null;

        RuntimeViewFrame initialView = RuntimeViewFrame.Create(
            scene.Camera.View,
            scene.Projection(boot.Width, boot.Height),
            boot.Width,
            boot.Height,
            temporalFrameIndex: 0);
        Entity viewEntity = renderWorld.CreateEntity(initialView.View);

        using var renderDevice = new RenderDevice(backend, device, FrameTimeout);
        using var coordinator = new RenderFrameCoordinator(renderDevice, FrameTimeout);
        RenderInstancePropertyLayout instanceLayout = RenderInstancePropertyLayout.Compose(
            ClusterRenderFeature.InstanceLayout, SkinInstanceProducer.Layout);
        var instances = new RenderInstanceStorageSystem(
            backend,
            device,
            coordinator,
            renderWorld,
            instanceLayout,
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
        var instanceSystem = new ClusterInstanceSystem<RenderInstanceProducerBundle<ClusterMaterialOffsetProducer, SkinInstanceProducer>>(
            cluster,
            new QueryDefinitionBuilder().Read<RenderInstance>().Read<RenderTransform>().Read<RenderPreviousTransform>()
                .Read<RenderClusterGeometry>().ReadBuffer<RenderMaterialBinding>().Optional<RenderSkinBinding>(QueryAccess.Read).Build(),
            instanceLayout,
            new(materialAssignments.CreateProducer(), new SkinInstanceProducer()));
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
        _ = instances.GetDataTransfer(renderGraph);
        using var skinning = new SkinningFeature(backend, device, renderGraph);
        using var skinBounds = new SkinBoundsFeature(backend, device, renderGraph, skinning);
        SurfaceMaterial? surfaceMaterial = includeInterface
            ? SurfaceMaterial.Create(device.Capabilities.CompileTarget) : null;
        GlyphsMaterial? glyphMaterial = includeInterface
            ? GlyphsMaterial.Create(device.Capabilities.CompileTarget) : null;
        using CanvasRenderer? surfaces = surfaceMaterial is null ? null
            : new CanvasRenderer(backend, device, renderGraph, renderWorld, surfaceMaterial, presentationFormat, glyphMaterial);
        GraphRegionId sceneRegion, primaryGeometryRegion, primaryDepthAfterRegion, targetGeometryRegion;
        GraphRegionId targetGeometryAfterRegion, shadingRegion, temporalRegion, presentationRegion, uiRegion, feedbackRegion;
        GraphRegionId frameOutputRegion, linearOutputRegion, colorEffectsRegion, additionalViewsRegion;
        using (RenderGraphEdit edit = renderGraph.BeginEdit())
        {
            _ = edit.AddRegion("Frame uploads");
            sceneRegion = edit.AddRegion("Scene");
            primaryGeometryRegion = edit.AddRegion("Primary view geometry");
            primaryDepthAfterRegion = edit.AddRegion("After primary view depth");
            targetGeometryRegion = edit.AddRegion("Target view geometry");
            targetGeometryAfterRegion = edit.AddRegion("After target view geometry");
            shadingRegion = edit.AddRegion("Shading");
            additionalViewsRegion = edit.AddRegion("Additional views");
            linearOutputRegion = edit.AddFrameRegion("Linear frame output");
            GraphRegionId postProcessing = edit.AddRegion("Post processing");
            colorEffectsRegion = edit.AddRegion("Surface effects", postProcessing);
            temporalRegion = edit.AddRegion("Temporal", postProcessing);
            presentationRegion = edit.AddRegion("Presentation", postProcessing);
            uiRegion = edit.AddFrameRegion("UI", postProcessing);
            frameOutputRegion = edit.AddFrameRegion("Frame output", postProcessing);
            feedbackRegion = edit.AddRegion("Feedback");
            edit.Commit();
        }
        var viewResources = new GraphResourceGroup();
        viewResources.Add(skinning);
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
        bool measureShadowInvalidation = (script?.Application ?? application)?.MeasureShadowInvalidation == true;
        ApplicationFrameOutput frameOutput = CreateFrameOutput(boot.Width, boot.Height);
        ApplicationFrameOutput? previousOutput = null;
        var frameSystems = new RenderFrameSystems(renderWorld, instances);
        var geometry = new ApplicationGeometryContext(backend, device, renderDevice, cluster, renderGraph,
            mainWorld, renderWorld, extraction, prepareSystems, frameSystems, () => scene.Camera.Position);
        var graphCompletionSlots = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        var graphCompletions = new QueueCompletion[renderGraph.MaximumQueueCompletionCount];
        FrameOutputVerifier? outputVerifier = null;
        FrameOutputVerifier? linearVerifier = null;
        RuntimeUiRenderer? ui = null;
        var additionalViews = new ApplicationViews(renderGraph, additionalViewsRegion,
            (settings, regions) => CreatePipeline(new ClusterPipelineOutputDescription(settings.Width, settings.Height, 1),
                metrics: false, additionalView: settings, viewRegions: regions),
            receiver =>
            {
                var systems = new RenderFrameSystems(renderWorld, instances);
                _ = systems.Add(receiver);
                return systems;
            });
        Task<GameUpdateTiming>? nextGameUpdate = null;
        var input = new RuntimeInput(elementInput);
        RenderGraphStatistics latestGraphStatistics = default;
        bool hasGraphStatistics = false;
        int analyzedFrames = 0;
        int reusedAnalysisFrames = 0;
        ClusterShadingPath shadingPath = script?.ShadingPath ?? options.ShadingPath;
        VirtualShadowFrameQuality? shadowQuality = null;
        Action<VirtualShadowFrameQuality>? receiveShadowQuality = null;

        ClusterPipelineSystem CreatePipeline(
            in ClusterPipelineOutputDescription description,
            bool metrics,
            ApplicationViewSettings? additionalView = null,
            ClusterGraphRegions? viewRegions = null,
            IGraphResourceProvider? stageResources = null,
            ClusteredViewGrid viewGrid = default) => new(
            backend,
            device,
            renderDevice,
            cluster,
            instanceSystem,
            instanceLayout,
            materialAssignments,
            pipelineConfiguration,
            renderGraph,
            viewRegions ?? new ClusterGraphRegions(primaryGeometryRegion, targetGeometryRegion, shadingRegion, feedbackRegion),
            description,
            streamingFeedback,
            additionalView is null ? stageResources : viewResources,
            new ClusterPipelineOptions
            {
                ShadingPath = script?.ShadingPath ?? options.ShadingPath,
                DebugMode = additionalView?.Mode ?? 0,
                EnableAsyncCompute = options.AsyncCompute,
                ForceHardwareRaster = forceHardwareRaster,
                SoftwareRasterEdgeThreshold = script?.RasterEdgePixels ?? 32,
                UseInstanceGroups = options.UseInstanceGroups,
                EnableFrameMetricsReadback = metrics || (additionalView is null && application?.FrameRendered is not null),
                LodThreshold = application?.LodThreshold ?? 0.25f,
                MeasureBlending = additionalView is null && options.MeasureBlending,
                BlendIndex = options.BlendIndex,
                BlendTriangleCapacity = script?.BlendTriangleCapacity ?? ClusterPipelineOptions.DefaultBlendTriangleCapacity,
                BlendReferenceCapacity = script?.BlendReferenceCapacity ?? 1_048_576,
                MaxCandidates = script?.ClusterWorkCapacity ?? 1_048_576,
            }, viewGrid: viewGrid, deformBounds: skinBounds)
        {
            BorrowPipelines = display.BorrowClusterPipelines,
        };

        GameUpdateTiming UpdateGameViews(double deltaSeconds, long gameUpdateStarted)
        {
            geometry.UpdateGame(deltaSeconds);
            long allocated = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            long started = measure ? Stopwatch.GetTimestamp() : 0;
            if (!measure)
            {
                elementSystems.UpdateViews(deltaSeconds);
                return default;
            }
            ViewTiming views = elementSystems.MeasureViews(deltaSeconds);
            views = views with
            {
                TotalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
            };
            return new GameUpdateTiming(views, 0, 0, Stopwatch.GetElapsedTime(gameUpdateStarted).TotalMilliseconds);
        }

        GameUpdateTiming ExtractGame(GameUpdateTiming timing)
        {
            long allocated = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
            long started = measure ? Stopwatch.GetTimestamp() : 0;
            using var trace = EngineTrace.Begin(ApplicationTrace.Extraction);
            extraction.Extract(mainWorld);
            return measure ? timing with
            {
                ExtractionMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                ExtractionAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
            } : timing;
        }

        void CaptureRenderGraphStatistics(in RenderGraphStatistics statistics)
        {
            latestGraphStatistics = statistics;
            hasGraphStatistics = true;
            analyzedFrames++;
            if (latestGraphStatistics.AnalysisReused) reusedAnalysisFrames++;
        }

        ApplicationFrameOutput CreateFrameOutput(int outputWidth, int outputHeight)
        {
            var description = new ClusterPipelineOutputDescription(outputWidth, outputHeight, 1);
            var lighting = new LightingFeature(backend, device, renderGraph, description.Width, description.Height,
                description.ViewCount, lightingShader, boot.LightingEntryPoint, region: sceneRegion);
            ClusteredViewGrid blendGrid = options.BlendIndex == ClusterBlendIndex.Xy
                ? lighting.CreateTileGrid() : lighting.ViewGrid;
            var stageResources = new GraphResourceGroup();
            stageResources.Add(lighting);
            stageResources.Add(skinning);
            ClusterPipelineSystem pipeline = CreatePipeline(description,
                metrics: options.VerifyFrameOutput || options.LinearFrameOutputPath is not null || script is not null,
                stageResources: stageResources, viewGrid: blendGrid);
            if (application?.BlendFrameCompleted is { } applicationBlendCompleted)
                pipeline.BlendFrameCompleted += applicationBlendCompleted;
            if (script?.BlendFrameCompleted is { } scriptBlendCompleted)
                pipeline.BlendFrameCompleted += scriptBlendCompleted;
            var shadows = new VirtualShadowFeature(backend, device, virtualShadowConfiguration, shadowSettings,
                renderGraph, pipeline, lighting, primaryDepthAfterRegion, targetGeometryAfterRegion, feedbackRegion,
                renderDevice);
            if (measureShadowInvalidation) shadows.EnableInvalidationTiming();
            stageResources.Add(shadows);
            var systems = new RenderFrameSystems(renderWorld, instances);
            _ = systems.Add(new LightingFeatureSystem(lighting, lightSystem, description.ViewCount));
            _ = systems.Add(new VirtualShadowFeatureSystem(shadows, lightSystem));
            _ = systems.Add(pipeline);
            using RenderGraphEdit edit = renderGraph.BeginEdit();
            GraphRegionId colorRegion = edit.AddRegion("Main view surface effects", colorEffectsRegion);
            edit.Commit();
            return new(renderGraph, description, lighting, pipeline, shadows, systems, colorRegion);
        }

        int frameIndex = 0;
        uint submittedFrames = 0;
        var applicationFrame = new ApplicationFrame(mainWorld, elementSystems, extraction, sceneEdit, input, cluster, startup, device.Adapter, measureShadowInvalidation)
            { Width = boot.Width, Height = boot.Height };
        int actionIndex = 0;
        try
        {
            geometry.Install(application?.GeometryFeatures ?? []);
            _ = prepareSystems.Add(new ClusterResidencySystem(cluster));
            _ = prepareSystems.Add(skinning);
            _ = prepareSystems.Add(skinBounds);
            _ = prepareSystems.Add(materialSystem);
            _ = prepareSystems.Add(instanceSystem);
            _ = frameSystems.Add(skinning);
            _ = frameSystems.Add(skinBounds);
            frameSystems.Initialize();
            frameOutput.Systems.Initialize();
            sceneEdit.Begin();
            try { application?.Initialize?.Invoke(applicationFrame); }
            finally { sceneEdit.End(); }
            if (updateGameAfterInput) elementSystems.UpdateViews(0);
            else
            {
                gameStarted = Stopwatch.GetTimestamp();
                sceneEdit.Invoke(application?.BeforeCameraInput, 0);
                gameTiming = ExtractGame(UpdateGameViews(0, gameStarted));
            }

            outputVerifier = options.VerifyFrameOutput || (options.FrameOutputPath is not null && options.CaptureFrame is null)
                ? new FrameOutputVerifier(backend, device, frameOutput.Width, frameOutput.Height, presentationFormat)
                : null;
            ui = new RuntimeUiRenderer(
                backend,
                device,
                window,
                uiShader,
                boot.UiVertexEntry,
                boot.UiPixelEntry,
                presentationFormat);
            Console.WriteLine(
                $"Runtime configuration '{title}' loaded from {contentRootLabel(script?.ContentDirectory ?? application?.ContentDirectory ?? FindContentRoot())}; " +
                $"sceneInstances={scene.MeshInstanceCount}, " +
                $"sceneBounds={scene.MeshPositionMin}..{scene.MeshPositionMax}, " +
                    $"clusterPipeline={boot.Pipeline.Value}.");
            long runtimeStarted = Stopwatch.GetTimestamp();
            startup.RecordStage("RenderResources");
            long previousFrameTimestamp = Stopwatch.GetTimestamp();
            long traceFrameIndex = 0;
            bool debugUiOpen = !options.HideDebugUi && !options.VerifyFrameOutput;
            FrameOutputMetrics? verifiedOutput = null;
            uint maximumRecoveredClusters = 0;
            RenderGraphStatisticsHandler? captureGraphStatistics =
                options.VerifyFrameOutput ? CaptureRenderGraphStatistics : null;
            while (options.FrameLimit == 0 || frameIndex < options.FrameLimit)
            {
                long frameStarted = Stopwatch.GetTimestamp();
                bool reportMilestones = frameIndex == 0;
                if (startup.Result is not null && startup.IsTracing)
                {
                    if (nextGameUpdate is not null)
                    {
                        gameTiming = RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
                        gameStarted = nextGameStarted;
                        nextGameUpdate = null;
                    }
                    startup.FinishTrace();
                }
                using var frameTrace = EngineTrace.BeginFrame(++traceFrameIndex);
                // Shared asset content can change only after the Game World extraction reader exits.
                if (nextGameUpdate is not null)
                {
                    gameTiming = RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
                    gameStarted = nextGameStarted;
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
                elementSystems.UpdateInput();
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
                bool cameraCut = script?.UseCameraHistory == false;
                bool outputSizeChanged = clientWidth > 0 && clientHeight > 0 &&
                    (clientWidth != frameOutput.Width || clientHeight != frameOutput.Height);
                if (outputSizeChanged)
                {
                    outputVerifier?.Dispose();
                    outputVerifier = null;
                    linearVerifier?.Dispose();
                    linearVerifier = null;
                    ReconfigureSwapchain(
                        backend,
                        swapchain,
                        options,
                        clientWidth,
                        clientHeight,
                        device.Adapter.HardwareAccelerated,
                        coordinator,
                        renderGraph);
                    previousOutput = frameOutput;
                    frameOutput = CreateFrameOutput(clientWidth, clientHeight);
                    frameOutput.Systems.Initialize();
                    cameraCut = true;
                    outputVerifier = options.VerifyFrameOutput || (options.FrameOutputPath is not null && options.CaptureFrame is null)
                        ? new FrameOutputVerifier(backend, device, frameOutput.Width, frameOutput.Height, presentationFormat)
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
                if (updateGameAfterInput) gameStarted = Stopwatch.GetTimestamp();
                if (reportMilestones)
                    Console.WriteLine("First frame: updating scene.");
                sceneEdit.Begin();
                try
                {
                    while (script is not null && actionIndex < script.Actions.Count && script.Actions[actionIndex].Frame == frameIndex)
                        script.Actions[actionIndex++].Execute(sceneEdit);
                    applicationFrame.Index = frameIndex;
                    applicationFrame.ShadowInvalidationTimingRequested = false;
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
                if (sceneEdit.ShadowMapRefreshRequested) frameOutput.Shadows.InvalidateAllCasters();

                if (updateGameAfterInput)
                    sceneEdit.Invoke(application?.BeforeCameraInput, sceneSeconds);
                if (reportMilestones)
                    Console.WriteLine("First frame: beginning interface frame.");
                ui.BeginFrame(deltaSeconds, frameOutput.Width, frameOutput.Height, window.DpiScale);
                SomeEngine.Elements.InputCapture elementCapture = elementInput?.InputCapture ?? default;
                scene.Camera.Update(
                    input,
                    deltaSeconds,
                    ui.WantCaptureKeyboard || elementCapture.Keyboard,
                    ui.WantCaptureMouse || elementCapture.Pointer);
                if (updateGameAfterInput)
                {
                    sceneEdit.Invoke(application?.SimulateScene, sceneSeconds);
                    if (synchronousGame) gameTiming = UpdateGameViews(deltaSeconds, gameStarted);
                    else
                    {
                        TraceContext gameTrace = EngineTrace.Context;
                        long updateStarted = gameStarted;
                        nextGameUpdate = Task.Run(() =>
                        {
                            using var association = EngineTrace.Associate(gameTrace);
                            using var trace = EngineTrace.Begin(ApplicationTrace.GameUpdate);
                            return UpdateGameViews(deltaSeconds, updateStarted);
                        });
                    }
                }
                // Game-world systems can run while submitted GPU work finishes using
                // instance and interface storage. Completion callbacks follow the game task.
                if (reportMilestones)
                    Console.WriteLine("First frame: waiting for frame resources.");
                WaitForFrameResources(instances, ui, backend, window);
                if (nextGameUpdate is not null)
                {
                    if (reportMilestones)
                        Console.WriteLine("First frame: waiting for game update.");
                    gameTiming = RuntimeWait.Task(nextGameUpdate, window, FrameTimeout);
                    nextGameUpdate = null;
                }
                using (EngineTrace.Begin(ApplicationTrace.CollectCompleted))
                    backend.CollectCompleted(device);
                using (EngineTrace.Begin(ApplicationTrace.CollectRenderResources))
                    coordinator.AdmitFrameResources();
                RenderGraphNextFrame nextGraphFrame = OpenNextGraphFrame(renderGraph);
                using var graphFrameResources = nextGraphFrame;
                // Completion callbacks can change shared assets. Collect their final
                // material and texture versions before publishing this game frame.
                if (reportMilestones)
                    Console.WriteLine("First frame: extracting scene.");
                if (updateGameAfterInput) gameTiming = ExtractGame(gameTiming);
                Matrix4x4 unjitteredProjection = scene.Projection(frameOutput.Width, frameOutput.Height);
                RuntimeViewFrame viewFrame = RuntimeViewFrame.Create(
                    scene.Camera.View,
                    unjitteredProjection,
                    frameOutput.Width,
                    frameOutput.Height,
                    submittedFrames,
                    temporalResolveEnabled: options.TemporalAntialiasing,
                    cameraCut: cameraCut,
                    cameraTarget: scene.Camera.Target,
                    depthOfField: scene.DepthOfField,
                    playerId: scene.PlayerId,
                    firstPerson: scene.FirstPerson);
                viewSystem.Stage(viewFrame.View);
                if (application?.DebugUiEnabled != false && debugUiOpen) ui.DrawDebugWindow(
                    ref debugUiOpen,
                    new RuntimeUiMetrics(
                        frameIndex,
                        deltaSeconds,
                        frameOutput.Width,
                        frameOutput.Height,
                        window.DpiScale,
                        viewFrame.JitterPixels,
                        scene.Camera.Position,
                        window.HasFocus,
                        options.TemporalAntialiasing), application?.DrawDebugUi);
                application?.DrawOverlay?.Invoke();
                ui.CompleteDrawData();
                IReadOnlyList<ApplicationView> selectedViews = application?.ViewsEnabled == true ? application.Views : [];
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
                    outputVerifier ??= new FrameOutputVerifier(backend, device, frameOutput.Width, frameOutput.Height, presentationFormat);
                if (captureLinear)
                {
                    linearVerifier ??= new FrameOutputVerifier(backend, device, frameOutput.Width, frameOutput.Height, Format.R16G16B16A16Float);
                    if (shadowSettings.Enabled)
                    {
                        frameOutput.Shadows.EnableQualityCapture();
                        receiveShadowQuality ??= value => shadowQuality = value;
                    }
                }
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
                foreach (var request in sceneEdit.PoseReads)
                {
                    Entity target = extraction.RequireMirror(request.Target);
                    Entity section = renderWorld.Read<RenderGpuPoseTarget>(target).Section;
                    skinning.RequestReadback(renderWorld.Read<RenderPoseSection>(section), request.Receive);
                }
                long reconcileStarted = Stopwatch.GetTimestamp();
                if (sceneEdit.ShadingPath is { } selectedShadingPath) shadingPath = selectedShadingPath;
                frameOutput.Pipeline.SetShadingPath(shadingPath);
                frameOutput.Shadows.ReconcileLights(lightSystem.RequireCurrent());
                frameOutput.Shadows.SetPrimaryDepthReceiverRequests(true);
                ClusterPipelineOutputs clusterOutputs;
                using (EngineTrace.Begin(ApplicationTrace.Reconcile))
                {
                    if (!additionalViews.Matches(selectedViews))
                    {
                        RuntimePresentationPipeline? imageConsumer = frameOutput.Presentation ?? previousOutput?.Presentation;
                        if (imageConsumer is not null && additionalViews.ChangesOutputs(selectedViews))
                            ui.InstallGraph(renderGraph, imageConsumer.Target, uiRegion, additionalViews.RetainedImages(selectedViews));
                        additionalViews.ReconcileSettings(selectedViews);
                    }
                    clusterOutputs = frameOutput.Pipeline.ReconcileGraph();
                    additionalViews.ReconcileGraph(shadingPath);
                }
                double reconcileMs = Stopwatch.GetElapsedTime(reconcileStarted).TotalMilliseconds;
                startup.RecordStage("ReconcileGraph");
                bool connectPostProcessing = frameOutput.Temporal is null;
                if (connectPostProcessing && (script?.CreateColorFeature ?? application?.CreateColorFeature) is { } createColorFeature)
                    frameOutput.ColorFeature = createColorFeature(new(backend, device, renderGraph, frameOutput.Width, frameOutput.Height,
                        viewFrame.View.Projection, frameOutput.ColorRegion) { StencilSource = frameOutput.Pipeline.GetStencil });
                frameOutput.Temporal ??= new TemporalFeature(
                    backend,
                    device,
                    renderGraph,
                    frameOutput.Width,
                    frameOutput.Height,
                    temporalShader,
                    boot.TemporalVertexEntry,
                    boot.TemporalPixelEntry,
                    region: temporalRegion);
                if (connectPostProcessing && frameOutput.ColorFeature is not null)
                {
                    RenderGraphEdit edit = renderGraph.BeginEdit();
                    try
                    {
                        frameOutput.Temporal.OrderAfter(ref edit, frameOutput.ColorFeature.CompletePass);
                        edit.Commit();
                    }
                    finally
                    {
                        edit.Dispose();
                    }
                }
                frameOutput.Presentation ??= new RuntimePresentationPipeline(
                    backend,
                    device,
                    renderGraph,
                    frameOutput.Temporal.Output.ResolvedColor,
                    frameOutput.Temporal.Output.Format,
                    frameOutput.Temporal.Output.Width,
                    frameOutput.Temporal.Output.Height,
                    frameOutput.Pipeline.PageFaultReadback,
                    presentationShader,
                    boot.PresentationVertexEntry,
                    boot.PresentationPixelEntry,
                    presentationFormat,
                    presentationRegion);
                if (frameOutput.PresentationReadback != frameOutput.Pipeline.PageFaultReadback)
                {
                    using RenderGraphEdit edit = renderGraph.BeginEdit();
                    frameOutput.Presentation.OrderAfter(edit, frameOutput.Pipeline.PageFaultReadback);
                    edit.Commit();
                    frameOutput.PresentationReadback = frameOutput.Pipeline.PageFaultReadback;
                }
                surfaces?.ReconcileGraph(frameOutput.Presentation.Target, new WorldCanvasTarget(frameOutput.Presentation.Target, clusterOutputs.Depth, viewEntity));
                ui.InstallGraph(renderGraph, frameOutput.Presentation.Target, uiRegion, additionalViews.Images);
                if (previousOutput is not null)
                {
                    previousOutput.UninstallGraph();
                    previousOutput.Dispose();
                    previousOutput = null;
                }
                if (reportMilestones)
                    Console.WriteLine("First frame: acquiring presentation image.");
                SwapchainImage? image = sceneEdit.FrameCanceled
                    ? null : AcquireImage(backend, swapchain, window);
                SceneFrame? capturedFrame = captureLinear ? scene.CaptureFrame(new SceneFrame
                {
                    Frame = frameIndex, SubmittedFrame = submittedFrames, SceneSeconds = sceneSeconds,
                    Width = frameOutput.Width, Height = frameOutput.Height, CameraPosition = scene.Camera.Position,
                    WorldOrigin = renderWorld.WorldOrigin,
                    View = scene.Camera.View, Projection = viewFrame.View.Projection,
                    UnjitteredProjection = unjitteredProjection, JitterPixels = viewFrame.JitterPixels,
                    Options = options with { ShadingPath = shadingPath },
                    ShadowSettings = shadowSettings,
                    TemporalSettings = script?.TemporalSettings ?? TemporalResolveSettings.Default,
                    RasterEdgePixels = script?.RasterEdgePixels ?? 32,
                    ClusterWorkCapacity = script?.ClusterWorkCapacity ?? 1_048_576,
                    Adapter = device.Adapter.Name, DriverVersion = device.Adapter.DriverVersion,
                }) : null;
                // ExecuteFrame drains linear capture readbacks before the next frame reuses this callback.
                shadowQuality = null;
                if (image is not null && applicationFrame.ShadowInvalidationTimingRequested)
                    frameOutput.Shadows.RequestInvalidationTiming(frameIndex);
                long executeStarted = Stopwatch.GetTimestamp();
                frameOutput.Pipeline.SetMeasurementFrame(frameIndex);
                startup.RecordStage("PostProcessing");
                FrameOutputMetrics? framePixels = ExecuteFrame(
                    backend,
                    renderGraph,
                    nextGraphFrame,
                    renderDevice,
                    graphicsQueue,
                    graphCompletionSlots,
                    graphCompletions,
                    coordinator,
                    frameSystems,
                    frameOutput.Systems,
                    geometry,
                    frameOutput.Shadows,
                    clusterOutputs,
                    frameOutput.ColorFeature,
                    frameOutput.Temporal,
                    frameOutput.Presentation,
                    ui,
                    additionalViews,
                    renderWorld.Read<RenderView>(viewEntity),
                    unjitteredProjection,
                    application?.PresentationBounds,
                    surfaces,
                    image,
                    frameOutput.Width,
                    frameOutput.Height,
                    window,
                    reportMilestones,
                    cameraCut,
                    viewEntity,
                    captureBeauty ? outputVerifier : null,
                    frameOutputRegion,
                    captureLinear ? linearVerifier : null,
                    linearOutputRegion,
                    captureLinear && shadowSettings.Enabled ? receiveShadowQuality : null,
                    captureGraphStatistics,
                    options.SynchronousFrames || application?.FrameRendered is not null,
                    options.TemporalAntialiasing,
                    viewFrame.JitterPixels,
                    script?.TemporalSettings ?? TemporalResolveSettings.Default,
                    measurements,
                    out ulong graphFrameIdentity);
                if (image is null)
                {
                    frameIndex++;
                    continue;
                }
                submittedFrames++;
                double executeMs = Stopwatch.GetElapsedTime(executeStarted).TotalMilliseconds;
                skinning.CompleteReadbacks();
                application?.FrameRendered?.Invoke(frameOutput.Pipeline.CaptureFrameMetrics());
                startup.RecordStage("ExecuteFrame");
                if (selectedCapture && options.FrameOutputPath is { } framePath)
                    outputVerifier!.SaveImage(framePath);
                if (capturedFrame is not null)
                {
                    ClusterRenderDiagnostics diagnostics = cluster.CaptureDiagnostics();
                    ClusterResidencyDiagnostics residency = diagnostics.Residency;
                    capturedFrame = capturedFrame with
                    {
                        Quality = new(shadowQuality, frameOutput.Pipeline.CaptureFrameMetrics(), new(
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
                        outputVerifier.Dispose();
                        outputVerifier = null;
                    }
                }
                if (framePixels is FrameOutputMetrics output)
                    verifiedOutput = output;
                if (options.VerifyFrameOutput)
                {
                    ClusterFrameMetrics metrics = frameOutput.Pipeline.CaptureFrameMetrics();
                    maximumRecoveredClusters = Math.Max(maximumRecoveredClusters,
                        metrics.PhaseTwoSoftwareClusters + metrics.PhaseTwoHardwareClusters);
                }

                if (reportMilestones)
                    Console.WriteLine("First frame: presenting.");
                PresentStatus present;
                using (EngineTrace.Begin(ApplicationTrace.Present))
                    present = backend.Present(graphicsQueue, image.Value);
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
                        frameOutput.Width,
                        frameOutput.Height,
                        device.Adapter.HardwareAccelerated,
                        coordinator,
                        renderGraph);

                applicationFrame.Timing = new(Stopwatch.GetElapsedTime(frameStarted).TotalMilliseconds,
                    prepareMs, reconcileMs, executeMs, gameTiming.Views, gameTiming.ExtractionMs,
                    gameTiming.ExtractionAllocatedBytes, frameIndex);
                application?.FrameCompleted?.Invoke(applicationFrame);
                measurements?.RecordCpu(graphFrameIdentity, frameIndex,
                    cpuMs,
                    gameTiming.GameMs, gameTiming.Views.AnimationMs, gameTiming.ExtractionMs,
                    prepareMs, reconcileMs, executeMs);
                frameIndex++;
                if (application is { ReloadRequested: true } or { ExitRequested: true }) break;
                bool hasFutureFrame = options.FrameLimit == 0 || frameIndex < options.FrameLimit;
                if (hasFutureFrame && !updateGameAfterInput)
                {
                    Action<SceneEdit, float>? beforeCameraInput = application?.BeforeCameraInput;
                    Action<SceneEdit, float>? simulateScene = application?.SimulateScene;
                    float nextSceneTime = options.FixedFrameSeconds > 0 ? frameIndex * options.FixedFrameSeconds : (float)Stopwatch.GetElapsedTime(
                        runtimeStarted,
                        Stopwatch.GetTimestamp()).TotalSeconds;
                    TraceContext gameTrace = EngineTrace.Context with { Frame = traceFrameIndex + 1 };
                    nextGameStarted = Stopwatch.GetTimestamp();
                    nextGameUpdate = Task.Run(() =>
                    {
                        long updateStarted = Stopwatch.GetTimestamp();
                        using var association = EngineTrace.Associate(gameTrace);
                        using var trace = EngineTrace.Begin(ApplicationTrace.GameUpdate);
                        // Frame callbacks have finished reading this game state. Submitted
                        // GPU work continues using its retained rendering resources.
                        sceneEdit.Invoke(beforeCameraInput, nextSceneTime);
                        sceneEdit.Invoke(simulateScene, nextSceneTime);
                        return ExtractGame(UpdateGameViews(deltaSeconds, updateStarted));
                    });
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
                    ClusterFrameMetrics frameMetrics = frameOutput.Pipeline.CaptureFrameMetrics();
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
            linearVerifier?.Dispose();
            coordinator.WaitForTrackedSubmissions();
            renderGraph.CollectCompleted();
            application?.Shutdown?.Invoke(applicationFrame);
            geometry.FinishGame();
            RuntimePresentationPipeline? finalPresentation = frameOutput.Presentation ?? previousOutput?.Presentation;
            if (ui is not null && finalPresentation is not null) ui.InstallGraph(renderGraph, finalPresentation.Target, uiRegion);
            additionalViews.Dispose();
            ui?.Dispose();
            frameOutput.Dispose();
            previousOutput?.Dispose();
            frameSystems.Dispose();
            Shutdown(prepareSystems, cluster, instances, coordinator);
            extraction.Dispose();
            geometry.DisposeFeatures();
            scene.Dispose();
            if (device.Status == DeviceStatus.Active)
                backend.CollectCompleted(device);
        }
    }

    private readonly record struct GameUpdateTiming(ViewTiming Views, double ExtractionMs, long ExtractionAllocatedBytes, double GameMs = 0);

    private static void WaitForFrameResources(
        RenderInstanceStorageSystem instances,
        RuntimeUiRenderer ui,
        IGraphicsBackend backend,
        NativeWindow window)
    {
        using var trace = EngineTrace.Begin(ApplicationTrace.FrameResources);
        bool instanceAdmitted = instances.TryAdmitFrameResources(
            out _, out QueueCompletion[] instanceRetirementFences);
        bool uiAdmitted = ui.TryAdmitFrameResources(
            out _, out QueueCompletion[] uiRetirementFences);
        long started = Environment.TickCount64;
        while (!instanceAdmitted || !uiAdmitted)
        {
            TimeSpan remaining = FrameTimeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Frame-resource admission did not become available before the runtime timeout.");
            TimeSpan waitSlice = remaining > TimeSpan.FromMilliseconds(2) ? TimeSpan.FromMilliseconds(2) : remaining;
            _ = WaitForAll(backend, instanceAdmitted ? uiRetirementFences : instanceRetirementFences, waitSlice);
            _ = window.PumpMessages();
            if (!instanceAdmitted)
                instanceAdmitted = instances.TryAdmitFrameResources(out _, out instanceRetirementFences);
            if (!uiAdmitted)
                uiAdmitted = ui.TryAdmitFrameResources(out _, out uiRetirementFences);
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
            using (EngineTrace.Begin(ApplicationTrace.PrepareMeshes))
            {
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
            }
            using (EngineTrace.Begin(ApplicationTrace.StreamingFeedback))
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
        RenderGraphNextFrame nextGraphFrame,
        RenderDevice renderDevice,
        Queue graphicsQueue,
        QueueCompletion[] completionSlots,
        QueueCompletion[] completions,
        RenderFrameCoordinator coordinator,
        RenderFrameSystems systems,
        RenderFrameSystems outputSystems,
        ApplicationGeometryContext geometry,
        VirtualShadowFeature virtualShadows,
        in ClusterPipelineOutputs clusterOutputs,
        IApplicationColorFeature? colorFeature,
        TemporalFeature temporal,
        RuntimePresentationPipeline presentation,
        RuntimeUiRenderer ui,
        ApplicationViews additionalViews,
        in RenderView mainView,
        in Matrix4x4 unjitteredProjection,
        ScissorRect? presentationBounds,
        CanvasRenderer? surfaces,
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
        RenderGraphStatisticsHandler? graphStatistics,
        bool synchronousFrames,
        bool temporalAntialiasing,
        Vector2 jitterPixels,
        TemporalResolveSettings temporalSettings,
        ApplicationFrameMeasurements? measurements,
        out ulong graphFrameIdentity)
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
                    nextGraphFrame,
                    new RenderGraphFrameOptions(
                        SubmissionMode: FrameSubmissionMode.RecordAhead,
                        GpuTimes: measurements is null ? null : measurements.ReceiveGpu)
                    {
                        Statistics = graphStatistics,
                    });
                graphFrameIdentity = graphFrame.FrameIdentity;
                try
                {
                    geometry.Observe(ref graphFrame);
                    if (reportMilestones)
                        Console.WriteLine("First frame: recording render systems.");
                    if (image is { } acquired) presentation.Bind(ref graphFrame, acquired, graphicsQueue, presentationBounds);
                    using (EngineTrace.Begin(ApplicationTrace.RenderSystems))
                    {
                        systems.Update(frame, graphFrame);
                        outputSystems.Update(frame, graphFrame);
                        additionalViews.Record(frame, graphFrame, mainView, unjitteredProjection);
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
                    surfaces?.UpdateFrame(ref graphFrame);
                    ui.Record(ref graphFrame, width, height);
                    if (image is null) return null;
                    if (captureShadowQuality is not null) virtualShadows.CaptureQuality(ref graphFrame, captureShadowQuality);
                    outputVerifier?.Record(ref graphFrame, presentation.Target, graphicsQueue, frameOutputRegion);
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
                    bool readOutput = outputVerifier is not null || linearVerifier is not null;
                    if (synchronousFrames || readOutput) Wait(backend, execution, window, synchronousFrames);
                    if (outputVerifier is not null)
                    {
                        outputVerifier.ReadPixels();
                        if (graphStatistics is not null) frameOutput = outputVerifier.Analyze();
                    }
                    linearVerifier?.ReadPixels();
                    if (synchronousFrames || readOutput) graph.CollectCompleted();
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
        if (AssetContent.Exists(root)) return root;
        throw new DirectoryNotFoundException(
            "The application directory has no Library/AssetPacks/packs.json or Library/AssetManifest/catalog.json. Build this project's content before starting the application.");
    }

    private static string contentRootLabel(string value) => Path.GetFullPath(value);
}
