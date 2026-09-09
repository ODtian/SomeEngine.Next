using System.Runtime.CompilerServices;
using SomeEngine.Graphics;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Native argument record sizes shared by graph ranges and RHI command layouts.</summary>
internal static class ClusterIndirectAbi
{
    internal const uint HardwareRasterStride = 32;

    internal static readonly uint DispatchStride =
        checked((uint)Unsafe.SizeOf<DispatchArguments>());

    internal static readonly uint DrawStride =
        checked((uint)Unsafe.SizeOf<DrawArguments>());

    internal static readonly uint DrawIndexedStride =
        checked((uint)Unsafe.SizeOf<DrawIndexedArguments>());

    internal static ulong DrawIndexedBytes => DrawIndexedStride;

    internal static ulong DispatchBytes => DispatchStride;

    internal static ulong DrawBytes => DrawStride;
}
