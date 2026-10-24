using System.Numerics;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Animation.Pose;

/// <summary>A skeleton's own local pose and evaluated model-space bone transforms.</summary>
public sealed class SkeletonInstance : IDisposable
{
    private PoseStorage? _storage;
    private ulong _version;
    private double _time;
    private double _sampleTime;
    private bool _localChanged;
    private ulong _parentVersion;
    private bool _privateParent;
    private readonly BoneSource[] _boneSources;
    private readonly SkeletonInstance[] _sections;
    private readonly List<SkeletonInstance> _children = [];

    public Skeleton Skeleton { get; }
    public SkeletonInstance? ParentInstance { get; private set; }
    public bool IsDisposed => _storage is null;
    public PoseRange Range { get; }
    /// <summary>Changes when this instance's parent-section correspondence is rebound.</summary>
    public ulong BindingVersion { get; private set; } = 1;
    public ReadOnlySpan<BoneTransform> LocalPose => _storage!.Local(Range);
    public ModelPose ModelPose => new(this);
    /// <summary>The resolved own bones currently written by synchronous CPU demand.</summary>
    public ReadOnlyMemory<short> CpuBones => CpuWrittenBones;
    internal ReadOnlySpan<Matrix4x4> ModelTransforms => _storage!.Model(Range);
    internal BoneSource[] BoneSources => _boneSources;
    internal ReadOnlySpan<SkeletonInstance> Sections => _sections;
    internal ulong Version => _version;
    internal double Time => _time;
    internal double SampleTime => _sampleTime;
    internal ReadOnlySpan<PoseCalculation> Calculations => _storage!.Calculations(Range);
    internal short[] CpuWrittenBones { get; set; } = [];

    internal SkeletonInstance(PoseStorage storage, Skeleton skeleton, PoseRange range,
        SkeletonInstance? parent, bool privateParent)
    {
        _storage = storage;
        Skeleton = skeleton;
        Range = range;
        ParentInstance = parent;
        _privateParent = privateParent;
        _boneSources = new BoneSource[skeleton.TotalBoneCount];
        _sections = new SkeletonInstance[(parent?.Sections.Length ?? 0) + 1];
        if (parent is not null)
        {
            parent._children.Add(this);
            parent.BoneSources.CopyTo(_boneSources, 0);
            parent.Sections.CopyTo(_sections);
        }
        for (int bone = 0; bone < range.Count; bone++)
            _boneSources[skeleton.InheritedBoneCount + bone] = new(this, bone);
        _sections[^1] = this;
        skeleton.ReferencePose.CopyTo(storage.Local(range));
        storage.SampleTimes(range).Clear();
        Evaluate(parent?.Time ?? 0);
    }

    /// <summary>Copies one local transform per bone, then evaluates and publishes the model pose at the supplied time.</summary>
    public void Update(ReadOnlySpan<BoneTransform> localPose, double time)
        => Update(localPose, time, sampleTime: time);

    /// <summary>Updates local samples while recording separate calculation and source sample times.</summary>
    public void Update(ReadOnlySpan<BoneTransform> localPose, double time, double sampleTime)
    {
        SetLocalPose(localPose, sampleTime);
        Compose(time);
    }

    internal void SetLocalPose(ReadOnlySpan<BoneTransform> localPose, double sampleTime)
    {
        localPose[..Range.Count].CopyTo(_storage!.Local(Range));
        _sampleTime = sampleTime;
        _storage.SampleTimes(Range).Fill(sampleTime);
        _localChanged = true;
    }

    internal void Compose(double time)
    {
        ParentInstance?.Compose(time);
        if (_localChanged || CpuWrittenBones.Length == 0 && _parentVersion != (ParentInstance?.Version ?? 0)) Evaluate(time);
    }

    internal void WriteBones(ReadOnlySpan<short> bones, ReadOnlySpan<BoneTransform> samples,
        double time, double sampleTime)
    {
        Span<BoneTransform> local = _storage!.Local(Range);
        Span<Matrix4x4> model = _storage.Model(Range);
        Span<PoseCalculation> calculations = _storage.Calculations(Range);
        Span<double> sourceTimes = _storage.SampleTimes(Range);
        _version++;
        for (int index = 0; index < bones.Length; index++)
        {
            int bone = bones[index];
            local[bone] = samples[index];
            sourceTimes[bone] = sampleTime;
            Matrix4x4 transform = samples[index].ToMatrix();
            int parent = Skeleton.Parents[bone], inherited = Skeleton.InheritedParents[bone];
            model[bone] = parent >= 0 ? transform * model[parent]
                : inherited >= 0 ? transform * _boneSources[inherited].Transform : transform;
            calculations[bone] = new(_version, time, sampleTime);
        }
        _time = time;
        _sampleTime = sampleTime;
        _parentVersion = ParentInstance?.Version ?? 0;
        _localChanged = false;
    }

    /// <summary>Uses an existing matching parent and refreshes the connected result sections at the supplied time.</summary>
    public void Attach(SkeletonInstance parent, double time)
    {
        DevelopmentCheck.Require(ReferenceEquals(parent.Skeleton, Skeleton.Parent),
            "The instance parent must use the skeleton's parent definition.");
        ReplaceParent(parent, privateParent: false, time);
    }

    /// <summary>Creates a private parent chain from the current parent poses and continues independently.</summary>
    public void Detach(double time)
    {
        SkeletonInstance parent = _storage!.CreateInstance(Skeleton.Parent!);
        CopyParentPose(ParentInstance!, parent);
        ReplaceParent(parent, privateParent: true, time);
    }

    /// <summary>Releases this instance's bone section after its evaluation and reads have completed.</summary>
    public void Dispose()
    {
        ParentInstance?._children.Remove(this);
        if (_privateParent) ParentInstance!.Dispose();
        _storage?.Release(Range);
        _storage = null;
    }

    private void ReplaceParent(SkeletonInstance parent, bool privateParent, double time)
    {
        SkeletonInstance? previous = ParentInstance;
        bool releasePrevious = _privateParent;
        previous?._children.Remove(this);
        ParentInstance = parent;
        _privateParent = privateParent;
        parent._children.Add(this);
        RefreshSources(time);
        if (releasePrevious) previous!.Dispose();
    }

    private void RefreshSources(double time)
    {
        BindingVersion++;
        ParentInstance!.BoneSources.CopyTo(_boneSources, 0);
        ParentInstance.Sections.CopyTo(_sections);
        Evaluate(time);
        foreach (SkeletonInstance child in _children) child.RefreshSources(time);
    }

    private static void CopyParentPose(SkeletonInstance source, SkeletonInstance destination)
    {
        if (source.ParentInstance is not null) CopyParentPose(source.ParentInstance, destination.ParentInstance!);
        destination.SetLocalPose(source.LocalPose, source.SampleTime);
        source._storage!.SampleTimes(source.Range).CopyTo(destination._storage!.SampleTimes(destination.Range));
        destination.Compose(source.Time);
    }

    private void Evaluate(double time)
    {
        ReadOnlySpan<BoneTransform> local = _storage!.Local(Range);
        Span<Matrix4x4> model = _storage.Model(Range);
        ReadOnlySpan<short> parents = Skeleton.Parents;
        ReadOnlySpan<short> inherited = Skeleton.InheritedParents;
        Span<PoseCalculation> calculations = _storage.Calculations(Range);
        ReadOnlySpan<double> sampleTimes = _storage.SampleTimes(Range);
        _version++;
        for (int bone = 0; bone < Range.Count; bone++)
        {
            Matrix4x4 transform = local[bone].ToMatrix();
            model[bone] = parents[bone] >= 0 ? transform * model[parents[bone]]
                : inherited[bone] >= 0 ? transform * _boneSources[inherited[bone]].Transform : transform;
            calculations[bone] = new(_version, time, sampleTimes[bone]);
        }
        _time = time;
        _parentVersion = ParentInstance?.Version ?? 0;
        _localChanged = false;
    }
}
