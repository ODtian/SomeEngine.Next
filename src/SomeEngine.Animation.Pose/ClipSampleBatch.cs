using System.Numerics;

namespace SomeEngine.Animation.Pose;

/// <summary>Bound clip selections with reusable decoding and model-pose storage.</summary>
public sealed class ClipSampleBatch
{
    private readonly ClipSampleSelection[] _selections;
    private readonly PoseRange[] _outputs;
    private readonly ClipGroup[] _groups;

    public int Count => _selections.Length;
    public int OutputCount { get; }

    public ClipSampleBatch(ReadOnlySpan<ClipSampleSelection> selections)
    {
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
    }

    /// <summary>The selection's section in the caller's output span.</summary>
    public PoseRange GetOutputRange(int sample) => _outputs[sample];

    /// <summary>Samples one time per selection and writes its local transforms in selection order.</summary>
    public void SampleLocal(ReadOnlySpan<float> times, Span<BoneTransform> destination)
    {
        foreach (ClipGroup group in _groups)
        {
            group.SampleLocal(times, group.LocalBones);
            for (int row = 0; row < group.Requests.Length; row++)
            {
                int sample = group.Requests[row];
                ReadOnlySpan<short> bones = _selections[sample].Bones;
                int start = row * group.Clip.Skeleton.BoneCount;
                int output = _outputs[sample].Start;
                for (int selected = 0; selected < bones.Length; selected++)
                    destination[output + selected] = group.Local[start + bones[selected]];
            }
        }
    }

    /// <summary>Samples one time per selection and evaluates its required ancestors before returning model matrices.</summary>
    public void SampleModel(ReadOnlySpan<float> times, Span<Matrix4x4> destination)
    {
        foreach (ClipGroup group in _groups)
        {
            group.SampleLocal(times, group.ModelBones);
            ReadOnlySpan<short> parents = group.Clip.Skeleton.Parents;
            int count = group.Clip.Skeleton.BoneCount;
            for (int row = 0; row < group.Requests.Length; row++)
            {
                int start = row * count;
                foreach (short bone in group.ModelBones)
                {
                    Matrix4x4 local = group.Local[start + bone].ToMatrix();
                    group.Model[start + bone] = parents[bone] < 0
                        ? local : local * group.Model[start + parents[bone]];
                }
                int sample = group.Requests[row];
                ReadOnlySpan<short> bones = _selections[sample].Bones;
                int output = _outputs[sample].Start;
                for (int selected = 0; selected < bones.Length; selected++)
                    destination[output + selected] = group.Model[start + bones[selected]];
            }
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
        }

        internal void SampleLocal(ReadOnlySpan<float> times, ReadOnlySpan<short> bones)
        {
            for (int row = 0; row < Requests.Length; row++)
                _times[row] = Math.Clamp(times[Requests[row]], 0, Clip.Duration);
            var samples = new ClipSamples(_times, bones, Local, Clip.Skeleton.BoneCount);
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
