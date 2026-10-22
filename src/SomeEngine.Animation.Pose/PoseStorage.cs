using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>Continuous local and model-space bone arrays shared by a world's skeleton instances.</summary>
public sealed class PoseStorage
{
    private BoneTransform[] _local;
    private Matrix4x4[] _model;
    private int _count;
    private readonly List<PoseRange> _available = [];

    public PoseStorage(int boneCapacity = 0)
    {
        _local = new BoneTransform[boneCapacity];
        _model = new Matrix4x4[boneCapacity];
    }

    /// <summary>Creates an independent instance initialized from the shared skeleton's reference pose.</summary>
    public SkeletonInstance CreateInstance(Skeleton skeleton)
    {
        for (int index = 0; index < _available.Count; index++)
        {
            PoseRange available = _available[index];
            if (available.Count < skeleton.BoneCount) continue;
            var reused = new PoseRange(available.Start, skeleton.BoneCount);
            if (available.Count == reused.Count) _available.RemoveAt(index);
            else _available[index] = new(reused.Start + reused.Count, available.Count - reused.Count);
            return new SkeletonInstance(this, skeleton, reused);
        }

        var range = new PoseRange(_count, skeleton.BoneCount);
        _count += range.Count;
        if (_count > _local.Length)
        {
            int capacity = Math.Max(_count, Math.Max(256, _local.Length * 2));
            Array.Resize(ref _local, capacity);
            Array.Resize(ref _model, capacity);
        }
        return new SkeletonInstance(this, skeleton, range);
    }

    internal Span<BoneTransform> Local(PoseRange range) => _local.AsSpan(range.Start, range.Count);
    internal Span<Matrix4x4> Model(PoseRange range) => _model.AsSpan(range.Start, range.Count);

    internal void Release(PoseRange range)
    {
        int index = 0;
        while (index < _available.Count && _available[index].Start < range.Start) index++;
        if (index > 0 && _available[index - 1].Start + _available[index - 1].Count == range.Start)
        {
            PoseRange before = _available[--index];
            range = new(before.Start, before.Count + range.Count);
            _available.RemoveAt(index);
        }
        if (index < _available.Count && range.Start + range.Count == _available[index].Start)
        {
            range = new(range.Start, range.Count + _available[index].Count);
            _available.RemoveAt(index);
        }
        _available.Insert(index, range);
    }
}
