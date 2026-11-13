using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

/// <summary>The instance and source input that produced a GPU pose.</summary>
public readonly record struct GpuPoseIdentity(Entity Target, SkeletonInstance Instance, uint Starts, ulong InputNumber,
    uint AnimationStarts);

/// <summary>The GPU-selected state clocks and crossfade weights accompanying a model pose.</summary>
public readonly record struct GpuPosePlayback(int State, int SourceState, float Time, float SourceTime,
    float SourceSampleTime, float TransitionWeight, bool Finished);

/// <summary>Independently retained, read-only matrices and calculation data from one submitted GPU input.</summary>
public sealed record GpuPoseReadbackResult(uint Starts, ulong InputNumber, double CalculationTime, float SampleTime,
    ReadOnlyMemory<Matrix4x4> Current, ReadOnlyMemory<Matrix4x4> Previous)
{
    public GpuPoseIdentity Identity { get; init; }
    public PoseRange Range { get; init; }
    public GpuPosePlayback Playback { get; init; }
    public ulong Version { get; init; }
    public double SourceCalculationTime { get; init; }
    public ReadOnlyMemory<PoseCalculation> Calculations { get; init; }
    public GpuPosePresentation Presentation { get; init; }
    public ReadOnlyMemory<short> SampledBones { get; init; }
    public ReadOnlyMemory<GpuPoseSampleTimes> SourceSamples { get; init; }
}

/// <summary>A selected own-bone range from one extracted GPU input.</summary>
public readonly record struct GpuPoseReadbackRequest(GpuPoseIdentity Identity, PoseRange Range);

/// <summary>One completed bone observation carried through the render feedback queue.</summary>
public readonly record struct GpuPoseReadback(Entity Target, uint Starts, ulong InputNumber, uint AnimationStarts,
    PoseRange Range, int BoneIndex, Matrix4x4 Current, Matrix4x4 Previous, PoseCalculation Calculation,
    bool Sampled, GpuPoseSampleTimes SourceSamples, GpuPoseFeedbackPresentation Presentation, GpuPosePlayback Playback,
    ulong Version, double CalculationTime, double SourceCalculationTime, float SampleTime);

public readonly record struct GpuPoseFeedbackInput(Entity Target, uint Starts, ulong InputNumber, uint AnimationStarts)
{
    public static GpuPoseFeedbackInput From(in GpuPoseIdentity identity) =>
        new(identity.Target, identity.Starts, identity.InputNumber, identity.AnimationStarts);
    public GpuPoseIdentity WithInstance(SkeletonInstance instance) => new(Target, instance, Starts, InputNumber, AnimationStarts);
}

public readonly record struct GpuPoseFeedbackPresentation(double PreviousTime, double CurrentTime, double Time,
    float Amount, GpuPoseFeedbackInput PreviousInput, GpuPoseFeedbackInput CurrentInput)
{
    public static GpuPoseFeedbackPresentation From(in GpuPosePresentation value) =>
        new(value.PreviousTime, value.CurrentTime, value.Time, value.Amount,
            GpuPoseFeedbackInput.From(value.PreviousInput), GpuPoseFeedbackInput.From(value.CurrentInput));
    public GpuPosePresentation WithInstance(SkeletonInstance instance) => new(PreviousTime, CurrentTime, Time, Amount)
    { PreviousInput = PreviousInput.WithInstance(instance), CurrentInput = CurrentInput.WithInstance(instance) };
}

public static partial class GpuPoseAnimation
{
    public static GpuPoseReadbackRequest RequestReadback(World world, Entity target, PoseRange range)
    {
        GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
        var request = new GpuPoseReadbackRequest(new(target, input.Skeleton, input.Starts, input.InputNumber,
            input.AnimationStarts), range);
        if (!input.Readbacks.Span.Contains(request))
        {
            input.Readbacks = input.Readbacks.ToArray().Append(request).ToArray();
            world.Replace(target, input);
        }
        return request;
    }

    public static bool TryGetReadback(World world, in GpuPoseReadbackRequest request, out GpuPoseReadbackResult result)
    {
        foreach (GpuPoseReadbackResult value in world.Read<GpuPoseTarget>(request.Identity.Target).CompletedReadbacks.Span)
            if (value.Identity == request.Identity && value.Range == request.Range) { result = value; return true; }
        result = null!;
        return false;
    }

    public static void ApplyReadback(World world, GpuPoseReadbackResult result)
    {
        GpuPoseTarget target = world.Read<GpuPoseTarget>(result.Identity.Target);
        var request = new GpuPoseReadbackRequest(result.Identity, result.Range);
        if (!target.Readbacks.Span.Contains(request)) return;
        target.Readbacks = target.Readbacks.ToArray().Where(value => value != request).ToArray();
        target.CompletedReadbacks = target.CompletedReadbacks.ToArray().Append(result).ToArray();
        world.Replace(result.Identity.Target, target);
    }

    public static bool SubmitFeedback(World world, Entity animation, Entity target, in AnimationLeafPlacement placement,
        GpuPoseReadbackRequest request, bool finished)
    {
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in placement);
        if (!time.Active) return false;
        GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
        bool accepted = TryGetReadback(world, in request, out GpuPoseReadbackResult result)
            && result.Identity.Target == target && ReferenceEquals(result.Identity.Instance, input.Skeleton)
            && result.Identity.Starts == time.Starts && input.Starts == time.Starts
            && result.Identity.AnimationStarts == placement.Starts && input.AnimationStarts == placement.Starts;
        AnimationGraph.Submit(world, animation, in placement, !accepted || !finished);
        return accepted;
    }
}
