using SomeEngine.Animation.Pose;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private void WriteGpuDefinitions(BinaryWriter writer)
    {
        var definitions = new GpuPoseDefinitions(writer);
        foreach (Section section in _sectionOrder)
        {
            section.ClipOffset = section.Data.Clip is { } clip ? definitions.WriteClip(clip) : uint.MaxValue;
            section.MachineOffset = section.Data.StateMachine is { } machine ? definitions.WriteMachine(machine) : uint.MaxValue;
            if (section.Data.Blend is { } blend) section.BlendOffset = definitions.WriteBlend(blend);
        }
    }
}
