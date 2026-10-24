using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

/// <summary>The visual sample interval and selected own bones. Other bones use their reference locals.</summary>
public sealed class GpuPoseEvaluation
{
    private readonly short[] _bones;
    public float Interval { get; }
    public bool AllBones { get; }
    public ReadOnlySpan<short> Bones => _bones;
    public GpuPoseEvaluation(float interval) => (Interval, AllBones, _bones) = (interval, true, []);
    public GpuPoseEvaluation(float interval, ReadOnlySpan<short> bones) => (Interval, _bones) = (interval, bones.ToArray());
}

/// <summary>The two actual sample calculation clocks and the local-TRS presentation between them.</summary>
public readonly record struct GpuPosePresentation(double PreviousTime, double CurrentTime, double Time, float Amount)
{
    public GpuPoseIdentity PreviousInput { get; init; }
    public GpuPoseIdentity CurrentInput { get; init; }
}

public readonly record struct GpuPoseSampleTimes(float PreviousTime, float CurrentTime);

public static partial class GpuPoseAnimation
{
    /// <summary>Installs or explicitly changes visual sampling and bone LOD.</summary>
    public static void SetEvaluation(World world, Entity target, GpuPoseEvaluation? evaluation)
    {
        GpuPoseTarget input = world.Read<GpuPoseTarget>(target);
        input.Evaluation = evaluation;
        world.Replace(target, input);
    }
}
