using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Benchmarks;

internal static class RhiBenchmarkRunner
{
    private static readonly TimeSpan GpuTimeout = TimeSpan.FromSeconds(30);
    private static readonly ResourceBinding[] NoResources = [];
    private static readonly RecordedCommands[] OneCommands = new RecordedCommands[1];

    internal static ProcessRun Run(in WorkerConfiguration configuration)
    {
        try
        {
            return configuration.Variant switch
            {
                ReceiverVariant.GenericRhi => RunGeneric(configuration),
                ReceiverVariant.InterfaceRhi => RunInterface(configuration),
                _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
            };
        }
        catch (Exception exception)
        {
            return new ProcessRun(
                configuration.Variant,
                RunDisposition.Failed,
                exception.ToString(),
                BenchmarkEnvironment.Unavailable(configuration.ProcessIndex, ".NET/Silk RHI"),
                []);
        }
    }

    private static ProcessRun RunGeneric(in WorkerConfiguration configuration)
    {
        D3D12Backend backend = new();
        using var graphics = new Graphics<D3D12Backend>(backend);
        return RunCore<GenericRhiDispatch, GenericRhiDispatch>(
            new GenericRhiDispatch(graphics),
            backend,
            configuration);
    }

    private static ProcessRun RunInterface(in WorkerConfiguration configuration)
    {
        using IGraphicsBackend backend = new D3D12Backend();
        return RunCore<InterfaceRhiDispatch, InterfaceRhiDispatch>(
            new InterfaceRhiDispatch(backend),
            (D3D12Backend)backend,
            configuration);
    }

    private static ProcessRun RunCore<TReceiver, TDispatch>(
        TReceiver receiver,
        D3D12Backend backend,
        in WorkerConfiguration configuration)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        AdapterInfo adapter = FindAdapter(backend, configuration.AdapterId);
        DeviceQueueDesc[] queueDescriptions =
        [
            new(QueueType.Graphics),
            new(QueueType.Compute),
            new(QueueType.Copy),
        ];
        using Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            RetirementType.Automatic,
            queueDescriptions,
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.CalibratedTimestamps,
            label: $"{configuration.Variant} benchmark device"));
        if (!backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics) || diagnostics is null)
            throw new InvalidOperationException("The D3D12 diagnostics snapshot is unavailable.");
        RuntimeEnvironment environment = BenchmarkEnvironment.Capture(
            configuration,
            adapter,
            diagnostics.DebugLayerEnabled || diagnostics.GpuBasedValidationEnabled || diagnostics.SynchronizedQueueValidationEnabled,
            diagnostics.DredEnabled,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription + " / Silk.NET 2.23.0");
        if (configuration.Profile == BenchmarkProfile.VendorCertification &&
            (environment.ValidationEnabled || environment.DredEnabled || environment.CaptureToolLoaded))
        {
            return new ProcessRun(
                configuration.Variant,
                RunDisposition.Unexecuted,
                "Validation, DRED, or a capture tool is enabled.",
                environment,
                []);
        }

        using BenchmarkShaderProgram shader = BenchmarkShaders.Open(configuration.ShaderDirectory);
        Format[] colorFormats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blendAttachments =
        [
            new(Enabled: false, WriteMask: ColorWriteMasks.All),
        ];
        BlendState blend = new(blendAttachments);
        AttachmentFormatSignature attachments = new(colorFormats, null);
        using Pipeline graphicsPipeline = backend.CreateGraphicsPipeline(
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
                blend,
                attachments,
                DynamicStates.Viewport | DynamicStates.Scissor,
                "graphics benchmark pipeline"));
        using Pipeline computePipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[2], "graphics benchmark compute"));
        VariableLayoutReflection globalLayout =
            shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        if (globalLayout == VariableLayoutReflection.Null)
            throw new InvalidDataException("The benchmark shader has no global parameter layout.");
        byte[] persistentData = new byte[16];
        WriteTint(persistentData, 1, 1, 1, 1);
        using PersistentParameterBindings persistentBindings = backend.CreatePersistentParameterBindings(
            device,
            new ParameterBlockBindings(globalLayout, NoResources, persistentData),
            "benchmark persistent tint");
        backend.PublishDescriptors(device);

        WorkloadRun[] workloads =
        [
            RunEmptySubmit<TReceiver, TDispatch>(receiver, backend, device, shader.ManifestSha256, configuration),
            RunDraw<TReceiver, TDispatch>(receiver, backend, diagnostics, device, graphicsPipeline, persistentBindings, globalLayout, shader.ManifestSha256, configuration, GraphicsWorkload.PersistentDraw10000),
            RunDraw<TReceiver, TDispatch>(receiver, backend, diagnostics, device, graphicsPipeline, persistentBindings, globalLayout, shader.ManifestSha256, configuration, GraphicsWorkload.TransientDraw10000),
            RunDraw<TReceiver, TDispatch>(receiver, backend, diagnostics, device, graphicsPipeline, persistentBindings, globalLayout, shader.ManifestSha256, configuration, GraphicsWorkload.StateSuppression10000),
            RunExplicitBarriers<TReceiver, TDispatch>(receiver, backend, device, computePipeline, shader.ManifestSha256, configuration),
            RunThreeQueuePresent<TReceiver, TDispatch>(receiver, backend, device, graphicsPipeline, computePipeline, persistentBindings, shader.ManifestSha256, configuration),
        ];
        RunDisposition disposition = configuration.Profile == BenchmarkProfile.VendorCertification
            ? RunDisposition.Passed
            : RunDisposition.FunctionalOnly;
        return new ProcessRun(
            configuration.Variant,
            disposition,
            configuration.Profile == BenchmarkProfile.VendorCertification
                ? "All fixed RHI workloads executed."
                : "All reduced-count RHI workloads executed on WARP; not performance evidence.",
            environment,
            workloads);
    }

    private static WorkloadRun RunEmptySubmit<TReceiver, TDispatch>(
        TReceiver receiver,
        D3D12Backend backend,
        Device device,
        string shaderManifest,
        in WorkerConfiguration configuration)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        var samples = new FrameSample[configuration.MeasuredFrames];
        using var allocations = new AllocationEventCounter();
        for (int frame = 0; frame < configuration.WarmupFrames; frame++)
        {
            QueueSubmitDesc submit = new([], [], [], [], []);
            QueueCompletion completion = TDispatch.Submit(receiver, queue, submit);
            RequireCompleted(TDispatch.WaitCpu(receiver, completion, GpuTimeout));
            TDispatch.CollectCompleted(receiver, device);
        }
        for (int frame = 0; frame < samples.Length; frame++)
        {
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long beforeEvents = allocations.Count;
            long started = Stopwatch.GetTimestamp();
            QueueSubmitDesc submit = new([], [], [], [], []);
            QueueCompletion completion = TDispatch.Submit(receiver, queue, submit);
            long stopped = Stopwatch.GetTimestamp();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            long events = AllocationEventCounter.AttributeIntervalEvents(
                bytes,
                allocations.Count - beforeEvents);
            RequireCompleted(TDispatch.WaitCpu(receiver, completion, GpuTimeout));
            TDispatch.CollectCompleted(receiver, device);
            long ticks = stopped - started;
            samples[frame] = new FrameSample(
                frame,
                ticks,
                BenchmarkClock.TicksToMicroseconds(ticks),
                null,
                bytes,
                events,
                completion.Value);
        }
        return BenchmarkOutput.Complete(
            GraphicsWorkload.EmptySubmit,
            configuration.Profile,
            configuration.WarmupFrames,
            configuration.MeasuredFrames,
            0,
            0,
            samples,
            [],
            BenchmarkOutput.FixedHash(GraphicsWorkload.EmptySubmit, shaderManifest),
            shaderManifest,
            [],
            default);
    }

    private static WorkloadRun RunDraw<TReceiver, TDispatch>(
        TReceiver receiver,
        D3D12Backend backend,
        D3D12Diagnostics diagnostics,
        Device device,
        Pipeline pipeline,
        PersistentParameterBindings persistent,
        VariableLayoutReflection layout,
        string shaderManifest,
        in WorkerConfiguration configuration,
        GraphicsWorkload workload)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        TextureDesc targetDescription = new(
            TextureDimension.Texture2D,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.ColorAttachment | TextureUsages.CopySource,
            label: $"{workload} target");
        using Texture target = backend.CreateTexture(device, targetDescription);
        TextureSubresourceRange targetRange = new(0, 1, 0, 1, TextureAspects.Color);
        using ColorAttachmentView targetView = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                target,
                targetRange,
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        using QueryPool queries = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 2));
        using Buffer queryReadback = backend.CreateBuffer(
            device,
            new BufferDesc(16, BufferUsages.QueryResolve),
            MemoryType.Readback);
        BufferRange queryRange = new(0, 16);
        using MappedBuffer queryMapping = TDispatch.Map(
            receiver,
            queryReadback,
            MapType.Read,
            queryRange);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 2, Label: $"{workload} context"));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        ColorAttachmentDesc[] colors =
        [
            new(targetView, LoadType.Clear, StoreType.Store, new Vector4(0.0625f, 0.125f, 0.25f, 1)),
        ];
        Viewport[] viewports = [new(0, 0, FixedGraphicsProtocol.RenderWidth, FixedGraphicsProtocol.RenderHeight)];
        ScissorRect[] scissors = [new(0, 0, FixedGraphicsProtocol.RenderWidth, FixedGraphicsProtocol.RenderHeight)];
        byte[] transientPackets = workload == GraphicsWorkload.TransientDraw10000
            ? new byte[checked(configuration.DrawCount * 16)]
            : [];
        DrawArguments draw = new(3, 1, 0, 0);
        CommandRecordingDesc recording = new(
            InitialCapturedResourceCapacity: 16,
            Label: workload.ToString());
        bool initialized = false;
        var samples = new FrameSample[configuration.MeasuredFrames];
        var calibrations = new CalibrationRecord[configuration.MeasuredFrames];
        D3D12CommandStatistics statistics = default;
        using var allocations = new AllocationEventCounter();

        for (int frame = 0; frame < configuration.WarmupFrames; frame++)
        {
            _ = ExecuteDrawFrame<TReceiver, TDispatch>(
                receiver,
                diagnostics,
                device,
                queue,
                context,
                queries,
                queryReadback,
                queryMapping,
                queryRange,
                target,
                targetRange,
                colors,
                viewports,
                scissors,
                pipeline,
                persistent,
                layout,
                transientPackets,
                draw,
                recording,
                workload,
                configuration.DrawCount,
                ref initialized,
                allocations,
                frame);
        }
        for (int frame = 0; frame < samples.Length; frame++)
        {
            FrameMeasurement measurement = ExecuteDrawFrame<TReceiver, TDispatch>(
                receiver,
                diagnostics,
                device,
                queue,
                context,
                queries,
                queryReadback,
                queryMapping,
                queryRange,
                target,
                targetRange,
                colors,
                viewports,
                scissors,
                pipeline,
                persistent,
                layout,
                transientPackets,
                draw,
                recording,
                workload,
                configuration.DrawCount,
                ref initialized,
                allocations,
                frame);
            samples[frame] = measurement.Sample;
            calibrations[frame] = ToCalibration(QueueType.Graphics, frame, measurement.Calibration);
            if (frame == 0)
                statistics = measurement.CommandStatistics;
            else if (statistics != measurement.CommandStatistics)
                throw new InvalidDataException("Native command setter counts changed between stable frames.");
        }

        string outputHash = ReadTextureHash<TReceiver, TDispatch>(
            receiver,
            backend,
            device,
            queue,
            target,
            targetRange);
        BarrierEvidence[] barriers =
        [
            new(0, nameof(TextureBarrier), 0, 1, null),
            new(1, nameof(TextureBarrier), 1, 1, null),
        ];
        NativeSetterEvidence setters = new(
            statistics.PipelineSetters,
            statistics.PersistentBindingSetters,
            statistics.ViewportSetters,
            statistics.ScissorSetters,
            DrawCalls: configuration.DrawCount);
        return BenchmarkOutput.Complete(
            workload,
            configuration.Profile,
            configuration.WarmupFrames,
            configuration.MeasuredFrames,
            configuration.DrawCount,
            barriers.Length,
            samples,
            calibrations,
            outputHash,
            shaderManifest,
            barriers,
            setters);
    }

    private static FrameMeasurement ExecuteDrawFrame<TReceiver, TDispatch>(
        TReceiver receiver,
        D3D12Diagnostics diagnostics,
        Device device,
        Queue queue,
        CommandContext context,
        QueryPool queries,
        Buffer queryReadback,
        MappedBuffer queryMapping,
        in BufferRange queryRange,
        Texture target,
        in TextureSubresourceRange targetRange,
        ColorAttachmentDesc[] colors,
        Viewport[] viewports,
        ScissorRect[] scissors,
        Pipeline pipeline,
        PersistentParameterBindings persistent,
        VariableLayoutReflection layout,
        byte[] transientPackets,
        in DrawArguments draw,
        in CommandRecordingDesc recording,
        GraphicsWorkload workload,
        int drawCount,
        ref bool initialized,
        AllocationEventCounter allocations,
        int frameIndex)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        CalibratedTimestampInfo calibration = TDispatch.Calibrate(receiver, queue);
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long beforeEvents = allocations.Count;
        long started = Stopwatch.GetTimestamp();
        if (workload == GraphicsWorkload.TransientDraw10000)
        {
            for (int drawIndex = 0; drawIndex < drawCount; drawIndex++)
                WritePacket(transientPackets.AsSpan(drawIndex * 16, 16), drawIndex);
        }
        TDispatch.Begin(receiver, context, recording);
        TDispatch.WriteTimestamp(receiver, context, queries, 0);
        TextureBarrier first = new(
            target,
            targetRange,
            initialized ? PipelineSync.Copy : PipelineSync.None,
            PipelineSync.RenderTarget,
            initialized ? ResourceAccess.CopySource : ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            initialized ? TextureLayout.CopySource : TextureLayout.Undefined,
            TextureLayout.RenderTarget);
        TDispatch.Barrier(receiver, context, first);
        TDispatch.SetPipeline(receiver, context, pipeline);
        TDispatch.SetViewports(receiver, context, viewports);
        TDispatch.SetScissors(receiver, context, scissors);
        if (workload is GraphicsWorkload.PersistentDraw10000 or GraphicsWorkload.StateSuppression10000)
            TDispatch.SetPersistentBindings(receiver, context, persistent);
        RenderingDesc rendering = new(
            colors,
            null,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight);
        TDispatch.BeginRendering(receiver, context, rendering);
        switch (workload)
        {
            case GraphicsWorkload.PersistentDraw10000:
                TDispatch.DrawRepeated(receiver, context, draw, drawCount);
                break;
            case GraphicsWorkload.TransientDraw10000:
                TDispatch.DrawTransientPackets(
                    receiver,
                    context,
                    layout,
                    transientPackets,
                    draw,
                    drawCount);
                break;
            case GraphicsWorkload.StateSuppression10000:
                TDispatch.DrawWithRedundantState(
                    receiver,
                    context,
                    pipeline,
                    persistent,
                    viewports,
                    scissors,
                    draw,
                    drawCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(workload));
        }
        TDispatch.EndRendering(receiver, context);
        TextureBarrier second = new(
            target,
            targetRange,
            PipelineSync.RenderTarget,
            PipelineSync.Copy,
            ResourceAccess.RenderTarget,
            ResourceAccess.CopySource,
            TextureLayout.RenderTarget,
            TextureLayout.CopySource);
        TDispatch.Barrier(receiver, context, second);
        TDispatch.WriteTimestamp(receiver, context, queries, 1);
        TDispatch.ResolveQueries(receiver, context, queries, 0, 2, queryReadback, queryRange);
        RecordedCommands recorded = TDispatch.End(receiver, context);
        OneCommands[0] = recorded;
        QueueSubmitDesc submit = new([], [], OneCommands, [], []);
        QueueCompletion completion = TDispatch.Submit(receiver, queue, submit);
        long stopped = Stopwatch.GetTimestamp();
        D3D12CommandStatistics commandStatistics = diagnostics.GetCommandStatistics(recorded);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        long events = AllocationEventCounter.AttributeIntervalEvents(
            bytes,
            allocations.Count - beforeEvents);
        recorded.Dispose();
        RequireCompleted(TDispatch.WaitCpu(receiver, completion, GpuTimeout));
        TDispatch.CollectCompleted(receiver, device);
        (ulong gpuStart, ulong gpuEnd) = ReadTimestampPair(queryMapping, queryRange);
        initialized = true;
        long cpuTicks = stopped - started;
        double gpuMicroseconds = (gpuEnd - gpuStart) * (1_000_000.0 / calibration.QueueFrequency);
        return new FrameMeasurement(
            new FrameSample(
                frameIndex,
                cpuTicks,
                BenchmarkClock.TicksToMicroseconds(cpuTicks),
                gpuMicroseconds,
                bytes,
                events,
                completion.Value),
            calibration,
            commandStatistics);
    }

    private static WorkloadRun RunExplicitBarriers<TReceiver, TDispatch>(
        TReceiver receiver,
        D3D12Backend backend,
        Device device,
        Pipeline computePipeline,
        string shaderManifest,
        in WorkerConfiguration configuration)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        using QueryPool queries = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Compute, 2));
        using Buffer queryReadback = backend.CreateBuffer(
            device,
            new BufferDesc(16, BufferUsages.QueryResolve),
            MemoryType.Readback);
        BufferRange queryRange = new(0, 16);
        using MappedBuffer queryMapping = TDispatch.Map(
            receiver,
            queryReadback,
            MapType.Read,
            queryRange);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 0, 2, Label: "explicit barrier context"));
        Queue queue = backend.GetQueue(device, QueueType.Compute);
        MemoryBarrier barrier = new(
            PipelineSync.ComputeShading,
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.UnorderedAccess);
        DispatchArguments dispatch = new(1, 1, 1);
        CommandRecordingDesc recording = new(InitialCapturedResourceCapacity: 8);
        var samples = new FrameSample[configuration.MeasuredFrames];
        var calibrations = new CalibrationRecord[configuration.MeasuredFrames];
        using var allocations = new AllocationEventCounter();

        for (int frame = 0; frame < configuration.WarmupFrames; frame++)
        {
            _ = ExecuteBarrierFrame<TReceiver, TDispatch>(
                receiver,
                device,
                queue,
                context,
                queries,
                queryReadback,
                queryMapping,
                queryRange,
                computePipeline,
                barrier,
                dispatch,
                recording,
                configuration.BarrierCount,
                allocations,
                frame);
        }
        for (int frame = 0; frame < samples.Length; frame++)
        {
            FrameMeasurement measurement = ExecuteBarrierFrame<TReceiver, TDispatch>(
                receiver,
                device,
                queue,
                context,
                queries,
                queryReadback,
                queryMapping,
                queryRange,
                computePipeline,
                barrier,
                dispatch,
                recording,
                configuration.BarrierCount,
                allocations,
                frame);
            samples[frame] = measurement.Sample;
            calibrations[frame] = ToCalibration(QueueType.Compute, frame, measurement.Calibration);
        }
        var evidence = new BarrierEvidence[configuration.BarrierCount];
        for (int index = 0; index < evidence.Length; index++)
            evidence[index] = new BarrierEvidence(index, nameof(MemoryBarrier), index, 1, null);
        return BenchmarkOutput.Complete(
            GraphicsWorkload.ExplicitBarrier4096,
            configuration.Profile,
            configuration.WarmupFrames,
            configuration.MeasuredFrames,
            0,
            configuration.BarrierCount,
            samples,
            calibrations,
            BenchmarkOutput.FixedHash(GraphicsWorkload.ExplicitBarrier4096, shaderManifest),
            shaderManifest,
            evidence,
            new NativeSetterEvidence(1, 0, 0, 0, 0));
    }

    private static FrameMeasurement ExecuteBarrierFrame<TReceiver, TDispatch>(
        TReceiver receiver,
        Device device,
        Queue queue,
        CommandContext context,
        QueryPool queries,
        Buffer queryReadback,
        MappedBuffer queryMapping,
        in BufferRange queryRange,
        Pipeline pipeline,
        in MemoryBarrier barrier,
        in DispatchArguments dispatch,
        in CommandRecordingDesc recording,
        int barrierCount,
        AllocationEventCounter allocations,
        int frameIndex)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        CalibratedTimestampInfo calibration = TDispatch.Calibrate(receiver, queue);
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long beforeEvents = allocations.Count;
        long started = Stopwatch.GetTimestamp();
        TDispatch.Begin(receiver, context, recording);
        TDispatch.WriteTimestamp(receiver, context, queries, 0);
        TDispatch.RecordMemoryBarriers(receiver, context, barrier, barrierCount);
        TDispatch.SetPipeline(receiver, context, pipeline);
        TDispatch.Dispatch(receiver, context, dispatch);
        TDispatch.WriteTimestamp(receiver, context, queries, 1);
        TDispatch.ResolveQueries(receiver, context, queries, 0, 2, queryReadback, queryRange);
        RecordedCommands recorded = TDispatch.End(receiver, context);
        OneCommands[0] = recorded;
        QueueSubmitDesc submit = new([], [], OneCommands, [], []);
        QueueCompletion completion = TDispatch.Submit(receiver, queue, submit);
        long stopped = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        long events = AllocationEventCounter.AttributeIntervalEvents(
            bytes,
            allocations.Count - beforeEvents);
        recorded.Dispose();
        RequireCompleted(TDispatch.WaitCpu(receiver, completion, GpuTimeout));
        TDispatch.CollectCompleted(receiver, device);
        (ulong gpuStart, ulong gpuEnd) = ReadTimestampPair(queryMapping, queryRange);
        long cpuTicks = stopped - started;
        return new FrameMeasurement(
            new FrameSample(
                frameIndex,
                cpuTicks,
                BenchmarkClock.TicksToMicroseconds(cpuTicks),
                (gpuEnd - gpuStart) * (1_000_000.0 / calibration.QueueFrequency),
                bytes,
                events,
                completion.Value),
            calibration);
    }

    private static WorkloadRun RunThreeQueuePresent<TReceiver, TDispatch>(
        TReceiver receiver,
        D3D12Backend backend,
        Device device,
        Pipeline graphicsPipeline,
        Pipeline computePipeline,
        PersistentParameterBindings persistent,
        string shaderManifest,
        in WorkerConfiguration configuration)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue computeQueue = backend.GetQueue(device, QueueType.Compute);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);
        using BenchmarkWindow window = new();
        using Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        SwapchainConfig swapchainConfig = new(
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight,
            Format.R8G8B8A8UNorm,
            ColorSpace.Srgb,
            PresentType.Mailbox,
            AllowTearing: false,
            MaximumFrameLatency: 2);
        using Swapchain swapchain = backend.CreateSwapchain(
            device,
            new SwapchainDesc(surface, 2, TextureUsages.CopyDestination, swapchainConfig));

        TextureSubresourceRange textureRange = new(0, 1, 0, 1, TextureAspects.Color);
        using Texture target = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                FixedGraphicsProtocol.RenderWidth,
                FixedGraphicsProtocol.RenderHeight,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource,
                label: "three-queue offscreen"));
        using ColorAttachmentView targetView = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(target, textureRange, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer work = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination | BufferUsages.CopySource | BufferUsages.ShaderRead));
        using Buffer sink = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination));
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            for (int index = 0; index < mapped.Bytes.Length; index++)
                mapped.Bytes[index] = unchecked((byte)(0x5E + index * 29));
            mapped.Flush(new BufferRange(0, 256));
        }
        using QueryPool copyQuery = backend.CreateQueryPool(device, new QueryPoolDesc(QueryType.Timestamp, QueueType.Copy, 1));
        using QueryPool graphicsQuery = backend.CreateQueryPool(device, new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1));
        using Buffer copyTimestamp = backend.CreateBuffer(device, new BufferDesc(8, BufferUsages.QueryResolve), MemoryType.Readback);
        using Buffer graphicsTimestamp = backend.CreateBuffer(device, new BufferDesc(8, BufferUsages.QueryResolve), MemoryType.Readback);
        BufferRange timestampRange = new(0, 8);
        using MappedBuffer copyTimestampMapping = TDispatch.Map(
            receiver,
            copyTimestamp,
            MapType.Read,
            timestampRange);
        using MappedBuffer graphicsTimestampMapping = TDispatch.Map(
            receiver,
            graphicsTimestamp,
            MapType.Read,
            timestampRange);
        using CommandContext copyContext = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Copy, 0, 0, 2));
        using CommandContext computeContext = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 0, 2));
        using CommandContext graphicsContext = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 0, 2));
        ColorAttachmentDesc[] colors = [new(targetView, LoadType.Clear, StoreType.Store, new Vector4(0.0625f, 0.125f, 0.25f, 1))];
        Viewport[] viewports = [new(0, 0, FixedGraphicsProtocol.RenderWidth, FixedGraphicsProtocol.RenderHeight)];
        ScissorRect[] scissors = [new(0, 0, FixedGraphicsProtocol.RenderWidth, FixedGraphicsProtocol.RenderHeight)];
        RecordedCommands[] copyCommands = new RecordedCommands[1];
        RecordedCommands[] computeCommands = new RecordedCommands[1];
        RecordedCommands[] graphicsCommands = new RecordedCommands[1];
        QueueCompletion[] computeWaits = new QueueCompletion[1];
        QueueCompletion[] graphicsWaits = new QueueCompletion[1];
        SwapchainImage[] images = new SwapchainImage[1];
        bool initialized = false;
        var samples = new FrameSample[configuration.MeasuredFrames];
        var calibrations = new CalibrationRecord[configuration.MeasuredFrames * 2];
        using var allocations = new AllocationEventCounter();

        for (int frame = 0; frame < configuration.WarmupFrames; frame++)
        {
            _ = ExecuteThreeQueueFrame<TReceiver, TDispatch>(
                receiver, device, copyQueue, computeQueue, graphicsQueue, copyContext, computeContext,
                graphicsContext, copyQuery, graphicsQuery, copyTimestamp, graphicsTimestamp, swapchain,
                copyTimestampMapping, graphicsTimestampMapping, timestampRange,
                upload, work, sink, target, targetView, textureRange, graphicsPipeline, computePipeline,
                persistent, colors, viewports, scissors, copyCommands, computeCommands, graphicsCommands,
                computeWaits, graphicsWaits, images, ref initialized, allocations, frame);
        }
        for (int frame = 0; frame < samples.Length; frame++)
        {
            ThreeQueueMeasurement measurement = ExecuteThreeQueueFrame<TReceiver, TDispatch>(
                receiver, device, copyQueue, computeQueue, graphicsQueue, copyContext, computeContext,
                graphicsContext, copyQuery, graphicsQuery, copyTimestamp, graphicsTimestamp, swapchain,
                copyTimestampMapping, graphicsTimestampMapping, timestampRange,
                upload, work, sink, target, targetView, textureRange, graphicsPipeline, computePipeline,
                persistent, colors, viewports, scissors, copyCommands, computeCommands, graphicsCommands,
                computeWaits, graphicsWaits, images, ref initialized, allocations, frame);
            samples[frame] = measurement.Sample;
            calibrations[frame * 2] = ToCalibration(QueueType.Copy, frame, measurement.CopyCalibration);
            calibrations[frame * 2 + 1] = ToCalibration(QueueType.Graphics, frame, measurement.GraphicsCalibration);
        }
        string outputHash = ReadTextureHash<TReceiver, TDispatch>(
            receiver,
            backend,
            device,
            graphicsQueue,
            target,
            textureRange);
        BarrierEvidence[] evidence =
        [
            new(0, nameof(QueueAcquire), 0, 1, null),
            new(1, nameof(QueueRelease), 1, 1, null),
            new(2, nameof(QueueAcquire), 2, 1, null),
            new(3, nameof(QueueRelease), 3, 1, null),
            new(4, nameof(QueueAcquire), 4, 1, null),
            new(5, nameof(QueueRelease), 5, 1, null),
            new(6, nameof(TextureBarrier), 6, 1, null),
            new(7, nameof(TextureBarrier), 7, 1, null),
            new(8, nameof(TextureBarrier), 8, 1, null),
            new(9, nameof(TextureBarrier), 9, 1, null),
        ];
        return BenchmarkOutput.Complete(
            GraphicsWorkload.ThreeQueuePresent,
            configuration.Profile,
            configuration.WarmupFrames,
            configuration.MeasuredFrames,
            1,
            evidence.Length,
            samples,
            calibrations,
            outputHash,
            shaderManifest,
            evidence,
            new NativeSetterEvidence(1, 1, 1, 1, 1));
    }

    private static ThreeQueueMeasurement ExecuteThreeQueueFrame<TReceiver, TDispatch>(
        TReceiver receiver,
        Device device,
        Queue copyQueue,
        Queue computeQueue,
        Queue graphicsQueue,
        CommandContext copyContext,
        CommandContext computeContext,
        CommandContext graphicsContext,
        QueryPool copyQuery,
        QueryPool graphicsQuery,
        Buffer copyTimestamp,
        Buffer graphicsTimestamp,
        Swapchain swapchain,
        MappedBuffer copyTimestampMapping,
        MappedBuffer graphicsTimestampMapping,
        in BufferRange timestampRange,
        Buffer upload,
        Buffer work,
        Buffer sink,
        Texture target,
        ColorAttachmentView targetView,
        in TextureSubresourceRange textureRange,
        Pipeline graphicsPipeline,
        Pipeline computePipeline,
        PersistentParameterBindings persistent,
        ColorAttachmentDesc[] colors,
        Viewport[] viewports,
        ScissorRect[] scissors,
        RecordedCommands[] copyCommands,
        RecordedCommands[] computeCommands,
        RecordedCommands[] graphicsCommands,
        QueueCompletion[] computeWaits,
        QueueCompletion[] graphicsWaits,
        SwapchainImage[] images,
        ref bool initialized,
        AllocationEventCounter allocations,
        int frameIndex)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        RequireAcquired(TDispatch.Acquire(
            receiver,
            swapchain,
            new SwapchainAcquireOptions(GpuTimeout, PreserveContents: false),
            out SwapchainImage image));
        CalibratedTimestampInfo copyCalibration = TDispatch.Calibrate(receiver, copyQueue);
        CalibratedTimestampInfo graphicsCalibration = TDispatch.Calibrate(receiver, graphicsQueue);
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long beforeEvents = allocations.Count;
        long started = Stopwatch.GetTimestamp();

        TDispatch.Begin(receiver, copyContext, default);
        TDispatch.WriteTimestamp(receiver, copyContext, copyQuery, 0);
        if (initialized)
        {
            TDispatch.Barrier(receiver, copyContext, new QueueAcquire(
                work,
                null,
                QueueType.Graphics,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                null));
        }
        else
        {
            TDispatch.Barrier(receiver, copyContext, new BufferBarrier(
                work,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopyDestination));
        }
        TDispatch.CopyBuffer(receiver, copyContext, new BufferCopy(upload, 0, work, 0, 256));
        TDispatch.Barrier(receiver, copyContext, new QueueRelease(
            work,
            null,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            null,
            QueueType.Compute));
        TDispatch.ResolveQueries(receiver, copyContext, copyQuery, 0, 1, copyTimestamp, new BufferRange(0, 8));
        RecordedCommands copyRecorded = TDispatch.End(receiver, copyContext);
        copyCommands[0] = copyRecorded;
        QueueCompletion copyCompletion = TDispatch.Submit(
            receiver,
            copyQueue,
            new QueueSubmitDesc([], [], copyCommands, [], []));

        TDispatch.Begin(receiver, computeContext, default);
        TDispatch.Barrier(receiver, computeContext, new QueueAcquire(
            work,
            null,
            QueueType.Copy,
            PipelineSync.ComputeShading,
            ResourceAccess.ShaderResource,
            null));
        TDispatch.SetPipeline(receiver, computeContext, computePipeline);
        TDispatch.Dispatch(receiver, computeContext, new DispatchArguments(1, 1, 1));
        TDispatch.Barrier(receiver, computeContext, new QueueRelease(
            work,
            null,
            PipelineSync.ComputeShading,
            ResourceAccess.ShaderResource,
            null,
            QueueType.Graphics));
        RecordedCommands computeRecorded = TDispatch.End(receiver, computeContext);
        computeCommands[0] = computeRecorded;
        computeWaits[0] = copyCompletion;
        QueueCompletion computeCompletion = TDispatch.Submit(
            receiver,
            computeQueue,
            new QueueSubmitDesc(computeWaits, [], computeCommands, [], []));

        TDispatch.Begin(receiver, graphicsContext, default);
        TDispatch.Barrier(receiver, graphicsContext, new QueueAcquire(
            work,
            null,
            QueueType.Compute,
            PipelineSync.Copy,
            ResourceAccess.CopySource,
            null));
        if (!initialized)
        {
            TDispatch.Barrier(receiver, graphicsContext, new BufferBarrier(
                sink,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopyDestination));
        }
        TDispatch.CopyBuffer(receiver, graphicsContext, new BufferCopy(work, 0, sink, 0, 256));
        TDispatch.Barrier(receiver, graphicsContext, new QueueRelease(
            work,
            null,
            PipelineSync.Copy,
            ResourceAccess.CopySource,
            null,
            QueueType.Copy));
        TDispatch.Barrier(receiver, graphicsContext, new TextureBarrier(
            target,
            textureRange,
            initialized ? PipelineSync.Copy : PipelineSync.None,
            PipelineSync.RenderTarget,
            initialized ? ResourceAccess.CopySource : ResourceAccess.NoAccess,
            ResourceAccess.RenderTarget,
            initialized ? TextureLayout.CopySource : TextureLayout.Undefined,
            TextureLayout.RenderTarget));
        TDispatch.SetPipeline(receiver, graphicsContext, graphicsPipeline);
        TDispatch.SetPersistentBindings(receiver, graphicsContext, persistent);
        TDispatch.SetViewports(receiver, graphicsContext, viewports);
        TDispatch.SetScissors(receiver, graphicsContext, scissors);
        TDispatch.BeginRendering(receiver, graphicsContext, new RenderingDesc(
            colors,
            null,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight));
        TDispatch.Draw(receiver, graphicsContext, new DrawArguments(3, 1, 0, 0));
        TDispatch.EndRendering(receiver, graphicsContext);
        TDispatch.Barrier(receiver, graphicsContext, new TextureBarrier(
            target,
            textureRange,
            PipelineSync.RenderTarget,
            PipelineSync.Copy,
            ResourceAccess.RenderTarget,
            ResourceAccess.CopySource,
            TextureLayout.RenderTarget,
            TextureLayout.CopySource));
        TDispatch.Barrier(receiver, graphicsContext, new TextureBarrier(
            image.Texture,
            textureRange,
            image.InitialSync,
            PipelineSync.Copy,
            image.InitialAccess,
            ResourceAccess.CopyDestination,
            image.InitialLayout,
            TextureLayout.CopyDestination));
        TDispatch.CopyTexture(receiver, graphicsContext, new TextureCopy(
            target, 0, 0, TextureAspects.Color, 0, 0, 0,
            image.Texture, 0, 0, TextureAspects.Color, 0, 0, 0,
            FixedGraphicsProtocol.RenderWidth, FixedGraphicsProtocol.RenderHeight, 1));
        TDispatch.Barrier(receiver, graphicsContext, new TextureBarrier(
            image.Texture,
            textureRange,
            PipelineSync.Copy,
            PipelineSync.None,
            ResourceAccess.CopyDestination,
            ResourceAccess.NoAccess,
            TextureLayout.CopyDestination,
            TextureLayout.Present));
        TDispatch.WriteTimestamp(receiver, graphicsContext, graphicsQuery, 0);
        TDispatch.ResolveQueries(receiver, graphicsContext, graphicsQuery, 0, 1, graphicsTimestamp, new BufferRange(0, 8));
        RecordedCommands graphicsRecorded = TDispatch.End(receiver, graphicsContext);
        graphicsCommands[0] = graphicsRecorded;
        graphicsWaits[0] = computeCompletion;
        images[0] = image;
        QueueCompletion graphicsCompletion = TDispatch.Submit(
            receiver,
            graphicsQueue,
            new QueueSubmitDesc(graphicsWaits, [], graphicsCommands, images, []));
        PresentStatus present = TDispatch.Present(receiver, graphicsQueue, image);
        if (present is not (PresentStatus.Success or PresentStatus.Suboptimal or PresentStatus.Occluded))
            throw new InvalidOperationException($"Present returned {present}.");
        long stopped = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        long events = AllocationEventCounter.AttributeIntervalEvents(
            bytes,
            allocations.Count - beforeEvents);

        copyRecorded.Dispose();
        computeRecorded.Dispose();
        graphicsRecorded.Dispose();
        RequireCompleted(TDispatch.WaitCpu(receiver, graphicsCompletion, GpuTimeout));
        TDispatch.CollectCompleted(receiver, device);
        ulong copyTick = ReadTimestamp(copyTimestampMapping, timestampRange);
        ulong graphicsTick = ReadTimestamp(graphicsTimestampMapping, timestampRange);
        double startCpu = MapQueueTickToCpuMicroseconds(copyTick, copyCalibration);
        double endCpu = MapQueueTickToCpuMicroseconds(graphicsTick, graphicsCalibration);
        initialized = true;
        long cpuTicks = stopped - started;
        FrameSample sample = new(
            frameIndex,
            cpuTicks,
            BenchmarkClock.TicksToMicroseconds(cpuTicks),
            Math.Max(0, endCpu - startCpu),
            bytes,
            events,
            graphicsCompletion.Value);
        return new ThreeQueueMeasurement(sample, copyCalibration, graphicsCalibration);
    }

    private static string ReadTextureHash<TReceiver, TDispatch>(
        TReceiver receiver,
        D3D12Backend backend,
        Device device,
        Queue queue,
        Texture texture,
        in TextureSubresourceRange range)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        const ulong size = FixedGraphicsProtocol.RenderWidth * FixedGraphicsProtocol.RenderHeight * 4UL;
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(size, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));
        TDispatch.Begin(receiver, context, default);
        TDispatch.CopyTextureToBuffer(receiver, context, new BufferTextureCopy(
            readback,
            0,
            256,
            FixedGraphicsProtocol.RenderHeight,
            texture,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight,
            1));
        RecordedCommands recorded = TDispatch.End(receiver, context);
        OneCommands[0] = recorded;
        QueueCompletion completion = TDispatch.Submit(
            receiver,
            queue,
            new QueueSubmitDesc([], [], OneCommands, [], []));
        recorded.Dispose();
        RequireCompleted(TDispatch.WaitCpu(receiver, completion, GpuTimeout));
        TDispatch.CollectCompleted(receiver, device);
        using MappedBuffer mapped = TDispatch.Map(receiver, readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        return Convert.ToHexString(SHA256.HashData(mapped.Bytes));
    }

    private static (ulong Start, ulong End) ReadTimestampPair(
        MappedBuffer mapped,
        in BufferRange range)
    {
        mapped.Invalidate(range);
        return (
            BinaryPrimitives.ReadUInt64LittleEndian(mapped.Bytes[..8]),
            BinaryPrimitives.ReadUInt64LittleEndian(mapped.Bytes[8..16]));
    }

    private static ulong ReadTimestamp(MappedBuffer mapped, in BufferRange range)
    {
        mapped.Invalidate(range);
        return BinaryPrimitives.ReadUInt64LittleEndian(mapped.Bytes);
    }

    private static double MapQueueTickToCpuMicroseconds(
        ulong queueTick,
        in CalibratedTimestampInfo calibration) =>
        calibration.CpuCounter * (1_000_000.0 / calibration.CpuFrequency) +
        ((double)queueTick - calibration.QueueCounter) * (1_000_000.0 / calibration.QueueFrequency);

    private static CalibrationRecord ToCalibration(
        QueueType queue,
        int frame,
        in CalibratedTimestampInfo value) => new(
        queue,
        frame,
        value.CpuCounter,
        value.CpuFrequency,
        value.QueueCounter,
        value.QueueFrequency);

    private static AdapterInfo FindAdapter(D3D12Backend backend, AdapterId id)
    {
        AdapterEnumerationOptions options = new(AdapterPreference.HighPerformance, IncludeSoftware: true);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        AdapterInfo[] adapters = new AdapterInfo[count];
        if (!backend.TryEnumerateAdapters(options, adapters, out int confirmed) || confirmed != count)
            throw new InvalidOperationException("The adapter inventory changed during enumeration.");
        foreach (AdapterInfo adapter in adapters)
        {
            if (adapter.Id == id)
                return adapter;
        }
        throw new NotSupportedException("The selected benchmark adapter is unavailable.");
    }

    private static void WritePacket(Span<byte> destination, int drawIndex)
    {
        float r = ((drawIndex * 17) & 255) / 255f;
        float g = ((drawIndex * 29 + 31) & 255) / 255f;
        float b = ((drawIndex * 43 + 7) & 255) / 255f;
        WriteTint(destination, r, g, b, 1);
    }

    private static void WriteTint(Span<byte> destination, float r, float g, float b, float a)
    {
        if (destination.Length != 16)
            throw new ArgumentException("Tint data must be 16 bytes.", nameof(destination));
        BinaryPrimitives.WriteInt32LittleEndian(destination[0..4], BitConverter.SingleToInt32Bits(r));
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..8], BitConverter.SingleToInt32Bits(g));
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..12], BitConverter.SingleToInt32Bits(b));
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..16], BitConverter.SingleToInt32Bits(a));
    }

    private static void RequireCompleted(WaitStatus status)
    {
        if (status != WaitStatus.Completed)
            throw new TimeoutException($"The benchmark submission ended with {status}.");
    }

    private static void RequireAcquired(SwapchainAcquireStatus status)
    {
        if (status != SwapchainAcquireStatus.Success)
            throw new TimeoutException($"Swapchain Acquire ended with {status}.");
    }

    private readonly record struct FrameMeasurement(
        FrameSample Sample,
        CalibratedTimestampInfo Calibration,
        D3D12CommandStatistics CommandStatistics = default);

    private readonly record struct ThreeQueueMeasurement(
        FrameSample Sample,
        CalibratedTimestampInfo CopyCalibration,
        CalibratedTimestampInfo GraphicsCalibration);
}
