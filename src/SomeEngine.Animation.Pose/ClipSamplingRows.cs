using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Reusable rows for one ready codec, with disjoint 32-sample decoding tiles.</summary>
internal sealed class ClipSamplingRows
{
    private const int TileSize = 32;
    private readonly AnimationClip _clip;
    private readonly Scheduler _scheduler;
    private readonly short[] _bones;
    private readonly List<SampleRow> _rows = [];
    private readonly List<JobResourceAccess> _accesses = [];
    private readonly JobResourceAccess _clipAccess, _dataAccess;
    private float[] _times = [];
    private BoneTransform[] _local = [];
    internal int Count => _rows.Count;

    internal ClipSamplingRows(Scheduler scheduler, AnimationClip clip, ReadOnlySpan<short> bones)
    {
        _clip = clip;
        _scheduler = scheduler;
        _bones = bones.ToArray();
        _clipAccess = JobResourceAccess.Read(scheduler.GetResourceToken(clip));
        _dataAccess = JobResourceAccess.Write(scheduler.GetResourceToken(this));
    }

    internal void Begin()
    {
        _rows.Clear();
        _accesses.Clear();
        _accesses.Add(_clipAccess);
        _accesses.Add(_dataAccess);
    }

    internal void Add(float time, BoneTransform[] destination, short[] bones, JobResourceAccess access)
    {
        int row = _rows.Count;
        if (row == _times.Length)
        {
            int capacity = Math.Max(TileSize, _times.Length * 2);
            Array.Resize(ref _times, capacity);
            Array.Resize(ref _local, capacity * _clip.Skeleton.BoneCount);
        }
        _times[row] = Math.Clamp(time, 0, _clip.Duration);
        _rows.Add(new(destination, bones));
        _accesses.Add(access);
    }

    internal void AddFull(float time, BoneTransform[] destination, JobResourceAccess access) =>
        Add(time, destination, _bones, access);

    internal JobHandle Schedule() => _scheduler.ScheduleParallel(new DecodeRows(this),
        (_rows.Count + TileSize - 1) / TileSize, 1, CollectionsMarshal.AsSpan(_accesses));

    private readonly record struct SampleRow(BoneTransform[] Destination, short[] Bones);
    private readonly record struct DecodeRows(ClipSamplingRows Rows) : IJobParallelFor
    {
        public void Execute(int index)
        {
            int first = index * TileSize, count = Math.Min(TileSize, Rows._rows.Count - first);
            int boneCount = Rows._clip.Skeleton.BoneCount;
            var samples = new ClipSamples(Rows._times.AsSpan(first, count), Rows._bones,
                Rows._local.AsSpan(first * boneCount, count * boneCount), boneCount);
            Rows._clip.SampleLocal(ref samples);
            for (int row = 0; row < count; row++)
            {
                SampleRow output = Rows._rows[first + row];
                ReadOnlySpan<BoneTransform> local = samples.LocalPose(row);
                if (output.Bones.Length == boneCount) local.CopyTo(output.Destination);
                else
                    for (int selected = 0; selected < output.Bones.Length; selected++)
                        output.Destination[selected] = local[output.Bones[selected]];
            }
        }
    }
}
