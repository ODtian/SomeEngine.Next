using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Checks;

internal static class GpuStateChecks
{
    internal static void Check()
    {
        using var world = new World();
        var skeleton = new Skeleton([-1], [new BoneTransform { Rotation = Quaternion.Identity, Scale = Vector3.One }], [1]);
        var clip = new AnimationClip(skeleton, 2, ClipSampler.Create<RawClipData, RawClipSampler>(new(2, [])));
        QuantizedClipData quantized = QuantizedClipData.Encode(clip, 4);
        var machine = new GpuPoseStateMachine([new(quantized, Loop: true)], []);
        var blend = new GpuPoseBlend(skeleton, new GpuBlendClip(quantized, Loop: true));
        var storage = new PoseStorage();
        using SkeletonInstance firstSkeleton = storage.CreateInstance(skeleton);
        using SkeletonInstance secondSkeleton = storage.CreateInstance(skeleton);
        using SkeletonInstance directSkeleton = storage.CreateInstance(skeleton);
        using SkeletonInstance clipSkeleton = storage.CreateInstance(skeleton);
        using SkeletonInstance blendSkeleton = storage.CreateInstance(skeleton);
        Entity first = GpuPoseAnimation.CreateTarget(world, firstSkeleton);
        Entity second = GpuPoseAnimation.CreateTarget(world, secondSkeleton);
        Entity direct = GpuPoseAnimation.CreateTarget(world, directSkeleton);
        Entity clipTarget = GpuPoseAnimation.CreateTarget(world, clipSkeleton);
        Entity blendTarget = GpuPoseAnimation.CreateTarget(world, blendSkeleton);
        var parameters = new GraphParameters();
        using var nested = new AnimationBuilder(world);
        using (var states = nested.StateMachine(ref parameters))
        using (states.State(0))
        using (nested.Parallel())
        {
            nested.Leaf<GpuStateParameters, GpuStateLeaf>(first, new(machine, Duration: 2));
            nested.Leaf<GpuStateParameters, GpuStateLeaf>(second, new(machine, Duration: 2));
        }
        Entity animation = nested.Play();
        using var separate = new AnimationBuilder(world);
        separate.Leaf<GpuStateParameters, GpuStateLeaf>(direct, new(machine, Duration: 4));
        Entity directAnimation = separate.Play();
        PropertyAnimation.SetSpeed(world, directAnimation, 2);
        using var mixed = new AnimationBuilder(world);
        using (mixed.Parallel())
        {
            mixed.Leaf<GpuClipParameters, GpuClipLeaf>(clipTarget, new(quantized, Loop: true));
            mixed.Leaf<GpuBlendParameters, GpuBlendLeaf>(blendTarget, new(blend, Duration: 2));
        }
        mixed.Play();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.AddDefaults(systems);
        GpuPoseAnimation.AddSystems(systems);

        Tick();
        RequireTimes(.25f, .5f, .25f);
        Tick();
        RequireTimes(.5f, 1, .5f);
        PropertyAnimation.Pause(world, animation);
        Tick();
        RequireTimes(.5f, 1.5f, .75f);
        PropertyAnimation.Resume(world, animation);
        Tick();
        RequireTimes(.75f, 2, 1);
        Console.WriteLine("GPU_STATE_CLOCKS_PASSED nested-targets independent-speed pause-resume shared-clip-blend batch-versions");

        void Tick()
        {
            var context = new ImmediateSystemContext(world, 0, 0, .25);
            systems.Update(ref context);
        }
        void RequireTimes(float nestedTime, float directTime, float mixedTime)
        {
            GpuPoseTarget a = world.Read<GpuPoseTarget>(first), b = world.Read<GpuPoseTarget>(second), c = world.Read<GpuPoseTarget>(direct);
            if ((a.PlaybackTime, b.PlaybackTime, c.PlaybackTime) != (nestedTime, nestedTime, directTime)
                || !a.Playing || !b.Playing || !c.Playing
                || a.StateMachine != machine || b.StateMachine != machine || c.StateMachine != machine)
                throw new InvalidOperationException("GPU-state leaves must keep their nested clocks, independent targets and pause state.");
            uint version = world.WriteVersion<GpuPoseTarget>(first);
            if (world.WriteVersion<GpuPoseTarget>(second) != version || world.WriteVersion<GpuPoseTarget>(direct) != version)
                throw new InvalidOperationException("GPU-state targets must share their output batch's write version.");
            GpuPoseTarget d = world.Read<GpuPoseTarget>(clipTarget), e = world.Read<GpuPoseTarget>(blendTarget);
            if (d.SampleTime != mixedTime || e.PlaybackTime != mixedTime
                || d.Clip != quantized || e.Blend != blend || !e.Playing)
                throw new InvalidOperationException("Clip and blend leaves must use the same nested clock for their own targets.");
        }
    }
}
