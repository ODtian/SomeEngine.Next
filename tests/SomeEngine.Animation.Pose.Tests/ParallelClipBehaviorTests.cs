using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class ParallelClipBehaviorTests
{
    [Fact]
    public void QueuedSamplesAndIndependentGraphClocksMatchSequentialPublicSampling()
    {
        // Sixty-four three-bone characters are example geometry for the general batched sampling/playback API.
        var skeleton = new Skeleton([-1, 0, 1], [Bone(Vector3.Zero), Bone(Vector3.UnitY), Bone(Vector3.UnitX)], [1, 2, 3]);
        var rawData = new RawClipData(4,
        [
            new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 4], [Vector4.Zero, new(4, 0, 1, 0)]),
            new(1, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 4], [new(0, 0, 0, 1), new(0, 0, .70710677f, .70710677f)]),
        ]);
        var raw = new AnimationClip(skeleton, 4, ClipSampler.Create<RawClipData, RawClipSampler>(rawData));
        AclClipData compressed = AclClipCompressor.Compress(raw);
        var acl = new AnimationClip(skeleton, 4, ClipSampler.Create<AclClipData, AclClipSampler>(compressed));
        const int count = 64;
        var selections = new ClipSampleSelection[count];
        for (int index = 0; index < count; index++) selections[index] = new(index % 2 == 0 ? raw : acl, [2, 0]);
        var batch = new ClipSampleBatch(selections);
        var times = new float[count];
        var local = new BoneTransform[batch.OutputCount];
        var model = new Matrix4x4[batch.OutputCount];
        var complete = new BoneTransform[skeleton.BoneCount];
        using var world = new World();
        var storage = new PoseStorage();
        using SkeletonInstance reference = storage.CreateInstance(skeleton);
        var instances = new SkeletonInstance[count];
        var demanded = new SkeletonInstance[count];
        using var rawBuilder = new AnimationBuilder(world);
        rawBuilder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(raw, new(0), Loop: true));
        using CompiledGraph rawGraph = rawBuilder.Compile();
        using var aclBuilder = new AnimationBuilder(world);
        aclBuilder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(acl, new(0), Loop: true));
        using CompiledGraph aclGraph = aclBuilder.Compile();
        using var gpuBuilder = new AnimationBuilder(world);
        gpuBuilder.Leaf<GpuClipParameters, GpuClipLeaf>(Entity.Null, new(QuantizedClipData.Encode(raw), Loop: true));
        using CompiledGraph gpuGraph = gpuBuilder.Compile();
        for (int index = 0; index < count; index++)
        {
            instances[index] = storage.CreateInstance(skeleton);
            Entity target = PoseAnimation.CreateTarget(world, instances[index]);
            Entity animation = (index % 2 == 0 ? rawGraph : aclGraph).Play(world, target);
            PropertyAnimation.SetSpeed(world, animation, (index % 8 + 1) * .5f);
            demanded[index] = storage.CreateInstance(skeleton);
            Entity gpuTarget = GpuPoseAnimation.CreateTarget(world, demanded[index]);
            GpuPoseTarget gpu = world.Read<GpuPoseTarget>(gpuTarget);
            gpu.CpuBones = new(selections[index].Clip, [2]);
            world.Replace(gpuTarget, gpu);
            PropertyAnimation.SetSpeed(world, gpuGraph.Play(world, gpuTarget), (index % 8 + 1) * .5f);
        }
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems);
        GpuPoseAnimation.AddSystems(systems);
        var context = new ImmediateSystemContext(world, 0, 0, .25);
        try
        {
            for (int frame = 1; frame <= 2; frame++)
            {
                JobHandle input = JobSystem.Schedule(new SampleTimes(times, frame), JobResourceAccess.Write(times));
                JobHandle sampled = batch.ScheduleLocal(times, local, input);
                batch.ScheduleModel(times, model, sampled).Complete();
                systems.Update(ref context);
                for (int index = 0; index < count; index++)
                {
                    ClipSampleSelection selection = selections[index];
                    selection.Clip.SampleLocal(times[index], complete);
                    reference.Update(complete, times[index]);
                    PoseRange output = batch.GetOutputRange(index);
                    for (int selected = 0; selected < selection.Bones.Length; selected++)
                    {
                        int bone = selection.Bones[selected];
                        Assert.Equal(complete[bone], local[output.Start + selected]);
                        Assert.Equal(reference.ModelPose.Transforms[bone], model[output.Start + selected]);
                        Assert.Equal(complete[bone], instances[index].LocalPose[bone]);
                        Assert.Equal(reference.ModelPose.Transforms[bone], instances[index].ModelPose.Transforms[bone]);
                        Assert.Equal(complete[bone], demanded[index].LocalPose[bone]);
                        Assert.Equal(reference.ModelPose.Transforms[bone], demanded[index].ModelPose.Transforms[bone]);
                    }
                    Assert.Equal(times[index], instances[index].ModelPose.SampleTime, 5);
                }
            }
        }
        finally
        {
            foreach (SkeletonInstance instance in instances) instance.Dispose();
            foreach (SkeletonInstance instance in demanded) instance.Dispose();
        }
    }

    private readonly record struct SampleTimes(float[] Values, int Frame) : IJob
    {
        public void Execute()
        {
            for (int index = 0; index < Values.Length; index++) Values[index] = (index % 8 + 1) * .125f * Frame;
        }
    }

    private static BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
}
