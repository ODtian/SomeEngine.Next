using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>Bound clip selections with reusable decoding and model-pose storage.</summary>
public sealed class ClipSampleBatch
{
    private readonly ClipSampleSelection[] _selections;
    private readonly PoseRange[] _outputs;
    private readonly ClipGroup[] _groups;
    private readonly SampleTile[] _tiles;
    private readonly ParentBinding[] _parents;
    private JobResourceAccess[]? _accesses;

    public int Count => _selections.Length;
    public int OutputCount { get; }
    public int SamplesPerJob { get; }

    public ClipSampleBatch(ReadOnlySpan<ClipSampleSelection> selections, int samplesPerJob = 32)
    {
        SamplesPerJob = samplesPerJob;
        _selections = selections.ToArray();
        _outputs = new PoseRange[selections.Length];
        var clips = new List<AnimationClip>();
        var requests = new List<List<int>>();
        int output = 0;
        for (int sample = 0; sample < selections.Length; sample++)
        {
            ClipSampleSelection selection = selections[sample];
            _outputs[sample] = new(output, selection.Bones.Length);
            output += selection.Bones.Length;
            int group = 0;
            while (group < clips.Count && !ReferenceEquals(clips[group], selection.Clip)) group++;
            if (group == clips.Count)
            {
                clips.Add(selection.Clip);
                requests.Add([]);
            }
            requests[group].Add(sample);
        }
        OutputCount = output;
        _groups = new ClipGroup[clips.Count];
        for (int group = 0; group < clips.Count; group++)
            _groups[group] = new(clips[group], requests[group].ToArray(), _selections);
        var tiles = new List<SampleTile>();
        var parents = new HashSet<SkeletonInstance>();
        foreach (ClipSampleSelection selection in _selections)
            if (selection.ParentPose is { } parent)
                parents.Add(parent.GetSection(parent.SectionCount - 1).Instance);
        foreach (ClipGroup group in _groups)
        {
            for (int row = 0; row < group.Requests.Length; row += samplesPerJob)
                tiles.Add(new(group, row, Math.Min(samplesPerJob, group.Requests.Length - row)));
        }
        _tiles = tiles.ToArray();
        _parents = parents.Select(parent => new ParentBinding(parent, 0)).ToArray();
    }

    /// <summary>The selection's section in the caller's output span.</summary>
    public PoseRange GetOutputRange(int sample) => _outputs[sample];

    /// <summary>Schedules local sampling into caller-owned memory and returns its completion handle.</summary>
    public JobHandle ScheduleLocal(ReadOnlyMemory<float> times, Memory<BoneTransform> destination, JobHandle dependency = default)
    {
        ReadOnlySpan<JobResourceAccess> resources = GetAccesses();
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[resources.Length + 2];
        resources.CopyTo(accesses);
        accesses[^2] = JobResourceAccess.Read(times);
        accesses[^1] = JobResourceAccess.Write(destination);
        return JobSystem.ScheduleParallel(new LocalSamples(this, times, destination), _tiles.Length, 1, accesses, dependency);
    }

    /// <summary>Schedules model sampling into caller-owned memory and returns its completion handle.</summary>
    public JobHandle ScheduleModel(ReadOnlyMemory<float> times, Memory<Matrix4x4> destination, JobHandle dependency = default)
    {
        ReadOnlySpan<JobResourceAccess> resources = GetAccesses();
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[resources.Length + 2];
        resources.CopyTo(accesses);
        accesses[^2] = JobResourceAccess.Read(times);
        accesses[^1] = JobResourceAccess.Write(destination);
        return JobSystem.ScheduleParallel(new ModelSamples(this, times, destination), _tiles.Length, 1, accesses, dependency);
    }

    private ReadOnlySpan<JobResourceAccess> GetAccesses()
    {
        bool changed = _accesses is null;
        for (int index = 0; index < _parents.Length; index++)
        {
            ref ParentBinding parent = ref _parents[index];
            changed |= parent.Version != parent.Instance.BindingVersion;
            parent.Version = parent.Instance.BindingVersion;
        }
        if (changed)
        {
            var parents = new HashSet<SkeletonInstance>();
            foreach (ClipGroup group in _groups)
                for (int row = 0; row < group.Requests.Length; row++)
                {
                    if (_selections[group.Requests[row]].ParentPose is null) continue;
                    foreach (short bone in group.ModelBones)
                        if (group.Clip.Skeleton.InheritedParents[bone] >= 0)
                            parents.Add(group.InheritedParents[row * group.Clip.Skeleton.BoneCount + bone].Instance);
                }
            var accesses = new JobResourceAccess[1 + _groups.Length + parents.Count];
            accesses[0] = JobResourceAccess.Write(JobSystem.Default.GetResourceToken(this));
            for (int group = 0; group < _groups.Length; group++)
                accesses[1 + group] = JobResourceAccess.Read(JobSystem.Default.GetResourceToken(_groups[group].Clip));
            int next = 1 + _groups.Length;
            foreach (SkeletonInstance parent in parents)
                accesses[next++] = JobResourceAccess.Read(JobSystem.Default.GetResourceToken(parent));
            _accesses = accesses;
        }
        return _accesses;
    }

    /// <summary>Samples one time per selection and writes its local transforms in selection order.</summary>
    public void SampleLocal(ReadOnlySpan<float> times, Span<BoneTransform> destination)
    {
        foreach (ClipGroup group in _groups)
            SampleLocal(group, 0, group.Requests.Length, times, destination);
    }

    private void SampleLocal(ClipGroup group, int first, int count, ReadOnlySpan<float> times, Span<BoneTransform> destination)
    {
        group.SampleLocal(times, group.LocalBones, first, count);
        for (int row = first; row < first + count; row++)
        {
            int sample = group.Requests[row];
            ReadOnlySpan<short> bones = _selections[sample].Bones;
            int start = row * group.Clip.Skeleton.BoneCount;
            int output = _outputs[sample].Start;
            for (int selected = 0; selected < bones.Length; selected++)
                destination[output + selected] = group.Local[start + bones[selected]];
        }
    }

    /// <summary>Samples one time per selection and evaluates its required ancestors before returning model matrices.</summary>
    public void SampleModel(ReadOnlySpan<float> times, Span<Matrix4x4> destination)
    {
        foreach (ClipGroup group in _groups)
            SampleModel(group, 0, group.Requests.Length, times, destination);
    }

    private void SampleModel(ClipGroup group, int first, int length, ReadOnlySpan<float> times, Span<Matrix4x4> destination)
    {
        group.SampleLocal(times, group.ModelBones, first, length);
        ReadOnlySpan<short> parents = group.Clip.Skeleton.Parents;
        ReadOnlySpan<short> inherited = group.Clip.Skeleton.InheritedParents;
        int count = group.Clip.Skeleton.BoneCount;
        for (int row = first; row < first + length; row++)
        {
            int start = row * count;
            foreach (short bone in group.ModelBones)
            {
                Matrix4x4 local = group.Local[start + bone].ToMatrix();
                group.Model[start + bone] = parents[bone] >= 0
                    ? local * group.Model[start + parents[bone]]
                    : inherited[bone] >= 0 ? local * group.InheritedParents[start + bone].Transform : local;
            }
            int sample = group.Requests[row];
            ReadOnlySpan<short> bones = _selections[sample].Bones;
            int output = _outputs[sample].Start;
            for (int selected = 0; selected < bones.Length; selected++)
                destination[output + selected] = group.Model[start + bones[selected]];
        }
    }

    private record struct ParentBinding(SkeletonInstance Instance, ulong Version);
    private readonly record struct SampleTile(ClipGroup Group, int First, int Count);
    private readonly record struct LocalSamples(ClipSampleBatch Batch, ReadOnlyMemory<float> Times,
        Memory<BoneTransform> Destination) : IJobParallelFor
    {
        public void Execute(int index)
        {
            SampleTile tile = Batch._tiles[index];
            Batch.SampleLocal(tile.Group, tile.First, tile.Count, Times.Span, Destination.Span);
        }
    }
    private readonly record struct ModelSamples(ClipSampleBatch Batch, ReadOnlyMemory<float> Times,
        Memory<Matrix4x4> Destination) : IJobParallelFor
    {
        public void Execute(int index)
        {
            SampleTile tile = Batch._tiles[index];
            Batch.SampleModel(tile.Group, tile.First, tile.Count, Times.Span, Destination.Span);
        }
    }

    private sealed class ClipGroup
    {
        internal readonly AnimationClip Clip;
        internal readonly int[] Requests;
        internal readonly short[] LocalBones;
        internal readonly short[] ModelBones;
        internal readonly BoneTransform[] Local;
        internal readonly Matrix4x4[] Model;
        internal readonly BonePose[] InheritedParents;
        private readonly float[] _times;

        internal ClipGroup(AnimationClip clip, int[] requests, ClipSampleSelection[] selections)
        {
            Clip = clip;
            Requests = requests;
            int count = clip.Skeleton.BoneCount;
            var required = new bool[count];
            foreach (int request in requests)
                foreach (short bone in selections[request].Bones) required[bone] = true;
            LocalBones = CollectBones(required);
            foreach (short bone in LocalBones)
                for (int parent = clip.Skeleton.Parents[bone]; parent >= 0; parent = clip.Skeleton.Parents[parent])
                    required[parent] = true;
            ModelBones = CollectBones(required);
            _times = new float[requests.Length];
            Local = new BoneTransform[count * requests.Length];
            Model = new Matrix4x4[count * requests.Length];
            InheritedParents = clip.Skeleton.Parent is null ? [] : new BonePose[count * requests.Length];
            for (int row = 0; row < requests.Length; row++)
            {
                if (selections[requests[row]].ParentPose is not { } parentPose) continue;
                foreach (short bone in ModelBones)
                {
                    int inherited = clip.Skeleton.InheritedParents[bone];
                    if (inherited >= 0) InheritedParents[row * count + bone] = parentPose.GetBone(inherited);
                }
            }
        }

        internal void SampleLocal(ReadOnlySpan<float> times, ReadOnlySpan<short> bones, int first, int count)
        {
            for (int row = first; row < first + count; row++)
                _times[row] = Math.Clamp(times[Requests[row]], 0, Clip.Duration);
            int boneCount = Clip.Skeleton.BoneCount;
            var samples = new ClipSamples(_times.AsSpan(first, count), bones,
                Local.AsSpan(first * boneCount, count * boneCount), boneCount);
            Clip.SampleLocal(ref samples);
        }

        private static short[] CollectBones(bool[] required)
        {
            var bones = new List<short>();
            for (int bone = 0; bone < required.Length; bone++)
                if (required[bone]) bones.Add((short)bone);
            return bones.ToArray();
        }
    }
}
