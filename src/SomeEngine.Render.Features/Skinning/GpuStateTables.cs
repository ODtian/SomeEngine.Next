using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private void WriteGpuDefinitions(BinaryWriter writer)
    {
        var clips = new Dictionary<QuantizedClipData, uint>();
        foreach (Section section in _sectionOrder)
        {
            section.ClipOffset = section.Data.Clip is QuantizedClipData clip ? WriteClip(writer, clips, clip) : uint.MaxValue;
            if (section.Data.StateMachine is GpuPoseStateMachine machine)
                foreach (GpuPoseState state in machine.States) WriteClip(writer, clips, state.Clip);
        }
        var machines = new Dictionary<GpuPoseStateMachine, uint>();
        foreach (Section section in _sectionOrder)
        {
            if (section.Data.StateMachine is not GpuPoseStateMachine machine)
            {
                section.MachineOffset = uint.MaxValue;
                continue;
            }
            if (!machines.TryGetValue(machine, out uint offset))
            {
                Align(writer, 16);
                offset = (uint)writer.BaseStream.Position;
                machines.Add(machine, offset);
                writer.Write(offset + 16u);
                writer.Write(offset + 16u + (uint)machine.States.Length * 24u);
                writer.Write((uint)machine.InitialState);
                writer.Write((uint)machine.InputCount);
                uint transitionStart = 0;
                for (int state = 0; state < machine.States.Length; state++)
                {
                    GpuPoseState definition = machine.States[state];
                    uint count = (uint)machine.TransitionsFrom(state).Length;
                    writer.Write(clips[definition.Clip]);
                    writer.Write(definition.Clip.Duration);
                    writer.Write(definition.Speed);
                    writer.Write(definition.Loop ? 1u : 0u);
                    writer.Write(transitionStart); writer.Write(count);
                    transitionStart += count;
                }
                foreach (GpuPoseTransition transition in machine.Transitions)
                {
                    writer.Write((uint)transition.Target); writer.Write((uint)transition.Input);
                    writer.Write((uint)transition.Comparison); writer.Write(transition.Threshold); writer.Write(transition.Duration);
                }
            }
            section.MachineOffset = offset;
        }
        WriteBlendDefinitions(writer, clips);
    }

    private static uint WriteClip(BinaryWriter writer, Dictionary<QuantizedClipData, uint> clips, QuantizedClipData clip)
    {
        if (clips.TryGetValue(clip, out uint offset)) return offset;
        Align(writer, 16);
        offset = (uint)writer.BaseStream.Position;
        clips.Add(clip, offset);
        writer.Write((uint)clip.BoneCount); writer.Write((uint)clip.SampleCount);
        writer.Write(clip.Duration == 0 ? 0 : (clip.SampleCount - 1) / clip.Duration);
        writer.Write(16u + (uint)clip.BoneCount * 64u);
        writer.Write(MemoryMarshal.AsBytes(clip.Ranges)); writer.Write(MemoryMarshal.AsBytes(clip.Samples));
        return offset;
    }
}
