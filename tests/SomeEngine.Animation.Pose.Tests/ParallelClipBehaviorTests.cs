using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class ParallelClipBehaviorTests
{
    [Fact]
    public void CommonClipGraphSamplesOnTheWorldsSchedulerBeforePublishingItsPose()
    {
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using var world = WorldJobs.CreateWorld(scheduler: scheduler);
        var skeleton = new Skeleton([-1], [Bone(Vector3.Zero)], [1]);
        var samples = new ClipExecution();
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<ClipExecution, ExecutionSampler>(samples));
        var storage = new PoseStorage();
        using SkeletonInstance character = storage.CreateInstance(skeleton);
        Entity target = PoseAnimation.CreateTarget(world, character);
        using var builder = new AnimationBuilder(world);
        builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clip, new(0)));
        using CompiledGraph graph = builder.Compile();
        graph.Play(world, target);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        PoseAnimation.AddSystems(systems);
        var context = new ImmediateSystemContext(world, 0, 0, .25);

        systems.Update(ref context);

        Assert.Same(scheduler, samples.Scheduler);
        Assert.Equal(new Vector3(1, 0, 0), character.ModelPose.Transforms[0].Translation);
        Assert.Equal(.25, character.ModelPose.SampleTime);
    }

    private sealed class ClipExecution
    {
        internal Scheduler? Scheduler;
    }

    private readonly struct ExecutionSampler : IClipSampler<ClipExecution>
    {
        public static void SampleLocal(ClipExecution data, float time, Span<BoneTransform> destination)
        {
            data.Scheduler = JobSystem.Current;
            destination[0].Translation.X = time * 4;
        }
    }
    [Fact]
    public void ScheduledSamplesUseTheCallingSchedulersResourcesAndCompleteTheirChildren()
    {
        var skeleton = new Skeleton([-1], [Bone(Vector3.Zero)], [1]);
        var data = new RawClipData(2,
            [new(0, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 2],
                [Vector4.Zero, new(8, 0, 0, 0)])]);
        var clip = new AnimationClip(skeleton, 2, ClipSampler.Create<RawClipData, RawClipSampler>(data));
        var batch = new ClipSampleBatch([new(clip), new(clip)], samplesPerJob: 1);
        float[] times = [.5f, 1.5f];
        var local = new BoneTransform[2];
        var model = new Matrix4x4[2];
        using var scheduler = new Scheduler();

        scheduler.Schedule(new SampleBatch(batch, times, local, model)).Complete();

        Assert.Equal(new Vector3(2, 0, 0), local[0].Translation);
        Assert.Equal(new Vector3(6, 0, 0), local[1].Translation);
        Assert.Equal(local[0].ToMatrix(), model[0]);
        Assert.Equal(local[1].ToMatrix(), model[1]);
    }

    private readonly record struct SampleBatch(ClipSampleBatch Batch, float[] Times,
        BoneTransform[] Local, Matrix4x4[] Model) : IJob
    {
        public void Execute()
        {
            JobHandle local = Batch.ScheduleLocal(Times, Local);
            Batch.ScheduleModel(Times, Model, local);
        }
    }

    [Fact]
    public async Task ScheduledModelSamplesFollowParentWritesAfterAttachmentAndDetachment()
    {
        var body = new Skeleton([-1], [Bone(Vector3.Zero)], [1]);
        var clothing = new Skeleton(body, [-1], [Bone(Vector3.UnitY)], [2], inheritedParents: [0]);
        var ornament = new Skeleton(clothing, [-1], [Bone(Vector3.UnitZ)], [3], inheritedParents: [0]);
        var clip = new AnimationClip(ornament, 1, ClipSampler.Create<RawClipData, RawClipSampler>(new(1, [])));
        var storage = new PoseStorage();
        using SkeletonInstance original = storage.CreateInstance(body), replacement = storage.CreateInstance(body);
        using SkeletonInstance garment = storage.CreateInstance(clothing, original);
        var batch = new ClipSampleBatch([new(clip, parentPose: garment.ModelPose)]);
        float[] times = [0];
        var output = new Matrix4x4[1];
        batch.ScheduleModel(times, output).Complete();
        garment.Attach(replacement, 1);

        for (int binding = 1; binding <= 2; binding++)
        {
            if (binding == 2) garment.Detach(2);
            SkeletonInstance parent = garment.ParentInstance!;
            var written = new ParentWriteFence();
            JobHandle producer = JobSystem.CreateExternalFenceHandle(written,
                JobResourceAccess.Write(JobSystem.Default.GetResourceToken(parent)));
            JobHandle sampled = batch.ScheduleModel(times, output);
            JobHandle write = JobSystem.Schedule(new WriteParent(parent, binding * 10, written));
            try
            {
                await sampled.WaitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await write.WaitAsync();
                Assert.Equal(new Vector3(binding * 10, 0, 1), output[0].Translation);
            }
            finally
            {
                written.Signal();
                write.Complete();
                producer.Complete();
                sampled.Complete();
            }
        }
    }

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

    private sealed class ParentWriteFence : IJobExternalFence
    {
        private readonly TaskCompletionSource _completion = new();
        public bool IsSignaled => _completion.Task.IsCompleted;
        public void Signal() => _completion.TrySetResult();
        public void OnSignaled(Action<object?> continuation, object? state) =>
            _completion.Task.ContinueWith(_ => continuation(state), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private readonly record struct WriteParent(SkeletonInstance Parent, float Position, ParentWriteFence Written) : IJob
    {
        public void Execute()
        {
            Parent.Update([Bone(new(Position, 0, 0))], Position);
            Written.Signal();
        }
    }

    private static BoneTransform Bone(Vector3 position) => new() { Translation = position, Rotation = Quaternion.Identity, Scale = Vector3.One };
}