using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class PoseBlendingBehaviorTests
{
    [Fact]
    public void GraphWeightsBlendTranslationRotationAndScaleBeforeModelComposition()
    {
        using var world = new World();
        Skeleton skeleton = CreateSkeleton();
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 3, output: new PoseResultId(2));
        PoseResultReference output = PoseAnimation.Resolve(world, target, new PoseResultId(2));
        AnimationClip first = MovingClip(skeleton, 1, new BoneTransform
        {
            Rotation = Quaternion.Identity, Scale = Vector3.One,
        }, 2);
        AnimationClip second = MovingClip(skeleton, 2, new BoneTransform
        {
            Translation = new(4, 2, 0), Rotation = new(0, 0, 1, 0), Scale = new(5, 3, 1),
        }, 6);
        var blend = new PoseBlend([new(new PoseResultId(0), Parameter: 0), new(new PoseResultId(1), Parameter: 1)]);
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(first, new PoseResultId(0), RootMotion: RootMotionMode.Extract));
            using (builder.Sequence())
            {
                builder.Delay(0.25f);
                builder.Leaf<ClipParameters, ClipLeaf>(target, new(second, new PoseResultId(1), RootMotion: RootMotionMode.Extract));
            }
            builder.BlendPose(target, new PoseBlendParameters(blend, new PoseResultId(2), new(StageEnd.Duration, 1)));
        }
        Entity animation = builder.Play();
        GraphParameters parameters = world.Read<GraphParameters>(animation);
        parameters.Values[0] = 3;
        parameters.Values[1] = 1;
        world.Replace(animation, parameters);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, static composition =>
            AnimationSystems.Add(composition, new PoseBlendSystem(), AnimationPhase.Composition));

        Tick(systems, world, 0.5);
        Near(new Vector3(1, 0.5f, 0), instance.LocalPose[0].Translation);
        Near(new Vector3(2, 1.5f, 1), instance.LocalPose[0].Scale);
        Near(new Vector3(0.8f, 0.6f, 0), Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation));
        Near(new Vector3(0.1f, 1.7f, 0), instance.ModelPose.Transforms[1].Translation);
        PoseResult firstResult = PoseAnimation.ReadResult(world, in output);
        Assert.Equal(0.4375, firstResult.Time);
        Assert.Equal(0.40625, firstResult.Phase);
        Near(new Vector3(1.125f, 0, 0), firstResult.RootMotion.Translation);

        parameters.Values[0] = 1;
        parameters.Values[1] = 3;
        world.Replace(animation, parameters);
        Tick(systems, world, 0.5);
        Near(new Vector3(3, 1.5f, 0), instance.LocalPose[0].Translation);
        Near(new Vector3(4, 2.5f, 1), instance.LocalPose[0].Scale);
        Near(new Vector3(-0.8f, 0.6f, 0), Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation));
        Near(new Vector3(1.5f, -0.5f, 0), instance.ModelPose.Transforms[1].Translation);
        PoseResult secondResult = PoseAnimation.ReadResult(world, in output);
        Assert.Equal(0.8125, secondResult.Time);
        Assert.Equal(0.53125, secondResult.Phase);
        Near(new Vector3(2.5f, 0, 0), secondResult.RootMotion.Translation);
    }

    [Fact]
    public void SharedOneDimensionalSpaceKeepsEachInstancesCoordinateAndCyclePhase()
    {
        using var world = new World();
        Skeleton skeleton = CreateSkeleton();
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton);
        using SkeletonInstance second = storage.CreateInstance(skeleton);
        Entity firstTarget = PoseAnimation.CreateTarget(world, first);
        Entity secondTarget = PoseAnimation.CreateTarget(world, second);
        BoneTransform root = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
        var space = BlendSpace.From1D([
            new(0, MovingClip(skeleton, 1, root, 2)),
            new(2, MovingClip(skeleton, 2, root, 3), PhaseOffset: 0.25f),
            new(4, MovingClip(skeleton, 4, root, 2.5f)),
        ]);
        using var builder = new AnimationBuilder(world);
        builder.BlendPose1D(Entity.Null, space, new PoseResultId(0), parameter: 0);
        using CompiledGraph graph = builder.Compile();
        Entity firstAnimation = graph.Play(world, firstTarget);
        Entity secondAnimation = graph.Play(world, secondTarget);
        GraphParameters firstParameters = world.Read<GraphParameters>(firstAnimation);
        GraphParameters secondParameters = world.Read<GraphParameters>(secondAnimation);
        firstParameters.Values[0] = 1;
        secondParameters.Values[0] = 4;
        world.Replace(firstAnimation, firstParameters);
        world.Replace(secondAnimation, secondParameters);
        PoseResultReference firstOutput = PoseAnimation.Resolve(world, firstTarget, new PoseResultId(0));
        PoseResultReference secondOutput = PoseAnimation.Resolve(world, secondTarget, new PoseResultId(0));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, static composition =>
            AnimationSystems.Add(composition, new BlendSpaceSystem(), AnimationPhase.Composition));

        Tick(systems, world, 0.5);
        Near(new Vector3(2.25f, 1, 0), first.ModelPose.Transforms[1].Translation);
        Near(new Vector3(1.25f, 1, 0), second.ModelPose.Transforms[1].Translation);
        Assert.Equal(0.375, PoseAnimation.ReadResult(world, in firstOutput).Phase);
        Assert.Equal(0.125, PoseAnimation.ReadResult(world, in secondOutput).Phase);

        firstParameters.Values[0] = 3;
        world.Replace(firstAnimation, firstParameters);
        Tick(systems, world, 0.5);
        Near(new Vector3(5.25f, 1, 0), first.ModelPose.Transforms[1].Translation);
        Near(new Vector3(2.5f, 1, 0), second.ModelPose.Transforms[1].Translation);
        Assert.Equal(0.5625, PoseAnimation.ReadResult(world, in firstOutput).Phase);
        Assert.Equal(0.25, PoseAnimation.ReadResult(world, in secondOutput).Phase);
    }

    [Fact]
    public void TwoDimensionalSpaceBlendsTheSelectedTriangleAtItsContinuingPhase()
    {
        using var world = new World();
        Skeleton skeleton = CreateSkeleton();
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance);
        BoneTransform root = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
        BoneTransform quarterTurn = new() { Rotation = new(0, 0, 0.70710677f, 0.70710677f), Scale = Vector3.One };
        BoneTransform sameTurn = new() { Rotation = new(0, 0, -0.70710677f, -0.70710677f), Scale = Vector3.One };
        var space = BlendSpace.From2D([
            new(new(0, 0), MovingClip(skeleton, 1, root, 2)),
            new(new(2, 0), MovingClip(skeleton, 2, quarterTurn, 3)),
            new(new(0, 2), MovingClip(skeleton, 4, sameTurn, 2.5f)),
            new(new(2, 2), MovingClip(skeleton, 1, root, 14), PhaseOffset: 0.25f),
        ], [new(0, 1, 2), new(1, 3, 2)]);
        using var builder = new AnimationBuilder(world);
        builder.BlendPose2D(target, space, new PoseResultId(0), parameterX: 0, parameterY: 1);
        Entity animation = builder.Play();
        GraphParameters parameters = world.Read<GraphParameters>(animation);
        parameters.Values[0] = parameters.Values[1] = 0.5f;
        world.Replace(animation, parameters);
        PoseResultReference output = PoseAnimation.Resolve(world, target, new PoseResultId(0));
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, static composition =>
            AnimationSystems.Add(composition, new BlendSpaceSystem(), AnimationPhase.Composition));

        Tick(systems, world, 0.5);
        Near(new Vector3(1.71875f, 0, 0), instance.LocalPose[0].Translation);
        Near(new Vector3(0.7071068f, 0.7071068f, 0), Vector3.Transform(Vector3.UnitX, instance.LocalPose[0].Rotation));
        Near(new Vector3(1.0116432f, 0.7071068f, 0), instance.ModelPose.Transforms[1].Translation);
        Assert.Equal(0.34375, PoseAnimation.ReadResult(world, in output).Phase);

        parameters.Values[0] = parameters.Values[1] = 1.5f;
        world.Replace(animation, parameters);
        Tick(systems, world, 0.5);
        Near(new Vector3(9.3125f, 0, 0), instance.LocalPose[0].Translation);
        Near(new Vector3(8.6053934f, 0.7071068f, 0), instance.ModelPose.Transforms[1].Translation);
        Assert.Equal(0.6875, PoseAnimation.ReadResult(world, in output).Phase);
    }

    [Fact]
    public void DependentBlendsConsumeThisFramesProducerResultInAnyDeclarationOrder()
    {
        using var world = new World();
        Skeleton skeleton = CreateSkeleton();
        var storage = new PoseStorage();
        using SkeletonInstance instance = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, instance, resultCount: 4, output: new PoseResultId(3));
        BoneTransform root = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
        BoneTransform shifted = new() { Translation = new(4, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
        var producer = new PoseBlend([new(new PoseResultId(0)), new(new PoseResultId(1))]);
        var consumer = new PoseBlend([new(new PoseResultId(2), Weight: 3), new(new PoseResultId(0))]);
        using var builder = new AnimationBuilder(world);
        using (builder.Parallel())
        {
            builder.BlendPose(target, new(consumer, new PoseResultId(3), new(StageEnd.Duration, 1)));
            builder.BlendPose(target, new(producer, new PoseResultId(2), new(StageEnd.Duration, 1)));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(MovingClip(skeleton, 1, root, 2), new PoseResultId(0)));
            builder.Leaf<ClipParameters, ClipLeaf>(target, new(MovingClip(skeleton, 1, shifted, 4), new PoseResultId(1)));
        }
        builder.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems, static composition =>
            AnimationSystems.Add(composition, new PoseBlendSystem(), AnimationPhase.Composition));

        Tick(systems, world, 0.5);
        Near(new Vector3(2.875f, 1, 0), instance.ModelPose.Transforms[1].Translation);
        Tick(systems, world, 0.5);
        Near(new Vector3(4.25f, 1, 0), instance.ModelPose.Transforms[1].Translation);
    }

    private static Skeleton CreateSkeleton() => new([-1, 0],
        [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
         new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2]);

    private static AnimationClip MovingClip(Skeleton skeleton, float duration, BoneTransform root, float speed) =>
        new(skeleton, duration, ClipSampler.Create<MovingRoot, MovingSampler>(new MovingRoot(root, speed)), rootMotionBone: 0);

    private static void Tick(SystemGroup<ImmediateSystemContext> systems, World world, double seconds)
    {
        var context = new ImmediateSystemContext(world, 0, 0, seconds);
        systems.Update(ref context);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0, 0.00001f);

    private sealed record MovingRoot(BoneTransform Root, float Speed);
    private readonly struct MovingSampler : IClipSampler<MovingRoot>
    {
        public static void SampleLocal(MovingRoot data, float time, Span<BoneTransform> destination)
        {
            destination[0] = data.Root;
            destination[0].Translation.X += data.Speed * time;
        }
    }
}
