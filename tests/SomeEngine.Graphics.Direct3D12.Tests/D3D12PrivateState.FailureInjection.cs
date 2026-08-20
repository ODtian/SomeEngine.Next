using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Direct3D12;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12.Tests;

internal static unsafe partial class D3D12PrivateState
{
    internal static void MarkSoftwareLost(Device device)
    {
        var loss = new GraphicsException(
            GraphicsError.DeviceLost,
            "Test-owned software terminal transition.");
        _ = Invoke(device, "MarkLost", loss);
    }

    internal static void ConfirmNativeDeviceLoss(Device device) =>
        _ = Invoke(device, "ConfirmNativeDeviceLoss");

    internal static bool NativeDeviceLossConfirmed(Device device) =>
        (bool)GetProperty(device, "NativeDeviceLossConfirmed").GetValue(device)!;

    internal static void RegisterDeviceChild(Device device, GraphicsObject child) =>
        _ = Invoke(device, "RegisterChild", child);

    internal static void UnregisterDeviceChild(Device device, GraphicsObject child) =>
        _ = Invoke(device, "UnregisterChild", child);

    internal static bool IsRuntimeQuarantined(D3D12Backend backend)
    {
        for (D3D12Backend? current = RuntimeQuarantineHead();
             current is not null;
             current = RuntimeQuarantineNext(current))
        {
            if (ReferenceEquals(current, backend))
                return true;
        }
        return false;
    }

    internal static D3D12Backend? RuntimeQuarantineHead() =>
        (D3D12Backend?)GetField(typeof(D3D12Backend), "s_runtimeQuarantineHead")
            .GetValue(null);

    internal static D3D12Backend? RuntimeQuarantineNext(D3D12Backend backend) =>
        (D3D12Backend?)GetField(backend, "_runtimeQuarantineNext").GetValue(backend);

    internal static IDisposable ReplaceFenceWithSetEventFailure(Queue queue)
    {
        PropertyInfo property = GetProperty(queue, "Fence");
        object original = property.GetValue(queue)!;
        property.SetValue(
            queue,
            Pointer.Box(FailingSetEventFence.Pointer, typeof(ID3D12Fence*)));
        return new PropertyRestore(queue, property, original);
    }

    internal static void InvokeResultAuthority(
        Device? device,
        int result,
        bool pipelineCreation)
    {
        Type operationType = typeof(D3D12Backend).GetNestedType(
            "NativeOperationType",
            BindingFlags.NonPublic)!;
        _ = InvokeStatic(
            "ThrowIfFailed",
            device,
            result,
            Enum.Parse(operationType, pipelineCreation ? "PipelineCreation" : "Ordinary"),
            "test native operation",
            null);
    }

    internal static void InvokeQueriedResultAuthority(
        Device device,
        int nativeCode) =>
        _ = InvokeStatic(
            "ThrowAfterDeviceRemovedReasonQuery",
            device,
            nativeCode,
            "test queried native operation");

    private sealed class PropertyRestore : IDisposable
    {
        private object? _receiver;
        private PropertyInfo? _property;
        private object? _value;

        internal PropertyRestore(object receiver, PropertyInfo property, object value)
        {
            _receiver = receiver;
            _property = property;
            _value = value;
        }

        public void Dispose()
        {
            object? receiver = Interlocked.Exchange(ref _receiver, null);
            PropertyInfo? property = Interlocked.Exchange(ref _property, null);
            object? value = Interlocked.Exchange(ref _value, null);
            if (receiver is not null && property is not null)
                property.SetValue(receiver, value);
        }
    }

    private static class FailingSetEventFence
    {
        private static readonly nint Instance = Create();

        internal static void* Pointer => (void*)Instance;

        private static nint Create()
        {
            nint* vtable = (nint*)NativeMemory.AllocZeroed((nuint)(11 * sizeof(nint)));
            nint* instance = (nint*)NativeMemory.AllocZeroed((nuint)sizeof(nint));
            vtable[8] = (nint)(delegate* unmanaged[Stdcall]<void*, ulong>)&GetCompletedValue;
            vtable[9] = (nint)(delegate* unmanaged[Stdcall]<void*, ulong, void*, int>)&SetEventOnCompletion;
            instance[0] = (nint)vtable;
            return (nint)instance;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static ulong GetCompletedValue(void* _) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetEventOnCompletion(void* _, ulong value, void* waitEvent) =>
            unchecked((int)0x80004005);
    }
}
