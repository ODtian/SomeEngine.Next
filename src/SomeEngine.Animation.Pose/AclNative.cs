using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Animation.Pose;

internal static unsafe partial class AclNative
{
    [LibraryImport("SomeEngineAcl", EntryPoint = "se_acl_compress")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Compress(BoneTransform* samples, uint boneCount, uint sampleCount,
        float sampleRate, short* parents, float precision, float shellDistance,
        out nint output, out uint outputSize, byte* error, int errorCapacity);

    [LibraryImport("SomeEngineAcl", EntryPoint = "se_acl_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Free(nint data);

    [LibraryImport("SomeEngineAcl", EntryPoint = "se_acl_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReadInfo(byte* data, uint size, out uint boneCount,
        out float sampleRate, byte* error, int errorCapacity);

    [LibraryImport("SomeEngineAcl", EntryPoint = "se_acl_sample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Sample(byte* data, float* times, int sampleCount,
        short* bones, int boneCount, BoneTransform* output, int rowStride);
}
