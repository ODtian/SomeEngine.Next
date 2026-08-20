using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Graphics.Benchmarks;

internal sealed unsafe class RepresentativeWorkerSignal : IDisposable
{
    private const uint WaitObject0 = 0;
    private const uint Infinite = uint.MaxValue;

    private static readonly nint Kernel32 = NativeLibrary.Load("kernel32.dll");
    private static readonly delegate* unmanaged[Stdcall]<void*, int, int, char*, nint>
        CreateEventW = (delegate* unmanaged[Stdcall]<void*, int, int, char*, nint>)
            NativeLibrary.GetExport(Kernel32, nameof(CreateEventW));
    private static readonly delegate* unmanaged[Stdcall, SuppressGCTransition]<nint, int>
        SetEvent = (delegate* unmanaged[Stdcall, SuppressGCTransition]<nint, int>)
            NativeLibrary.GetExport(Kernel32, nameof(SetEvent));
    private static readonly delegate* unmanaged[Stdcall]<nint, uint, uint>
        WaitForSingleObject = (delegate* unmanaged[Stdcall]<nint, uint, uint>)
            NativeLibrary.GetExport(Kernel32, nameof(WaitForSingleObject));
    private static readonly delegate* unmanaged[Stdcall, SuppressGCTransition]<nint, int>
        CloseHandle = (delegate* unmanaged[Stdcall, SuppressGCTransition]<nint, int>)
            NativeLibrary.GetExport(Kernel32, nameof(CloseHandle));

    private nint _handle;

    internal RepresentativeWorkerSignal()
    {
        _handle = CreateEventW(null, 0, 0, null);
        if (_handle == 0)
            ThrowNativeFailure("CreateEventW");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Signal()
    {
        nint handle = _handle;
        if (handle == 0 || SetEvent(handle) == 0)
            ThrowNativeFailure("SetEvent");
    }

    internal void Wait()
    {
        nint handle = _handle;
        if (handle == 0 || WaitForSingleObject(handle, Infinite) != WaitObject0)
            ThrowNativeFailure("WaitForSingleObject");
    }

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0 && CloseHandle(handle) == 0)
            ThrowNativeFailure("CloseHandle");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNativeFailure(string operation) =>
        throw new InvalidOperationException($"{operation} failed for a benchmark worker signal.");
}
