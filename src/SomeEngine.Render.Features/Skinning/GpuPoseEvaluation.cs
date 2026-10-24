using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private void WriteEvaluation(BinaryWriter writer)
    {
        foreach (Section section in _sectionOrder)
        {
            section.SelectedBones = new bool[section.Range.Count];
            GpuPoseEvaluation? evaluation = section.Data.Evaluation;
            if (evaluation is null || evaluation.AllBones) Array.Fill(section.SelectedBones, true);
            else foreach (short bone in evaluation.Bones) section.SelectedBones[bone] = true;
            foreach (short bone in section.Data.CpuBones.Span) section.SelectedBones[bone] = true;
            if (section.Data.Rig is { } rig)
            {
                if (rig.TranslationBone >= 0) section.SelectedBones[rig.TranslationBone] = true;
                foreach (GpuPoseSource operation in rig.Steps)
                    if (operation is GpuTwoBonePose limb)
                    {
                        section.SelectedBones[limb.Chain.Root] = true;
                        section.SelectedBones[limb.Chain.Middle] = true;
                        section.SelectedBones[limb.Chain.Tip] = true;
                    }
                    else foreach (GpuAimBone bone in ((GpuAimPose)operation).Chain.Bones) section.SelectedBones[bone.Bone] = true;
            }
        }
        foreach (Section section in _sectionOrder)
            for (int bone = 0; bone < section.Range.Count; bone++)
                if (section.SelectedBones[bone]) RequireParents(section, bone);
        foreach (Section section in _sectionOrder)
        {
            var sampled = new List<short>();
            Align(writer, 16);
            section.ReferenceOffset = (uint)writer.BaseStream.Position;
            for (int bone = 0; bone < section.Range.Count; bone++)
            {
                BoneTransform local = section.Data.Skeleton.ReferencePose[bone];
                writer.Write(local.Translation.X); writer.Write(local.Translation.Y); writer.Write(local.Translation.Z); writer.Write(0u);
                writer.Write(local.Rotation.X); writer.Write(local.Rotation.Y); writer.Write(local.Rotation.Z); writer.Write(local.Rotation.W);
                writer.Write(local.Scale.X); writer.Write(local.Scale.Y); writer.Write(local.Scale.Z); writer.Write(0u);
                writer.Write((section.SelectedBones[bone] ? 1u : 0u) | (section.Data.Skeleton.Parents[bone] < 0 ? 2u : 0u));
                writer.Write(0u); writer.Write(0u); writer.Write(0u);
                if (section.Data.IsGpu && section.SelectedBones[bone] && section.Data.CpuBones.Span.BinarySearch((short)bone) < 0) sampled.Add((short)bone);
            }
            section.SampledBones = sampled.ToArray();
        }
        void RequireParents(Section section, int bone)
        {
            RenderBoneParent parent = section.Data.Parents[bone];
            if (parent.Section == Entity.Null) return;
            Section producer = _sections[parent.Section];
            producer.SelectedBones[parent.Bone] = true;
            RequireParents(producer, parent.Bone);
        }
    }

    private static void PrepareSampling(Section section, double time)
    {
        RenderBoneSectionData data = section.Data;
        if (!data.IsGpu)
        {
            section.Presentation = new(data.CalculationTime, data.CalculationTime, data.CalculationTime, 1);
            return;
        }
        section.ResetSamples = !section.HasSamples || !ReferenceEquals(section.AcceptedEvaluation, data.Evaluation)
            || section.AcceptedInput.Starts != data.Starts || section.AcceptedInput.AnimationStarts != data.Identity.AnimationStarts
            || !ReferenceEquals(section.AcceptedInput.Clip, data.Clip) || !ReferenceEquals(section.AcceptedInput.Blend, data.Blend)
            || !ReferenceEquals(section.AcceptedInput.StateMachine, data.StateMachine)
            || section.AcceptedBinding != data.BindingVersion || !section.AcceptedCpuBones.Equals(data.CpuBones)
            || section.AcceptedExtractRoot != data.ExtractRootMotion;
        float interval = data.Evaluation?.Interval ?? 0;
        section.SampleSource = data.IsGpu && (section.ResetSamples
            || section.AdvanceSource && data.CalculationTime >= section.SampleTime + interval);
        double current = section.SampleSource ? data.CalculationTime : section.SampleTime;
        double previous = section.SampleSource ? section.ResetSamples ? current : section.SampleTime : section.PreviousSampleTime;
        float amount = interval == 0 || current == previous ? 1 : (float)Math.Clamp((time - interval - previous) / (current - previous), 0, 1);
        section.Presentation = new(previous, current, previous + (current - previous) * amount, amount)
        {
            PreviousInput = section.SampleSource ? section.ResetSamples ? data.Identity : section.SampleIdentity : section.PreviousSampleIdentity,
            CurrentInput = section.SampleSource ? data.Identity : section.SampleIdentity,
        };
    }
}
