using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using NativeBarrier = Silk.NET.Direct3D12.ResourceBarrier;
using NativeBuffer = Silk.NET.Direct3D12.ID3D12Resource;
using NativeFormat = Silk.NET.DXGI.Format;
using NativeQueryType = Silk.NET.Direct3D12.QueryType;
using NativeResourceStates = Silk.NET.Direct3D12.ResourceStates;
using NativeViewport = Silk.NET.Direct3D12.Viewport;

namespace SomeEngine.Graphics.Benchmarks;

internal static unsafe class DirectSilkBenchmarkRunner
{
    private const ulong TextureByteSize =
        FixedGraphicsProtocol.RenderWidth * FixedGraphicsProtocol.RenderHeight * 4UL;
    private const ulong ConstantBufferAlignment = 256;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static bool NormalizedFloatEquals(float left, float right) => left.Equals(right);

    internal static ProcessRun Run(in WorkerConfiguration configuration)
    {
        try
        {
            (byte[] vertex, byte[] pixel, byte[] compute, string manifest) =
                BenchmarkShaders.LoadNativeArtifacts(configuration.ShaderDirectory);
            using var context = new DirectSilkContext(
                configuration.AdapterId,
                vertex,
                pixel,
                compute);
            RuntimeEnvironment environment = BenchmarkEnvironment.Capture(
                configuration,
                context.Adapter,
                validationEnabled: false,
                dredEnabled: false,
                ".NET 10 / Silk.NET 2.23.0 direct D3D12");
            if (configuration.Profile == BenchmarkProfile.VendorCertification &&
                (environment.ValidationEnabled || environment.DredEnabled || environment.CaptureToolLoaded))
            {
                return new ProcessRun(
                    ReceiverVariant.DirectSilk,
                    RunDisposition.Unexecuted,
                    "Validation, DRED, or a capture tool is enabled.",
                    environment,
                    []);
            }

            WorkloadRun[] workloads =
            [
                RunEmptySubmit(context, manifest, configuration),
                RunDraw(context, manifest, configuration, GraphicsWorkload.PersistentDraw10000),
                RunDraw(context, manifest, configuration, GraphicsWorkload.TransientDraw10000),
                RunDraw(context, manifest, configuration, GraphicsWorkload.StateSuppression10000),
                RunExplicitBarriers(context, manifest, configuration),
                RunThreeQueuePresent(context, manifest, configuration),
            ];
            RunDisposition disposition = configuration.Profile == BenchmarkProfile.VendorCertification
                ? RunDisposition.Passed
                : RunDisposition.FunctionalOnly;
            return new ProcessRun(
                ReceiverVariant.DirectSilk,
                disposition,
                configuration.Profile == BenchmarkProfile.VendorCertification
                    ? "All fixed direct Silk D3D12 workloads executed."
                    : "All reduced-count direct Silk D3D12 workloads executed on WARP; not performance evidence.",
                environment,
                workloads);
        }
        catch (Exception exception)
        {
            return new ProcessRun(
                ReceiverVariant.DirectSilk,
                RunDisposition.Failed,
                exception.ToString(),
                BenchmarkEnvironment.Unavailable(configuration.ProcessIndex, ".NET/Silk direct D3D12"),
                []);
        }
    }

    private static WorkloadRun RunEmptySubmit(
        DirectSilkContext context,
        string shaderManifest,
        in WorkerConfiguration configuration)
    {
        var samples = new FrameSample[configuration.MeasuredFrames];
        using var allocations = new AllocationEventCounter();
        for (int frame = 0; frame < configuration.WarmupFrames; frame++)
        {
            ulong completion = context.Copy.SignalOnly();
            context.Copy.WaitCpu(completion);
        }
        for (int frame = 0; frame < samples.Length; frame++)
        {
            long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
            long beforeEvents = allocations.Count;
            long started = Stopwatch.GetTimestamp();
            ulong completion = context.Copy.SignalOnly();
            long stopped = Stopwatch.GetTimestamp();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
            long events = AllocationEventCounter.AttributeIntervalEvents(
                bytes,
                allocations.Count - beforeEvents);
            context.Copy.WaitCpu(completion);
            long ticks = stopped - started;
            samples[frame] = new FrameSample(
                frame,
                ticks,
                BenchmarkClock.TicksToMicroseconds(ticks),
                null,
                bytes,
                events,
                completion);
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

    private static WorkloadRun RunDraw(
        DirectSilkContext context,
        string shaderManifest,
        in WorkerConfiguration configuration,
        GraphicsWorkload workload)
    {
        NativeBuffer* target = null;
        NativeBuffer* persistent = null;
        NativeBuffer* transient = null;
        NativeBuffer* queryReadback = null;
        ID3D12DescriptorHeap* rtvHeap = null;
        ID3D12QueryHeap* queryHeap = null;
        byte* persistentData = null;
        byte* transientData = null;
        try
        {
            target = context.CreateTargetTexture();
            rtvHeap = context.CreateRtvHeap(1);
            CpuDescriptorHandle rtv = context.CreateRtv(target, rtvHeap);
            persistent = context.CreateBuffer(
                ConstantBufferAlignment,
                HeapType.Upload,
                NativeResourceStates.GenericRead);
            persistentData = DirectSilkContext.MapWrite(persistent);
            WriteTint(new Span<byte>(persistentData, 16), 1, 1, 1, 1);
            if (workload == GraphicsWorkload.TransientDraw10000)
            {
                transient = context.CreateBuffer(
                    checked((ulong)configuration.DrawCount * ConstantBufferAlignment),
                    HeapType.Upload,
                    NativeResourceStates.GenericRead);
                transientData = DirectSilkContext.MapWrite(transient);
            }
            queryHeap = context.CreateTimestampHeap(CommandListType.Direct, 2);
            queryReadback = context.CreateBuffer(
                16,
                HeapType.Readback,
                NativeResourceStates.CopyDest);

            bool initialized = false;
            var samples = new FrameSample[configuration.MeasuredFrames];
            var calibrations = new CalibrationRecord[configuration.MeasuredFrames];
            using var allocations = new AllocationEventCounter();
            for (int frame = 0; frame < configuration.WarmupFrames; frame++)
            {
                _ = ExecuteDrawFrame(
                    context,
                    target,
                    rtv,
                    persistent,
                    transient,
                    transientData,
                    queryHeap,
                    queryReadback,
                    workload,
                    configuration.DrawCount,
                    ref initialized,
                    allocations,
                    frame);
            }
            for (int frame = 0; frame < samples.Length; frame++)
            {
                FrameMeasurement measurement = ExecuteDrawFrame(
                    context,
                    target,
                    rtv,
                    persistent,
                    transient,
                    transientData,
                    queryHeap,
                    queryReadback,
                    workload,
                    configuration.DrawCount,
                    ref initialized,
                    allocations,
                    frame);
                samples[frame] = measurement.Sample;
                calibrations[frame] = ToCalibration(QueueType.Graphics, frame, measurement.Calibration);
            }

            string outputHash = ReadTextureHash(context, target);
            BarrierEvidence[] barriers =
            [
                new(0, "TextureBarrier", 0, 1, null),
                new(1, "TextureBarrier", 1, 1, null),
            ];
            NativeSetterEvidence setters = new(
                PipelineSetters: 1,
                PersistentBindingSetters: workload == GraphicsWorkload.TransientDraw10000 ? 0 : 1,
                ViewportSetters: 1,
                ScissorSetters: 1,
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
        finally
        {
            if (persistentData is not null)
                persistent->Unmap(0, null);
            if (transientData is not null)
                transient->Unmap(0, null);
            DirectSilkContext.Release(queryReadback);
            DirectSilkContext.Release(queryHeap);
            DirectSilkContext.Release(transient);
            DirectSilkContext.Release(persistent);
            DirectSilkContext.Release(rtvHeap);
            DirectSilkContext.Release(target);
        }
    }

    private static FrameMeasurement ExecuteDrawFrame(
        DirectSilkContext context,
        NativeBuffer* target,
        CpuDescriptorHandle rtv,
        NativeBuffer* persistent,
        NativeBuffer* transient,
        byte* transientData,
        ID3D12QueryHeap* queryHeap,
        NativeBuffer* queryReadback,
        GraphicsWorkload workload,
        int drawCount,
        ref bool initialized,
        AllocationEventCounter allocations,
        int frameIndex)
    {
        CalibratedTimestampInfo calibration = context.Graphics.Calibrate();
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long beforeEvents = allocations.Count;
        long started = Stopwatch.GetTimestamp();
        if (workload == GraphicsWorkload.TransientDraw10000)
        {
            for (int drawIndex = 0; drawIndex < drawCount; drawIndex++)
                WritePacket(
                    new Span<byte>(transientData + checked(drawIndex * (int)ConstantBufferAlignment), 16),
                    drawIndex);
        }

        ID3D12GraphicsCommandList* list = context.Graphics.Begin();
        list->EndQuery(queryHeap, NativeQueryType.Timestamp, 0);
        NativeBarrier first = DirectSilkContext.Transition(
            target,
            initialized ? NativeResourceStates.CopySource : NativeResourceStates.Common,
            NativeResourceStates.RenderTarget);
        list->ResourceBarrier(1, &first);
        NativeViewport viewport = new(
            0,
            0,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight,
            0,
            1);
        Box2D<int> scissor = new(
            0,
            0,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight);
        float* clear = stackalloc float[4];
        clear[0] = 0.0625f;
        clear[1] = 0.125f;
        clear[2] = 0.25f;
        clear[3] = 1;
        bool suppressState = workload == GraphicsWorkload.StateSuppression10000;
        DirectStateShadow stateShadow = new(list);
        if (suppressState)
        {
            stateShadow.SetPipeline(context.GraphicsPipeline, context.GraphicsRoot);
            stateShadow.SetViewport(viewport);
            stateShadow.SetScissor(scissor);
        }
        else
        {
            list->SetPipelineState(context.GraphicsPipeline);
            list->SetGraphicsRootSignature(context.GraphicsRoot);
            list->RSSetViewports(1, &viewport);
            list->RSSetScissorRects(1, &scissor);
        }
        list->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        list->OMSetRenderTargets(1, &rtv, false, null);
        list->ClearRenderTargetView(rtv, clear, 0, null);
        ulong persistentAddress = 0;
        if (workload != GraphicsWorkload.TransientDraw10000)
        {
            persistentAddress = persistent->GetGPUVirtualAddress();
            if (suppressState)
                stateShadow.SetPersistentBinding(persistentAddress);
            else
                list->SetGraphicsRootConstantBufferView(0, persistentAddress);
        }
        for (int drawIndex = 0; drawIndex < drawCount; drawIndex++)
        {
            if (workload == GraphicsWorkload.TransientDraw10000)
            {
                list->SetGraphicsRootConstantBufferView(
                    0,
                    transient->GetGPUVirtualAddress() + checked((ulong)drawIndex * ConstantBufferAlignment));
            }
            else if (suppressState)
            {
                stateShadow.SetPipeline(context.GraphicsPipeline, context.GraphicsRoot);
                stateShadow.SetPersistentBinding(persistentAddress);
                stateShadow.SetViewport(viewport);
                stateShadow.SetScissor(scissor);
            }
            list->DrawInstanced(3, 1, 0, 0);
        }
        NativeBarrier second = DirectSilkContext.Transition(
            target,
            NativeResourceStates.RenderTarget,
            NativeResourceStates.CopySource);
        list->ResourceBarrier(1, &second);
        list->EndQuery(queryHeap, NativeQueryType.Timestamp, 1);
        list->ResolveQueryData(queryHeap, NativeQueryType.Timestamp, 0, 2, queryReadback, 0);
        ulong completion = context.Graphics.Execute();
        long stopped = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        long events = AllocationEventCounter.AttributeIntervalEvents(
            bytes,
            allocations.Count - beforeEvents);
        context.Graphics.WaitCpu(completion);
        (ulong gpuStart, ulong gpuEnd) = ReadTimestampPair(queryReadback);
        initialized = true;
        long ticks = stopped - started;
        return new FrameMeasurement(
            new FrameSample(
                frameIndex,
                ticks,
                BenchmarkClock.TicksToMicroseconds(ticks),
                (gpuEnd - gpuStart) * (1_000_000.0 / calibration.QueueFrequency),
                bytes,
                events,
                completion),
            calibration);
    }

    private static WorkloadRun RunExplicitBarriers(
        DirectSilkContext context,
        string shaderManifest,
        in WorkerConfiguration configuration)
    {
        ID3D12QueryHeap* queryHeap = null;
        NativeBuffer* queryReadback = null;
        try
        {
            queryHeap = context.CreateTimestampHeap(CommandListType.Compute, 2);
            queryReadback = context.CreateBuffer(16, HeapType.Readback, NativeResourceStates.CopyDest);
            var samples = new FrameSample[configuration.MeasuredFrames];
            var calibrations = new CalibrationRecord[configuration.MeasuredFrames];
            using var allocations = new AllocationEventCounter();
            for (int frame = 0; frame < configuration.WarmupFrames; frame++)
                _ = ExecuteBarrierFrame(context, queryHeap, queryReadback, configuration.BarrierCount, allocations, frame);
            for (int frame = 0; frame < samples.Length; frame++)
            {
                FrameMeasurement measurement = ExecuteBarrierFrame(
                    context,
                    queryHeap,
                    queryReadback,
                    configuration.BarrierCount,
                    allocations,
                    frame);
                samples[frame] = measurement.Sample;
                calibrations[frame] = ToCalibration(QueueType.Compute, frame, measurement.Calibration);
            }
            var evidence = new BarrierEvidence[configuration.BarrierCount];
            for (int index = 0; index < evidence.Length; index++)
                evidence[index] = new BarrierEvidence(index, "MemoryBarrier", index, 1, null);
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
        finally
        {
            DirectSilkContext.Release(queryReadback);
            DirectSilkContext.Release(queryHeap);
        }
    }

    private static FrameMeasurement ExecuteBarrierFrame(
        DirectSilkContext context,
        ID3D12QueryHeap* queryHeap,
        NativeBuffer* queryReadback,
        int barrierCount,
        AllocationEventCounter allocations,
        int frameIndex)
    {
        CalibratedTimestampInfo calibration = context.Compute.Calibrate();
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long beforeEvents = allocations.Count;
        long started = Stopwatch.GetTimestamp();
        ID3D12GraphicsCommandList* list = context.Compute.Begin();
        list->EndQuery(queryHeap, NativeQueryType.Timestamp, 0);
        if (context.EnhancedBarriers)
        {
            GlobalBarrier barrier = new()
            {
                SyncBefore = BarrierSync.ComputeShading,
                SyncAfter = BarrierSync.ComputeShading,
                AccessBefore = BarrierAccess.UnorderedAccess,
                AccessAfter = BarrierAccess.UnorderedAccess,
            };
            BarrierGroup group = new()
            {
                Type = BarrierType.Global,
                NumBarriers = 1,
                Anonymous = new BarrierGroupUnion { PGlobalBarriers = &barrier },
            };
            for (int index = 0; index < barrierCount; index++)
                context.Compute.EnhancedList->Barrier(1, &group);
        }
        else
        {
            NativeBarrier barrier = DirectSilkContext.UavBarrier();
            for (int index = 0; index < barrierCount; index++)
                list->ResourceBarrier(1, &barrier);
        }
        list->SetPipelineState(context.ComputePipeline);
        list->SetComputeRootSignature(context.ComputeRoot);
        list->Dispatch(1, 1, 1);
        list->EndQuery(queryHeap, NativeQueryType.Timestamp, 1);
        list->ResolveQueryData(queryHeap, NativeQueryType.Timestamp, 0, 2, queryReadback, 0);
        ulong completion = context.Compute.Execute();
        long stopped = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        long events = AllocationEventCounter.AttributeIntervalEvents(
            bytes,
            allocations.Count - beforeEvents);
        context.Compute.WaitCpu(completion);
        (ulong gpuStart, ulong gpuEnd) = ReadTimestampPair(queryReadback);
        long ticks = stopped - started;
        return new FrameMeasurement(
            new FrameSample(
                frameIndex,
                ticks,
                BenchmarkClock.TicksToMicroseconds(ticks),
                (gpuEnd - gpuStart) * (1_000_000.0 / calibration.QueueFrequency),
                bytes,
                events,
                completion),
            calibration);
    }

    private static WorkloadRun RunThreeQueuePresent(
        DirectSilkContext context,
        string shaderManifest,
        in WorkerConfiguration configuration)
    {
        NativeBuffer* target = null;
        NativeBuffer* persistent = null;
        NativeBuffer* upload = null;
        NativeBuffer* work = null;
        NativeBuffer* sink = null;
        NativeBuffer* copyTimestamp = null;
        NativeBuffer* graphicsTimestamp = null;
        ID3D12DescriptorHeap* rtvHeap = null;
        ID3D12QueryHeap* copyQuery = null;
        ID3D12QueryHeap* graphicsQuery = null;
        byte* persistentData = null;
        byte* uploadData = null;
        try
        {
            using var window = new BenchmarkWindow();
            using var swapchain = new NativeSwapchain(context, window.Handle);
            target = context.CreateTargetTexture();
            rtvHeap = context.CreateRtvHeap(1);
            CpuDescriptorHandle rtv = context.CreateRtv(target, rtvHeap);
            persistent = context.CreateBuffer(ConstantBufferAlignment, HeapType.Upload, NativeResourceStates.GenericRead);
            persistentData = DirectSilkContext.MapWrite(persistent);
            WriteTint(new Span<byte>(persistentData, 16), 1, 1, 1, 1);
            upload = context.CreateBuffer(256, HeapType.Upload, NativeResourceStates.GenericRead);
            uploadData = DirectSilkContext.MapWrite(upload);
            for (int index = 0; index < 256; index++)
                uploadData[index] = unchecked((byte)(0x5E + index * 29));
            work = context.CreateBuffer(256, HeapType.Default, NativeResourceStates.Common);
            sink = context.CreateBuffer(256, HeapType.Default, NativeResourceStates.CopyDest);
            copyQuery = context.CreateTimestampHeap(CommandListType.Copy, 1);
            graphicsQuery = context.CreateTimestampHeap(CommandListType.Direct, 1);
            copyTimestamp = context.CreateBuffer(8, HeapType.Readback, NativeResourceStates.CopyDest);
            graphicsTimestamp = context.CreateBuffer(8, HeapType.Readback, NativeResourceStates.CopyDest);

            bool initialized = false;
            var samples = new FrameSample[configuration.MeasuredFrames];
            var calibrations = new CalibrationRecord[configuration.MeasuredFrames * 2];
            using var allocations = new AllocationEventCounter();
            for (int frame = 0; frame < configuration.WarmupFrames; frame++)
            {
                _ = ExecuteThreeQueueFrame(
                    context,
                    swapchain,
                    target,
                    rtv,
                    persistent,
                    upload,
                    work,
                    sink,
                    copyQuery,
                    graphicsQuery,
                    copyTimestamp,
                    graphicsTimestamp,
                    ref initialized,
                    allocations,
                    frame);
            }
            for (int frame = 0; frame < samples.Length; frame++)
            {
                ThreeQueueMeasurement measurement = ExecuteThreeQueueFrame(
                    context,
                    swapchain,
                    target,
                    rtv,
                    persistent,
                    upload,
                    work,
                    sink,
                    copyQuery,
                    graphicsQuery,
                    copyTimestamp,
                    graphicsTimestamp,
                    ref initialized,
                    allocations,
                    frame);
                samples[frame] = measurement.Sample;
                calibrations[frame * 2] = ToCalibration(QueueType.Copy, frame, measurement.CopyCalibration);
                calibrations[frame * 2 + 1] = ToCalibration(QueueType.Graphics, frame, measurement.GraphicsCalibration);
            }

            string outputHash = ReadTextureHash(context, target);
            BarrierEvidence[] evidence =
            [
                new(0, "QueueAcquire", 0, 1, null),
                new(1, "QueueRelease", 1, 1, null),
                new(2, "QueueAcquire", 2, 1, null),
                new(3, "QueueRelease", 3, 1, null),
                new(4, "QueueAcquire", 4, 1, null),
                new(5, "QueueRelease", 5, 1, null),
                new(6, "TextureBarrier", 6, 1, null),
                new(7, "TextureBarrier", 7, 1, null),
                new(8, "TextureBarrier", 8, 1, null),
                new(9, "TextureBarrier", 9, 1, null),
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
        finally
        {
            if (persistentData is not null)
                persistent->Unmap(0, null);
            if (uploadData is not null)
                upload->Unmap(0, null);
            DirectSilkContext.Release(graphicsTimestamp);
            DirectSilkContext.Release(copyTimestamp);
            DirectSilkContext.Release(graphicsQuery);
            DirectSilkContext.Release(copyQuery);
            DirectSilkContext.Release(sink);
            DirectSilkContext.Release(work);
            DirectSilkContext.Release(upload);
            DirectSilkContext.Release(persistent);
            DirectSilkContext.Release(rtvHeap);
            DirectSilkContext.Release(target);
        }
    }

    private static ThreeQueueMeasurement ExecuteThreeQueueFrame(
        DirectSilkContext context,
        NativeSwapchain swapchain,
        NativeBuffer* target,
        CpuDescriptorHandle rtv,
        NativeBuffer* persistent,
        NativeBuffer* upload,
        NativeBuffer* work,
        NativeBuffer* sink,
        ID3D12QueryHeap* copyQuery,
        ID3D12QueryHeap* graphicsQuery,
        NativeBuffer* copyTimestamp,
        NativeBuffer* graphicsTimestamp,
        ref bool initialized,
        AllocationEventCounter allocations,
        int frameIndex)
    {
        NativeBuffer* backBuffer = swapchain.CurrentBuffer;
        CalibratedTimestampInfo copyCalibration = context.Copy.Calibrate();
        CalibratedTimestampInfo graphicsCalibration = context.Graphics.Calibrate();
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        long beforeEvents = allocations.Count;
        long started = Stopwatch.GetTimestamp();

        ID3D12GraphicsCommandList* copyList = context.Copy.Begin();
        copyList->EndQuery(copyQuery, NativeQueryType.Timestamp, 0);
        NativeBarrier copyAcquire = DirectSilkContext.Transition(
            work,
            NativeResourceStates.Common,
            NativeResourceStates.CopyDest);
        copyList->ResourceBarrier(1, &copyAcquire);
        copyList->CopyBufferRegion(work, 0, upload, 0, 256);
        NativeBarrier copyRelease = DirectSilkContext.Transition(
            work,
            NativeResourceStates.CopyDest,
            NativeResourceStates.Common);
        copyList->ResourceBarrier(1, &copyRelease);
        copyList->ResolveQueryData(copyQuery, NativeQueryType.Timestamp, 0, 1, copyTimestamp, 0);
        ulong copyCompletion = context.Copy.Execute();

        context.Compute.WaitGpu(context.Copy, copyCompletion);
        ID3D12GraphicsCommandList* computeList = context.Compute.Begin();
        NativeBarrier computeAcquire = DirectSilkContext.Transition(
            work,
            NativeResourceStates.Common,
            NativeResourceStates.NonPixelShaderResource);
        computeList->ResourceBarrier(1, &computeAcquire);
        computeList->SetPipelineState(context.ComputePipeline);
        computeList->SetComputeRootSignature(context.ComputeRoot);
        computeList->Dispatch(1, 1, 1);
        NativeBarrier computeRelease = DirectSilkContext.Transition(
            work,
            NativeResourceStates.NonPixelShaderResource,
            NativeResourceStates.Common);
        computeList->ResourceBarrier(1, &computeRelease);
        ulong computeCompletion = context.Compute.Execute();

        context.Graphics.WaitGpu(context.Compute, computeCompletion);
        ID3D12GraphicsCommandList* graphicsList = context.Graphics.Begin();
        NativeBarrier graphicsAcquire = DirectSilkContext.Transition(
            work,
            NativeResourceStates.Common,
            NativeResourceStates.CopySource);
        graphicsList->ResourceBarrier(1, &graphicsAcquire);
        graphicsList->CopyBufferRegion(sink, 0, work, 0, 256);
        NativeBarrier workReturn = DirectSilkContext.Transition(
            work,
            NativeResourceStates.CopySource,
            NativeResourceStates.Common);
        graphicsList->ResourceBarrier(1, &workReturn);
        NativeBarrier targetToRender = DirectSilkContext.Transition(
            target,
            initialized ? NativeResourceStates.CopySource : NativeResourceStates.Common,
            NativeResourceStates.RenderTarget);
        graphicsList->ResourceBarrier(1, &targetToRender);
        NativeViewport viewport = new(
            0,
            0,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight,
            0,
            1);
        Box2D<int> scissor = new(
            0,
            0,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight);
        float* clear = stackalloc float[4];
        clear[0] = 0.0625f;
        clear[1] = 0.125f;
        clear[2] = 0.25f;
        clear[3] = 1;
        graphicsList->SetPipelineState(context.GraphicsPipeline);
        graphicsList->SetGraphicsRootSignature(context.GraphicsRoot);
        graphicsList->SetGraphicsRootConstantBufferView(0, persistent->GetGPUVirtualAddress());
        graphicsList->RSSetViewports(1, &viewport);
        graphicsList->RSSetScissorRects(1, &scissor);
        graphicsList->IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        graphicsList->OMSetRenderTargets(1, &rtv, false, null);
        graphicsList->ClearRenderTargetView(rtv, clear, 0, null);
        graphicsList->DrawInstanced(3, 1, 0, 0);
        NativeBarrier targetToCopy = DirectSilkContext.Transition(
            target,
            NativeResourceStates.RenderTarget,
            NativeResourceStates.CopySource);
        graphicsList->ResourceBarrier(1, &targetToCopy);
        NativeBarrier swapchainToCopy = DirectSilkContext.Transition(
            backBuffer,
            NativeResourceStates.Present,
            NativeResourceStates.CopyDest);
        graphicsList->ResourceBarrier(1, &swapchainToCopy);
        graphicsList->CopyResource(backBuffer, target);
        NativeBarrier swapchainToPresent = DirectSilkContext.Transition(
            backBuffer,
            NativeResourceStates.CopyDest,
            NativeResourceStates.Present);
        graphicsList->ResourceBarrier(1, &swapchainToPresent);
        graphicsList->EndQuery(graphicsQuery, NativeQueryType.Timestamp, 0);
        graphicsList->ResolveQueryData(graphicsQuery, NativeQueryType.Timestamp, 0, 1, graphicsTimestamp, 0);
        ulong graphicsCompletion = context.Graphics.Execute();
        swapchain.Present();
        long stopped = Stopwatch.GetTimestamp();
        long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        long events = AllocationEventCounter.AttributeIntervalEvents(
            bytes,
            allocations.Count - beforeEvents);
        context.Graphics.WaitCpu(graphicsCompletion);
        ulong copyTick = ReadTimestamp(copyTimestamp);
        ulong graphicsTick = ReadTimestamp(graphicsTimestamp);
        double startCpu = MapQueueTickToCpuMicroseconds(copyTick, copyCalibration);
        double endCpu = MapQueueTickToCpuMicroseconds(graphicsTick, graphicsCalibration);
        initialized = true;
        long ticks = stopped - started;
        return new ThreeQueueMeasurement(
            new FrameSample(
                frameIndex,
                ticks,
                BenchmarkClock.TicksToMicroseconds(ticks),
                Math.Max(0, endCpu - startCpu),
                bytes,
                events,
                graphicsCompletion),
            copyCalibration,
            graphicsCalibration);
    }

    private static string ReadTextureHash(DirectSilkContext context, NativeBuffer* texture)
    {
        NativeBuffer* readback = null;
        try
        {
            readback = context.CreateBuffer(TextureByteSize, HeapType.Readback, NativeResourceStates.CopyDest);
            ID3D12GraphicsCommandList* list = context.Graphics.Begin();
            TextureCopyLocation destination = new(
                readback,
                TextureCopyType.PlacedFootprint,
                placedFootprint: new PlacedSubresourceFootprint(
                    0,
                    new SubresourceFootprint(
                        NativeFormat.FormatR8G8B8A8Unorm,
                        FixedGraphicsProtocol.RenderWidth,
                        FixedGraphicsProtocol.RenderHeight,
                        1,
                        256)));
            TextureCopyLocation source = new(
                texture,
                TextureCopyType.SubresourceIndex,
                subresourceIndex: 0);
            list->CopyTextureRegion(&destination, 0, 0, 0, &source, null);
            ulong completion = context.Graphics.Execute();
            context.Graphics.WaitCpu(completion);
            byte* data = DirectSilkContext.MapRead(readback, (nuint)TextureByteSize);
            try
            {
                return Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(data, checked((int)TextureByteSize))));
            }
            finally
            {
                readback->Unmap(0, null);
            }
        }
        finally
        {
            DirectSilkContext.Release(readback);
        }
    }

    private static (ulong Start, ulong End) ReadTimestampPair(NativeBuffer* buffer)
    {
        byte* data = DirectSilkContext.MapRead(buffer, 16);
        try
        {
            return (
                BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(data, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(data + 8, 8)));
        }
        finally
        {
            buffer->Unmap(0, null);
        }
    }

    private static ulong ReadTimestamp(NativeBuffer* buffer)
    {
        byte* data = DirectSilkContext.MapRead(buffer, 8);
        try
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(data, 8));
        }
        finally
        {
            buffer->Unmap(0, null);
        }
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

    private static void WritePacket(Span<byte> destination, int drawIndex)
    {
        float r = ((drawIndex * 17) & 255) / 255f;
        float g = ((drawIndex * 29 + 31) & 255) / 255f;
        float b = ((drawIndex * 43 + 7) & 255) / 255f;
        WriteTint(destination, r, g, b, 1);
    }

    private static void WriteTint(Span<byte> destination, float r, float g, float b, float a)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[0..4], BitConverter.SingleToInt32Bits(r));
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..8], BitConverter.SingleToInt32Bits(g));
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..12], BitConverter.SingleToInt32Bits(b));
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..16], BitConverter.SingleToInt32Bits(a));
    }

    private readonly record struct FrameMeasurement(
        FrameSample Sample,
        CalibratedTimestampInfo Calibration);

    private readonly record struct ThreeQueueMeasurement(
        FrameSample Sample,
        CalibratedTimestampInfo CopyCalibration,
        CalibratedTimestampInfo GraphicsCalibration);

    private struct DirectStateShadow
    {
        private readonly ID3D12GraphicsCommandList* _list;
        private ID3D12PipelineState* _pipeline;
        private ID3D12RootSignature* _root;
        private ulong _persistentAddress;
        private NativeViewport _viewport;
        private Box2D<int> _scissor;
        private bool _hasViewport;
        private bool _hasScissor;

        internal DirectStateShadow(ID3D12GraphicsCommandList* list)
        {
            _list = list;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void SetPipeline(ID3D12PipelineState* pipeline, ID3D12RootSignature* root)
        {
            if (_pipeline == pipeline && _root == root)
                return;
            _list->SetPipelineState(pipeline);
            _list->SetGraphicsRootSignature(root);
            _pipeline = pipeline;
            _root = root;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void SetPersistentBinding(ulong address)
        {
            if (_persistentAddress == address)
                return;
            _list->SetGraphicsRootConstantBufferView(0, address);
            _persistentAddress = address;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void SetViewport(in NativeViewport viewport)
        {
            if (_hasViewport && ViewportEquals(_viewport, viewport))
                return;
            NativeViewport copy = viewport;
            _list->RSSetViewports(1, &copy);
            _viewport = viewport;
            _hasViewport = true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal void SetScissor(in Box2D<int> scissor)
        {
            if (_hasScissor && ScissorBitsEqual(_scissor, scissor))
                return;
            Box2D<int> copy = scissor;
            _list->RSSetScissorRects(1, &copy);
            _scissor = scissor;
            _hasScissor = true;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool ViewportEquals(in NativeViewport left, in NativeViewport right) =>
            NormalizedFloatEquals(left.TopLeftX, right.TopLeftX) &&
            NormalizedFloatEquals(left.TopLeftY, right.TopLeftY) &&
            NormalizedFloatEquals(left.Width, right.Width) &&
            NormalizedFloatEquals(left.Height, right.Height) &&
            NormalizedFloatEquals(left.MinDepth, right.MinDepth) &&
            NormalizedFloatEquals(left.MaxDepth, right.MaxDepth);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool ScissorBitsEqual(in Box2D<int> left, in Box2D<int> right)
        {
            ref byte leftBytes = ref System.Runtime.CompilerServices.Unsafe.As<Box2D<int>, byte>(
                ref System.Runtime.CompilerServices.Unsafe.AsRef(in left));
            ref byte rightBytes = ref System.Runtime.CompilerServices.Unsafe.As<Box2D<int>, byte>(
                ref System.Runtime.CompilerServices.Unsafe.AsRef(in right));
            return System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref leftBytes) ==
                   System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(ref rightBytes) &&
                   System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(
                       ref System.Runtime.CompilerServices.Unsafe.Add(ref leftBytes, 8)) ==
                   System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(
                       ref System.Runtime.CompilerServices.Unsafe.Add(ref rightBytes, 8));
        }
    }

    private sealed unsafe class NativeSwapchain : IDisposable
    {
        private const uint DxgiUsageBackBuffer = 0x40;
        private IDXGISwapChain4* _swapchain;
        private NativeBuffer* _buffer0;
        private NativeBuffer* _buffer1;
        private int _disposed;

        internal NativeSwapchain(DirectSilkContext context, nint window)
        {
            SwapChainDesc1 description = new(
                FixedGraphicsProtocol.RenderWidth,
                FixedGraphicsProtocol.RenderHeight,
                NativeFormat.FormatR8G8B8A8Unorm,
                stereo: false,
                new SampleDesc(1, 0),
                DxgiUsageBackBuffer,
                2,
                Scaling.Stretch,
                SwapEffect.FlipDiscard,
                AlphaMode.Ignore,
                0);
            IDXGISwapChain1* initial = null;
            try
            {
                DirectSilkContext.Check(
                    context.Factory->CreateSwapChainForHwnd(
                        (IUnknown*)context.Graphics.Queue,
                        window,
                        &description,
                        null,
                        null,
                        &initial),
                    "IDXGIFactory6::CreateSwapChainForHwnd");
                IDXGISwapChain4* swapchain = null;
                Guid iid = IDXGISwapChain4.Guid;
                DirectSilkContext.Check(
                    initial->QueryInterface(&iid, (void**)&swapchain),
                    "IDXGISwapChain1::QueryInterface(IDXGISwapChain4)");
                _swapchain = swapchain;
                Guid resourceIid = NativeBuffer.Guid;
                NativeBuffer* buffer0 = null;
                NativeBuffer* buffer1 = null;
                DirectSilkContext.Check(
                    _swapchain->GetBuffer(0, &resourceIid, (void**)&buffer0),
                    "IDXGISwapChain::GetBuffer(0)");
                _buffer0 = buffer0;
                DirectSilkContext.Check(
                    _swapchain->GetBuffer(1, &resourceIid, (void**)&buffer1),
                    "IDXGISwapChain::GetBuffer(1)");
                _buffer1 = buffer1;
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                DirectSilkContext.Release(initial);
            }
        }

        internal NativeBuffer* CurrentBuffer => _swapchain->GetCurrentBackBufferIndex() switch
        {
            0 => _buffer0,
            1 => _buffer1,
            _ => throw new InvalidOperationException("The two-buffer swapchain returned an invalid index."),
        };

        internal void Present() =>
            DirectSilkContext.Check(_swapchain->Present(0, 0), "IDXGISwapChain::Present");

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            DirectSilkContext.Release(_buffer1);
            _buffer1 = null;
            DirectSilkContext.Release(_buffer0);
            _buffer0 = null;
            DirectSilkContext.Release(_swapchain);
            _swapchain = null;
        }
    }
}
