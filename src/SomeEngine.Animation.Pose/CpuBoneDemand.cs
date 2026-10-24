using System.Runtime.CompilerServices;
using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Pose;

/// <summary>A ready local-pose source and the own bones required before render extraction.</summary>
public sealed class CpuBoneDemand
{
    private readonly short[] _bones;
    public ReadOnlySpan<short> Bones => _bones;
    public AnimationClip? Clip { get; }
    public PoseResultReference? Source { get; }

    public CpuBoneDemand(AnimationClip clip, ReadOnlySpan<short> bones) => (Clip, _bones) = (clip, bones.ToArray());
    public CpuBoneDemand(PoseResultReference source, ReadOnlySpan<short> bones) => (Source, _bones) = (source, bones.ToArray());
}

public static partial class GpuPoseAnimation
{
    /// <summary>Installs or replaces a synchronous bone set and its local-pose source.</summary>
    public static void SetCpuBones(World world, Entity target, CpuBoneDemand? demand)
    {
        GpuPoseTarget output = world.Read<GpuPoseTarget>(target);
        output.CpuBones = demand;
        world.Replace(target, output);
        CpuBoneDemands.For(world).Changed = true;
        CpuBoneDemands.For(world).EnsureBound();
    }
}

internal struct CpuBoneSystem : ISystem<ImmediateSystemContext>
{
    private double _time;
    private PoseCompositionSystem _models;
    public void OnCreate(ref ImmediateSystemContext context) => _models.OnCreate(ref context);
    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _time += context.DeltaTime;
        CpuBoneDemands.For(context.World).Complete(_time);
        _models.ComposeModels(context.World, _time);
    }
    public void OnDestroy(ref ImmediateSystemContext context) => _models.OnDestroy(ref context);
}

/// <summary>Resolved CPU-written sections, their selected samplers and parent-job dependencies.</summary>
internal sealed class CpuBoneDemands(World world)
{
    private static readonly ConditionalWeakTable<World, CpuBoneDemands> Worlds = new();
    private readonly Dictionary<SkeletonInstance, Source> _sources = [];
    private readonly List<Work> _work = [];
    private (SkeletonInstance Skeleton, ulong Version)[] _bindings = [];
    internal bool Changed;
    internal static CpuBoneDemands For(World world) => Worlds.GetValue(world, static world => new(world));

    internal void EnsureBound()
    {
        foreach (var binding in _bindings)
            if (binding.Skeleton.BindingVersion != binding.Version) { Changed = true; break; }
        if (!Changed)
            foreach (Work work in _work)
                if (!world.IsAlive(work.Target) || work.BindingVersion != work.Skeleton.BindingVersion
                    || world.Read<GpuPoseTarget>(work.Target).CpuBones != work.Demand
                    || world.Read<GpuPoseTarget>(work.Target).Clip != work.GpuClip) { Changed = true; break; }
        if (Changed) Bind();
    }

    internal void Complete(double time, int level = int.MaxValue)
    {
        EnsureBound();
        foreach (Work work in _work)
        {
            if (work.Node is not null && work.Node.Level > level) continue;
            bool ready = true;
            foreach (PoseNode parent in work.Dependencies) ready &= parent.Level <= level;
            if (!ready) continue;
            GpuPoseTarget input = world.Read<GpuPoseTarget>(work.Target);
            double sampleTime;
            ulong sourceVersion;
            uint starts;
            if (work.Source is { } result)
            {
                PoseResult source = PoseAnimation.ReadResult(world, in result);
                sourceVersion = source.Version;
                sampleTime = source.Time;
                starts = source.Starts;
            }
            else
            {
                sourceVersion = input.InputNumber;
                sampleTime = input.StateMachine is not null || input.Blend is not null ? input.PlaybackTime : input.SampleTime;
                sampleTime = Math.Clamp(sampleTime, 0, work.Duration);
                starts = input.Starts;
            }
            work.Skeleton.ParentInstance?.Compose(time);
            ulong parentVersion = work.Skeleton.ParentInstance?.ModelPose.Version ?? 0;
            if (work.HasResult && work.SourceVersion == sourceVersion && work.Starts == starts
                && work.ParentVersion == parentVersion) continue;
            work.Times[0] = (float)sampleTime;
            JobHandle job;
            if (work.Source is { } reference)
            {
                var copy = (Range: reference.Range, work.Bones, work.Samples);
                world.ExecuteBufferRead<LocalPose, (PoseRange Range, short[] Bones, BoneTransform[] Samples)>(reference.Entity,
                    ref copy, static (poses, ref copy) =>
                    {
                        ReadOnlySpan<BoneTransform> local = LocalPose.Transforms(poses.AsSpan().Slice(copy.Range.Start, copy.Range.Count));
                        for (int index = 0; index < copy.Bones.Length; index++) copy.Samples[index] = local[copy.Bones[index]];
                    });
                job = JobSystem.Schedule(new ComposeBones(work, time, sampleTime), work.Accesses);
            }
            else job = JobSystem.Schedule(new SampleBones(work, time, sampleTime, input.ExtractRootMotion, input.RootCorrection), work.Accesses);
            job.Complete();
            work.SourceVersion = sourceVersion; work.Starts = starts;
            work.ParentVersion = parentVersion; work.HasResult = true;
        }
    }

    private void Bind()
    {
        foreach (Work work in _work) work.Skeleton.CpuWrittenBones = [];
        _work.Clear(); _sources.Clear();
        QueryHandle targets = world.Query(new QueryDefinitionBuilder().Read<GpuPoseTarget>());
        CpuBoneDemands demands = this;
        world.ExecuteQuery(targets, ref demands, static (scoped QueryCursor rows, ref CpuBoneDemands demands) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                GpuPoseTarget target = row.Read<GpuPoseTarget>();
                demands._sources.Add(target.Skeleton, new(row.Entity, target));
            }
        });
        world.ReleaseQuery(targets);
        Dictionary<PoseResultReference, PoseNode> nodes = PoseAnimation.ReadNodes(world);
        Dictionary<SkeletonInstance, PoseResultReference> modelSources = PoseAnimation.ReadModelSources(world);
        _bindings = modelSources.Keys.Concat(_sources.Keys).Distinct()
            .Select(static skeleton => (skeleton, skeleton.BindingVersion)).ToArray();
        bool expanded;
        do
        {
            expanded = false;
            foreach (Source source in _sources.Values)
                if (source.Input.CpuBones is { } demand)
                {
                    foreach (short bone in demand.Bones) Require(source, bone);
                    if (demand.Source is { } result) RequireAlgorithm(source, result);
                }
            foreach (PoseNode node in nodes.Values)
                if (node.ModelBones.Length != 0)
                {
                    SkeletonInstance skeleton = world.Read<PoseTarget>(node.Output.Entity).Skeleton;
                    foreach (short bone in node.ModelBones)
                        for (int ancestor = bone; ancestor >= 0; ancestor = skeleton.Skeleton.Parents[ancestor])
                            RequireParent(skeleton, ancestor);
                }
        } while (expanded);
        foreach (Source source in _sources.Values) Add(source);
        _work.Sort(static (a, b) => a.Skeleton.Sections.Length.CompareTo(b.Skeleton.Sections.Length));
        Changed = false;
        PoseAnimation.RefreshModelDependencies(world);

        void Require(Source source, int bone)
        {
            if (source.Required[bone]) return;
            source.Required[bone] = true;
            expanded = true;
            Skeleton skeleton = source.Input.Skeleton.Skeleton;
            int parent = skeleton.Parents[bone];
            if (parent >= 0) Require(source, parent);
            else if (skeleton.InheritedParents[bone] >= 0)
            {
                RequireParent(source.Input.Skeleton, bone);
            }
        }
        void RequireParent(SkeletonInstance skeleton, int bone)
        {
            int parent = skeleton.Skeleton.InheritedParents[bone];
            if (parent < 0) return;
            BonePose inherited = skeleton.ModelPose.GetBone(parent);
            if (_sources.TryGetValue(inherited.Instance, out Source? source)) Require(source, inherited.BoneIndex);
        }
        void RequireAlgorithm(Source source, PoseResultReference result)
        {
            if (!nodes.TryGetValue(result, out PoseNode? node)) return;
            bool required = false;
            foreach (short bone in node.ModelBones) required |= source.Required[bone];
            if (required) foreach (short bone in node.ModelBones) Require(source, bone);
            foreach (PoseResultReference input in node.Inputs) RequireAlgorithm(source, input);
        }
        void Add(Source source)
        {
            if (source.Added) return;
            source.Added = true;
            SkeletonInstance skeleton = source.Input.Skeleton;
            if (skeleton.ParentInstance is { } parent && _sources.TryGetValue(parent, out Source? parentSource)) Add(parentSource);
            var bones = new List<short>();
            for (int bone = 0; bone < source.Required.Length; bone++) if (source.Required[bone]) bones.Add((short)bone);
            if (bones.Count == 0) return;
            CpuBoneDemand? demand = source.Input.CpuBones;
            AnimationClip? clip = demand is null ? source.Input.Clip?.CreateClip(skeleton.Skeleton) : demand.Clip;
            DevelopmentCheck.Require(clip is not null || demand?.Source is not null,
                "A demanded GPU section requires a ready clip or CPU pose result.");
            PoseNode? node = null;
            if (demand?.Source is { } result) nodes.TryGetValue(result, out node);
            var dependencies = new List<PoseNode>();
            for (SkeletonInstance? ancestor = skeleton.ParentInstance; ancestor is not null; ancestor = ancestor.ParentInstance)
                if (modelSources.TryGetValue(ancestor, out PoseResultReference modelSource)
                    && nodes.TryGetValue(modelSource, out PoseNode? producer)) dependencies.Add(producer);
            var work = new Work(source.Entity, skeleton, bones.ToArray(), clip, demand?.Source, demand, source.Input.Clip, node, dependencies.ToArray());
            skeleton.CpuWrittenBones = work.Bones;
            _work.Add(work);
        }
    }

    private sealed class Source(Entity entity, GpuPoseTarget input)
    {
        internal readonly Entity Entity = entity;
        internal readonly GpuPoseTarget Input = input;
        internal readonly bool[] Required = new bool[input.Skeleton.Skeleton.BoneCount];
        internal bool Added;
    }
    private sealed class Work
    {
        internal readonly Entity Target;
        internal readonly SkeletonInstance Skeleton;
        internal readonly ulong BindingVersion;
        internal readonly short[] Bones;
        internal readonly PoseResultReference? Source;
        internal readonly CpuBoneDemand? Demand;
        internal readonly QuantizedClipData? GpuClip;
        internal readonly PoseNode? Node;
        internal readonly PoseNode[] Dependencies;
        internal readonly ClipSampleBatch? Clip;
        internal readonly float Duration;
        internal readonly BoneTransform[] Samples;
        internal readonly float[] Times = new float[1];
        internal readonly JobResourceAccess[] Accesses;
        internal ulong SourceVersion, ParentVersion;
        internal uint Starts;
        internal bool HasResult;
        internal Work(Entity target, SkeletonInstance skeleton, short[] bones, AnimationClip? clip, PoseResultReference? source,
            CpuBoneDemand? demand, QuantizedClipData? gpuClip, PoseNode? node, PoseNode[] dependencies)
        {
            Target = target; Skeleton = skeleton; Bones = bones; Source = source;
            Demand = demand; GpuClip = gpuClip; Node = node;
            Dependencies = dependencies;
            BindingVersion = skeleton.BindingVersion;
            Samples = new BoneTransform[bones.Length];
            if (clip is not null) { Clip = new([new(clip, bones)]); Duration = clip.Duration; }
            var accesses = new List<JobResourceAccess>
            {
                JobResourceAccess.Write(JobSystem.Default.GetResourceToken(skeleton)),
                JobResourceAccess.Write(JobSystem.Default.GetResourceToken(this)),
            };
            if (clip is not null) accesses.Add(JobResourceAccess.Read(JobSystem.Default.GetResourceToken(clip)));
            foreach (SkeletonInstance parent in skeleton.Sections)
                if (!ReferenceEquals(parent, skeleton)) accesses.Add(JobResourceAccess.Read(JobSystem.Default.GetResourceToken(parent)));
            Accesses = accesses.ToArray();
        }
    }
    private readonly record struct SampleBones(Work Work, double Time, double SampleTime, bool ExtractRoot, BoneTransform Correction) : IJob
    {
        public void Execute()
        {
            Work.Clip!.SampleLocal(Work.Times, Work.Samples);
            if (ExtractRoot)
            {
                Matrix4x4 correction = Correction.ToMatrix();
                for (int bone = 0; bone < Work.Bones.Length; bone++)
                    if (Work.Skeleton.Skeleton.Parents[Work.Bones[bone]] < 0)
                    {
                        ref BoneTransform local = ref Work.Samples[bone];
                        local.Translation = Vector3.Transform(local.Translation, correction);
                        local.Rotation = Quaternion.Concatenate(local.Rotation, Correction.Rotation);
                    }
            }
            Work.Skeleton.WriteBones(Work.Bones, Work.Samples, Time, SampleTime);
        }
    }
    private readonly record struct ComposeBones(Work Work, double Time, double SampleTime) : IJob
    {
        public void Execute() => Work.Skeleton.WriteBones(Work.Bones, Work.Samples, Time, SampleTime);
    }
}
