using System.Runtime.InteropServices;

namespace SomeEngine.Animation.Pose;

/// <summary>Samples a ready clip uniformly and compresses its local tracks with ACL.</summary>
public static unsafe class AclClipCompressor
{
    public static AclClipData Compress(AnimationClip source, float sampleRate = 60,
        float precision = .001f, float shellDistance = 1)
    {
        int intervals = (int)MathF.Ceiling(source.Duration * sampleRate);
        int sampleCount = intervals + 1;
        float actualRate = intervals == 0 ? sampleRate : intervals / source.Duration;
        int boneCount = source.Skeleton.BoneCount;
        var samples = new BoneTransform[sampleCount * boneCount];
        for (int sample = 0; sample < sampleCount; sample++)
            source.SampleLocal(sample / actualRate, samples.AsSpan(sample * boneCount, boneCount));

        byte* error = stackalloc byte[256];
        fixed (BoneTransform* poses = samples)
        fixed (short* parents = source.Skeleton.Parents)
        {
            if (AclNative.Compress(poses, (uint)boneCount, (uint)sampleCount, actualRate, parents,
                precision, shellDistance, out nint bytes, out uint byteCount, error, 256) != 0)
                throw new InvalidDataException(Marshal.PtrToStringUTF8((nint)error));
            try
            {
                return new(new ReadOnlySpan<byte>((void*)bytes, (int)byteCount), source.Duration,
                    [.. source.Events], source.RootMotionBone);
            }
            finally { AclNative.Free(bytes); }
        }
    }
}
