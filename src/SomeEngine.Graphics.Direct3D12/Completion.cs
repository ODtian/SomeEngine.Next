using System.Runtime.InteropServices;
using Silk.NET.Core.Native;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public bool IsComplete(in QueueCompletion completion)
    {
        D3D12Queue queue = NativeCast.Queue(completion.Queue);
        return queue.IsComplete(completion.Value);
    }

    public WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout)
    {
        ValidateWaitTimeout(timeout);
        D3D12Queue queue = NativeCast.Queue(completion.Queue);
        if (queue.IsComplete(completion.Value))
            return WaitStatus.Completed;

        nint waitEvent = SilkMarshal.CreateWindowsEvent(
            null,
            bManualReset: false,
            bInitialState: false,
            null);
        try
        {
            ThrowIfDeviceFailed(
                queue.NativeDevice,
                queue.Fence->SetEventOnCompletion(completion.Value, (void*)waitEvent),
                "ID3D12Fence::SetEventOnCompletion");

            uint milliseconds = timeout == Timeout.InfiniteTimeSpan
                ? uint.MaxValue
                : checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
            uint result = SilkMarshal.WaitWindowsObjects(waitEvent, milliseconds);
            return result switch
            {
                0 => queue.IsComplete(completion.Value)
                    ? WaitStatus.Completed
                    : throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        "The Direct3D 12 completion event was signaled before its fence value."),
                0x102 => WaitStatus.Timeout,
                _ => throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "Waiting for a Direct3D 12 completion failed.",
                    Marshal.GetHRForLastWin32Error()),
            };
        }
        finally
        {
            _ = SilkMarshal.CloseWindowsHandle(waitEvent);
        }
    }

    private static void ValidateWaitTimeout(TimeSpan timeout)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return;
        if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "A wait timeout must be nonnegative, at most Int32.MaxValue milliseconds, or infinite.");
        }
    }
}
