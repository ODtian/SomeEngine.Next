using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class CommandContextParallelismTests
{
    [Fact]
    public void Independent_contexts_keep_single_use_thread_ownership()
    {
        using var device = new Device();
        ICommandContext first = device.AcquireCommandContext(QueueType.Graphics, "worker-a");
        ICommandContext second = device.AcquireCommandContext(QueueType.Graphics, "worker-b");

        CommandListHandle[] lists = new CommandListHandle[2];
        Exception?[] failures = new Exception?[2];
        Thread firstThread = StartRecordingThread(first, "a", lists, failures, 0);
        Thread secondThread = StartRecordingThread(second, "b", lists, failures, 1);
        Assert.True(firstThread.Join(TimeSpan.FromSeconds(5)));
        Assert.True(secondThread.Join(TimeSpan.FromSeconds(5)));
        Assert.All(failures, static failure => Assert.Null(failure));

        Assert.All(lists, static list => Assert.True(list.IsValid));
        GpuCompletion completion = device.Submit(QueueType.Graphics, lists);
        Assert.True(device.Wait(completion, TimeSpan.Zero));
        Assert.Equal(2, device.Statistics.CommandContextAcquires);
        Assert.Equal(2, device.Statistics.SubmittedCommandLists);

        ICommandContext owned = device.AcquireCommandContext(QueueType.Graphics);
        using var recorded = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? workerFailure = null;
        var ownerThread = new Thread(() =>
        {
            try
            {
                owned.PushDebugGroup("worker-owned");
                recorded.Set();
                release.Wait();
                owned.Dispose();
            }
            catch (Exception error)
            {
                workerFailure = error;
                recorded.Set();
            }
        });
        ownerThread.Start();
        Assert.True(recorded.Wait(TimeSpan.FromSeconds(5)));
        Assert.Throws<InvalidOperationException>(() => owned.PopDebugGroup());
        release.Set();
        Assert.True(ownerThread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(workerFailure);
    }

    private static CommandListHandle FinishOnOwningThread(ICommandContext context, string marker)
    {
        using (context)
        {
            context.PushDebugGroup(marker);
            context.PopDebugGroup();
            return context.Finish();
        }
    }

    private static Thread StartRecordingThread(
        ICommandContext context,
        string marker,
        CommandListHandle[] lists,
        Exception?[] failures,
        int index)
    {
        var thread = new Thread(() =>
        {
            try
            {
                lists[index] = FinishOnOwningThread(context, marker);
            }
            catch (Exception error)
            {
                failures[index] = error;
            }
        });
        thread.Start();
        return thread;
    }
}
