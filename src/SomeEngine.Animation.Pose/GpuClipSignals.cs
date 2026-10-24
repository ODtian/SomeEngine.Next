using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

internal sealed class GpuClipSignalDefinition(GpuClipParameters parameters)
{
    private readonly Dictionary<Skeleton, GpuClipSignalData> _skeletons = [];
    internal GpuClipSignals Bind(Skeleton skeleton)
    {
        if (!_skeletons.TryGetValue(skeleton, out GpuClipSignalData? data))
            _skeletons.Add(skeleton, data = new(parameters, skeleton));
        return new(data);
    }
}

internal sealed class GpuClipSignalData
{
    internal readonly AnimationClip Clip;
    internal readonly short[] RootChain;
    internal readonly ClipRootMotion Root;
    internal GpuClipSignalData(GpuClipParameters parameters, Skeleton skeleton)
    {
        Clip = parameters.Clip.CreateClip(skeleton);
        var chain = new List<short>();
        if (parameters.RootMotion == RootMotionMode.Extract && Clip.RootMotionBone >= 0)
        {
            for (int bone = Clip.RootMotionBone; bone >= 0; bone = Clip.Skeleton.Parents[bone]) chain.Add((short)bone);
            Root = ClipRootMotion.Create(Clip);
        }
        RootChain = chain.ToArray();
    }
}

internal sealed class GpuClipSignals
{
    internal readonly GpuClipSignalData Definition;
    private readonly ClipSampleBatch? _root;
    private readonly BoneTransform[] _samples;
    private readonly float[] _times = new float[1];
    private float _previousTime = -1;
    private Matrix4x4 _previousMotion = Matrix4x4.Identity;
    internal GpuClipSignals(GpuClipSignalData definition)
    {
        Definition = definition;
        _samples = new BoneTransform[definition.RootChain.Length];
        if (_samples.Length != 0) _root = new([new(definition.Clip, definition.RootChain)]);
    }
    internal void Reset() { _previousTime = -1; _previousMotion = Matrix4x4.Identity; }
    internal void Update(World world, Entity animation, in GpuClipParameters parameters, float elapsed, ref GpuPoseTarget output)
    {
        foreach (ClipEvent marker in Definition.Clip.Events)
        {
            uint count = ClipSystem.CrossedEvents(_previousTime, elapsed, marker.Time, Definition.Clip.Duration, parameters.Loop);
            if (count > 0) Animation.EmitEvent(world, animation, marker.Id, count);
        }
        _previousTime = elapsed;
        output.ExtractRootMotion = _root is not null;
        if (_root is null) return;
        _times[0] = parameters.Loop ? elapsed % Definition.Clip.Duration : elapsed;
        _root.SampleLocal(_times, _samples);
        Matrix4x4 model = _samples[0].ToMatrix();
        for (int bone = 1; bone < _samples.Length; bone++) model *= _samples[bone].ToMatrix();
        Matrix4x4.Decompose(model, out _, out Quaternion rotation, out Vector3 position);
        Matrix4x4 phase = Definition.Root.InverseInitial
            * (Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));
        Matrix4x4 motion = phase * ClipRootMotion.Power(Definition.Root.Cycle, parameters.Loop ? (int)(elapsed / Definition.Clip.Duration) : 0);
        Matrix4x4.Invert(_previousMotion, out Matrix4x4 inversePrevious);
        output.RootMotion = new(motion * inversePrevious * output.RootMotion.Transform);
        _previousMotion = motion;
        Matrix4x4.Invert(phase, out Matrix4x4 correction);
        output.RootCorrection = new() { Translation = correction.Translation,
            Rotation = Quaternion.CreateFromRotationMatrix(correction), Scale = Vector3.One };
    }
}
