using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.Render;

namespace SomeEngine.Render.Features.Skinning;

/// <summary>Applies completed pose observations to their requesting GameWorld components.</summary>
public sealed class GpuPoseFeedbackSystem(RenderFeedback<GpuPoseReadback> feedback, ulong afterFrame = 0)
{
    private readonly Dictionary<GpuPoseReadbackRequest, BoneResults> _results = [];

    public void Apply(World world, ulong completedFrame)
    {
        while (feedback.TryRead(completedFrame, out RenderFeedbackRecord<GpuPoseReadback> record))
        {
            if (record.FrameIdentity <= afterFrame) continue;
            GpuPoseReadback value = record.Value;
            if (!world.IsAlive(value.Target)) continue;
            GpuPoseTarget target = world.Read<GpuPoseTarget>(value.Target);
            GpuPoseReadbackRequest? request = null;
            foreach (GpuPoseReadbackRequest candidate in target.Readbacks.Span)
                if (candidate.Identity.Starts == value.Starts && candidate.Identity.InputNumber == value.InputNumber
                    && candidate.Identity.AnimationStarts == value.AnimationStarts && candidate.Range == value.Range)
                { request = candidate; break; }
            if (request is not { } identity) continue;
            if (!_results.TryGetValue(identity, out BoneResults? result))
                _results.Add(identity, result = new(value.Range.Count));
            result.Current[value.BoneIndex] = value.Current;
            result.Previous[value.BoneIndex] = value.Previous;
            result.Calculations[value.BoneIndex] = value.Calculation;
            result.Samples[value.BoneIndex] = value.SourceSamples;
            if (value.Sampled) result.SampledBones.Add((short)(value.Range.Start + value.BoneIndex));
            if (++result.Count != value.Range.Count) continue;
            GpuPoseAnimation.ApplyReadback(world, new(value.Starts, value.InputNumber, value.CalculationTime,
                value.SampleTime, result.Current, result.Previous)
            {
                Identity = identity.Identity, Range = value.Range, Playback = value.Playback,
                Version = value.Version, SourceCalculationTime = value.SourceCalculationTime,
                Presentation = value.Presentation.WithInstance(identity.Identity.Instance), Calculations = result.Calculations,
                SampledBones = result.SampledBones.ToArray(), SourceSamples = result.Samples,
            });
            _results.Remove(identity);
        }
    }

    private sealed class BoneResults(int count)
    {
        internal Matrix4x4[] Current { get; } = new Matrix4x4[count];
        internal Matrix4x4[] Previous { get; } = new Matrix4x4[count];
        internal PoseCalculation[] Calculations { get; } = new PoseCalculation[count];
        internal GpuPoseSampleTimes[] Samples { get; } = new GpuPoseSampleTimes[count];
        internal List<short> SampledBones { get; } = [];
        internal int Count;
    }
}
