using System.Collections.Immutable;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

/// <summary>Common clip metadata and the public conversion from an encoded Block to ready sampling.</summary>
public abstract class AnimationClipBlock : AssetBlock
{
    protected AnimationClipBlock() { }
    protected AnimationClipBlock(AnimationClipBlock source, object? syncRoot = null) : base(source, syncRoot) { }

    [BinaryIgnore] public abstract AssetId<SkeletonAsset> Skeleton { get; }
    [BinaryIgnore] public abstract float Duration { get; }
    [BinaryIgnore] public abstract short RootMotionBone { get; }
    [BinaryIgnore] public abstract ImmutableArray<ClipEvent> Events { get; }
    [BinaryIgnore] public abstract ClipSampler Sampler { get; }

    public AnimationClip CreateClip(Skeleton skeleton)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            return new(skeleton, Duration, Sampler, Events, RootMotionBone);
        }
    }
}
