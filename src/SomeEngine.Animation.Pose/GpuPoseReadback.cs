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
public sealed record GpuPoseReadback(uint Starts, ulong InputNumber, double CalculationTime, float SampleTime,
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

/// <summary>A selected own-bone range from the current input, delivered after its GPU copy completes.</summary>
public sealed class GpuPoseReadbackRequest
{
    private readonly TaskCompletionSource<GpuPoseReadback> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public GpuPoseIdentity Identity { get; }
    public PoseRange Range { get; }
    public Task<GpuPoseReadback> Completion => _completion.Task;

    internal GpuPoseReadbackRequest(GpuPoseIdentity identity, PoseRange range) => (Identity, Range) = (identity, range);

    /// <summary>Called by the extracted GPU producer after the accepted copy's queue completion.</summary>
    public void Deliver(GpuPoseReadback result) => _completion.SetResult(result);
}

public static partial class GpuPoseAnimation
{
    /// <summary>Requests own bones after this update's GPU input has been formed, before extraction.</summary>
    public static GpuPoseReadbackRequest RequestReadback(World world, Entity target, PoseRange range)
    {
        GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
        var request = new GpuPoseReadbackRequest(new(target, input.Skeleton, input.Starts, input.InputNumber, input.AnimationStarts), range);
        var requests = new List<GpuPoseReadbackRequest>();
        foreach (GpuPoseReadbackRequest previous in input.Readbacks.Span)
            if (!previous.Completion.IsCompleted) requests.Add(previous);
        requests.Add(request);
        input.Readbacks = requests.ToArray();
        world.Replace(target, input);
        return request;
    }

    /// <summary>Submits running until the requested input's completed feedback matches this leaf's current execution.</summary>
    public static bool SubmitFeedback(World world, Entity animation, Entity target, in AnimationLeafPlacement placement,
        GpuPoseReadbackRequest request, bool finished)
    {
        AnimationLeafTime time = AnimationGraph.ReadTime(world, animation, in placement);
        if (!time.Active) return false;
        GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
        bool accepted = false;
        if (request.Completion.IsCompletedSuccessfully)
        {
            GpuPoseReadback result = request.Completion.Result;
            accepted = result.Identity == request.Identity && result.Range == request.Range
                && result.Identity.Target == target && ReferenceEquals(result.Identity.Instance, input.Skeleton)
                && result.Identity.Starts == time.Starts && input.Starts == time.Starts
                && result.Identity.AnimationStarts == placement.Starts && input.AnimationStarts == placement.Starts;
        }
        AnimationGraph.Submit(world, animation, in placement, !accepted || !finished);
        return accepted;
    }
}
