using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>A rigid root transform at a source time in seconds.</summary>
public readonly record struct RootMotionKey(float Time, Vector3 Translation, Quaternion Rotation)
{
    public Matrix4x4 ToMatrix() => Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Translation);
}

/// <summary>Shared root keys with read-only sampling relative to the first transform.</summary>
public sealed class RootMotionPath
{
    private readonly RootMotionKey[] _keys;
    private readonly Matrix4x4 _inverseInitial;

    public ReadOnlySpan<RootMotionKey> Keys => _keys;
    public float Duration => _keys[^1].Time;

    public RootMotionPath(ReadOnlySpan<RootMotionKey> keys)
    {
        _keys = keys.ToArray();
        Matrix4x4.Invert(_keys[0].ToMatrix(), out _inverseInitial);
    }

    public Matrix4x4 Sample(float time)
    {
        if (time <= _keys[0].Time) return Matrix4x4.Identity;
        if (time >= Duration) return _inverseInitial * _keys[^1].ToMatrix();
        int first = 0, last = _keys.Length - 1;
        while (last - first > 1)
        {
            int middle = (first + last) / 2;
            if (_keys[middle].Time <= time) first = middle;
            else last = middle;
        }
        RootMotionKey from = _keys[first], to = _keys[last];
        float amount = (time - from.Time) / (to.Time - from.Time);
        Quaternion rotation = Quaternion.Slerp(from.Rotation, to.Rotation, amount);
        Vector3 translation = Vector3.Lerp(from.Translation, to.Translation, amount);
        return _inverseInitial * (Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation));
    }

    /// <summary>Precomputes a selected clip's model-space root movement at a uniform source rate.</summary>
    public static RootMotionPath FromClip(AnimationClip clip, float sampleRate = 60)
    {
        int intervals = (int)MathF.Ceiling(clip.Duration * sampleRate);
        float actualRate = intervals == 0 ? sampleRate : intervals / clip.Duration;
        var keys = new RootMotionKey[intervals + 1];
        var pose = new BoneTransform[clip.Skeleton.BoneCount];
        for (int sample = 0; sample < keys.Length; sample++)
        {
            float time = sample == intervals ? clip.Duration : sample / actualRate;
            clip.SampleLocal(time, pose);
            Matrix4x4 root = ClipRootMotion.RigidModel(clip, pose);
            keys[sample] = new(time, root.Translation, Quaternion.CreateFromRotationMatrix(root));
        }
        return new(keys);
    }
}
