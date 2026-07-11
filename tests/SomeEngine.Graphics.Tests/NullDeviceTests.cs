using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class NullDeviceTests
{
    [Fact]
    public void Completion_set_is_immutable_queue_normalized_and_device_scoped()
    {
        using Device first = new(new Options { AutoCompleteSubmissions = false });
        using Device second = new();
        GpuCompletion firstValue = SubmitEmpty(first, QueueType.Copy);
        GpuCompletion secondValue = SubmitEmpty(first, QueueType.Copy);

        GpuCompletionSet set = new([secondValue, firstValue, secondValue]);

        GpuCompletion normalized = Assert.Single(set.Completions);
        Assert.Equal(first.Domain, set.Domain);
        Assert.Equal(secondValue, normalized);
        GpuCompletion[] detached = set.ToArray();
        detached[0] = default;
        Assert.Equal(secondValue, Assert.Single(set.Completions));

        GpuCompletion foreign = SubmitEmpty(second, QueueType.Copy);
        Assert.Throws<ArgumentException>(() => new GpuCompletionSet([secondValue, foreign]));
    }

    [Fact]
    public void Handles_and_completions_are_scoped_to_their_device()
    {
        using Device first = new();
        using Device second = new();
        Assert.NotEqual(first.Domain, second.Domain);

        BufferHandle firstBuffer = first.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopySource),
            MemoryType.Upload);
        Assert.Throws<ArgumentException>(() => second.WriteBuffer(firstBuffer, 0, [1]));

        CommandListHandle foreignList;
        using (ICommandContext commands = first.AcquireCommandContext(QueueType.Copy))
        {
            foreignList = commands.Finish();
        }
        Assert.Throws<ArgumentException>(() => second.DiscardCommandList(foreignList));
        first.DiscardCommandList(foreignList);

        GpuCompletion firstCompletion;
        using (ICommandContext commands = first.AcquireCommandContext(QueueType.Copy))
        {
            firstCompletion = first.Submit(QueueType.Copy, [commands.Finish()]);
        }

        CommandListHandle secondList;
        using (ICommandContext commands = second.AcquireCommandContext(QueueType.Copy))
        {
            secondList = commands.Finish();
        }
        GpuCompletion notYetPublished = new(second.Domain, QueueType.Copy, 1);
        Assert.Throws<ArgumentException>(() =>
            second.Submit(QueueType.Copy, [secondList], [notYetPublished]));
        Assert.Throws<ArgumentException>(() =>
            second.Submit(QueueType.Copy, [secondList], [firstCompletion]));

        GpuCompletion secondCompletion = second.Submit(QueueType.Copy, [secondList]);
        GpuCompletion unpublished = new(second.Domain, QueueType.Copy, checked(secondCompletion.Value + 1));
        Assert.Throws<ArgumentException>(() => second.Wait(unpublished, TimeSpan.Zero));

        ulong completedValue = 0;
        bool waited = false;
        Exception? workerFailure = null;
        Thread worker = new(() =>
        {
            try
            {
                completedValue = second.GetCompletedValue(QueueType.Copy);
                waited = second.Wait(secondCompletion, TimeSpan.FromSeconds(1));
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
        });
        worker.Start();
        worker.Join();
        Assert.Null(workerFailure);
        Assert.Equal(secondCompletion.Value, completedValue);
        Assert.True(waited);

        first.DestroyBuffer(firstBuffer);
    }

    [Fact]
    public void Generation_changes_when_a_resource_slot_is_reused()
    {
        using Device device = new();
        BufferHandle first = device.CreateBuffer(new BufferDesc(32, BufferUsage.CopySource), MemoryType.Upload);
        device.DestroyBuffer(first);
        device.CollectGarbage();
        BufferHandle second = device.CreateBuffer(new BufferDesc(32, BufferUsage.CopySource), MemoryType.Upload);

        Assert.Equal(first.Slot, second.Slot);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.ThrowsAny<Exception>(() => device.WriteBuffer(first, 0, [1]));
        device.DestroyBuffer(second);
    }

    [Fact]
    public void Copy_submission_and_discard_have_distinct_lifetimes()
    {
        using Device device = new();
        BufferHandle source = device.CreateBuffer(new BufferDesc(16, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle destination = device.CreateBuffer(new BufferDesc(16, BufferUsage.CopyDestination), MemoryType.Readback);
        byte[] expected = Enumerable.Range(0, 16).Select(static value => checked((byte)(value * 3))).ToArray();
        device.WriteBuffer(source, 0, expected);

        using (ICommandContext discarded = device.AcquireCommandContext(QueueType.Copy))
        {
            discarded.CopyBuffer(source, 0, destination, 0, 16);
            device.DiscardCommandList(discarded.Finish());
        }

        byte[] untouched = new byte[16];
        device.ReadBuffer(destination, 0, untouched);
        Assert.All(untouched, static value => Assert.Equal(0, value));

        using (ICommandContext submitted = device.AcquireCommandContext(QueueType.Copy))
        {
            submitted.CopyBuffer(source, 0, destination, 0, 16);
            GpuCompletion completion = device.Submit(QueueType.Copy, [submitted.Finish()]);
            Assert.True(device.Wait(completion, TimeSpan.FromSeconds(1)));
        }

        byte[] actual = new byte[16];
        device.ReadBuffer(destination, 0, actual);
        Assert.Equal(expected, actual);
        device.DestroyBuffer(destination);
        device.DestroyBuffer(source);
    }

    [Fact]
    public void Finished_command_list_keeps_resources_alive_until_exact_completion()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        BufferHandle source = device.CreateBuffer(new BufferDesc(16, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle destination = device.CreateBuffer(new BufferDesc(16, BufferUsage.CopyDestination), MemoryType.Readback);
        using ICommandContext commands = device.AcquireCommandContext(QueueType.Copy);
        commands.CopyBuffer(source, 0, destination, 0, 16);
        GpuCompletion completion = device.Submit(QueueType.Copy, [commands.Finish()]);

        device.DestroyBuffer(destination);
        device.DestroyBuffer(source);
        Assert.Equal(0, device.CollectGarbage());
        device.AdvanceCompletion(completion);
        Assert.True(device.CollectGarbage() >= 2);
    }

    [Fact]
    public void Placed_child_and_heap_can_retire_together_after_their_exact_completion()
    {
        using Device device = new(new Options { AutoCompleteSubmissions = false });
        BufferDesc placedDesc = new(16, BufferUsage.CopySource);
        ResourceRequirements requirements = device.GetBufferRequirements(placedDesc, MemoryType.DeviceLocal);
        HeapHandle heap = device.CreateHeap(new HeapDesc(
            requirements.Size,
            MemoryType.DeviceLocal,
            requirements.ResourceClass));
        BufferHandle placed = device.CreatePlacedBuffer(heap, 0, placedDesc);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopyDestination),
            MemoryType.Readback);

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Copy);
        commands.Barriers([
            ResourceBarrier.Transition(placed.Resource, ResourceState.Common, ResourceState.CopySource),
        ]);
        commands.CopyBuffer(placed, 0, readback, 0, 16);
        GpuCompletion completion = device.Submit(QueueType.Copy, [commands.Finish()]);

        device.DestroyBuffer(placed);
        device.DestroyHeap(heap);
        Assert.Equal(0, device.CollectGarbage());

        device.AdvanceCompletion(completion);
        Assert.True(device.CollectGarbage() >= 3);
        device.DestroyBuffer(readback);
    }

    private static GpuCompletion SubmitEmpty(Device device, QueueType queue)
    {
        using ICommandContext commands = device.AcquireCommandContext(queue);
        return device.Submit(queue, [commands.Finish()]);
    }
}
