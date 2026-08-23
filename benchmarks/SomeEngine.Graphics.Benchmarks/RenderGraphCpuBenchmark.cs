using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;
using Queue = SomeEngine.Graphics.Queue;

namespace SomeEngine.Graphics.Benchmarks;

internal static class RenderGraphCpuBenchmark
{
    private const int Width = 1_920;
    private const int Height = 1_080;
    private const int PassCount = 73;
    private const int RasterPassCount = 24;
    private const int ComputePassCount = 17;
    private const int CopyPassCount = 6;
    private const int ControlPassCount = 26;
    private const int SourceScheduleFrameWindow = 2;
    private const uint IndirectDrawsPerRasterPass = 64;
    private const ulong DrawIndirectBytes = IndirectDrawsPerRasterPass * 16;
    private const ulong DispatchIndirectBytes = 12;
    private const ulong CopyBytes = 256;
    private const int MaterialCount = 8;
    private const double P95LimitMicroseconds = 500;
    private const int PlateauWindowSize = 64;
    private const int RequiredStableWindows = 3;
    private const double PlateauTolerance = 0.03;
    private static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(30);
    private static readonly ResourceBinding[] NoResources = [];
    private static readonly TextureSubresourceRange ColorRange =
        new(0, 1, 0, 1, TextureAspects.Color);
    private static readonly Viewport[] Viewports = [new(0, 0, Width, Height)];
    private static readonly ScissorRect[] CpuOnlyScissors = [new(0, 0, 1, 1)];
    private static readonly WeightedInt[] LifetimeDistribution =
    [
        new(0, 108), new(1, 120), new(2, 36), new(6, 6), new(7, 6), new(9, 4),
        new(13, 4), new(28, 4), new(33, 12), new(35, 8), new(37, 4), new(38, 4),
        new(39, 8), new(41, 4), new(42, 12), new(43, 4), new(44, 4), new(45, 4),
        new(55, 4), new(56, 8), new(60, 8), new(62, 16), new(63, 4), new(66, 4),
        new(67, 16), new(69, 8), new(70, 8), new(71, 16), new(73, 8), new(74, 8),
        new(79, 6), new(80, 8), new(82, 4), new(84, 4), new(85, 16), new(89, 16),
        new(91, 12), new(92, 4),
    ];

    private static readonly WeightedSize[] SizeDistribution =
    [
        new(1, 28), new(4, 60), new(880, 20), new(12_288, 20),
        new(131_072, 12), new(524_288, 20), new(589_824, 4),
        new(917_504, 20), new(1_245_184, 40), new(2_228_224, 36),
        new(2_490_368, 82), new(3_538_944, 96), new(4_915_200, 16),
        new(8_847_360, 76),
    ];

    private static readonly GraphicsCpuSourceIdentity DagorSource = new(
        "Gaijin Dagor Engine / Enlisted production-like input",
        "6ae9529f0fa5405615648e6610a336bdb41de76f",
        "prog/gameLibs/render/daFrameGraph/tests/performance.cpp; " +
        "prog/gameLibs/render/daFrameGraph/backend/resourceScheduling/resourceScheduler.cpp; " +
        "prog/daNetGame/render/world/frameGraphNodes; " +
        "prog/daNetGame/render/world/dynModelRenderer.cpp",
        "https://github.com/GaijinEntertainment/DagorEngine/blob/6ae9529f0fa5405615648e6610a336bdb41de76f/prog/gameLibs/render/daFrameGraph/tests/performance.cpp");

    internal static int Run(BenchmarkOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The real D3D12 RenderGraph CPU benchmark requires Windows.");
        if (!options.AdapterSpecified)
            throw new BenchmarkUsageException("graph-cpu requires an explicit hardware --adapter LUID.");
        if (options.MeasuredFrames < FixedGraphicsProtocol.GraphicsCpuMeasuredFrames)
        {
            throw new BenchmarkUsageException(
                $"graph-cpu requires at least {FixedGraphicsProtocol.GraphicsCpuMeasuredFrames} measured frames per source case.");
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(options.ShaderDirectory);
        BenchmarkShaders.EmitSharedArtifacts(options.ShaderDirectory);

        IGraphicsBackend nativeBackend = D3D12GraphicsBackend.Create(new D3D12BackendOptions(
            new D3D12ValidationOptions(
                DisableGpuBasedValidation: true,
                DisableSynchronizedQueueValidation: true,
                DisableDred: true),
            UseQueueSpecificCommonLayouts: true));
        bool diagnosticValidation = string.Equals(
            Environment.GetEnvironmentVariable("SOMEENGINE_GRAPH_CPU_VALIDATION"),
            "1",
            StringComparison.Ordinal);
        using IGraphicsBackend backend = diagnosticValidation
            ? new ValidationLayer(
                nativeBackend,
                new ValidationOptions(new DelegateValidationMessageSink(static message =>
                    Console.Error.WriteLine(
                        $"validation[{message.Area}] {message.Text} label={message.Label ?? "<none>"}"))))
            : nativeBackend;
        AdapterInfo adapter = FindAdapter(backend, options.AdapterId);
        if (!adapter.HardwareAccelerated)
            throw new BenchmarkUsageException("graph-cpu requires a hardware adapter; WARP is functional evidence only.");

        using Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics)],
            requiredFeatures: DeviceFeatures.IndirectCommands,
            label: "Dagor Enlisted source-derived graphics CPU benchmark"));
        if (!backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics) || diagnostics is null)
            throw new InvalidOperationException("The D3D12 diagnostics capability is unavailable.");
        if (!diagnosticValidation &&
            (diagnostics.DebugLayerEnabled ||
             diagnostics.GpuBasedValidationEnabled ||
             diagnostics.SynchronizedQueueValidationEnabled ||
             diagnostics.DredEnabled))
        {
            throw new InvalidOperationException(
                "Validation or DRED is enabled. Disable it before collecting CPU performance evidence.");
        }

        WorkerConfiguration environmentConfiguration = new(
            BenchmarkProfile.GraphicsCpuDevelopment,
            ReceiverVariant.InterfaceReceiver,
            options.AdapterId,
            0,
            options.WarmupFrames,
            options.MeasuredFrames,
            checked(RasterPassCount * (1 + (int)IndirectDrawsPerRasterPass)),
            0,
            options.ShaderDirectory,
            options.OutputPath);
        RuntimeEnvironment environment = BenchmarkEnvironment.Capture(
            environmentConfiguration,
            adapter,
            validationEnabled: diagnosticValidation,
            dredEnabled: diagnostics.DredEnabled,
            ".NET 10 / SomeEngine RenderGraph / Silk.NET D3D12");

        using BenchmarkShaderProgram shader = BenchmarkShaders.Open(options.ShaderDirectory);
        using Pipeline graphicsPipeline = CreateGraphicsPipeline(backend, device, shader);
        using Pipeline computePipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[2], "Dagor graph CPU compute"));
        VariableLayoutReflection globalLayout =
            shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        if (globalLayout == VariableLayoutReflection.Null)
            throw new InvalidDataException("The benchmark shader has no global parameter layout.");

        PersistentParameterBindings[] materials = CreateMaterials(
            backend,
            device,
            graphicsPipeline,
            globalLayout,
            MaterialCount);
        try
        {
            using IndirectCommandLayout drawIndirect = backend.CreateIndirectCommandLayout(
                device,
                new IndirectCommandLayoutDesc(
                    [new IndirectArgumentDesc(IndirectArgumentType.Draw)],
                    16,
                    label: "Dagor multidraw arguments"));
            using IndirectCommandLayout dispatchIndirect = backend.CreateIndirectCommandLayout(
                device,
                new IndirectCommandLayoutDesc(
                    [new IndirectArgumentDesc(IndirectArgumentType.Dispatch)],
                    12,
                    label: "Dagor indirect dispatch arguments"));
            Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);

            int[] resourceCounts = options.GraphicsCpuResourceCounts;
            var results = new GraphicsCpuWorkloadResult[resourceCounts.Length];
            for (int index = 0; index < resourceCounts.Length; index++)
            {
                int resourceCount = resourceCounts[index];
                uint seed = unchecked(0x6AE9529u ^ ((uint)resourceCount * 0x9E3779B9u));
                results[index] = RunSourceCase(
                    backend,
                    device,
                    graphicsQueue,
                    graphicsPipeline,
                    computePipeline,
                    materials,
                    drawIndirect,
                    dispatchIndirect,
                    resourceCount,
                    seed,
                    options.WarmupFrames,
                    options.MeasuredFrames);
                Console.WriteLine(
                    $"{results[index].Case}: P50 {results[index].Cpu.P50:F1} us, " +
                    $"P95 {results[index].Cpu.P95:F1} us, P99 {results[index].Cpu.P99:F1} us, " +
                    $"max {results[index].Cpu.Maximum:F1} us, " +
                    $"barriers={results[index].Shape.BarrierCount}, " +
                    $"plateau={results[index].WarmupPlateauReached}, " +
                    $"0.5ms-target={(results[index].P95Passed ? "PASS" : "FAIL")}");
            }

            bool passed = results.All(static result => result.P95Passed);
            string standardPath = Path.GetFullPath(Path.Combine(
                BenchmarkOptions.FindRepositoryRoot(AppContext.BaseDirectory),
                "benchmarks",
                "SomeEngine.Graphics.Benchmarks",
                "GRAPHICS_CPU_WORKLOAD_STANDARD.md"));
            var report = new GraphicsCpuBenchmarkReport(
                "someengine.graphics.rendergraph-cpu/v2",
                passed ? "Passed" : "Failed",
                passed
                    ? "Every selected Dagor/Enlisted distribution-derived projection met P95 < 500 us."
                    : "At least one selected Dagor/Enlisted distribution-derived projection had P95 >= 500 us.",
                started,
                DateTimeOffset.UtcNow,
                environment,
                standardPath,
                results);
            report.Write(options.OutputPath);

            Console.WriteLine($"Dagor/Enlisted RenderGraph CPU evidence: {options.OutputPath}");
            Console.WriteLine($"Disposition: {(passed ? "PASS" : "FAIL")}");
            return passed ? 0 : 1;
        }
        finally
        {
            DisposeAll(materials);
        }
    }

    private static GraphicsCpuWorkloadResult RunSourceCase(
        IGraphicsBackend backend,
        Device device,
        Queue graphicsQueue,
        Pipeline graphicsPipeline,
        Pipeline computePipeline,
        PersistentParameterBindings[] materials,
        IndirectCommandLayout drawIndirect,
        IndirectCommandLayout dispatchIndirect,
        int resourceCount,
        uint seed,
        int minimumWarmupFrames,
        int measuredFrames)
    {
        using var graph = new RenderGraph.RenderGraph(
            backend,
            device,
            [graphicsQueue],
            new RenderGraphDesc(
                MaximumFramesInFlight: 1,
                Label: $"Dagor Enlisted high watermark {resourceCount} resources"));
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            BuildSourceGraph(
                edit,
                graphicsQueue,
                graphicsPipeline,
                computePipeline,
                materials,
                drawIndirect,
                dispatchIndirect,
                resourceCount,
                seed);
            edit.Commit();
        }

        CapturedStatistics captured = CaptureStatistics(graph, backend);
        RenderGraphStatistics statistics = captured.Statistics;
        if (statistics.ScheduledPassCount != PassCount)
            throw new InvalidOperationException(
                $"The source case scheduled {statistics.ScheduledPassCount} passes instead of {PassCount}.");
        if (statistics.BufferCount + statistics.TextureCount != resourceCount)
            throw new InvalidOperationException(
                $"The source case materialized {statistics.BufferCount + statistics.TextureCount} resources instead of {resourceCount}.");

        Measurement measurement = MeasureStable(
            graph,
            backend,
            new RenderGraphFrameOptions(
                FrameSubmissionMode.Pipelined,
                Debug: RenderGraphDebugOptions.DisableSplitBarriers),
            minimumWarmupFrames,
            measuredFrames);
        MetricDistribution cpu = MetricDistribution.From(
            measurement.Samples.Select(static sample => sample.CpuMicroseconds).ToArray());
        const int indirectDispatches = ComputePassCount / 4;
        const int directDispatches = ComputePassCount - indirectDispatches;
        const int nativeCopyCommands = CopyPassCount + 2;
        bool passed = cpu.P95 < P95LimitMicroseconds;
        return new GraphicsCpuWorkloadResult(
            GraphicsCpuWorkload.DagorEnlistedHighWatermark,
            $"resources-{resourceCount:D3}-seed-0x{seed:X8}",
            "Dagor/Enlisted production-distribution high-watermark projection; command counts are benchmark choices, not measured Enlisted counts",
            DagorSource,
            new GraphicsCpuWorkloadShape(
                seed,
                statistics.ScheduledPassCount,
                statistics.BufferCount + statistics.TextureCount,
                statistics.BufferCount,
                statistics.TextureCount,
                statistics.AccessCount,
                statistics.DependencyCount,
                statistics.BarrierCount,
                captured.BarrierBoundaryCount,
                captured.BufferBarrierCount,
                captured.TextureBarrierCount,
                captured.QueueTransferBarrierCount,
                captured.AliasingBarrierCount,
                captured.SplitBarrierPairCount,
                RasterPassCount,
                ComputePassCount,
                CopyPassCount,
                ControlPassCount,
                RasterPassCount,
                RasterPassCount,
                checked(RasterPassCount * (int)IndirectDrawsPerRasterPass),
                directDispatches,
                indirectDispatches,
                nativeCopyCommands,
                statistics.QueueCount,
                1,
                1,
                false,
                true,
                statistics.LogicalTransientBytes,
                statistics.PhysicalTransientBytes),
            "Immediately before RenderGraph.BeginFrame through real D3D12 command-list close and Queue.Submit return; frame-slot GPU wait is outside.",
            minimumWarmupFrames,
            measurement.Warmup.Length,
            measurement.PlateauReached,
            measurement.Warmup,
            measurement.Samples,
            cpu,
            P95LimitMicroseconds,
            passed);
    }

    private static void BuildSourceGraph(
        RenderGraphEdit edit,
        Queue graphicsQueue,
        Pipeline graphicsPipeline,
        Pipeline computePipeline,
        PersistentParameterBindings[] materials,
        IndirectCommandLayout drawIndirect,
        IndirectCommandLayout dispatchIndirect,
        int resourceCount,
        uint seed)
    {
        if (resourceCount < 4)
            throw new ArgumentOutOfRangeException(nameof(resourceCount));

        GraphPersistentParameterBindingsId[] bindingIds =
            new GraphPersistentParameterBindingsId[materials.Length];
        for (int index = 0; index < materials.Length; index++)
            bindingIds[index] = edit.RegisterPersistentParameterBindings(materials[index], []);

        GraphTextureId color = edit.CreateTransientTexture(ColorTextureDescription(
            $"Dagor full-HD color target {resourceCount}"));
        GraphColorAttachmentViewId colorView = edit.CreateColorAttachmentView(
            color,
            ColorRange,
            Format.R8G8B8A8UNorm,
            TextureViewDimension.Texture2D);

        var rng = new DeterministicRng(seed);
        ulong[] sizes = new ulong[resourceCount - 1];
        for (int index = 0; index < sizes.Length; index++)
            sizes[index] = SampleSize(ref rng);
        var buffers = new GraphBufferId[3];
        const BufferUsages commonUsages =
            BufferUsages.ShaderRead |
            BufferUsages.ShaderWrite |
            BufferUsages.CopySource |
            BufferUsages.CopyDestination;
        for (int index = 0; index < buffers.Length; index++)
        {
            BufferUsages usages = commonUsages;
            if (index == 0) usages |= BufferUsages.Indirect;
            ulong minimumSize = index == 0 ? DrawIndirectBytes : CopyBytes;
            buffers[index] = edit.CreateTransientBuffer(new BufferDesc(
                Math.Max(sizes[index], minimumSize),
                usages,
                $"Dagor source allocation {index:D3} size {sizes[index]}"));
        }

        var textures = new GraphTextureId[sizes.Length - buffers.Length];
        for (int index = 0; index < textures.Length; index++)
        {
            int sourceIndex = index + buffers.Length;
            textures[index] = edit.CreateTransientTexture(SourceTextureDescription(
                sizes[sourceIndex],
                $"Dagor source texture {sourceIndex:D3} size {sizes[sourceIndex]}"));
        }

        List<GraphTextureId>[] writes = CreatePassLists<GraphTextureId>();
        List<GraphTextureId>[] reads = CreatePassLists<GraphTextureId>();
        for (int resource = 0; resource < textures.Length; resource++)
        {
            int lifetime = SampleLifetime(ref rng);
            if (lifetime == 0)
            {
                writes[rng.Next(PassCount)].Add(textures[resource]);
                continue;
            }
            int span = Math.Clamp(lifetime, 1, PassCount - 1);
            int start = rng.Next(PassCount - span);
            int end = start + span;
            writes[start].Add(textures[resource]);
            reads[end].Add(textures[resource]);
        }

        DagorPassKind[] kinds = BuildPassKinds();
        int rasterOrdinal = 0;
        int computeOrdinal = 0;
        int copyOrdinal = 0;
        int controlOrdinal = 0;
        GraphBufferId indirectArguments = buffers[0];
        GraphBufferId copyA = buffers[1];
        GraphBufferId copyB = buffers[2];

        for (int pass = 0; pass < kinds.Length; pass++)
        {
            DagorPassKind kind = kinds[pass];
            int kindOrdinal = kind switch
            {
                DagorPassKind.Raster => rasterOrdinal++,
                DagorPassKind.Compute => computeOrdinal++,
                DagorPassKind.Copy => copyOrdinal++,
                DagorPassKind.Control => controlOrdinal++,
                _ => throw new ArgumentOutOfRangeException(),
            };
            bool firstCopy = kind == DagorPassKind.Copy && kindOrdinal == 0;
            bool firstRaster = kind == DagorPassKind.Raster && kindOrdinal == 0;
            bool indirectCompute = kind == DagorPassKind.Compute && (kindOrdinal & 3) == 3;
            GraphBufferId copySource = (kindOrdinal & 1) == 1 ? copyA : copyB;
            GraphBufferId copyDestination = (kindOrdinal & 1) == 1 ? copyB : copyA;
            GraphPersistentParameterBindingsId bindings =
                bindingIds[kind == DagorPassKind.Raster ? kindOrdinal % bindingIds.Length : 0];
            var state = new DagorPassState(
                kind,
                kindOrdinal,
                graphicsPipeline,
                computePipeline,
                bindings,
                colorView,
                indirectArguments,
                copyA,
                copyB,
                copySource,
                copyDestination,
                drawIndirect,
                dispatchIndirect,
                firstRaster,
                firstCopy,
                indirectCompute,
                writes[pass].ToArray(),
                reads[pass].ToArray());
            AddPass(edit, graphicsQueue, pass, in state);
        }

        if (rasterOrdinal != RasterPassCount ||
            computeOrdinal != ComputePassCount ||
            copyOrdinal != CopyPassCount ||
            controlOrdinal != ControlPassCount)
        {
            throw new InvalidOperationException("The Dagor command-site projection has inconsistent pass counts.");
        }
    }

    private static List<T>[] CreatePassLists<T>()
    {
        var result = new List<T>[PassCount];
        for (int index = 0; index < result.Length; index++)
            result[index] = [];
        return result;
    }

    private static DagorPassKind[] BuildPassKinds()
    {
        int[] targets = [RasterPassCount, ComputePassCount, CopyPassCount, ControlPassCount];
        int[] used = [0, 0, 1, 0];
        var result = new DagorPassKind[PassCount];
        result[0] = DagorPassKind.Copy;
        for (int pass = 1; pass < result.Length; pass++)
        {
            int selected = -1;
            int bestScore = int.MinValue;
            for (int kind = 0; kind < targets.Length; kind++)
            {
                if (used[kind] >= targets[kind]) continue;
                int score = checked(targets[kind] * (pass + 1) - used[kind] * PassCount);
                if (score <= bestScore) continue;
                selected = kind;
                bestScore = score;
            }
            if (selected < 0)
                throw new InvalidOperationException("Unable to distribute the Dagor pass kinds.");
            result[pass] = (DagorPassKind)selected;
            used[selected]++;
        }
        for (int kind = 0; kind < targets.Length; kind++)
            if (used[kind] != targets[kind])
                throw new InvalidOperationException("The Dagor pass-kind distribution is incomplete.");
        return result;
    }

    private static void AddPass(
        RenderGraphEdit edit,
        Queue queue,
        int pass,
        in DagorPassState state)
    {
        const PassRecordingMode recording = PassRecordingMode.WorkerEligible;
        uint executionCost = state.Kind switch
        {
            DagorPassKind.Raster => 4,
            DagorPassKind.Compute => 2,
            DagorPassKind.Copy => 2,
            _ => 0,
        };
        uint recordingCost = state.Kind switch
        {
            DagorPassKind.Raster => 4,
            DagorPassKind.Compute => 2,
            DagorPassKind.Copy => 2,
            _ => 1,
        };
        PassOptions options = new(
            PassCullingMode.NeverCull,
            PassSchedulingMode.PreserveDeclarationPosition,
            recording,
            state.Kind == DagorPassKind.Raster
                ? RasterPassMergeMode.Mergeable
                : RasterPassMergeMode.Isolated,
            EstimatedExecutionCost: executionCost,
            EstimatedRecordingCost: recordingCost);
        string label = $"Dagor {state.Kind.ToString().ToLowerInvariant()} {state.KindOrdinal:D2} pass {pass:D2}";
        switch (state.Kind)
        {
            case DagorPassKind.Raster:
                _ = edit.AddRasterPass<DagorPassState, byte>(
                    label,
                    PassQueueSelection.Exact(queue),
                    state,
                    options,
                    DeclareRaster,
                    RecordRaster);
                break;
            case DagorPassKind.Compute:
                _ = edit.AddComputePass<DagorPassState, byte>(
                    label,
                    PassQueueSelection.Exact(queue),
                    state,
                    options,
                    DeclareCompute,
                    RecordCompute);
                break;
            case DagorPassKind.Copy:
                _ = edit.AddCopyPass<DagorPassState, byte>(
                    label,
                    PassQueueSelection.Exact(queue),
                    state,
                    options,
                    DeclareCopy,
                    RecordCopy);
                break;
            case DagorPassKind.Control:
                _ = edit.AddGeneralPass<DagorPassState, byte>(
                    label,
                    PassQueueSelection.Exact(queue),
                    state,
                    options,
                    DeclareControl,
                    static (ref GeneralPassCommandScope _, in DagorPassState _, in byte _) => { });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static void DeclareRaster(ref PassDefinition definition, ref DagorPassState state)
    {
        definition.Bind(state.Bindings);
        definition.ColorAttachment(
            0,
            state.ColorTarget,
            state.FirstRaster ? LoadType.Clear : LoadType.Load,
            StoreType.Store,
            state.FirstRaster ? WriteCoverage.Complete : WriteCoverage.Partial,
            Vector4.Zero);
        _ = definition.Read(
            state.IndirectArguments,
            new BufferRange(0, DrawIndirectBytes),
            PipelineSync.ExecuteIndirect,
            ResourceAccess.IndirectArgument);
        DeclareCommon(
            ref definition,
            in state,
            PipelineSync.PixelShading,
            ResourceAccess.ShaderResource,
            ResourceAccess.UnorderedAccess);
    }

    private static void RecordRaster(
        ref RasterPassCommandScope commands,
        in DagorPassState state,
        in byte _)
    {
        commands.SetPipeline(state.GraphicsPipeline);
        commands.SetPersistentParameterBindings(state.Bindings);
        commands.SetViewports(Viewports);
        commands.SetScissors(CpuOnlyScissors);
        commands.Draw(new DrawArguments(3, 1, 0, 0));
        Buffer arguments = commands.GetBuffer(state.IndirectArguments);
        commands.ExecuteIndirect(
            state.DrawIndirect,
            new BufferRegion(arguments, new BufferRange(0, DrawIndirectBytes)),
            IndirectDrawsPerRasterPass);
    }

    private static void DeclareCompute(ref PassDefinition definition, ref DagorPassState state)
    {
        if (state.IndirectCompute)
        {
            _ = definition.Read(
                state.IndirectArguments,
                new BufferRange(0, DispatchIndirectBytes),
                PipelineSync.ExecuteIndirect,
                ResourceAccess.IndirectArgument);
        }
        DeclareCommon(
            ref definition,
            in state,
            PipelineSync.ComputeShading,
            ResourceAccess.ShaderResource,
            ResourceAccess.UnorderedAccess);
    }

    private static void RecordCompute(
        ref ComputePassCommandScope commands,
        in DagorPassState state,
        in byte _)
    {
        commands.SetPipeline(state.ComputePipeline);
        if (!state.IndirectCompute)
        {
            commands.Dispatch(new DispatchArguments(1, 1, 1));
            return;
        }
        Buffer arguments = commands.GetBuffer(state.IndirectArguments);
        commands.ExecuteIndirect(
            state.DispatchIndirect,
            new BufferRegion(arguments, new BufferRange(0, DispatchIndirectBytes)),
            1);
    }

    private static void DeclareCopy(ref PassDefinition definition, ref DagorPassState state)
    {
        if (state.FirstCopy)
        {
            _ = definition.Write(
                state.IndirectArguments,
                new BufferRange(0, DrawIndirectBytes),
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                WriteCoverage.Complete,
                ResourceContentState.Defined);
            _ = definition.Write(
                state.CopyA,
                new BufferRange(0, CopyBytes),
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                WriteCoverage.Complete,
                ResourceContentState.Defined);
            _ = definition.Write(
                state.CopyB,
                new BufferRange(0, CopyBytes),
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                WriteCoverage.Complete,
                ResourceContentState.Defined);
        }
        else
        {
            _ = definition.Read(
                state.CopySource,
                new BufferRange(0, CopyBytes),
                PipelineSync.Copy,
                ResourceAccess.CopySource);
            _ = definition.Write(
                state.CopyDestination,
                new BufferRange(0, CopyBytes),
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                WriteCoverage.Complete,
                ResourceContentState.Defined);
        }
        DeclareCommon(
            ref definition,
            in state,
            PipelineSync.Copy,
            ResourceAccess.CopySource,
            ResourceAccess.CopyDestination);
    }

    private static void RecordCopy(
        ref CopyPassCommandScope commands,
        in DagorPassState state,
        in byte _)
    {
        if (state.FirstCopy)
        {
            commands.ClearBuffer(
                commands.GetBuffer(state.IndirectArguments),
                new BufferRange(0, DrawIndirectBytes),
                1);
            commands.ClearBuffer(
                commands.GetBuffer(state.CopyA),
                new BufferRange(0, CopyBytes),
                1);
            commands.ClearBuffer(
                commands.GetBuffer(state.CopyB),
                new BufferRange(0, CopyBytes),
                1);
            return;
        }
        commands.CopyBuffer(new BufferCopy(
            commands.GetBuffer(state.CopySource),
            0,
            commands.GetBuffer(state.CopyDestination),
            0,
            CopyBytes));
    }

    private static void DeclareControl(ref PassDefinition definition, ref DagorPassState state) =>
        DeclareCommon(
            ref definition,
            in state,
            PipelineSync.AllShading,
            ResourceAccess.ShaderResource,
            ResourceAccess.UnorderedAccess);

    private static void DeclareCommon(
        ref PassDefinition definition,
        in DagorPassState state,
        PipelineSync sync,
        ResourceAccess readAccess,
        ResourceAccess writeAccess)
    {
        const TextureLayout writeLayout = TextureLayout.General;
        const TextureLayout readLayout = TextureLayout.General;
        foreach (GraphTextureId texture in state.Writes)
        {
            _ = definition.Write(
                texture,
                ColorRange,
                sync,
                writeAccess,
                writeLayout,
                WriteCoverage.Complete,
                ResourceContentState.Defined);
        }
        foreach (GraphTextureId texture in state.Reads)
        {
            _ = definition.Read(
                texture,
                ColorRange,
                sync,
                readAccess,
                readLayout);
        }
    }

    private static CapturedStatistics CaptureStatistics(
        RenderGraph.RenderGraph graph,
        IGraphicsBackend backend)
    {
        var capture = new StatisticsCapture();
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int count;
        using (RenderGraphFrame frame = graph.BeginFrame(new RenderGraphFrameOptions(
            FrameSubmissionMode.RecordAllThenSubmit,
            Debug: RenderGraphDebugOptions.DisableSplitBarriers,
            Diagnostics: capture.Accept)))
        {
            count = frame.Execute(completions);
        }
        WaitForPending(backend, graph, completions, count);
        if (!capture.HasValue)
            throw new InvalidOperationException("The RenderGraph did not publish diagnostic statistics.");
        if (count != 1)
            throw new InvalidOperationException($"The source case submitted {count} queues instead of one.");
        return new CapturedStatistics(
            capture.Statistics,
            capture.BarrierBoundaryCount,
            capture.BufferBarrierCount,
            capture.TextureBarrierCount,
            capture.QueueTransferBarrierCount,
            capture.AliasingBarrierCount,
            capture.SplitBarrierPairCount);
    }

    private static Measurement MeasureStable(
        RenderGraph.RenderGraph graph,
        IGraphicsBackend backend,
        RenderGraphFrameOptions frameOptions,
        int minimumWarmupFrames,
        int measuredFrames)
    {
        var pending = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int pendingCount = 0;
        var warmup = new List<GraphicsCpuFrameSample>();
        bool plateau = WarmUntilPlateau(Execute, minimumWarmupFrames, warmup);
        var samples = new GraphicsCpuFrameSample[measuredFrames];
        for (int frame = 0; frame < samples.Length; frame++)
            samples[frame] = Execute(frame);
        WaitForPending(backend, graph, pending, pendingCount);
        return new Measurement(warmup.ToArray(), samples, plateau);

        GraphicsCpuFrameSample Execute(int frameIndex)
        {
            WaitForPending(backend, graph, pending, pendingCount);
            long started = Stopwatch.GetTimestamp();
            using RenderGraphFrame frame = graph.BeginFrame(frameOptions);
            pendingCount = frame.Execute(pending);
            long stopped = Stopwatch.GetTimestamp();
            long ticks = stopped - started;
            return new GraphicsCpuFrameSample(
                frameIndex,
                ticks,
                BenchmarkClock.TicksToMicroseconds(ticks),
                pendingCount);
        }
    }

    private static bool WarmUntilPlateau(
        Func<int, GraphicsCpuFrameSample> execute,
        int minimumWarmupFrames,
        List<GraphicsCpuFrameSample> destination)
    {
        int maximumWarmupFrames = Math.Max(
            checked(minimumWarmupFrames * 8),
            checked(minimumWarmupFrames + PlateauWindowSize * (RequiredStableWindows + 1)));
        int stableWindows = 0;
        for (int frame = 0; frame < maximumWarmupFrames; frame++)
        {
            destination.Add(execute(frame));
            int count = destination.Count;
            if (count < minimumWarmupFrames ||
                count < PlateauWindowSize * 2 ||
                count % PlateauWindowSize != 0)
            {
                continue;
            }

            double previous = WindowMedian(destination, count - PlateauWindowSize * 2, PlateauWindowSize);
            double current = WindowMedian(destination, count - PlateauWindowSize, PlateauWindowSize);
            double scale = Math.Max(Math.Abs(previous), 1);
            if (Math.Abs(current - previous) / scale <= PlateauTolerance)
                stableWindows++;
            else
                stableWindows = 0;
            if (stableWindows >= RequiredStableWindows)
                return true;
        }
        return false;
    }

    private static double WindowMedian(
        List<GraphicsCpuFrameSample> samples,
        int start,
        int count)
    {
        var values = new double[count];
        for (int index = 0; index < count; index++)
            values[index] = samples[start + index].CpuMicroseconds;
        Array.Sort(values);
        int middle = count / 2;
        return (count & 1) == 0
            ? (values[middle - 1] + values[middle]) * 0.5
            : values[middle];
    }

    private static void WaitForPending(
        IGraphicsBackend backend,
        RenderGraph.RenderGraph graph,
        QueueCompletion[] pending,
        int count)
    {
        for (int index = 0; index < count; index++)
        {
            if (backend.WaitCpu(pending[index], GpuTimeout) != WaitStatus.Completed)
                throw new TimeoutException("A benchmark frame did not complete within thirty seconds.");
        }
        if (count != 0)
            graph.CollectCompleted();
    }

    private static Pipeline CreateGraphicsPipeline(
        IGraphicsBackend backend,
        Device device,
        BenchmarkShaderProgram shader)
    {
        Format[] colorFormats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blendAttachments =
        [
            new(Enabled: false, WriteMask: ColorWriteMasks.All),
        ];
        return backend.CreateGraphicsPipeline(
            device,
            new GraphicsPipelineDesc(
                shader.Program,
                shader.Entries[0],
                shader.Entries[1],
                [],
                [],
                PrimitiveTopology.TriangleList,
                StripCut.Disabled,
                new RasterizerState(Cull: CullType.None),
                new MultisampleState(SampleCount: 1),
                new DepthStencilState(),
                new BlendState(blendAttachments),
                new AttachmentFormatSignature(colorFormats, null),
                DynamicStates.Viewport | DynamicStates.Scissor,
                "Dagor graph CPU graphics"));
    }

    private static PersistentParameterBindings[] CreateMaterials(
        IGraphicsBackend backend,
        Device device,
        Pipeline pipeline,
        VariableLayoutReflection layout,
        int count)
    {
        var result = new PersistentParameterBindings[count];
        try
        {
            for (int index = 0; index < result.Length; index++)
            {
                byte[] data = new byte[16];
                WriteFloat(data.AsSpan(0, 4), ((index * 17) & 255) / 255f);
                WriteFloat(data.AsSpan(4, 4), ((index * 29 + 31) & 255) / 255f);
                WriteFloat(data.AsSpan(8, 4), ((index * 43 + 7) & 255) / 255f);
                WriteFloat(data.AsSpan(12, 4), 1);
                result[index] = backend.CreatePersistentParameterBindings(
                    device,
                    pipeline,
                    new ParameterBlockBindings(layout, NoResources, data),
                    $"Dagor graph CPU material {index}");
            }
            return result;
        }
        catch
        {
            DisposeAll(result);
            throw;
        }
    }

    private static void WriteFloat(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static TextureDesc ColorTextureDescription(string label) => new(
        TextureDimension.Texture2D,
        Width,
        Height,
        1,
        1,
        1,
        1,
        Format.R8G8B8A8UNorm,
        TextureUsages.ColorAttachment | TextureUsages.Sampled | TextureUsages.Storage,
        label: label);

    private static TextureDesc SourceTextureDescription(ulong sampledBytes, string label)
    {
        const ulong bytesPerPixel = 4;
        ulong pixelCount = Math.Max(1, (sampledBytes + bytesPerPixel - 1) / bytesPerPixel);
        uint width = checked((uint)Math.Max(
            1,
            Math.Ceiling(Math.Sqrt(pixelCount * (16.0 / 9.0)))));
        uint height = checked((uint)Math.Max(
            1,
            (pixelCount + width - 1) / width));
        return new TextureDesc(
            TextureDimension.Texture2D,
            width,
            height,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.Sampled |
            TextureUsages.Storage |
            TextureUsages.CopySource |
            TextureUsages.CopyDestination,
            label: label);
    }

    private static int SampleLifetime(ref DeterministicRng rng)
    {
        int twoFrameTimepoints = SampleWeighted(ref rng, LifetimeDistribution);
        return (twoFrameTimepoints + SourceScheduleFrameWindow - 1) /
            SourceScheduleFrameWindow;
    }

    private static ulong SampleSize(ref DeterministicRng rng)
    {
        int total = 0;
        foreach (WeightedSize item in SizeDistribution)
            total = checked(total + item.Weight);
        int selected = rng.Next(total);
        foreach (WeightedSize item in SizeDistribution)
        {
            if (selected < item.Weight) return item.Value;
            selected -= item.Weight;
        }
        throw new InvalidOperationException("The size distribution is empty.");
    }

    private static int SampleWeighted(
        ref DeterministicRng rng,
        ReadOnlySpan<WeightedInt> distribution)
    {
        int total = 0;
        foreach (WeightedInt item in distribution)
            total = checked(total + item.Weight);
        int selected = rng.Next(total);
        foreach (WeightedInt item in distribution)
        {
            if (selected < item.Weight) return item.Value;
            selected -= item.Weight;
        }
        throw new InvalidOperationException("The weighted distribution is empty.");
    }

    private static AdapterInfo FindAdapter(IGraphicsBackend backend, AdapterId id)
    {
        AdapterEnumerationOptions options = new(AdapterPreference.HighPerformance, IncludeSoftware: true);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) ||
            confirmed != adapters.Length)
        {
            throw new InvalidOperationException("The adapter set changed during enumeration.");
        }
        foreach (ref readonly AdapterInfo adapter in adapters.AsSpan())
            if (adapter.Id == id) return adapter;
        string available = string.Join(", ", adapters.Select(static value =>
            $"{value.Name} [0x{value.Id.Low:X}:0x{value.Id.High:X}]"));
        throw new BenchmarkUsageException(
            $"The explicitly selected adapter is unavailable. Available adapters: {available}.");
    }

    private static void DisposeAll(PersistentParameterBindings[] values)
    {
        for (int index = values.Length - 1; index >= 0; index--)
            values[index]?.Dispose();
    }

    private enum DagorPassKind : byte
    {
        Raster,
        Compute,
        Copy,
        Control,
    }

    private readonly record struct DagorPassState(
        DagorPassKind Kind,
        int KindOrdinal,
        Pipeline GraphicsPipeline,
        Pipeline ComputePipeline,
        GraphPersistentParameterBindingsId Bindings,
        GraphColorAttachmentViewId ColorTarget,
        GraphBufferId IndirectArguments,
        GraphBufferId CopyA,
        GraphBufferId CopyB,
        GraphBufferId CopySource,
        GraphBufferId CopyDestination,
        IndirectCommandLayout DrawIndirect,
        IndirectCommandLayout DispatchIndirect,
        bool FirstRaster,
        bool FirstCopy,
        bool IndirectCompute,
        GraphTextureId[] Writes,
        GraphTextureId[] Reads);

    private readonly record struct WeightedInt(int Value, int Weight);
    private readonly record struct WeightedSize(ulong Value, int Weight);

    private readonly record struct Measurement(
        GraphicsCpuFrameSample[] Warmup,
        GraphicsCpuFrameSample[] Samples,
        bool PlateauReached);

    private readonly record struct CapturedStatistics(
        RenderGraphStatistics Statistics,
        int BarrierBoundaryCount,
        int BufferBarrierCount,
        int TextureBarrierCount,
        int QueueTransferBarrierCount,
        int AliasingBarrierCount,
        int SplitBarrierPairCount);

    private sealed class StatisticsCapture
    {
        private readonly HashSet<BarrierBoundary> _barrierBoundaries = [];

        internal bool HasValue { get; private set; }
        internal RenderGraphStatistics Statistics { get; private set; }
        internal int BarrierBoundaryCount => _barrierBoundaries.Count;
        internal int BufferBarrierCount { get; private set; }
        internal int TextureBarrierCount { get; private set; }
        internal int QueueTransferBarrierCount { get; private set; }
        internal int AliasingBarrierCount { get; private set; }
        internal int SplitBarrierPairCount { get; private set; }

        internal void Accept(in RenderGraphDiagnosticsView diagnostics)
        {
            _barrierBoundaries.Clear();
            Statistics = diagnostics.Statistics;
            foreach (ref readonly RenderGraphBarrierDiagnostic barrier in diagnostics.Barriers)
            {
                _barrierBoundaries.Add(new BarrierBoundary(barrier.Pass, barrier.Phase));
                if (barrier.Phase == BarrierPhase.Begin)
                    SplitBarrierPairCount++;
                switch (barrier.Kind)
                {
                    case RenderGraphBarrierKind.Buffer:
                        BufferBarrierCount++;
                        break;
                    case RenderGraphBarrierKind.Texture:
                        TextureBarrierCount++;
                        break;
                    case RenderGraphBarrierKind.QueueAcquire:
                    case RenderGraphBarrierKind.QueueRelease:
                        QueueTransferBarrierCount++;
                        break;
                    case RenderGraphBarrierKind.Aliasing:
                        AliasingBarrierCount++;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
            HasValue = true;
        }
    }

    private readonly record struct BarrierBoundary(GraphPassId Pass, BarrierPhase Phase);

    private struct DeterministicRng
    {
        private uint _state;

        internal DeterministicRng(uint seed) => _state = seed == 0 ? 0xA341316Cu : seed;

        internal int Next(int exclusiveMaximum)
        {
            if (exclusiveMaximum <= 0)
                throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
            uint value = NextUInt();
            return (int)(((ulong)value * (uint)exclusiveMaximum) >> 32);
        }

        private uint NextUInt()
        {
            uint value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return value;
        }
    }
}
