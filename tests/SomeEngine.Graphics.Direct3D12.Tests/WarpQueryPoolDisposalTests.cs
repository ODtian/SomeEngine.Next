using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;
using Xunit.Abstractions;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpQueryPoolDisposalTests(ITestOutputHelper output)
{
    [Fact]
    public void Unused_query_pool_disposal_does_not_throw()
    {
        List<ValidationMessage> messages = [];
        using ValidationLayer backend = CreateLayer(messages);
        using Device device = CreateDevice(backend, out D3D12Diagnostics diagnostics);
        using (QueryPool pool = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 4)))
        {
            QueryPoolDesc description = pool.Description;
            QueryResultInfo resultInfo = pool.ResultInfo;

            Assert.Null(Record.Exception(pool.Dispose));
            Assert.Equal(description, pool.Description);
            Assert.Equal(resultInfo, pool.ResultInfo);
            Assert.Null(Record.Exception(pool.Dispose));
        }

        Assert.Equal(DeviceStatus.Active, device.Status);
        device.Dispose();
        backend.Dispose();
        Assert.Null(diagnostics.TeardownFailure);
        Assert.DoesNotContain(messages, static message => message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Disposing_one_query_pool_preserves_another_pools_resolution_and_reuse()
    {
        List<ValidationMessage> messages = [];
        using ValidationLayer backend = CreateLayer(messages);
        using Device device = CreateDevice(backend, out D3D12Diagnostics diagnostics);
        {
            using QueryPool firstPool = backend.CreateQueryPool(
                device,
                new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1));
            using QueryPool secondPool = backend.CreateQueryPool(
                device,
                new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1));
            using Buffer firstDestination = backend.CreateBuffer(
                device,
                new BufferDesc(firstPool.ResultInfo.ResultStride,
                    BufferUsages.QueryResolve | BufferUsages.CopyDestination),
                MemoryType.Readback);
            using Buffer secondDestination = backend.CreateBuffer(
                device,
                new BufferDesc(secondPool.ResultInfo.ResultStride,
                    BufferUsages.QueryResolve | BufferUsages.CopyDestination),
                MemoryType.Readback);
            using CommandContext context = backend.CreateCommandContext(
                device,
                new CommandContextDesc(QueueType.Graphics, 0, 1));
            Queue queue = backend.GetQueue(device, QueueType.Graphics);
            BufferRange firstRange = new(0, firstPool.ResultInfo.ResultStride);
            BufferRange secondRange = new(0, secondPool.ResultInfo.ResultStride);

            backend.Begin(context);
            backend.ResetQueries(context, firstPool, 0, 1);
            backend.WriteTimestamp(context, firstPool, 0);
            backend.ResolveQueries(context, firstPool, 0, 1, firstDestination, firstRange);
            backend.ResetQueries(context, secondPool, 0, 1);
            backend.WriteTimestamp(context, secondPool, 0);
            using (RecordedCommands commands = backend.End(context))
            {
                QueueCompletion completion = backend.Submit(
                    queue, new QueueSubmitDesc([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed,
                    backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                backend.CollectCompleted(device);
            }

            firstPool.Dispose();

            // Only resolution is recorded: the earlier write must remain available.
            backend.Begin(context);
            backend.ResolveQueries(context, secondPool, 0, 1, secondDestination, secondRange);
            using (RecordedCommands commands = backend.End(context))
            {
                QueueCompletion completion = backend.Submit(
                    queue, new QueueSubmitDesc([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed,
                    backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                backend.CollectCompleted(device);
            }

            backend.Begin(context);
            backend.ResetQueries(context, secondPool, 0, 1);
            backend.WriteTimestamp(context, secondPool, 0);
            backend.ResolveQueries(context, secondPool, 0, 1, secondDestination, secondRange);
            using (RecordedCommands commands = backend.End(context))
            {
                QueueCompletion completion = backend.Submit(
                    queue, new QueueSubmitDesc([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed,
                    backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                backend.CollectCompleted(device);
            }
        }

        Assert.Equal(DeviceStatus.Active, device.Status);
        device.Dispose();
        backend.Dispose();
        Assert.Null(diagnostics.TeardownFailure);
        Assert.DoesNotContain(messages, static message => message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Unused_query_pool_disposal_after_prior_query_work_does_not_throw()
    {
        List<ValidationMessage> messages = [];
        using ValidationLayer backend = CreateLayer(messages);
        using Device device = CreateDevice(backend, out D3D12Diagnostics diagnostics);
        {
            using QueryPool usedPool = backend.CreateQueryPool(
                device,
                new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1));
            using Buffer destination = backend.CreateBuffer(
                device,
                new BufferDesc(usedPool.ResultInfo.ResultStride,
                    BufferUsages.QueryResolve | BufferUsages.CopyDestination),
                MemoryType.Readback);
            using CommandContext context = backend.CreateCommandContext(
                device,
                new CommandContextDesc(QueueType.Graphics, 0, 1));

            backend.Begin(context);
            backend.ResetQueries(context, usedPool, 0, 1);
            backend.WriteTimestamp(context, usedPool, 0);
            backend.ResolveQueries(context, usedPool, 0, 1, destination,
                new BufferRange(0, usedPool.ResultInfo.ResultStride));
            using (RecordedCommands commands = backend.End(context))
            {
                QueueCompletion completion = backend.Submit(
                    backend.GetQueue(device, QueueType.Graphics),
                    new QueueSubmitDesc([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed,
                    backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                backend.CollectCompleted(device);
            }

            usedPool.Dispose();
        }

        using (QueryPool unusedPool = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 4)))
        {
            QueryPoolDesc description = unusedPool.Description;
            QueryResultInfo resultInfo = unusedPool.ResultInfo;

            Assert.Null(Record.Exception(unusedPool.Dispose));
            Assert.Equal(description, unusedPool.Description);
            Assert.Equal(resultInfo, unusedPool.ResultInfo);
            Assert.Null(Record.Exception(unusedPool.Dispose));
        }

        Assert.Equal(DeviceStatus.Active, device.Status);
        device.Dispose();
        backend.Dispose();
        Assert.Null(diagnostics.TeardownFailure);
        Assert.DoesNotContain(messages, static message => message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Recorded_queries_remain_resolvable_after_pool_disposal()
    {
        List<ValidationMessage> messages = [];
        using ValidationLayer backend = CreateLayer(messages);
        using Device device = CreateDevice(backend, out D3D12Diagnostics diagnostics);
        {
            using QueryPool pool = backend.CreateQueryPool(
                device,
                new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1));
            using Buffer destination = backend.CreateBuffer(
                device,
                new BufferDesc(pool.ResultInfo.ResultStride,
                    BufferUsages.QueryResolve | BufferUsages.CopyDestination),
                MemoryType.Readback);
            using CommandContext context = backend.CreateCommandContext(
                device,
                new CommandContextDesc(QueueType.Graphics, 0, 2));
            Queue queue = backend.GetQueue(device, QueueType.Graphics);
            BufferRange range = new(0, pool.ResultInfo.ResultStride);

            backend.Begin(context);
            backend.ResetQueries(context, pool, 0, 1);
            backend.WriteTimestamp(context, pool, 0);
            backend.ResolveQueries(context, pool, 0, 1, destination, range);
            using (RecordedCommands initialCommands = backend.End(context))
            {
                QueueCompletion completion = backend.Submit(
                    queue, new QueueSubmitDesc([], [], [initialCommands], [], []));
                Assert.Equal(WaitStatus.Completed,
                    backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                backend.CollectCompleted(device);
            }

            // Both recordings capture the query payload while the pool is still live.
            backend.Begin(context);
            backend.ResetQueries(context, pool, 0, 1);
            backend.WriteTimestamp(context, pool, 0);
            using RecordedCommands writeCommands = backend.End(context);

            backend.Begin(context);
            backend.ResolveQueries(context, pool, 0, 1, destination, range);
            using RecordedCommands resolveCommands = backend.End(context);

            pool.Dispose();
            Assert.Equal(RecordedCommandsStatus.Executable, writeCommands.Status);
            Assert.Equal(RecordedCommandsStatus.Executable, resolveCommands.Status);

            QueueCompletion writeCompletion = backend.Submit(
                queue, new QueueSubmitDesc([], [], [writeCommands], [], []));
            Assert.Equal(WaitStatus.Completed,
                backend.WaitCpu(writeCompletion, TimeSpan.FromSeconds(10)));
            backend.CollectCompleted(device);
            writeCommands.Dispose();

            // The resolution recording retains the payload after the write is retired.
            Assert.Null(Record.Exception(pool.Dispose));
            Assert.Equal(RecordedCommandsStatus.Executable, resolveCommands.Status);
            QueueCompletion resolveCompletion = backend.Submit(
                queue, new QueueSubmitDesc([], [], [resolveCommands], [], []));
            Assert.Equal(WaitStatus.Completed,
                backend.WaitCpu(resolveCompletion, TimeSpan.FromSeconds(10)));
            backend.CollectCompleted(device);
        }

        Assert.Equal(DeviceStatus.Active, device.Status);
        device.Dispose();
        backend.Dispose();
        Assert.Null(diagnostics.TeardownFailure);
        Assert.DoesNotContain(messages, static message => message.Type == ValidationMessageType.Error);
    }

    private static ValidationLayer CreateLayer(List<ValidationMessage> messages)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("BLOCKED_NATIVE_VALIDATION: D3D12 WARP requires Windows.");

        return new ValidationLayer(
            D3D12GraphicsBackend.Create(),
            new ValidationOptions(MessageSink: new DelegateValidationMessageSink(messages.Add)));
    }

    private Device CreateDevice(ValidationLayer backend, out D3D12Diagnostics diagnostics)
    {
        AdapterInfo adapter = D3D12TestSupport.SelectWarp(backend);
        Device device = backend.CreateDevice(new DeviceDesc(
            adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics)],
            label: "query pool disposal WARP device"));
        try
        {
            if (!backend.TryGetCapability(device, out D3D12Diagnostics? available) || available is null)
            {
                throw new NotSupportedException(
                    "BLOCKED_NATIVE_VALIDATION: public D3D12 diagnostics are unavailable.");
            }

            diagnostics = available;
            output.WriteLine(
                "D3D12 diagnostics: DebugLayer={0}; GBV={1}; SynchronizedQueueValidation={2}; DRED={3}",
                diagnostics.DebugLayerEnabled,
                diagnostics.GpuBasedValidationEnabled,
                diagnostics.SynchronizedQueueValidationEnabled,
                diagnostics.DredEnabled);
            if (!diagnostics.DebugLayerEnabled ||
                !diagnostics.GpuBasedValidationEnabled ||
                !diagnostics.SynchronizedQueueValidationEnabled)
            {
                throw new NotSupportedException(
                    "BLOCKED_NATIVE_VALIDATION: debug layer, GBV and synchronized queue validation are required.");
            }

            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }
}
