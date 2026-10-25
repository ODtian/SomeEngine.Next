using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Checks;

internal static class CpuPoseMeasurements
{
    private const int Count = 1_000, BoneCount = 96, Warmups = 128, Samples = 64;
    private const float Delta = 1f / 240;

    internal static void Run()
    {
        JobSystem.Initialize(new() { WorkerCount = 4, EnableCounters = false });
        Console.WriteLine($"framework={RuntimeInformation.FrameworkDescription}; architecture={RuntimeInformation.ProcessArchitecture}; nativeAot={!RuntimeFeature.IsDynamicCodeSupported}; processors={Environment.ProcessorCount}; jobWorkers={JobSystem.WorkerCount}; configuration=Release; tieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}; warmup={Warmups}; samples={Samples}; poses={Count}; skeletonBones={BoneCount}; frameDelta={Delta}; allocation=process-total-all-threads-after-completed-frame");
        Console.WriteLine("path,codec,space,poses,skeleton_bones,selected,time_count,median_us_batch,p95_us_batch,p99_us_batch,max_us_batch,process_bytes_batch");
        Skeleton skeleton = CreateSkeleton();
        AnimationClip raw = CreateRaw(skeleton);
        AclClipData compressed = AclClipCompressor.Compress(raw);
        var acl = new AnimationClip(skeleton, raw.Duration, ClipSampler.Create<AclClipData, AclClipSampler>(compressed));
        long allocated = 0;
        foreach (var codec in new[] { (Name: "raw", Clip: raw), (Name: "acl", Clip: acl) })
        {
            QuantizedClipData gpu = QuantizedClipData.Encode(codec.Clip, 60);
            foreach (short[] bones in new[] { new short[] { 31, 47, 63, 79, 95 }, Enumerable.Range(0, BoneCount).Select(static bone => (short)bone).ToArray() })
                foreach (int times in new[] { 1, 4 })
                {
                    var direct = new DirectSamples(codec.Clip, bones, times);
                    allocated += Record("direct-sequential", codec.Name, bones.Length, times, direct.Sample);
                    direct = new DirectSamples(codec.Clip, bones, times);
                    allocated += Record("direct-parallel", codec.Name, bones.Length, times, direct.SampleParallel);
                    using var graph = new DemandGraph(skeleton, codec.Clip, gpu, bones, times);
                    allocated += Record("full-graph", codec.Name, bones.Length, times, graph.Update);
                }
        }
        PosePath selected = PosePath.Sequence([
            PosePath.Blend([new(PosePath.FromClip(raw), new(.3f, .3f, 4)), new(PosePath.FromClip(acl), new(.7f, .7f, 4))]),
            PosePath.FromClip(acl),
        ]);
        foreach (short[] bones in new[] { new short[] { 31, 47, 63, 79, 95 }, Enumerable.Range(0, BoneCount).Select(static bone => (short)bone).ToArray() })
            foreach (int times in new[] { 1, 4 })
            {
                var prediction = new SelectedSamples(selected, bones, times);
                allocated += Record("selected-path", "raw+acl", bones.Length, times, prediction.Sample);
            }
        using (var graph = new ClipGraph(skeleton, raw, acl))
            allocated += Record("clip-blend-graph", "raw+acl", BoneCount, 4, graph.Update);
        Console.WriteLine($"CPU_POSE_ALLOCATION total_bytes={allocated}; completed_frames={Samples}; workers_included=true");
        JobSystem.Shutdown();
        if (allocated != 0) throw new InvalidOperationException("The declared steady CPU pose workloads require zero process allocation.");
    }

    private static long Record(string path, string codec, int selected, int timeCount, Action update)
    {
        for (int warmup = 0; warmup < Warmups; warmup++) update();
        var elapsed = new double[Samples];
        GC.GetTotalAllocatedBytes(true);
        long before = GC.GetTotalAllocatedBytes(true);
        for (int sample = 0; sample < Samples; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            update();
            elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        long bytes = GC.GetTotalAllocatedBytes(true) - before;
        Array.Sort(elapsed);
        Console.WriteLine(FormattableString.Invariant($"{path},{codec},model,{Count},{BoneCount},{selected},{timeCount},{elapsed[32]:F3},{elapsed[60]:F3},{elapsed[63]:F3},{elapsed[^1]:F3},{bytes / (double)Samples:F3}"));
        return bytes;
    }

    private sealed class DirectSamples
    {
        private readonly ClipSampleBatch _batch;
        private readonly Matrix4x4[] _output;
        private readonly float[] _times = new float[Count];
        private readonly int _timeCount;
        private float _elapsed;
        internal DirectSamples(AnimationClip clip, short[] bones, int timeCount)
        {
            var selection = new ClipSampleSelection(clip, bones);
            var selections = new ClipSampleSelection[Count];
            Array.Fill(selections, selection);
            _batch = new(selections);
            _output = new Matrix4x4[_batch.OutputCount];
            _timeCount = timeCount;
        }
        internal void Sample()
        {
            SetTimes();
            _batch.SampleModel(_times, _output);
        }
        internal void SampleParallel()
        {
            SetTimes();
            _batch.ScheduleModel(_times, _output).Complete();
        }
        private void SetTimes()
        {
            _elapsed += Delta;
            for (int index = 0; index < Count; index++) _times[index] = .25f + index % _timeCount * .25f + _elapsed;
        }
    }

    private sealed class SelectedSamples
    {
        private readonly PosePredictionBatch _batch;
        private readonly Matrix4x4[] _output;
        private readonly PosePredictionSample[] _samples = new PosePredictionSample[Count];
        private readonly double[] _times = new double[Count];
        private readonly int _timeCount;
        private double _elapsed;
        internal SelectedSamples(PosePath path, short[] bones, int timeCount)
        {
            var selection = new PosePredictionSelection(new(path, new(0)), bones);
            var selections = new PosePredictionSelection[Count];
            Array.Fill(selections, selection);
            _batch = new(selections);
            _output = new Matrix4x4[_batch.OutputCount];
            _timeCount = timeCount;
        }
        internal void Sample()
        {
            _elapsed += Delta;
            for (int index = 0; index < Count; index++) _times[index] = .25 + index % _timeCount * 2 + _elapsed;
            _batch.SampleModel(_times, _elapsed, _output, _samples);
        }
    }

    private sealed class DemandGraph : IDisposable
    {
        private readonly World _world = new();
        private readonly SkeletonInstance[] _instances = new SkeletonInstance[Count];
        private readonly SystemGroup<ImmediateSystemContext> _systems;
        private readonly CompiledGraph _graph;
        private ImmediateSystemContext _context;
        internal DemandGraph(Skeleton skeleton, AnimationClip clip, QuantizedClipData gpu, short[] bones, int timeCount)
        {
            var storage = new PoseStorage();
            using var builder = new AnimationBuilder(_world);
            builder.Leaf<GpuClipParameters, GpuClipLeaf>(Entity.Null, new(gpu, Loop: true));
            _graph = builder.Compile();
            var demand = new CpuBoneDemand(clip, bones);
            for (int index = 0; index < Count; index++)
            {
                _instances[index] = storage.CreateInstance(skeleton);
                Entity target = GpuPoseAnimation.CreateTarget(_world, _instances[index]);
                GpuPoseTarget value = _world.Read<GpuPoseTarget>(target);
                value.CpuBones = demand;
                _world.Replace(target, value);
                SetTime(_world, _graph.Play(_world, target), index, timeCount);
            }
            _systems = new(new ImmediateSystemDriver(_world));
            GpuPoseAnimation.AddSystems(_systems);
            _context = new(_world, 0, 0, Delta);
        }
        internal void Update() => _systems.Update(ref _context);
        public void Dispose()
        {
            _systems.Dispose(); _graph.Dispose(); _world.Dispose();
            foreach (SkeletonInstance instance in _instances) instance.Dispose();
        }
    }

    private sealed class ClipGraph : IDisposable
    {
        private readonly World _world = new();
        private readonly SkeletonInstance[] _instances = new SkeletonInstance[Count];
        private readonly SystemGroup<ImmediateSystemContext> _systems;
        private readonly CompiledGraph _graph;
        private ImmediateSystemContext _context;
        internal ClipGraph(Skeleton skeleton, AnimationClip raw, AnimationClip acl)
        {
            var storage = new PoseStorage();
            using var builder = new AnimationBuilder(_world);
            using (builder.Parallel())
            {
                builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(raw, new(0), Loop: true));
                builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(acl, new(1), Loop: true));
                builder.BlendPose(Entity.Null, new(new PoseBlend([new(new(0), .3f), new(new(1), .7f)]), new(2), new(StageEnd.Settled)));
            }
            _graph = builder.Compile();
            for (int index = 0; index < Count; index++)
            {
                _instances[index] = storage.CreateInstance(skeleton);
                Entity target = PoseAnimation.CreateTarget(_world, _instances[index], 3, new(2));
                SetTime(_world, _graph.Play(_world, target), index, 4);
            }
            _systems = new(new ImmediateSystemDriver(_world));
            PoseAnimation.AddSystems(_systems, nodes: static nodes => nodes.Add(new PoseBlendSystem()));
            _context = new(_world, 0, 0, Delta);
        }
        internal void Update() => _systems.Update(ref _context);
        public void Dispose()
        {
            _systems.Dispose(); _graph.Dispose(); _world.Dispose();
            foreach (SkeletonInstance instance in _instances) instance.Dispose();
        }
    }

    private static void SetTime(World world, Entity animation, int index, int timeCount)
    {
        Timeline value = world.Read<Timeline>(animation);
        value.Time = .25f + index % timeCount * .25f;
        world.Replace(animation, value);
    }

    private static Skeleton CreateSkeleton()
    {
        var parents = new short[BoneCount];
        var transforms = new BoneTransform[BoneCount];
        for (int bone = 0; bone < BoneCount; bone++)
        {
            parents[bone] = (short)(bone == 0 ? -1 : (bone - 1) / 2);
            transforms[bone] = new() { Translation = new(0, .025f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
        }
        return new(parents, transforms, new uint[BoneCount]);
    }

    private static AnimationClip CreateRaw(Skeleton skeleton)
    {
        var channels = ImmutableArray.CreateBuilder<RawClipChannel>(BoneCount * 2);
        for (short bone = 0; bone < BoneCount; bone++)
        {
            channels.Add(new(bone, BoneTransformProperty.Translation, ClipInterpolation.Linear, [0, 4],
                [new(0, .025f, 0, 0), new(.025f, .05f, .01f, 0)]));
            channels.Add(new(bone, BoneTransformProperty.Rotation, ClipInterpolation.Linear, [0, 4],
                [new(0, 0, 0, 1), new(0, .258819f, 0, .9659258f)]));
        }
        return new(skeleton, 4, ClipSampler.Create<RawClipData, RawClipSampler>(new(4, channels.MoveToImmutable())));
    }
}
