using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Pose;

/// <summary>Movement-stage velocities and desired body facing in actor model space.</summary>
public readonly record struct GpuPoseMovement(Vector3 Velocity, Vector3 AnimationVelocity, Vector3 FacingDirection, float Weight = 1);

public readonly record struct GpuStrideLeg(GpuTwoBoneChain Chain, int Goal);
public readonly record struct GpuStrideSpine(int Bone, float Weight);

/// <summary>Shared stride limits, pelvis and the legs and spine participating in movement adjustment.</summary>
public sealed class GpuStrideRig
{
    private readonly GpuStrideLeg[] _legs;
    private readonly GpuStrideSpine[] _spine;
    public int PelvisBone { get; }
    public Vector3 Up { get; }
    /// <summary>Local facing axis of the last spine joint, or the pelvis when the spine list is empty.</summary>
    public Vector3 FacingAxis { get; init; } = Vector3.UnitZ;
    public ReadOnlySpan<GpuStrideLeg> Legs => _legs;
    public ReadOnlySpan<GpuStrideSpine> Spine => _spine;
    public float MinimumScale { get; }
    public float MaximumScale { get; }
    public float MaximumAngle { get; }
    public float MaximumPelvisAdjustment { get; }

    public GpuStrideRig(int pelvisBone, Vector3 up, ReadOnlySpan<GpuStrideLeg> legs, ReadOnlySpan<GpuStrideSpine> spine,
        float minimumScale = .5f, float maximumScale = 1.5f, float maximumAngle = MathF.PI,
        float maximumPelvisAdjustment = .5f)
    {
        PelvisBone = pelvisBone; Up = Vector3.Normalize(up);
        _legs = legs.ToArray(); _spine = spine.ToArray();
        MinimumScale = minimumScale; MaximumScale = maximumScale;
        MaximumAngle = maximumAngle; MaximumPelvisAdjustment = maximumPelvisAdjustment;
    }
}

public sealed class GpuStridePose(GpuPoseSource source, GpuStrideRig rig) : GpuPoseSource
{
    public GpuPoseSource Source { get; } = source;
    public GpuStrideRig Rig { get; } = rig;
    public override Skeleton Skeleton => Source.Skeleton;
}

public readonly record struct GpuStrideParameters(GpuStridePose Pose, int TranslationBone = -1,
    float Duration = float.PositiveInfinity);

public readonly struct GpuStrideLeaf : IAnimationLeaf<GpuStrideParameters>
{
    public static MotionBoundary Describe(in GpuStrideParameters parameters) => GpuRigPlayback.Boundary(parameters.Duration);
    public static object Prepare(ReadOnlySpan<GpuStrideParameters> parameters)
    {
        var rigs = new GpuPoseRig[parameters.Length];
        for (int index = 0; index < rigs.Length; index++) rigs[index] = new(parameters[index].Pose, parameters[index].TranslationBone);
        return rigs;
    }
    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<GpuStrideParameters> definitions, int index, in AnimationLeafPlacement placement) =>
        GpuRigPlayback.Install(world, animation, target, ((GpuPoseRig[])definitions.Data!)[index],
            definitions.Parameters[index].Duration, in placement);
    public static void Release(World world, Entity animation) => GpuRigPlayback.Release(world, animation);
}
