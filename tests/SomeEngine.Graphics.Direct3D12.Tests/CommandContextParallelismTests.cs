using System.Buffers.Binary;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class CommandContextParallelismTests
{
    [Fact]
    public void Warp_records_parallel_contexts_and_submits_in_declared_order()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "The required Direct3D12/WARP parallel-recording lane must run; it may not silently skip.");

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        BufferHandle firstUpload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle secondUpload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle destination = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource | BufferUsage.CopyDestination));
        BufferHandle readback = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(value, 11);
        device.WriteBuffer(firstUpload, 0, value);
        BinaryPrimitives.WriteUInt32LittleEndian(value, 29);
        device.WriteBuffer(secondUpload, 0, value);

        ICommandContext firstContext = device.AcquireCommandContext(QueueType.Copy, "parallel-recording.first");
        ICommandContext secondContext = device.AcquireCommandContext(QueueType.Copy, "parallel-recording.second");
        using Barrier start = new(2);
        Task<(CommandListHandle Handle, int Thread)> firstTask = Task.Run(() => Record(firstContext, firstUpload));
        Task<(CommandListHandle Handle, int Thread)> secondTask = Task.Run(() => Record(secondContext, secondUpload));
#pragma warning disable xUnit1031 // The native device's coordinator thread must not migrate across an await.
        Task.WaitAll(firstTask, secondTask);
        (CommandListHandle Handle, int Thread) first = firstTask.Result;
        (CommandListHandle Handle, int Thread) second = secondTask.Result;
#pragma warning restore xUnit1031
        Assert.NotEqual(first.Thread, second.Thread);

        GpuCompletion writes = device.Submit(
            QueueType.Copy,
            [first.Handle, second.Handle]);
        Assert.True(device.Wait(writes, TimeSpan.FromSeconds(10)));

        using (ICommandContext copyBack = device.AcquireCommandContext(QueueType.Copy, "parallel-recording.readback"))
        {
            copyBack.CopyBuffer(destination, 0, readback, 0, 4);
            GpuCompletion copied = device.Submit(QueueType.Copy, [copyBack.Finish()]);
            Assert.True(device.Wait(copied, TimeSpan.FromSeconds(10)));
        }

        Span<byte> result = stackalloc byte[4];
        device.ReadBuffer(readback, 0, result);
        Assert.Equal(29u, BinaryPrimitives.ReadUInt32LittleEndian(result));
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

        (CommandListHandle Handle, int Thread) Record(ICommandContext commands, BufferHandle source)
        {
            using (commands)
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
                int thread = Environment.CurrentManagedThreadId;
                commands.CopyBuffer(source, 0, destination, 0, 4);
                return (commands.Finish(), thread);
            }
        }
    }
}
