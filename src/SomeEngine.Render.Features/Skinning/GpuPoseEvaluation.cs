using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Features.Skinning;

public sealed partial class SkinningFeature
{
    private void WriteEvaluation(BinaryWriter writer)
    {
        bool[][] before = _sectionOrder.Select(section => section.SelectedBones).ToArray();
        foreach (Section section in _sectionOrder)
        {
            section.SelectedBones = new bool[section.Range.Count];
            GpuPoseEvaluation.SelectBones(section.SelectedBones, section.Data.Evaluation, section.Data.CpuBones.Span, section.Data.Rig);
        }
        foreach (Section section in _sectionOrder)
            for (int bone = 0; bone < section.Range.Count; bone++)
                if (section.SelectedBones[bone]) RequireParents(section, bone);
        for (int index = 0; index < _sectionOrder.Length; index++)
        {
            Section section = _sectionOrder[index];
            if (!section.SelectedBones.AsSpan().SequenceEqual(before[index])) section.HasSamples = false;
            var sampled = new List<short>();
            section.ReferenceOffset = new GpuPoseDefinitions(writer).WriteReference(section.Data.Skeleton, section.SelectedBones);
            for (int bone = 0; bone < section.Range.Count; bone++)
            {
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
        ref readonly RenderBoneSectionData data = ref section.Data;
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
        section.Presentation = GpuPosePresentation.FromSampleTimes(previous, current, time, interval) with
        {
            PreviousInput = section.SampleSource ? section.ResetSamples ? data.Identity : section.SampleIdentity : section.PreviousSampleIdentity,
            CurrentInput = section.SampleSource ? data.Identity : section.SampleIdentity,
        };
    }
}
