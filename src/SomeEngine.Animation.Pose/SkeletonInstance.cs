using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>A skeleton's own local pose and evaluated model-space bone transforms.</summary>
public sealed class SkeletonInstance : IDisposable
{
    private PoseStorage? _storage;
    private ulong _version;
    private double _time;

    public Skeleton Skeleton { get; }
    public PoseRange Range { get; }
    public ReadOnlySpan<BoneTransform> LocalPose => _storage!.Local(Range);
    public ModelPose ModelPose => new(_storage!.Model(Range), _version, _time);

    internal SkeletonInstance(PoseStorage storage, Skeleton skeleton, PoseRange range)
    {
        _storage = storage;
        Skeleton = skeleton;
        Range = range;
        skeleton.ReferencePose.CopyTo(storage.Local(range));
        Evaluate(0);
    }

    /// <summary>Copies one local transform per bone, then evaluates and publishes the model pose at the supplied time.</summary>
    public void Update(ReadOnlySpan<BoneTransform> localPose, double time)
    {
        localPose[..Range.Count].CopyTo(_storage!.Local(Range));
        Evaluate(time);
    }

    /// <summary>Releases this instance's bone section after its evaluation and reads have completed.</summary>
    public void Dispose()
    {
        _storage?.Release(Range);
        _storage = null;
    }

    private void Evaluate(double time)
    {
        ReadOnlySpan<BoneTransform> local = _storage!.Local(Range);
        Span<Matrix4x4> model = _storage.Model(Range);
        ReadOnlySpan<short> parents = Skeleton.Parents;
        for (int bone = 0; bone < Range.Count; bone++)
        {
            Matrix4x4 transform = local[bone].ToMatrix();
            model[bone] = parents[bone] < 0 ? transform : transform * model[parents[bone]];
        }
        _time = time;
        _version++;
    }
}
