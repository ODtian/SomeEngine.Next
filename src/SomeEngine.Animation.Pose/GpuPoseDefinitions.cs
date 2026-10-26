using System.Runtime.InteropServices;

namespace SomeEngine.Animation.Pose;

/// <summary>Shared clip, blend and state-machine data consumed by the concrete GPU pose kernels.</summary>
public sealed class GpuPoseDefinitions(BinaryWriter writer)
{
    private readonly Dictionary<QuantizedClipData, uint> _clips = [];
    private readonly Dictionary<GpuPoseBlend, uint> _blends = [];
    private readonly Dictionary<GpuBlendSpace, uint> _spaces = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BoneMask, uint> _masks = [];
    private readonly Dictionary<GpuPoseStateMachine, uint> _machines = [];

    public uint WriteClip(QuantizedClipData clip)
    {
        if (_clips.TryGetValue(clip, out uint offset)) return offset;
        Align(16); offset = (uint)writer.BaseStream.Position; _clips.Add(clip, offset);
        writer.Write((uint)clip.BoneCount); writer.Write((uint)clip.SampleCount);
        writer.Write(clip.Duration == 0 ? 0 : (clip.SampleCount - 1) / clip.Duration);
        writer.Write(16u + (uint)clip.BoneCount * 64u);
        writer.Write(MemoryMarshal.AsBytes(clip.Ranges)); writer.Write(MemoryMarshal.AsBytes(clip.Samples));
        return offset;
    }

    public uint WriteBlend(GpuPoseBlend blend)
    {
        if (_blends.TryGetValue(blend, out uint installed)) return installed;
        foreach (GpuBlendStep step in blend.Steps)
        {
            if (step.Node is GpuBlendClip clip) WriteClip(clip.Clip);
            if (step.Node is GpuBlendSpace space)
                foreach (QuantizedClipData sample in space.Clips) WriteClip(sample);
        }
        Align(16); uint reference = (uint)writer.BaseStream.Position;
        foreach (BoneTransform bone in blend.Skeleton.ReferencePose) WriteLocal(in bone);
        var data = new uint[blend.Steps.Length];
        var masks = new uint[blend.Steps.Length];
        for (int index = 0; index < blend.Steps.Length; index++)
        {
            switch (blend.Steps[index].Node)
            {
                case GpuBlendClip clip: data[index] = _clips[clip.Clip]; break;
                case GpuBlendSpace space:
                    if (!_spaces.TryGetValue(space, out uint spaceOffset))
                    { spaceOffset = WriteSpace(space); _spaces.Add(space, spaceOffset); }
                    data[index] = spaceOffset;
                    break;
                case GpuBlendLayer layer:
                    data[index] = reference;
                    if (!_masks.TryGetValue(layer.Mask, out uint mask))
                    {
                        Align(4); mask = (uint)writer.BaseStream.Position;
                        foreach (float weight in layer.Mask.Weights) writer.Write(weight);
                        _masks.Add(layer.Mask, mask);
                    }
                    masks[index] = mask;
                    break;
            }
        }
        Align(16); uint offset = (uint)writer.BaseStream.Position; _blends.Add(blend, offset);
        writer.Write(offset + 16u); writer.Write((uint)blend.Steps.Length);
        writer.Write((uint)blend.Skeleton.BoneCount); writer.Write((uint)blend.Steps.Length - 1);
        for (int index = 0; index < blend.Steps.Length; index++)
        {
            GpuBlendStep step = blend.Steps[index];
            writer.Write(step.Node is GpuBlendClip ? 0u : step.Node is GpuBlendSpace ? 1u : 2u);
            writer.Write(data[index]); writer.Write(step.Base); writer.Write(step.Source); writer.Write(masks[index]);
            writer.Write(step.Node switch { GpuBlendClip clip => clip.Loop ? 1u : 0u, GpuBlendLayer layer => (uint)layer.Mode, _ => 0u });
            writer.Write(step.Node switch { GpuBlendClip clip => clip.Speed, GpuBlendLayer layer => layer.Weight, _ => 1f });
            writer.Write(step.Node is GpuBlendLayer operation ? operation.WeightParameter : -1);
        }
        return offset;
    }

    public uint WriteMachine(GpuPoseStateMachine machine)
    {
        if (_machines.TryGetValue(machine, out uint offset)) return offset;
        foreach (GpuPoseState state in machine.States) WriteClip(state.Clip);
        Align(16); offset = (uint)writer.BaseStream.Position; _machines.Add(machine, offset);
        writer.Write(offset + 16u); writer.Write(offset + 16u + (uint)machine.States.Length * 24u);
        writer.Write((uint)machine.InitialState); writer.Write((uint)machine.InputCount);
        uint firstTransition = 0;
        for (int state = 0; state < machine.States.Length; state++)
        {
            GpuPoseState definition = machine.States[state];
            uint count = (uint)machine.TransitionsFrom(state).Length;
            writer.Write(_clips[definition.Clip]); writer.Write(definition.Clip.Duration);
            writer.Write(definition.Speed); writer.Write(definition.Loop ? 1u : 0u);
            writer.Write(firstTransition); writer.Write(count); firstTransition += count;
        }
        foreach (GpuPoseTransition transition in machine.Transitions)
        {
            writer.Write((uint)transition.Target); writer.Write((uint)transition.Input);
            writer.Write((uint)transition.Comparison); writer.Write(transition.Threshold); writer.Write(transition.Duration);
        }
        return offset;
    }

    public uint WriteReference(Skeleton skeleton)
    {
        Align(16); uint offset = (uint)writer.BaseStream.Position;
        for (int bone = 0; bone < skeleton.BoneCount; bone++)
        {
            WriteLocal(in skeleton.ReferencePose[bone]);
            writer.Write(skeleton.Parents[bone] < 0 ? 3u : 1u);
            writer.Write(0u); writer.Write(0u); writer.Write(0u);
        }
        return offset;
    }

    private void WriteLocal(in BoneTransform bone)
    {
        writer.Write(bone.Translation.X); writer.Write(bone.Translation.Y); writer.Write(bone.Translation.Z); writer.Write(0f);
        writer.Write(bone.Rotation.X); writer.Write(bone.Rotation.Y); writer.Write(bone.Rotation.Z); writer.Write(bone.Rotation.W);
        writer.Write(bone.Scale.X); writer.Write(bone.Scale.Y); writer.Write(bone.Scale.Z); writer.Write(0f);
    }
    private uint WriteSpace(GpuBlendSpace definition)
    {
        BlendSpace space = definition.Space;
        Align(16); uint offset = (uint)writer.BaseStream.Position;
        uint samples = offset + 32u, intervals = samples + (uint)space.Samples.Length * 24u;
        uint triangles = intervals + (uint)space.Intervals.Length * 16u;
        writer.Write(samples); writer.Write((uint)space.Samples.Length);
        writer.Write(intervals); writer.Write((uint)space.Intervals.Length);
        writer.Write(triangles); writer.Write((uint)space.Triangles.Length);
        writer.Write((uint)definition.ParameterX); writer.Write((uint)definition.ParameterY);
        for (int point = 0; point < space.Samples.Length; point++)
        {
            BlendSpaceSample sample = space.Samples[point];
            writer.Write(sample.Coordinate.X); writer.Write(sample.Coordinate.Y);
            writer.Write(sample.PhaseOffset); writer.Write(sample.Clip.Duration);
            writer.Write(_clips[definition.Clips[point]]); writer.Write(0u);
        }
        foreach (BlendSpaceInterval interval in space.Intervals)
        { writer.Write((uint)interval.A); writer.Write((uint)interval.B); writer.Write(interval.From); writer.Write(interval.InverseWidth); }
        foreach (BlendTriangle triangle in space.Triangles)
        { writer.Write((uint)triangle.A); writer.Write((uint)triangle.B); writer.Write((uint)triangle.C); }
        return offset;
    }
    private void Align(int alignment)
    { while (writer.BaseStream.Position % alignment != 0) writer.Write((byte)0); }
}
