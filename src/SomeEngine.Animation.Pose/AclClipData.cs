using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace SomeEngine.Animation.Pose;

/// <summary>Aligned ACL tracks and clip metadata held by ordinary shared references.</summary>
public sealed unsafe class AclClipData
{
    private readonly byte[] _storage;
    private readonly int _offset;
    private readonly int _length;

    public float Duration { get; }
    public int BoneCount { get; }
    public float SampleRate { get; }
    public ImmutableArray<ClipEvent> Events { get; }
    public short RootMotionBone { get; }
    public ReadOnlySpan<byte> Bytes => _storage.AsSpan(_offset, _length);

    public AclClipData(ReadOnlySpan<byte> bytes, float duration,
        ImmutableArray<ClipEvent> events = default, short rootMotionBone = -1)
    {
        _storage = GC.AllocateUninitializedArray<byte>(bytes.Length + 15, pinned: true);
        _length = bytes.Length;
        fixed (byte* storage = _storage)
            _offset = (int)((16 - ((nuint)storage & 15)) & 15);
        bytes.CopyTo(_storage.AsSpan(_offset));
        byte* error = stackalloc byte[256];
        fixed (byte* data = Bytes)
        {
            if (AclNative.ReadInfo(data, (uint)bytes.Length, out uint boneCount, out float sampleRate, error, 256) != 0)
                throw new InvalidDataException(Marshal.PtrToStringUTF8((nint)error));
            BoneCount = (int)boneCount;
            SampleRate = sampleRate;
        }
        Duration = duration;
        Events = events.IsDefault ? [] : events;
        RootMotionBone = rootMotionBone;
    }
}

public readonly unsafe struct AclClipSampler : IClipSampler<AclClipData>
{
    public static void SampleLocal(AclClipData data, float time, Span<BoneTransform> destination)
    {
        fixed (byte* bytes = data.Bytes)
        fixed (BoneTransform* output = destination)
            AclNative.Sample(bytes, &time, 1, null, 0, output, data.BoneCount);
    }

    public static void SampleLocalBatch<TSampler>(AclClipData data, ref ClipSamples samples)
        where TSampler : struct, IClipSampler<AclClipData>
    {
        fixed (byte* bytes = data.Bytes)
        fixed (float* times = samples.Times)
        fixed (short* bones = samples.Bones)
        fixed (BoneTransform* output = samples.LocalPoses)
            AclNative.Sample(bytes, times, samples.Count, bones,
                samples.Bones.Length == data.BoneCount ? 0 : samples.Bones.Length,
                output, samples.BoneCount);
    }
}
