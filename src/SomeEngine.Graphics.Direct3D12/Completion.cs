using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public bool IsComplete(in QueueCompletion completion)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12Queue queue = RequireQueue(completion.Queue, nameof(completion));
        return queue.IsComplete(completion.Value);
    }

    public WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiWaitAndCollect);
        int milliseconds = Timeouts.ToMilliseconds(timeout, nameof(timeout));
        D3D12Queue queue = RequireQueue(completion.Queue, nameof(completion));
        long started = Stopwatch.GetTimestamp();
        if (queue.IsComplete(completion.Value))
            return WaitStatus.Completed;
        if (milliseconds == 0)
            return WaitStatus.Timeout;

        using var waitTrace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.CpuFenceWait);
        // Poll for up to 1 ms within the same timeout budget as the event wait.
        long deadline = milliseconds == Timeout.Infinite ? long.MaxValue
            : checked(started + (long)Math.Ceiling(milliseconds * (double)Stopwatch.Frequency / 1000d));
        long spinDeadline = Math.Min(deadline, started + Stopwatch.Frequency / 1000);
        while (Stopwatch.GetTimestamp() < spinDeadline)
        {
            Thread.SpinWait(64);
            if (queue.IsComplete(completion.Value))
                return WaitStatus.Completed;
        }
        if (Stopwatch.GetTimestamp() >= deadline)
            return WaitStatus.Timeout;

        nint waitEvent;
        using (global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.CreateWaitEvent))
            waitEvent = SilkMarshal.CreateWindowsEvent(null, bManualReset: false, bInitialState: false, null);
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
                    ResourceTrace.SetEventOnCompletion(queue.Fence, completion.Value, (void*)waitEvent),
                NativeOperationType.Ordinary,
                "ID3D12Fence::SetEventOnCompletion");

            if (milliseconds != Timeout.Infinite)
                milliseconds = (int)Math.Max(0, Math.Ceiling(
                    (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency));
            uint result = SilkMarshal.WaitWindowsObjects(
                waitEvent,
                milliseconds == Timeout.Infinite ? uint.MaxValue : checked((uint)milliseconds));
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
            using var closeTrace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.CloseWaitEvent);
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
