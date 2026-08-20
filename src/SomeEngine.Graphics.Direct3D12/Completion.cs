using System.Runtime.InteropServices;
using Silk.NET.Core.Native;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public bool IsComplete(in QueueCompletion completion)
    {
        D3D12Queue queue = RequireQueue(completion.Queue, nameof(completion));
        return queue.IsComplete(completion.Value);
    }

    public WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout)
    {
        int milliseconds = Timeouts.ToMilliseconds(timeout, nameof(timeout));
        D3D12Queue queue = RequireQueue(completion.Queue, nameof(completion));
        if (queue.IsComplete(completion.Value))
            return WaitStatus.Completed;

        nint waitEvent = SilkMarshal.CreateWindowsEvent(
            null,
            bManualReset: false,
            bInitialState: false,
            null);
        if (waitEvent == 0)
        {
            ThrowAfterDeviceRemovedReasonQuery(
                queue.NativeDevice,
                Marshal.GetHRForLastWin32Error(),
                "Creating the Direct3D 12 completion wait event");
        }
        try
        {
            ThrowIfFailed(
                queue.NativeDevice,
                queue.Fence->SetEventOnCompletion(completion.Value, (void*)waitEvent),
                NativeOperationType.Ordinary,
                "ID3D12Fence::SetEventOnCompletion");

            uint result = SilkMarshal.WaitWindowsObjects(
                waitEvent,
                milliseconds == Timeout.Infinite
                    ? uint.MaxValue
                    : checked((uint)milliseconds));
            return result switch
            {
                0 => queue.IsComplete(completion.Value)
                    ? WaitStatus.Completed
                    : throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        "The Direct3D 12 completion event was signaled before its fence value."),
                0x102 => WaitStatus.Timeout,
                _ => ThrowWaitFailure(queue.NativeDevice),
            };
        }
        finally
        {
            _ = SilkMarshal.CloseWindowsHandle(waitEvent);
        }
    }

    private static WaitStatus ThrowWaitFailure(D3D12Device device)
    {
        ThrowAfterDeviceRemovedReasonQuery(
            device,
            Marshal.GetHRForLastWin32Error(),
            "Waiting for a Direct3D 12 completion");
        return default;
    }
}
