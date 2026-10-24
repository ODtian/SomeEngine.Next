using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>A body's local frame expressed in its own bone's local frame.</summary>
public readonly record struct PhysicsBone(int Bone, Matrix4x4 BodyInBone);

/// <summary>Shared body order and offsets for a skeleton's own bones.</summary>
public sealed class PhysicsPoseMap
{
    private readonly PhysicsBone[] _bodies;
    public Skeleton Skeleton { get; }
    public ReadOnlySpan<PhysicsBone> Bodies => _bodies;
    internal readonly Matrix4x4[] BoneInBody;
    internal readonly int[] BoneBodies;

    public PhysicsPoseMap(Skeleton skeleton, ReadOnlySpan<PhysicsBone> bodies)
    {
        Skeleton = skeleton;
        _bodies = bodies.ToArray();
        BoneInBody = new Matrix4x4[bodies.Length];
        BoneBodies = new int[skeleton.BoneCount];
        Array.Fill(BoneBodies, -1);
        for (int body = 0; body < bodies.Length; body++)
        {
            BoneBodies[bodies[body].Bone] = body;
            Matrix4x4.Invert(bodies[body].BodyInBone, out BoneInBody[body]);
        }
    }
}

/// <summary>Blends completed body transforms into the current animation using a mask and common layer weight.</summary>
public readonly record struct PhysicsPoseParameters(PoseResultId Source, PoseResultId Output, PhysicsPoseMap Map, BoneMask Mask);

[BufferCapacity(1)]
public struct PhysicsPoseInstance : IBufferElement
{
    public AnimationLeafBatch<PhysicsPoseParameters> Definition;
    public int Index;
    public AnimationLeafPlacement Placement;
    public PoseResultReference Source, Output;
    /// <summary>The actor's model-to-world transform for the completed physics step.</summary>
    public Matrix4x4 ActorTransform;
    /// <summary>Completed body-local-to-world transforms in Map.Bodies order, filled before node evaluation.</summary>
    public Span<Matrix4x4> BodyTransforms => Samples.BodyTransforms;
    internal PhysicsPoseSamples Samples;
    internal PoseNode Node;
    internal PoseResult InputResult;
    internal float Weight;
    internal bool Active, Sampled;
}

internal sealed class PhysicsPoseSamples
{
    internal readonly BoneTransform[] Source, Output;
    internal readonly Matrix4x4[] BodyTransforms;
    internal readonly PhysicsPoseMap Map;
    internal readonly BoneMask Mask;
    internal readonly JobResourceAccess[] Accesses;
    private readonly Matrix4x4[] _animationModels, _physicsModels, _inherited;
    private readonly BonePose[] _parents;

    internal PhysicsPoseSamples(PhysicsPoseMap map, BoneMask mask, SkeletonInstance skeleton)
    {
        Map = map;
        Mask = mask;
        Source = new BoneTransform[map.Skeleton.BoneCount];
        Output = new BoneTransform[map.Skeleton.BoneCount];
        BodyTransforms = new Matrix4x4[map.Bodies.Length];
        _animationModels = new Matrix4x4[Source.Length];
        _physicsModels = new Matrix4x4[Source.Length];
        _inherited = new Matrix4x4[Source.Length];
        _parents = new BonePose[Source.Length];
        for (int bone = 0; bone < Source.Length; bone++)
            if (map.Skeleton.InheritedParents[bone] >= 0)
                _parents[bone] = skeleton.ModelPose.GetBone(map.Skeleton.InheritedParents[bone]);
        Accesses = [JobResourceAccess.Read(Source), JobResourceAccess.Read(BodyTransforms), JobResourceAccess.Write(Output),
            JobResourceAccess.Write(_animationModels), JobResourceAccess.Write(_physicsModels), JobResourceAccess.Read(_inherited),
            JobResourceAccess.Read(JobSystem.Default.GetResourceToken(map)),
            JobResourceAccess.Read(JobSystem.Default.GetResourceToken(mask))];
    }

    internal void ReadParents()
    {
        for (int bone = 0; bone < Source.Length; bone++)
            if (Map.Skeleton.InheritedParents[bone] >= 0) _inherited[bone] = _parents[bone].Transform;
    }

    internal void Blend(in Matrix4x4 actor, float weight)
    {
        Source.CopyTo(Output, 0);
        if (weight == 0) return;
        ReadOnlySpan<short> parents = Map.Skeleton.Parents;
        for (int bone = 0; bone < Source.Length; bone++)
            _animationModels[bone] = Source[bone].ToMatrix() * (parents[bone] >= 0
                ? _animationModels[parents[bone]] : RootParent(bone));
        Matrix4x4.Invert(actor, out Matrix4x4 worldToActor);
        for (int body = 0; body < BodyTransforms.Length; body++)
            _physicsModels[Map.Bodies[body].Bone] = Map.BoneInBody[body] * BodyTransforms[body] * worldToActor;
        for (int bone = 0; bone < Output.Length; bone++)
        {
            float influence = weight * Mask.Weights[bone];
            if (Map.BoneBodies[bone] < 0 || influence == 0) continue;
            int parent = parents[bone];
            Matrix4x4 parentModel = parent >= 0
                ? Map.BoneBodies[parent] >= 0 ? _physicsModels[parent] : _animationModels[parent]
                : RootParent(bone);
            Matrix4x4.Invert(parentModel, out Matrix4x4 modelToParent);
            Matrix4x4 local = _physicsModels[bone] * modelToParent;
            Matrix4x4.Decompose(local, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
            var physics = new BoneTransform { Translation = translation, Rotation = rotation, Scale = scale };
            Output[bone] = BoneTransformOps.Lerp(in Source[bone], in physics, influence);
        }
    }

    private Matrix4x4 RootParent(int bone) => Map.Skeleton.InheritedParents[bone] >= 0
        ? _inherited[bone] : Matrix4x4.Identity;
}

public readonly struct PhysicsPoseLeaf : IAnimationLeaf<PhysicsPoseParameters>
{
    public static MotionBoundary Describe(in PhysicsPoseParameters parameters) => new(StageEnd.Settled);

    public static void Install(World world, Entity animation, Entity target,
        AnimationLeafBatch<PhysicsPoseParameters> definitions, int index, in AnimationLeafPlacement placement)
    {
        if (!world.HasBuffer<PhysicsPoseInstance>(animation)) world.AddBuffer<PhysicsPoseInstance>(animation);
        PhysicsPoseParameters parameters = definitions.Parameters[index];
        var instance = new PhysicsPoseInstance
        {
            Definition = definitions, Index = index, Placement = placement,
            Source = PoseAnimation.Resolve(world, target, parameters.Source),
            Output = PoseAnimation.Resolve(world, target, parameters.Output),
            ActorTransform = Matrix4x4.Identity,
            Samples = new(parameters.Map, parameters.Mask, world.Read<PoseTarget>(target).Skeleton),
        };
        short[] bones = parameters.Map.Bodies.ToArray().Select(static body => (short)body.Bone).ToArray();
        instance.Node = PoseAnimation.RegisterNode(world, animation, instance.Output, [instance.Source], in placement, bones);
        world.ExecuteBufferWrite<PhysicsPoseInstance, PhysicsPoseInstance>(animation, ref instance,
            static (instances, ref instance) => instances.Add(instance));
    }

    public static void Complete(World world, Entity animation) => PoseAnimation.CompleteNodes(world, animation);

    public static void Release(World world, Entity animation)
    {
        world.ExecuteBufferWrite<PhysicsPoseInstance>(animation, static instances => instances.Clear());
        PoseAnimation.ReleaseNodes(world, animation);
    }
}

public static partial class PoseGraphExtensions
{
    public static void BlendPhysicsPose(this AnimationBuilder builder, Entity target, in PhysicsPoseParameters parameters, byte layer = 0) =>
        builder.Leaf<PhysicsPoseParameters, PhysicsPoseLeaf>(target, in parameters, layer);
}
