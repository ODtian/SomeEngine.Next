using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed unsafe class WarpNativeAccessTests
{
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
}
