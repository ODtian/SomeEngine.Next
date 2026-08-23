using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Graphics.Benchmarks;

internal static partial class RhiBenchmarkRunner
{
    private static WorkloadRun RunRepresentativeFrame<TReceiver, TDispatch>(
        TReceiver receiver,
        IGraphicsBackend backend,
        Device device,
        Pipeline pipeline,
        VariableLayoutReflection layout,
        string shaderManifest,
        in WorkerConfiguration configuration,
        GraphicsWorkload workload)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        byte[] materialSequence = RepresentativeFrameProfile.LoadMaterials(configuration.ShaderDirectory);
        PersistentParameterBindings[] materials = CreateRepresentativeMaterials(
            backend,
            device,
            pipeline,
            layout);
        ValidateRepresentativeInputs(materialSequence, materials);
        CommandContext[] contexts = CreateRepresentativeContexts(backend, device);
        RhiRepresentativeWorker<TReceiver, TDispatch>[] workers = [];
        try
        {
            return RunRepresentativeFrameCore<TReceiver, TDispatch>(
                receiver,
                backend,
                device,
                pipeline,
                shaderManifest,
                configuration,
                workload,
                materialSequence,
                materials,
                contexts,
                ref workers);
        }
        finally
        {
            DisposeAll(workers);
            DisposeAll(contexts);
            DisposeAll(materials);
        }
    }

    private static WorkloadRun RunRepresentativeFrameCore<TReceiver, TDispatch>(
        TReceiver receiver,
        IGraphicsBackend backend,
        Device device,
        Pipeline pipeline,
        string shaderManifest,
        in WorkerConfiguration configuration,
        GraphicsWorkload workload,
        byte[] materialSequence,
        PersistentParameterBindings[] materials,
        CommandContext[] contexts,
        ref RhiRepresentativeWorker<TReceiver, TDispatch>[] workers)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
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
                TextureUsages.ColorAttachment,
                label: "representative CPU frame target"));
        using ColorAttachmentView targetView = backend.CreateColorAttachmentView(
            device,
            new ColorAttachmentViewDesc(
                target,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                Format.R8G8B8A8UNorm,
                TextureViewDimension.Texture2D));
        ColorAttachmentDesc[] loadAttachments =
        [
            new(targetView, LoadType.Load, StoreType.Store, default),
        ];
        ColorAttachmentDesc[] clearAttachments =
        [
            new(targetView, LoadType.Clear, StoreType.Store, new(0.0625f, 0.125f, 0.25f, 1)),
        ];
        Viewport[] viewports =
        [
            new(0, 0, FixedGraphicsProtocol.RenderWidth, FixedGraphicsProtocol.RenderHeight),
        ];
        ScissorRect[] scissors =
        [
            new(0, 0, FixedGraphicsProtocol.RenderWidth, FixedGraphicsProtocol.RenderHeight),
        ];
        using Buffer objectData = backend.CreateBuffer(
            device,
            new BufferDesc(
                RepresentativeFrameProfile.ObjectCount * RepresentativeFrameProfile.ObjectPacketSize,
                BufferUsages.CopySource,
                "representative frame object data"),
            MemoryType.Upload);
        BufferRange objectRange = new(0, objectData.Info.Size);
        using MappedBuffer mapping = TDispatch.Map(receiver, objectData, MapType.Write, objectRange);
        CommandRecordingDesc recording = new(
            InitialCapturedResourceCapacity: 64,
            Label: workload.ToString());
        MemoryBarrier barrier = new(
            PipelineSync.Draw,
            PipelineSync.Draw,
            ResourceAccess.ConstantBuffer,
            ResourceAccess.ConstantBuffer);
        var commands = new RecordedCommands[RepresentativeFrameProfile.CommandListCount];
        bool measureBreakdown =
            Environment.GetEnvironmentVariable("SOMEENGINE_RHI_BREAKDOWN") == "1";

        if (workload == GraphicsWorkload.RepresentativeFrameParallel)
        {
            workers = CreateRhiRepresentativeWorkers<TReceiver, TDispatch>(
                receiver,
                contexts,
                pipeline,
                materials,
                materialSequence,
                loadAttachments,
                viewports,
                scissors,
                recording,
                measureBreakdown);
        }

        var samples = new FrameSample[configuration.MeasuredFrames];
        for (int frame = 0; frame < configuration.WarmupFrames; frame++)
        {
            _ = ExecuteRepresentativeFrame<TReceiver, TDispatch>(
                receiver,
                contexts,
                workers,
                pipeline,
                materials,
                materialSequence,
                clearAttachments,
                loadAttachments,
                viewports,
                scissors,
                mapping.Bytes,
                commands,
                barrier,
                recording,
                workload == GraphicsWorkload.RepresentativeFrameParallel,
                frame);
        }
        if (measureBreakdown)
        {
            foreach (RhiRepresentativeWorker<TReceiver, TDispatch> worker in workers)
                worker.ClearTiming();
        }
        for (int frame = 0; frame < samples.Length; frame++)
        {
            samples[frame] = ExecuteRepresentativeFrame<TReceiver, TDispatch>(
                receiver,
                contexts,
                workers,
                pipeline,
                materials,
                materialSequence,
                clearAttachments,
                loadAttachments,
                viewports,
                scissors,
                mapping.Bytes,
                commands,
                barrier,
                recording,
                workload == GraphicsWorkload.RepresentativeFrameParallel,
                frame);
        }
        if (measureBreakdown)
        {
            var timing = new long[6];
            foreach (RhiRepresentativeWorker<TReceiver, TDispatch> worker in workers)
                worker.AddTimingTo(timing);
            Console.Error.WriteLine(
                $"RHI_BREAKDOWN begin={BenchmarkClock.TicksToMicroseconds(timing[0])} " +
                $"setup={BenchmarkClock.TicksToMicroseconds(timing[1])} " +
                $"beginRendering={BenchmarkClock.TicksToMicroseconds(timing[2])} " +
                $"draw={BenchmarkClock.TicksToMicroseconds(timing[3])} " +
                $"endRendering={BenchmarkClock.TicksToMicroseconds(timing[4])} " +
                $"end={BenchmarkClock.TicksToMicroseconds(timing[5])}");
        }

        BarrierEvidence[] barriers = RepresentativeFrameProfile.NativeBarrierCommandCount == 0
            ? []
            :
            [
                new(0, nameof(MemoryBarrier), 0, 1, "pre-pass dependency"),
                new(1, nameof(MemoryBarrier), 1, 1, "shadow-to-scene dependency"),
                new(2, nameof(MemoryBarrier), 2, 1, "shadow-to-scene dependency"),
                new(3, nameof(MemoryBarrier), 3, 1, "post-pass dependency"),
            ];
        return BenchmarkOutput.Complete(
            workload,
            configuration.Profile,
            configuration.WarmupFrames,
            configuration.MeasuredFrames,
            RepresentativeFrameProfile.LogicalDrawRequestCount,
            RepresentativeFrameProfile.NativeBarrierCommandCount,
            samples,
            [],
            RepresentativeFrameProfile.MaterialSequenceSha256,
            shaderManifest,
            barriers,
            RepresentativeFrameProfile.CreateWorkloadEvidence());
    }

    private static PersistentParameterBindings[] CreateRepresentativeMaterials(
        IGraphicsBackend backend,
        Device device,
        Pipeline pipeline,
        VariableLayoutReflection layout)
    {
        var result = new PersistentParameterBindings[RepresentativeFrameProfile.MaterialCount];
        try
        {
            byte[] data = new byte[16];
            for (int material = 0; material < result.Length; material++)
            {
                WriteTint(
                    data,
                    ((material * 17) & 255) / 255f,
                    ((material * 29 + 31) & 255) / 255f,
                    ((material * 43 + 7) & 255) / 255f,
                    1);
                result[material] = backend.CreatePersistentParameterBindings(
                    device,
                    pipeline,
                    new ParameterBlockBindings(layout, NoResources, data),
                    $"representative material {material}");
            }
            return result;
        }
        catch
        {
            DisposeAll(result);
            throw;
        }
    }

    private static void ValidateRepresentativeInputs(
        byte[] materialSequence,
        PersistentParameterBindings[] materials)
    {
        if (materialSequence.Length != RepresentativeFrameProfile.ObjectCount)
        {
            throw new ArgumentException(
                "The representative material sequence is invalid.",
                nameof(materialSequence));
        }
        if (materials.Length != RepresentativeFrameProfile.MaterialCount ||
            Array.Exists(materials, static material => material is null))
        {
            throw new ArgumentException(
                "The representative material table is invalid.",
                nameof(materials));
        }
        for (int worker = 0; worker < RepresentativeFrameProfile.WorkerCount; worker++)
        {
            (int start, int count) = RepresentativeFrameProfile.GetWorkerRange(worker);
            int end = checked(start + count);
            if ((uint)start > (uint)materialSequence.Length ||
                end < start ||
                (uint)end > (uint)materialSequence.Length)
            {
                throw new InvalidDataException(
                    "A representative worker range is outside the material sequence.");
            }
        }
    }

    private static CommandContext[] CreateRepresentativeContexts(IGraphicsBackend backend, Device device)
    {
        var result = new CommandContext[RepresentativeFrameProfile.CommandListCount];
        try
        {
            for (int index = 0; index < result.Length; index++)
            {
                result[index] = backend.CreateCommandContext(
                    device,
                    new CommandContextDesc(
                        QueueType.Graphics,
                        0,
                        1,
                        Label: $"representative list {index}"));
            }
            return result;
        }
        catch
        {
            DisposeAll(result);
            throw;
        }
    }

    private static RhiRepresentativeWorker<TReceiver, TDispatch>[] CreateRhiRepresentativeWorkers<TReceiver, TDispatch>(
        TReceiver receiver,
        CommandContext[] contexts,
        Pipeline pipeline,
        PersistentParameterBindings[] materials,
        byte[] materialSequence,
        ColorAttachmentDesc[] attachments,
        Viewport[] viewports,
        ScissorRect[] scissors,
        in CommandRecordingDesc recording,
        bool measureBreakdown)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        var result = new RhiRepresentativeWorker<TReceiver, TDispatch>[RepresentativeFrameProfile.WorkerCount];
        try
        {
            for (int worker = 0; worker < result.Length; worker++)
            {
                (int start, int count) = RepresentativeFrameProfile.GetWorkerRange(worker);
                result[worker] = new RhiRepresentativeWorker<TReceiver, TDispatch>(
                    receiver,
                    contexts[3 + worker],
                    contexts[6 + worker],
                    pipeline,
                    materials,
                    materialSequence,
                    attachments,
                    viewports,
                    scissors,
                    recording,
                    start,
                    count,
                    worker,
                    measureBreakdown);
            }
            return result;
        }
        catch
        {
            DisposeAll(result);
            throw;
        }
    }

    private static FrameSample ExecuteRepresentativeFrame<TReceiver, TDispatch>(
        TReceiver receiver,
        CommandContext[] contexts,
        RhiRepresentativeWorker<TReceiver, TDispatch>[] workers,
        Pipeline pipeline,
        PersistentParameterBindings[] materials,
        byte[] materialSequence,
        ColorAttachmentDesc[] clearAttachments,
        ColorAttachmentDesc[] loadAttachments,
        Viewport[] viewports,
        ScissorRect[] scissors,
        Span<byte> objectBytes,
        RecordedCommands[] commands,
        in MemoryBarrier barrier,
        in CommandRecordingDesc recording,
        bool parallel,
        int frameIndex)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
#if !SOMEENGINE_DISABLE_REPRESENTATIVE_ALLOCATION_MEASUREMENT
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
#endif
        long workerBytes = 0;
        long started = Stopwatch.GetTimestamp();
        long stopped;
        long cleanupStarted = 0;
        long cleanupStopped = 0;
        try
        {
            RepresentativeFrameProfile.WriteObjectPacketsUnchecked(objectBytes, frameIndex);
            commands[0] = RecordRepresentativeMainList<TReceiver, TDispatch>(
                receiver,
                contexts[0],
                clearAttachments,
                barrier,
                recording,
                barrierCount: 1,
                clear: true);

            if (parallel)
            {
                foreach (RhiRepresentativeWorker<TReceiver, TDispatch> worker in workers)
                    worker.StartShadow();
                for (int worker = 0; worker < workers.Length; worker++)
                {
                    commands[3 + worker] = workers[worker].WaitShadow();
                }
                workerBytes += SumWorkerAllocations(workers);
            }
            else
            {
                for (int worker = 0; worker < RepresentativeFrameProfile.WorkerCount; worker++)
                {
                    (int start, int count) = RepresentativeFrameProfile.GetWorkerRange(worker);
                    commands[3 + worker] = RecordRepresentativePass<TReceiver, TDispatch>(
                        receiver,
                        contexts[3 + worker],
                        pipeline,
                        materials,
                        materialSequence,
                        loadAttachments,
                        viewports,
                        scissors,
                        recording,
                        start,
                        count,
                        scene: false);
                }
            }

            commands[1] = RecordRepresentativeMainList<TReceiver, TDispatch>(
                receiver,
                contexts[1],
                clearAttachments,
                barrier,
                recording,
                barrierCount: 2,
                clear: false);

            if (parallel)
            {
                foreach (RhiRepresentativeWorker<TReceiver, TDispatch> worker in workers)
                    worker.StartScene();
                for (int worker = 0; worker < workers.Length; worker++)
                {
                    commands[6 + worker] = workers[worker].WaitScene();
                }
                workerBytes += SumWorkerAllocations(workers);
            }
            else
            {
                for (int worker = 0; worker < RepresentativeFrameProfile.WorkerCount; worker++)
                {
                    (int start, int count) = RepresentativeFrameProfile.GetWorkerRange(worker);
                    commands[6 + worker] = RecordRepresentativePass<TReceiver, TDispatch>(
                        receiver,
                        contexts[6 + worker],
                        pipeline,
                        materials,
                        materialSequence,
                        loadAttachments,
                        viewports,
                        scissors,
                        recording,
                        start,
                        count,
                        scene: true);
                }
            }

            commands[2] = RecordRepresentativeMainList<TReceiver, TDispatch>(
                receiver,
                contexts[2],
                clearAttachments,
                barrier,
                recording,
                barrierCount: 1,
                clear: false);
            stopped = Stopwatch.GetTimestamp();
        }
        finally
        {
            cleanupStarted = Stopwatch.GetTimestamp();
            try
            {
                DisposeRecordedCommands(commands);
            }
            finally
            {
                cleanupStopped = Stopwatch.GetTimestamp();
            }
        }
#if SOMEENGINE_DISABLE_REPRESENTATIVE_ALLOCATION_MEASUREMENT
        const long bytes = 0;
#else
        long bytes = checked(GC.GetAllocatedBytesForCurrentThread() - beforeBytes + workerBytes);
#endif
        long ticks = stopped - started;
        long cleanupTicks = cleanupStopped - cleanupStarted;
        return new FrameSample(
            frameIndex,
            ticks,
            BenchmarkClock.TicksToMicroseconds(ticks),
            null,
            bytes,
            0,
            checked((ulong)frameIndex + 1),
            cleanupTicks,
            BenchmarkClock.TicksToMicroseconds(cleanupTicks));
    }

    [SkipLocalsInit]
    private static RecordedCommands RecordRepresentativeMainList<TReceiver, TDispatch>(
        TReceiver receiver,
        CommandContext context,
        ColorAttachmentDesc[] attachments,
        in MemoryBarrier barrier,
        in CommandRecordingDesc recording,
        int barrierCount,
        bool clear)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        TDispatch.Begin(receiver, context, recording);
        bool rendering = false;
        try
        {
#if !REPRESENTATIVE_LIFECYCLE_ONLY && !REPRESENTATIVE_STATE_ONLY
            for (int index = 0; index < barrierCount; index++)
                TDispatch.Barrier(receiver, context, barrier);
            if (clear)
            {
                RenderingDesc renderingDesc = new(
                    attachments,
                    null,
                    FixedGraphicsProtocol.RenderWidth,
                    FixedGraphicsProtocol.RenderHeight);
                TDispatch.BeginRendering(receiver, context, renderingDesc);
                rendering = true;
                TDispatch.EndRendering(receiver, context);
                rendering = false;
            }
#endif
            RecordedCommands result = TDispatch.End(receiver, context);
            return result;
        }
        catch
        {
            if (rendering)
                TDispatch.EndRendering(receiver, context);
            TDispatch.Discard(receiver, context);
            throw;
        }
    }

    [SkipLocalsInit]
    private static RecordedCommands RecordRepresentativePass<TReceiver, TDispatch>(
        TReceiver receiver,
        CommandContext context,
        Pipeline pipeline,
        PersistentParameterBindings[] materials,
        byte[] materialSequence,
        ColorAttachmentDesc[] attachments,
        Viewport[] viewports,
        ScissorRect[] scissors,
        in CommandRecordingDesc recording,
        int start,
        int count,
        bool scene,
        long[]? timing = null)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        long phaseStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
        TDispatch.Begin(receiver, context, recording);
        if (timing is not null)
            timing[0] += Stopwatch.GetTimestamp() - phaseStarted;
        bool rendering = false;
        try
        {
#if !REPRESENTATIVE_LIFECYCLE_ONLY
            phaseStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
            TDispatch.SetPipeline(receiver, context, pipeline);
            TDispatch.SetViewports(receiver, context, viewports);
            TDispatch.SetScissors(receiver, context, scissors);
#if !REPRESENTATIVE_PER_DRAW_BINDINGS
            if (!scene)
                TDispatch.SetPersistentBindings(receiver, context, materials[0]);
#endif
            if (timing is not null)
                timing[1] += Stopwatch.GetTimestamp() - phaseStarted;
#if !REPRESENTATIVE_STATE_ONLY
            RenderingDesc renderingDesc = new(
                attachments,
                null,
                FixedGraphicsProtocol.RenderWidth,
                FixedGraphicsProtocol.RenderHeight);
            phaseStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
            TDispatch.BeginRendering(receiver, context, renderingDesc);
            if (timing is not null)
                timing[2] += Stopwatch.GetTimestamp() - phaseStarted;
            rendering = true;
#endif
            phaseStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
            int end =
#if REPRESENTATIVE_FIXED_ONLY || REPRESENTATIVE_LIFECYCLE_ONLY || REPRESENTATIVE_STATE_ONLY
                start;
#else
                start + count;
#endif
            ref byte firstMaterial = ref MemoryMarshal.GetArrayDataReference(materialSequence);
            ref PersistentParameterBindings firstBinding =
                ref MemoryMarshal.GetArrayDataReference(materials);
            DrawArguments draw = new(3, 1, 0, 0);
            if (!scene)
            {
#if !REPRESENTATIVE_BINDINGS_ONLY
                for (int index = start; index < end; index++)
                {
#if REPRESENTATIVE_PER_DRAW_BINDINGS
                    TDispatch.SetPersistentBindings(receiver, context, materials[0]);
#endif
                    TDispatch.Draw(receiver, context, draw);
                }
#endif
            }
            else
            {
#if !REPRESENTATIVE_PER_DRAW_BINDINGS
                int currentMaterial = -1;
#endif
                for (int index = start; index < end; index++)
                {
#if REPRESENTATIVE_UNIFORM_MATERIAL
                    int material = 0;
#else
                    int material = Unsafe.Add(ref firstMaterial, index);
#endif
#if REPRESENTATIVE_PER_DRAW_BINDINGS
                    TDispatch.SetPersistentBindings(
                        receiver,
                        context,
                        Unsafe.Add(ref firstBinding, material));
#else
                    if (material != currentMaterial)
                    {
                        TDispatch.SetPersistentBindings(
                            receiver,
                            context,
                            Unsafe.Add(ref firstBinding, material));
                        currentMaterial = material;
                    }
#endif
#if !REPRESENTATIVE_BINDINGS_ONLY
                    TDispatch.Draw(receiver, context, draw);
#endif
                }
            }
            if (timing is not null)
                timing[3] += Stopwatch.GetTimestamp() - phaseStarted;
#if !REPRESENTATIVE_STATE_ONLY
            phaseStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
            TDispatch.EndRendering(receiver, context);
            if (timing is not null)
                timing[4] += Stopwatch.GetTimestamp() - phaseStarted;
            rendering = false;
#endif
#endif
            phaseStarted = timing is null ? 0 : Stopwatch.GetTimestamp();
            RecordedCommands result = TDispatch.End(receiver, context);
            if (timing is not null)
                timing[5] += Stopwatch.GetTimestamp() - phaseStarted;
            return result;
        }
        catch
        {
            if (rendering)
                TDispatch.EndRendering(receiver, context);
            TDispatch.Discard(receiver, context);
            throw;
        }
    }

    private static void DisposeRecordedCommands(RecordedCommands[] commands)
    {
        for (int index = commands.Length - 1; index >= 0; index--)
        {
            commands[index].Dispose();
            commands[index] = default;
        }
    }

    private static long SumWorkerAllocations<TReceiver, TDispatch>(
        RhiRepresentativeWorker<TReceiver, TDispatch>[] workers)
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        long result = 0;
        foreach (RhiRepresentativeWorker<TReceiver, TDispatch> worker in workers)
            result = checked(result + worker.TakeAllocatedBytes());
        return result;
    }

    private static void DisposeAll<T>(T[] values)
        where T : class, IDisposable
    {
        for (int index = values.Length - 1; index >= 0; index--)
            values[index]?.Dispose();
    }

    private sealed class RhiRepresentativeWorker<TReceiver, TDispatch> : IDisposable
        where TDispatch : struct, IRhiDispatch<TReceiver>
    {
        private readonly TReceiver _receiver;
        private readonly CommandContext _shadowContext;
        private readonly CommandContext _sceneContext;
        private readonly Pipeline _pipeline;
        private readonly PersistentParameterBindings[] _materials;
        private readonly byte[] _materialSequence;
        private readonly ColorAttachmentDesc[] _attachments;
        private readonly Viewport[] _viewports;
        private readonly ScissorRect[] _scissors;
        private readonly CommandRecordingDesc _recording;
        private readonly long[] _timing = new long[6];
        private readonly bool _measureBreakdown;
        private readonly int _start;
        private readonly int _count;
        private readonly RepresentativeWorkerSignal _shadowStart = new();
        private readonly RepresentativeWorkerSignal _shadowDone = new();
        private readonly RepresentativeWorkerSignal _sceneStart = new();
        private readonly RepresentativeWorkerSignal _sceneDone = new();
        private readonly Thread _thread;
        private ExceptionDispatchInfo? _failure;
        private RecordedCommands _shadowCommands;
        private RecordedCommands _sceneCommands;
        private long _allocatedBytes;
        private bool _stop;

        internal RhiRepresentativeWorker(
            TReceiver receiver,
            CommandContext shadowContext,
            CommandContext sceneContext,
            Pipeline pipeline,
            PersistentParameterBindings[] materials,
            byte[] materialSequence,
            ColorAttachmentDesc[] attachments,
            Viewport[] viewports,
            ScissorRect[] scissors,
            in CommandRecordingDesc recording,
            int start,
            int count,
            int workerIndex,
            bool measureBreakdown)
        {
            _receiver = receiver;
            _shadowContext = shadowContext;
            _sceneContext = sceneContext;
            _pipeline = pipeline;
            _materials = materials;
            _materialSequence = materialSequence;
            _attachments = attachments;
            _viewports = viewports;
            _scissors = scissors;
            _recording = recording;
            _start = start;
            _count = count;
            _measureBreakdown = measureBreakdown;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"RHI representative worker {workerIndex}",
            };
            _thread.Start();
        }

        internal void StartShadow()
        {
            _failure = null;
            _allocatedBytes = 0;
            _shadowStart.Signal();
        }

        internal RecordedCommands WaitShadow()
        {
            _shadowDone.Wait();
            _failure?.Throw();
            RecordedCommands result = _shadowCommands;
            _shadowCommands = default;
            return result;
        }

        internal void StartScene() => _sceneStart.Signal();

        internal RecordedCommands WaitScene()
        {
            _sceneDone.Wait();
            _failure?.Throw();
            RecordedCommands result = _sceneCommands;
            _sceneCommands = default;
            return result;
        }

        internal long TakeAllocatedBytes()
        {
            long result = _allocatedBytes;
            _allocatedBytes = 0;
            return result;
        }

        internal void ClearTiming() => Array.Clear(_timing);

        internal void AddTimingTo(long[] destination)
        {
            for (int index = 0; index < _timing.Length; index++)
                destination[index] += _timing[index];
        }

        public void Dispose()
        {
            Volatile.Write(ref _stop, true);
            _shadowStart.Signal();
            _sceneStart.Signal();
            _thread.Join();
            _sceneCommands.Dispose();
            _shadowCommands.Dispose();
            _sceneDone.Dispose();
            _sceneStart.Dispose();
            _shadowDone.Dispose();
            _shadowStart.Dispose();
        }

        private void Run()
        {
            while (true)
            {
                _shadowStart.Wait();
                if (Volatile.Read(ref _stop))
                    return;
                RunPhase(_shadowContext, scene: false, _shadowDone);
                if (_failure is not null)
                    continue;
                _sceneStart.Wait();
                if (Volatile.Read(ref _stop))
                    return;
                RunPhase(_sceneContext, scene: true, _sceneDone);
            }
        }

        private void RunPhase(
            CommandContext context,
            bool scene,
            RepresentativeWorkerSignal completed)
        {
            RecordedCommands commands = default;
            try
            {
#if !SOMEENGINE_DISABLE_REPRESENTATIVE_ALLOCATION_MEASUREMENT
                long before = GC.GetAllocatedBytesForCurrentThread();
#endif
                commands = RecordRepresentativePass<TReceiver, TDispatch>(
                    _receiver,
                    context,
                    _pipeline,
                    _materials,
                    _materialSequence,
                    _attachments,
                    _viewports,
                    _scissors,
                    _recording,
                    _start,
                    _count,
                    scene,
                    _measureBreakdown ? _timing : null);
#if !SOMEENGINE_DISABLE_REPRESENTATIVE_ALLOCATION_MEASUREMENT
                _allocatedBytes = checked(
                    _allocatedBytes + GC.GetAllocatedBytesForCurrentThread() - before);
#endif
                if (scene)
                    _sceneCommands = commands;
                else
                    _shadowCommands = commands;
                commands = default;
            }
            catch (Exception exception)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                commands.Dispose();
                completed.Signal();
            }
        }
    }
}
