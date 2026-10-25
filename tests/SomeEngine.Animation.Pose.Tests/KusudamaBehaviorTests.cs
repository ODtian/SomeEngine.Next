using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class KusudamaBehaviorTests
{
    [Fact]
    public void MultipleTargetsCrossTheConnectedSwingCorridorWithLimitedTwistAndIndependentSoftPreference()
    {
        // Two short branches are example geometry for a reusable connected-cone constraint in a rotated parent/frame.
        Quaternion parent = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .3f);
        Quaternion frame = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .35f) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f);
        Quaternion limitingModel = parent * frame;
        var skeleton = new Skeleton([-1, 0, 1, 0, 3],
            [Bone(Vector3.Zero, parent), Bone(-Vector3.UnitX, frame), Bone(Vector3.UnitY, Quaternion.Identity),
             Bone(Vector3.UnitX, Quaternion.Identity), Bone(Vector3.UnitY, Quaternion.Identity)], [1, 2, 3, 4, 5]);
        Vector3 a = new(-.5f, .8660254f, 0), b = new(.5f, .8660254f, 0);
        var region = new Kusudama([new(a, .12f), new(b, .12f)], frame, Vector3.UnitY, -.2f, .25f);
        var rig = new EwbikRig(skeleton, 0, [new(2), new(1), new(4)], bones: [new(0, 0)],
            settings: new(Damping: .18f, Iterations: 192, PositionTolerance: .001f, OrientationTolerance: .001f),
            constraints: [new(1, region)]);
        var clip = new AnimationClip(skeleton, 2, ClipSampler.Create<Skeleton, ReferenceSampler>(skeleton));
        using var world = new World();
        var storage = new PoseStorage();
        using SkeletonInstance hard = storage.CreateInstance(skeleton), soft = storage.CreateInstance(skeleton);
        Entity hardTarget = PoseAnimation.CreateTarget(world, hard, 3, new(2));
        Entity softTarget = PoseAnimation.CreateTarget(world, soft, 3, new(2));
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.BlendPose(Entity.Null, new(new PoseBlend([new(new(1))]), new(2), new(StageEnd.Settled)));
            builder.SolveEwbik(Entity.Null, new(rig, new(0), new(1)));
            builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clip, new(0), Loop: true));
        }
        using CompiledGraph graph = builder.Compile();
        Entity hardAnimation = graph.Play(world, hardTarget), softAnimation = graph.Play(world, softTarget);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, nodes: static nodes => { nodes.Add(new PoseBlendSystem()); nodes.Add(new EwbikSystem()); });
        Vector3 origin = Vector3.Transform(-Vector3.UnitX, parent), rightOrigin = Vector3.Transform(Vector3.UnitX, parent);
        Vector3 rightTip = rightOrigin + Vector3.Transform(new Vector3(0, .8660254f, .5f), parent);
        Quaternion rightOrientation = parent * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6);
        Vector3 previous = default;
        Quaternion previousRotation = Quaternion.Identity;
        float[] twists = [-.2f, -.2f, -.2f, -.175f, 0, .175f, .25f, .25f, .25f];
        var context = new ImmediateSystemContext(world, 0, 0, .1);
        for (int step = 0; step < 9; step++)
        {
            float heading = -.2f + .05f * step;
            Vector3 direction = new(MathF.Sin(heading) * MathF.Cos(.06f), MathF.Cos(heading) * MathF.Cos(.06f), MathF.Sin(.06f));
            SetTargets(direction, -.7f + .175f * step);
            systems.Update(ref context);
            foreach (SkeletonInstance instance in new[] { hard, soft })
            {
                Quaternion relative = Relative(instance);
                Vector3 actual = Vector3.Transform(Vector3.UnitY, relative);
                Near(direction, actual);
                Assert.True(Vector3.Dot(actual, a) < MathF.Cos(.12f) - .02f);
                Assert.True(Vector3.Dot(actual, b) < MathF.Cos(.12f) - .02f);
                Assert.InRange(Twist(relative), twists[step] - .004f, twists[step] + .004f);
                Near(origin + Vector3.Transform(direction, limitingModel), instance.ModelPose.GetBone(2).Transform.Translation);
                Near(rightTip, instance.ModelPose.GetBone(4).Transform.Translation);
            }
            Quaternion currentRotation = Relative(hard);
            Vector3 current = Vector3.Transform(Vector3.UnitY, currentRotation);
            if (step > 0)
            {
                Assert.True(Vector3.Distance(previous, current) < .07f);
                Assert.True(MathF.Abs(Quaternion.Dot(previousRotation, currentRotation)) > .994f);
            }
            previous = current;
            previousRotation = currentRotation;
        }

        SetTargets(new(0, MathF.Cos(.35f), MathF.Sin(.35f)), .8f);
        systems.Update(ref context);
        Vector3 boundary = Vector3.Transform(Vector3.UnitY, Relative(hard));
        // The outside goal is limited by the corridor's z = sin(.12) tangent-circle boundary.
        Assert.InRange(boundary.Z, .1157122f, .1237122f);
        Assert.True(boundary.Y > .98f);
        Assert.InRange(Twist(Relative(hard)), .246f, .254f);
        previous = Vector3.Transform(Vector3.UnitY, Relative(soft));
        for (int step = 1; step <= 5; step++)
        {
            float weight = .1f * step;
            world.ExecuteBufferWrite<EwbikInstance, float>(softAnimation, ref weight,
                static (instances, ref weight) => instances[0].PreferenceWeights[0] = weight);
            systems.Update(ref context);
            Vector3 actual = Vector3.Transform(Vector3.UnitY, Relative(soft));
            Assert.InRange(MathF.Abs(actual.Z), 0, .1200f);
            Assert.InRange(Twist(Relative(soft)), -.204f, .254f);
            Assert.True(Vector3.Distance(previous, actual) < .08f);
            Near(rightTip, soft.ModelPose.GetBone(4).Transform.Translation);
            previous = actual;
        }
        Assert.True(MathF.Abs(previous.Z) < MathF.Abs(boundary.Z) - .02f);
        Assert.True(MathF.Abs(Twist(Relative(soft)) - .025f) < MathF.Abs(Twist(Relative(hard)) - .025f) - .02f);
        Assert.True(hard.ModelPose.Version > 10);
        Assert.Equal(1.5, soft.ModelPose.SampleTime, 5);

        Quaternion Relative(SkeletonInstance instance)
        {
            Matrix4x4.Decompose(instance.ModelPose.GetBone(1).Transform, out _, out Quaternion rotation, out _);
            return Quaternion.Normalize(Quaternion.Conjugate(limitingModel) * rotation);
        }
        void SetTargets(Vector3 direction, float twist)
        {
            Quaternion orientation = limitingModel * Aim(direction) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, twist);
            EwbikTarget[] targets = [new(origin + Vector3.Transform(direction, limitingModel), orientation, Weight: 50),
                new(origin, orientation, AxisPriorities: Vector3.One), new(rightTip, rightOrientation, AxisPriorities: Vector3.One)];
            world.ExecuteBufferWrite<EwbikInstance, EwbikTarget[]>(hardAnimation, ref targets,
                static (instances, ref targets) => targets.CopyTo(instances[0].Targets, 0));
            world.ExecuteBufferWrite<EwbikInstance, EwbikTarget[]>(softAnimation, ref targets,
                static (instances, ref targets) => targets.CopyTo(instances[0].Targets, 0));
        }
    }

    private static Quaternion Aim(Vector3 direction) => Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitY, direction), 1 + direction.Y));
    private static float Twist(Quaternion relative)
    {
        Vector3 direction = Vector3.Transform(Vector3.UnitY, relative);
        Quaternion turn = Quaternion.Conjugate(Aim(direction)) * relative;
        Vector3 x = Vector3.Transform(Vector3.UnitX, turn);
        return MathF.Atan2(-x.Z, x.X);
    }
    private static BoneTransform Bone(Vector3 position, Quaternion rotation) => new() { Translation = position, Rotation = rotation, Scale = Vector3.One };
    private static void Near(Vector3 expected, Vector3 actual) => Assert.True(Vector3.Distance(expected, actual) < .004f, $"Expected {expected}; actual {actual}.");
    private readonly struct ReferenceSampler : IClipSampler<Skeleton>
    {
        public static void SampleLocal(Skeleton skeleton, float time, Span<BoneTransform> destination) => skeleton.ReferencePose.CopyTo(destination);
    }
}
