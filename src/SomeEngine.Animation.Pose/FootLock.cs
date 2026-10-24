using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Physics;

namespace SomeEngine.Animation.Pose;

/// <summary>A contact foot's GPU chain, goal number, root-local bend direction and tip-local sole normal.</summary>
public readonly record struct FootLockLeg(GpuTwoBoneChain Chain, int Goal, Vector3 BendDirection, Vector3 SoleNormal);

/// <summary>Shared pelvis and leg correspondence in the same order as the source foot tracks.</summary>
public sealed class FootLockRig
{
    private readonly FootLockLeg[] _legs;
    private readonly Vector3[] _neutralSoles;
    public int PelvisBone { get; }
    public ReadOnlySpan<FootLockLeg> Legs => _legs;
    internal ReadOnlySpan<Vector3> NeutralSoles => _neutralSoles;

    public FootLockRig(int pelvisBone, ReadOnlySpan<FootLockLeg> legs)
    {
        PelvisBone = pelvisBone;
        _legs = legs.ToArray();
        _neutralSoles = new Vector3[legs.Length];
        var storage = new PoseStorage();
        using SkeletonInstance reference = storage.CreateInstance(legs[0].Chain.Skeleton);
        for (int foot = 0; foot < legs.Length; foot++)
        {
            Matrix4x4 model = reference.ModelPose.Transforms[reference.Skeleton.InheritedBoneCount + legs[foot].Chain.Tip];
            Matrix4x4.Invert(model, out Matrix4x4 inverse);
            _neutralSoles[foot] = Vector3.Normalize(Vector3.TransformNormal(legs[foot].SoleNormal, Matrix4x4.Transpose(inverse)));
        }
    }
}

public readonly record struct FootLockParameters(FootLockRig Rig, PosePredictionRequest Source,
    ICollisionQueries Scene, FootSupportSettings Support, float ContactWindow = 1,
    float Duration = float.PositiveInfinity);

[BufferCapacity(1)]
public struct FootLockInstance : IBufferElement
{
    public AnimationLeafBatch<FootLockParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public Entity Target;
    /// <summary>The movement stage's entity providing the current WorldTransform; initially the target.</summary>
    public Entity Actor;
    public ReadOnlySpan<FootContactTarget> Contacts => Samples.Contacts;
    internal FootLockSamples Samples;
    internal uint Starts;
}

/// <summary>Installs per-character contact history alongside an ordinary GPU pose leaf.</summary>
public readonly struct FootLockLeaf : IAnimationLeaf<FootLockParameters>
{
    public static MotionBoundary Describe(in FootLockParameters parameters) => GpuRigPlayback.Boundary(parameters.Duration);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<FootLockParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<FootLockInstance>(animation)) world.AddBuffer<FootLockInstance>(animation);
        var instance = new FootLockInstance
        {
            Definition = definitions, Index = index, Placement = placement, Target = target, Actor = target, Starts = placement.Starts,
            Samples = new(definitions.Parameters[index], world.Read<GpuPoseTarget>(target).Skeleton),
        };
        world.ExecuteBufferWrite<FootLockInstance, FootLockInstance>(animation, ref instance,
            static (instances, ref instance) =>
            {
                if (instances.Count > 0 && instances.Read(0).Placement.Starts != instance.Placement.Starts) instances.Clear();
                instances.Add(instance);
            });
    }

    public static void Release(World world, Entity animation) =>
        world.ExecuteBufferWrite<FootLockInstance>(animation, static instances => instances.Clear());
}

public static partial class PoseGraphExtensions
{
    public static void LockFeet(this AnimationBuilder builder, Entity target, in FootLockParameters parameters, byte layer = 0) =>
        builder.Leaf<FootLockParameters, FootLockLeaf>(target, in parameters, layer);
}
