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

    public static void SelectBones(Span<bool> selected, GpuPoseEvaluation? evaluation, ReadOnlySpan<short> cpuBones, GpuPoseRig? rig)
    {
        selected.Clear();
        if (evaluation is null || evaluation.AllBones) selected.Fill(true);
        else foreach (short bone in evaluation.Bones) selected[bone] = true;
        foreach (short bone in cpuBones) selected[bone] = true;
        if (rig is null) return;
        if (rig.TranslationBone >= 0) selected[rig.TranslationBone] = true;
        foreach (GpuPoseSource operation in rig.Steps)
            switch (operation)
            {
                case GpuTwoBonePose limb:
                    selected[limb.Chain.Root] = selected[limb.Chain.Middle] = selected[limb.Chain.Tip] = true;
                    break;
                case GpuXpbdPose chain:
                    foreach (GpuXpbdBone bone in chain.Chain.Bones) selected[bone.Bone] = true;
                    break;
                case GpuStridePose stride:
                    selected[stride.Rig.PelvisBone] = true;
                    foreach (GpuStrideLeg leg in stride.Rig.Legs)
                        selected[leg.Chain.Root] = selected[leg.Chain.Middle] = selected[leg.Chain.Tip] = true;
                    foreach (GpuStrideSpine bone in stride.Rig.Spine) selected[bone.Bone] = true;
                    break;
                case GpuAimPose aim:
                    foreach (GpuAimBone bone in aim.Chain.Bones) selected[bone.Bone] = true;
                    break;
            }
    }
}

/// <summary>The two actual sample calculation clocks and the local-TRS presentation between them.</summary>
public readonly record struct GpuPosePresentation(double PreviousTime, double CurrentTime, double Time, float Amount)
{
    public GpuPoseIdentity PreviousInput { get; init; }
    public GpuPoseIdentity CurrentInput { get; init; }

    public static GpuPosePresentation FromSampleTimes(double previous, double current, double time, float interval)
    {
        float amount = interval == 0 || current == previous ? 1 : (float)Math.Clamp((time - interval - previous) / (current - previous), 0, 1);
        return new(previous, current, previous + (current - previous) * amount, amount);
    }
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
