using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpSubmissionPreflightTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_completion_wait_is_rejected_before_submission_acceptance(bool validated)
    {
        IGraphicsBackend backend = validated
            ? new ValidationLayer(new D3D12Backend())
            : new D3D12Backend();
        using (backend)
        using (Device device = D3D12TestSupport.CreateWarpDevice(backend))
        {
            Queue queue = backend.GetQueue(device, QueueType.Copy);
            Queue completionQueue = backend.GetQueue(device, QueueType.Compute);
            QueueCompletion validWait = backend.Submit(
                completionQueue,
                new QueueSubmitDesc([], [], [], [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(validWait, TimeSpan.FromSeconds(10)));

            VerifyRejectedWaitCanRetry(
                backend,
                device,
                queue,
                validWait,
                invalidFirst: false,
                expected: CreatePattern(64, 23));
            VerifyRejectedWaitCanRetry(
                backend,
                device,
                queue,
                validWait,
                invalidFirst: true,
                expected: CreatePattern(64, 71));
        }
    }

    [Fact]
    public void Warm_completion_wait_high_water_submit_allocates_no_managed_memory()
    {
        using IGraphicsBackend backend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Queue queue = backend.GetQueue(device, QueueType.Copy);
        Queue completionQueue = backend.GetQueue(device, QueueType.Compute);
        QueueCompletion[] waits =
        [
            backend.Submit(completionQueue, new QueueSubmitDesc([], [], [], [], [])),
            backend.Submit(completionQueue, new QueueSubmitDesc([], [], [], [], [])),
        ];
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(waits[1], TimeSpan.FromSeconds(10)));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        var payloads = new RecordedCommands[1];

        backend.Begin(context);
        using (RecordedCommands warmCommands = backend.End(context))
        {
            payloads[0] = warmCommands;
            QueueCompletion warm = backend.Submit(
                queue,
                new QueueSubmitDesc(waits, [], payloads, [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(warm, TimeSpan.FromSeconds(10)));
        }
        backend.CollectCompleted(device);

        backend.Begin(context);
        using RecordedCommands measuredCommands = backend.End(context);
        payloads[0] = measuredCommands;
        QueueSubmitDesc measuredSubmit = new(waits, [], payloads, [], []);

        _ = GC.GetAllocatedBytesForCurrentThread();
        long before = GC.GetAllocatedBytesForCurrentThread();
        QueueCompletion measured = backend.Submit(queue, measuredSubmit);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(measured, TimeSpan.FromSeconds(10)));
        backend.CollectCompleted(device);
    }

    private static void VerifyRejectedWaitCanRetry(
        IGraphicsBackend backend,
        Device device,
        Queue queue,
        QueueCompletion validWait,
        bool invalidFirst,
        byte[] expected)
    {
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(checked((ulong)expected.Length), BufferUsages.CopySource),
            MemoryType.Upload);
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(checked((ulong)expected.Length), BufferUsages.CopyDestination),
            MemoryType.Readback);
        BufferRange range = new(0, checked((ulong)expected.Length));
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, range))
        {
            expected.CopyTo(mapped.Bytes);
            mapped.Flush(range);
        }

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 1));
        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(upload, 0, readback, 0, range.Size));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion[] rejectedWaits = invalidFirst
            ? [default, validWait]
            : [validWait, default];
        RecordedCommands[] payloads = [commands];

        Assert.Throws<InvalidOperationException>(() => backend.Submit(
            queue,
            new QueueSubmitDesc(rejectedWaits, [], payloads, [], [])));
        Assert.Equal(DeviceStatus.Active, device.Status);
        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);

        QueueCompletion completed = backend.Submit(
            queue,
            new QueueSubmitDesc([validWait], [], payloads, [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completed, TimeSpan.FromSeconds(10)));
        using (MappedBuffer mapped = backend.Map(readback, MapType.Read, range))
        {
            mapped.Invalidate(range);
            Assert.Equal(expected, mapped.Bytes.ToArray());
        }
        backend.CollectCompleted(device);
    }

    private static byte[] CreatePattern(int length, int seed)
    {
        var result = new byte[length];
        for (int index = 0; index < result.Length; index++)
            result[index] = unchecked((byte)(seed + index * 29));
        return result;
    }
}
