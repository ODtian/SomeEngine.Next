using SomeEngine.Animation.Pose;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private void WriteBlendDefinitions(BinaryWriter writer, Dictionary<QuantizedClipData, uint> clips)
    {
        var definitions = new Dictionary<GpuPoseBlend, uint>();
        var spaces = new Dictionary<GpuBlendSpace, uint>(ReferenceEqualityComparer.Instance);
        var masks = new Dictionary<BoneMask, uint>();
        foreach (Section section in _sectionOrder)
        {
            if (section.Data.Blend is not GpuPoseBlend blend) continue;
            if (definitions.TryGetValue(blend, out uint installed)) { section.BlendOffset = installed; continue; }
            foreach (GpuBlendStep step in blend.Steps)
            {
                if (step.Node is GpuBlendClip clip) WriteClip(writer, clips, clip.Clip);
                if (step.Node is GpuBlendSpace space)
                    foreach (QuantizedClipData clipData in space.Clips) WriteClip(writer, clips, clipData);
            }
            Align(writer, 16);
            uint reference = (uint)writer.BaseStream.Position;
            foreach (BoneTransform bone in blend.Skeleton.ReferencePose)
            {
                writer.Write(bone.Translation.X); writer.Write(bone.Translation.Y); writer.Write(bone.Translation.Z); writer.Write(0f);
                writer.Write(bone.Rotation.X); writer.Write(bone.Rotation.Y); writer.Write(bone.Rotation.Z); writer.Write(bone.Rotation.W);
                writer.Write(bone.Scale.X); writer.Write(bone.Scale.Y); writer.Write(bone.Scale.Z); writer.Write(0f);
            }
            var data = new uint[blend.Steps.Length];
            var maskOffsets = new uint[blend.Steps.Length];
            for (int index = 0; index < blend.Steps.Length; index++)
            {
                GpuBlendNode node = blend.Steps[index].Node;
                switch (node)
                {
                    case GpuBlendClip clip: data[index] = clips[clip.Clip]; break;
                    case GpuBlendSpace space:
                        if (!spaces.TryGetValue(space, out uint offset))
                        {
                            offset = WriteSpace(writer, space, clips);
                            spaces.Add(space, offset);
                        }
                        data[index] = offset;
                        break;
                    case GpuBlendLayer layer:
                        data[index] = reference;
                        if (!masks.TryGetValue(layer.Mask, out uint mask))
                        {
                            Align(writer, 4);
                            mask = (uint)writer.BaseStream.Position;
                            foreach (float weight in layer.Mask.Weights) writer.Write(weight);
                            masks.Add(layer.Mask, mask);
                        }
                        maskOffsets[index] = mask;
                        break;
                }
            }
            Align(writer, 16);
            section.BlendOffset = (uint)writer.BaseStream.Position;
            definitions.Add(blend, section.BlendOffset);
            writer.Write(section.BlendOffset + 16u); writer.Write((uint)blend.Steps.Length);
            writer.Write((uint)blend.Skeleton.BoneCount); writer.Write((uint)blend.Steps.Length - 1);
            for (int index = 0; index < blend.Steps.Length; index++)
            {
                GpuBlendStep step = blend.Steps[index];
                uint kind = step.Node is GpuBlendClip ? 0u : step.Node is GpuBlendSpace ? 1u : 2u;
                writer.Write(kind); writer.Write(data[index]); writer.Write(step.Base); writer.Write(step.Source);
                writer.Write(maskOffsets[index]);
                writer.Write(step.Node switch { GpuBlendClip clip => clip.Loop ? 1u : 0u,
                    GpuBlendLayer layer => (uint)layer.Mode, _ => 0u });
                writer.Write(step.Node switch { GpuBlendClip clip => clip.Speed, GpuBlendLayer layer => layer.Weight, _ => 1f });
                writer.Write(step.Node is GpuBlendLayer operation ? operation.WeightParameter : -1);
            }
        }
    }

    private static uint WriteSpace(BinaryWriter writer, GpuBlendSpace definition, Dictionary<QuantizedClipData, uint> clips)
    {
        BlendSpace space = definition.Space;
        Align(writer, 16);
        uint offset = (uint)writer.BaseStream.Position;
        uint samples = offset + 32u;
        uint intervals = samples + (uint)space.Samples.Length * 24u;
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
            writer.Write(clips[definition.Clips[point]]); writer.Write(0u);
        }
        foreach (BlendSpaceInterval interval in space.Intervals)
        {
            writer.Write((uint)interval.A); writer.Write((uint)interval.B);
            writer.Write(interval.From); writer.Write(interval.InverseWidth);
        }
        foreach (BlendTriangle triangle in space.Triangles)
        { writer.Write((uint)triangle.A); writer.Write((uint)triangle.B); writer.Write((uint)triangle.C); }
        return offset;
    }
}
