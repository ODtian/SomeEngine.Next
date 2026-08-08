using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed unsafe class WarpNativeAccessTests
{
    [Fact]
    public void Native_getters_and_command_list_borrow_expose_exact_borrowed_objects_and_dirty_state()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() {}
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_native_access_compute",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)]);
        using D3D12Backend backend = new();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                4,
                4,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.CopyDestination));
        using Heap heap = backend.CreateHeap(
            device,
            new HeapDesc(65_536, 0, MemoryType.DeviceLocal, HeapFlags.Buffers));
        using QueryPool queryPool = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Compute, 1));
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));

        Assert.NotEqual(0, (nint)backend.GetNativeDevice(device));
        Assert.NotEqual(0, (nint)backend.GetNativeAdapter(device));
        Assert.NotEqual(0, (nint)backend.GetNativeResource(buffer));
        Assert.NotEqual(0, (nint)backend.GetNativeResource(texture));
        Assert.NotEqual(0, (nint)backend.GetNativeHeap(heap));
        Assert.NotEqual(0, (nint)backend.GetNativePipelineState(pipeline));
        Assert.NotEqual(0, (nint)backend.GetNativeRootSignature(pipeline));
        Assert.NotEqual(0, (nint)backend.GetNativeQueryHeap(queryPool));

        D3D12CommandListBorrow invalid = default;
        Assert.False(invalid.IsValid);
        AssertCommandListUnavailable(invalid);

        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));
        backend.Begin(context, default);
        backend.SetPipeline(context, pipeline);
        backend.SetPipeline(context, pipeline);
        D3D12CommandListBorrow borrow = backend.BorrowCommandList(context, [buffer, texture]);
        D3D12CommandListBorrow copy = borrow;
        Assert.True(borrow.IsValid);
        Assert.True(copy.IsValid);
        Assert.NotEqual(0, (nint)borrow.Pointer);

        backend.SetPipeline(context, pipeline);
        Assert.False(borrow.IsValid);
        Assert.False(copy.IsValid);
        AssertCommandListUnavailable(copy);
        using RecordedCommands commands = backend.End(context);
        Assert.Equal(2, diagnostics!.GetCommandStatistics(commands).StateSetters.Pipelines);
    }

    [Theory]
    [InlineData(RetirementType.Automatic, true)]
    [InlineData(RetirementType.Manual, false)]
    public void Native_encoded_resources_follow_Device_retirement_policy(
        RetirementType retirementType,
        bool disposeBeforeSubmit)
    {
        using D3D12Backend backend = new();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend, retirementType);
        Buffer source = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopyDestination),
            MemoryType.Readback);
        try
        {
            using (MappedBuffer mapping = backend.Map(
                source,
                MapType.Write,
                new BufferRange(0, 256)))
            {
                mapping.Bytes.Fill(0x5A);
                mapping.Flush(new BufferRange(0, 256));
            }

            using CommandContext context = backend.CreateCommandContext(
                device,
                new CommandContextDesc(QueueType.Copy, 0, 1));
            backend.Begin(context, default);
            D3D12CommandListBorrow borrow = backend.BorrowCommandList(
                context,
                [source, destination]);
            borrow.Pointer->CopyBufferRegion(
                backend.GetNativeResource(destination),
                0,
                backend.GetNativeResource(source),
                0,
                256);
            using RecordedCommands commands = backend.End(context);
            Assert.False(borrow.IsValid);

            if (disposeBeforeSubmit)
            {
                source.Dispose();
                destination.Dispose();
            }

            Queue queue = backend.GetQueue(device, QueueType.Copy);
            QueueCompletion completion = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            backend.CollectCompleted(device);
        }
        finally
        {
            source.Dispose();
            destination.Dispose();
        }
    }

    [Fact]
    public void Validation_native_access_rejects_foreign_disposed_wrong_family_and_illegal_state()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() {}
            """;
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "rhi_validated_native_access",
            source,
            [new D3D12TestShaderEntry("computeMain", SlangStage.Compute)]);
        D3D12Backend nativeBackend = new();
        using ValidationLayer<D3D12Backend> backend = new(nativeBackend);
        using Device foreignDevice = D3D12TestSupport.CreateWarpDevice(nativeBackend);
        using Buffer foreign = nativeBackend.CreateBuffer(
            foreignDevice,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.GetEntryPoint(0)));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 1));

        Assert.NotEqual(0, (nint)backend.GetNativeDevice(device));
        Assert.NotEqual(0, (nint)backend.GetNativeResource(buffer));
        AssertNotSupportedByValidation(() => backend.GetNativeStateObject(pipeline));
        AssertInvalidBorrow(backend, context, buffer);

        Buffer disposed = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        disposed.Dispose();
        AssertNotSupportedByValidation(() => backend.GetNativeResource(disposed));

        AssertNotSupportedByValidation(() => backend.GetNativeResource(foreign));

        Queue queue = backend.GetQueue(device, QueueType.Compute);
        D3D12CommandQueueLock held = backend.LockCommandQueue(queue);
        try
        {
            AssertInvalidQueueLock(backend, queue);
        }
        finally
        {
            held.Dispose();
        }

        backend.Begin(context);
        D3D12CommandListBorrow valid = backend.BorrowCommandList(context, [buffer]);
        Assert.True(valid.IsValid);
        backend.Discard(context);
        Assert.False(valid.IsValid);
    }

    [Fact]
    public void Native_capabilities_and_command_queue_lock_report_exact_availability()
    {
        using D3D12Backend backend = new();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.True(backend.TryGetCapability(device, out D3D12NativeAccess? nativeAccess));
        Assert.NotNull(nativeAccess);
        Assert.Same(device, nativeAccess.Device);
        Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
        Assert.NotNull(diagnostics);
        Assert.Same(device, diagnostics.Device);
        Assert.False(diagnostics.DebugLayerEnabled);
        Assert.False(diagnostics.GpuBasedValidationEnabled);
        Assert.False(diagnostics.SynchronizedQueueValidationEnabled);
        Assert.False(diagnostics.DredEnabled);
        Assert.Null(diagnostics.DeviceLoss);

        D3D12CommandQueueLock invalid = default;
        Assert.False(invalid.IsHeld);
        AssertPointerUnavailable(invalid);
        invalid.Dispose();

        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        D3D12CommandQueueLock held = backend.LockCommandQueue(queue);
        D3D12CommandQueueLock copy = held;
        Assert.True(held.IsHeld);
        Assert.True(copy.IsHeld);
        Assert.NotEqual(0, (nint)held.Pointer);

        using ManualResetEventSlim started = new();
        using ManualResetEventSlim finished = new();
        QueueCompletion submitted = default;
        Exception? submitFailure = null;
        Thread waitingSubmit = new(() =>
        {
            started.Set();
            try
            {
                submitted = backend.Submit(queue, new QueueSubmitDesc([], [], [], [], []));
            }
            catch (Exception exception)
            {
                submitFailure = exception;
            }
            finally
            {
                finished.Set();
            }
        });
        waitingSubmit.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.False(finished.Wait(TimeSpan.FromMilliseconds(100)));
        }
        finally
        {
            copy.Dispose();
        }

        Assert.False(held.IsHeld);
        Assert.False(copy.IsHeld);
        AssertPointerUnavailable(held);
        held.Dispose();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        waitingSubmit.Join();
        Assert.Null(submitFailure);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(submitted, TimeSpan.FromSeconds(5)));

        D3D12CommandQueueLock next = backend.LockCommandQueue(queue);
        Assert.True(next.IsHeld);
        Assert.False(held.IsHeld);
        Assert.NotEqual(0, (nint)next.Pointer);
        next.Dispose();
        next.Dispose();
        Assert.False(next.IsHeld);
    }

    private static void AssertPointerUnavailable(D3D12CommandQueueLock value)
    {
        try
        {
            _ = value.Pointer;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("A released native Queue lock exposed its pointer.");
    }

    private static void AssertCommandListUnavailable(D3D12CommandListBorrow value)
    {
        try
        {
            _ = value.Pointer;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("An invalid native command-list borrow exposed its pointer.");
    }

    private static void AssertNotSupportedByValidation(Action action) =>
        Assert.Throws<InvalidOperationException>(action);

    private static void AssertInvalidBorrow(
        ValidationLayer<D3D12Backend> backend,
        CommandContext context,
        Buffer retainedResource)
    {
        try
        {
            _ = backend.BorrowCommandList(context, [retainedResource]);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("Validation accepted an illegal native command-list borrow.");
    }

    private static void AssertInvalidQueueLock(
        ValidationLayer<D3D12Backend> backend,
        Queue queue)
    {
        try
        {
            D3D12CommandQueueLock unexpected = backend.LockCommandQueue(queue);
            unexpected.Dispose();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Xunit.Sdk.XunitException("Validation accepted same-thread native Queue re-entry.");
    }
}
