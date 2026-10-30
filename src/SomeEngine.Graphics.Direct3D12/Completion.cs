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
        if (queue.IsComplete(completion.Value))
            return WaitStatus.Completed;

        nint waitEvent = queue.RentCpuWaitEvent();
        bool reusable = false;
        try
        {
            ThrowIfFailed(
                queue.NativeDevice,
                    ResourceTrace.SetEventOnCompletion(queue.Fence, completion.Value, (void*)waitEvent),
                NativeOperationType.Ordinary,
                "ID3D12Fence::SetEventOnCompletion");

            uint result;
            using (global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.CpuFenceWait))
                result = SilkMarshal.WaitWindowsObjects(
                    waitEvent,
                    milliseconds == Timeout.Infinite ? uint.MaxValue : checked((uint)milliseconds));
            WaitStatus status = result switch
            {
                0 => queue.IsComplete(completion.Value)
                    ? WaitStatus.Completed
                    : throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        "The Direct3D 12 completion event was signaled before its fence value."),
                0x102 => WaitStatus.Timeout,
                _ => ThrowWaitFailure(queue.NativeDevice),
            };
            reusable = status == WaitStatus.Completed;
            return status;
        }
        finally
        {
            if (reusable) queue.ReturnCpuWaitEvent(waitEvent);
            else CloseCpuWaitEvent(waitEvent);
        }
    }

    private static void CloseCpuWaitEvent(nint waitEvent)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.CloseWaitEvent);
        _ = SilkMarshal.CloseWindowsHandle(waitEvent);
    }

    private sealed partial class D3D12Queue
    {
        private static readonly nint ClosedCpuWaitEvent = -1;
        private nint _cpuWaitEvent;

        internal nint RentCpuWaitEvent()
        {
            while (true)
            {
                nint available = Volatile.Read(ref _cpuWaitEvent);
                if (available == ClosedCpuWaitEvent)
                    throw new ObjectDisposedException(nameof(D3D12Queue));
                if (available == 0) break;
                if (Interlocked.CompareExchange(ref _cpuWaitEvent, 0, available) == available)
                    return available;
            }

            nint waitEvent;
            using (global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.CreateWaitEvent))
                waitEvent = SilkMarshal.CreateWindowsEvent(null, bManualReset: false, bInitialState: false, null);
            if (waitEvent == 0)
            {
                ThrowAfterDeviceRemovedReasonQuery(
                    NativeDevice,
                    Marshal.GetHRForLastWin32Error(),
                    "Creating the Direct3D 12 completion wait event");
            }
            return waitEvent;
        }

        internal void ReturnCpuWaitEvent(nint waitEvent)
        {
            // Only a successful wait has consumed the auto-reset signal. Pending
            // timed-out registrations must never enter this single idle slot.
            if (Interlocked.CompareExchange(ref _cpuWaitEvent, waitEvent, 0) != 0)
                CloseCpuWaitEvent(waitEvent);
        }

        internal void ReleaseCpuWaitEvent()
        {
            // A concurrent waiter owns its rented handle and closes it on return.
            nint waitEvent = Interlocked.Exchange(ref _cpuWaitEvent, ClosedCpuWaitEvent);
            if (waitEvent != 0 && waitEvent != ClosedCpuWaitEvent)
                CloseCpuWaitEvent(waitEvent);
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
