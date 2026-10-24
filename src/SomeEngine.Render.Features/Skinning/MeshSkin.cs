using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Features.Skinning;

/// <summary>A mesh's source joints bound to the skeleton instance producing its model transforms.</summary>
public readonly struct MeshSkin : IComponent
{
    public SkinBindingInstance Joints { get; }
    public MeshSkin(SkeletonInstance skeleton, SkinBinding binding) => Joints = binding.Bind(skeleton);
}

/// <summary>The Rendering-world section produced by one extracted GPU target.</summary>
public readonly record struct RenderGpuPoseTarget(Entity Section) : IComponent;

/// <summary>A model-space output range, its GPU playback row and the extracted Game control input.</summary>
public readonly record struct RenderPoseSection(PoseRange Range, uint Starts, ulong InputNumber,
    double CalculationTime, float InputTime, int PlaybackIndex = 0, ulong Version = 0) : IComponent;

internal readonly record struct RenderBoneParent(Entity Section, int Bone);
internal readonly record struct RenderSkinJoint(Entity Section, int Bone);
internal readonly record struct RenderMeshSkin(SkinBinding Binding, RenderSkinJoint[] Joints) : IComponent;
internal readonly record struct RenderBoneSectionData(Skeleton Skeleton, RenderBoneParent[] Parents,
    Matrix4x4[] Current, ulong Version, ulong BindingVersion, QuantizedClipData? Clip,
    uint Starts, ulong InputNumber, double CalculationTime, float SampleTime,
    GpuPoseStateMachine? StateMachine, GpuPoseBlend? Blend, float[] Controls, float PlaybackTime, bool Playing,
    GpuPoseRig? Rig, GpuPoseGoal[] Goals, Vector3 ModelOffset,
    ReadOnlyMemory<short> CpuBones, PoseCalculation[] Calculations, GpuPoseIdentity Identity,
    ReadOnlyMemory<GpuPoseReadbackRequest> Readbacks, GpuPoseEvaluation? Evaluation,
    bool ExtractRootMotion, BoneTransform RootCorrection) : IComponent
{
    internal bool IsGpu => Clip is not null || StateMachine is not null || Blend is not null;
}

/// <summary>Copies pose controls and completed CPU sections through the shared extraction window.</summary>
public sealed class SkinRenderExtractor : IRenderExtractionSystem
{
    private readonly HashSet<Entity> _meshSources = [];
    private readonly HashSet<Entity> _targetSources = [];
    private readonly Dictionary<Entity, SourceMesh> _meshes = [];
    private readonly Dictionary<SkeletonInstance, SourceSection> _sections = [];
    private readonly Dictionary<SkeletonInstance, GpuPoseTarget> _gpuControls = [];
    private readonly Dictionary<Entity, SourceSection> _targets = [];
    private readonly List<SkeletonInstance> _removedSections = [];
    private readonly List<Entity> _removedMeshes = [];
    private ulong _input;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<MeshSkin>();
        query.ReadOptional<PoseTarget>();
        query.ReadOptional<GpuPoseTarget>();
        query.ReadOptionalBuffer<GpuPoseGoal>();
        query.Write<RenderMeshSkin>();
        query.Write<RenderSkinBinding>();
        query.Write<RenderBoneSectionData>();
        query.Write<RenderPoseSection>();
        query.Write<RenderGpuPoseTarget>();
    }

    public void Reset() { _input++; _gpuControls.Clear(); _targets.Clear(); }

    public void Collect(QueryChunkView chunk)
    {
        if (chunk.Has<MeshSkin>()) foreach (Entity entity in chunk.Entities) _meshSources.Add(entity);
        if (chunk.Has<GpuPoseTarget>()) foreach (Entity entity in chunk.Entities) _targetSources.Add(entity);
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context) { Collect(chunk); return false; }

    public bool CompleteSourceCollection(World world, bool fullCollection)
    {
        _meshSources.RemoveWhere(entity => !world.IsAlive(entity) || !world.Has<MeshSkin>(entity));
        _targetSources.RemoveWhere(entity => !world.IsAlive(entity) || !world.Has<GpuPoseTarget>(entity));
        foreach (Entity entity in _targetSources)
        {
            GpuPoseTarget target = world.Read<GpuPoseTarget>(entity);
            _gpuControls[target.Skeleton] = target;
            SourceSection section = Section(target.Skeleton);
            section.Target = entity;
            if (target.Rig is GpuPoseRig rig)
            {
                if (section.GoalCapture.Length != rig.GoalCount)
                {
                    section.GoalCapture = new GpuPoseGoal[rig.GoalCount];
                    section.GoalValues = new GpuPoseGoal[rig.GoalCount];
                }
                GpuPoseGoal[] capture = section.GoalCapture;
                world.ExecuteBufferRead<GpuPoseGoal, GpuPoseGoal[]>(entity, ref capture,
                    static (goals, ref capture) => goals.AsSpan().CopyTo(capture));
            }
            Mark(section);
            _targets.Add(entity, section);
        }
        foreach (Entity entity in _meshSources)
        {
            SkinBindingInstance binding = world.Read<MeshSkin>(entity).Joints;
            if (!_meshes.TryGetValue(entity, out SourceMesh? mesh)) _meshes.Add(entity, mesh = new());
            if (!ReferenceEquals(mesh.Binding, binding) || mesh.BindingVersion != binding.SkeletonInstance.BindingVersion)
            {
                mesh.Binding = binding;
                mesh.BindingVersion = binding.SkeletonInstance.BindingVersion;
                mesh.Joints = new SourceJoint[binding.JointCount];
                for (int joint = 0; joint < mesh.Joints.Length; joint++)
                {
                    BonePose bone = binding.GetJointPose(joint);
                    mesh.Joints[joint] = new(Section(bone.Instance), bone.BoneIndex);
                }
                mesh.Changed = true;
            }
            foreach (SourceJoint joint in mesh.Joints) Mark(joint.Section!);
        }
        _removedSections.Clear();
        foreach ((SkeletonInstance source, SourceSection section) in _sections)
        {
            if (section.Input != _input) { _removedSections.Add(source); continue; }
            _gpuControls.TryGetValue(source, out GpuPoseTarget gpu);
            ModelPose model = source.ModelPose;
            bool isGpu = gpu.Clip is not null || gpu.StateMachine is not null || gpu.Blend is not null;
            bool copied = (!isGpu || source.CpuBones.Length != 0)
                && (section.CpuVersion != model.Version || section.Current.Length == 0);
            if (copied)
            {
                if (section.Capture.Length == 0)
                {
                    section.Capture = new Matrix4x4[source.Skeleton.BoneCount];
                    section.Current = new Matrix4x4[source.Skeleton.BoneCount];
                    section.CalculationCapture = new PoseCalculation[source.Skeleton.BoneCount];
                    section.CalculationValues = new PoseCalculation[source.Skeleton.BoneCount];
                }
                model.GetSection(model.SectionCount - 1).Transforms.CopyTo(section.Capture);
                model.GetSection(model.SectionCount - 1).Calculations.CopyTo(section.CalculationCapture);
            }
            if (gpu.StateMachine is not null || gpu.Blend is not null)
            {
                int count = gpu.StateMachine?.InputCount ?? gpu.Blend!.InputCount;
                if (section.ControlCapture.Length != count)
                {
                    section.ControlCapture = new float[count];
                    section.ControlValues = new float[count];
                }
                gpu.Controls!.AsSpan().CopyTo(section.ControlCapture);
            }
            section.Copied = copied;
            section.Data = new(source.Skeleton, section.Data.Parents, section.Data.Current,
                model.Version, source.BindingVersion, gpu.Clip, gpu.Starts,
                isGpu ? gpu.InputNumber : _input,
                isGpu ? gpu.CalculationTime : model.Time,
                isGpu ? gpu.SampleTime : (float)model.SampleTime,
                gpu.StateMachine, gpu.Blend, section.ControlValues, gpu.PlaybackTime, gpu.Playing,
                gpu.Rig, section.GoalValues, gpu.ModelOffset, source.CpuBones, section.CalculationValues,
                new(section.Target, source, gpu.Starts, gpu.InputNumber, gpu.AnimationStarts), gpu.Readbacks,
                gpu.Evaluation, gpu.ExtractRootMotion, gpu.RootCorrection);
        }
        _removedMeshes.Clear();
        foreach (Entity source in _meshes.Keys) if (!_meshSources.Contains(source)) _removedMeshes.Add(source);
        return _sections.Count != 0 || _removedMeshes.Count != 0;
    }

    private SourceSection Section(SkeletonInstance source)
    {
        if (!_sections.TryGetValue(source, out SourceSection? section))
            _sections.Add(source, section = new(source));
        if (section.BindingVersion == source.BindingVersion) return section;
        section.BindingVersion = source.BindingVersion;
        section.Parent = source.ParentInstance is null ? null : Section(source.ParentInstance);
        section.Links = new SourceJoint[source.Skeleton.BoneCount];
        for (int bone = 0; bone < section.Links.Length; bone++)
        {
            int own = source.Skeleton.Parents[bone], inherited = source.Skeleton.InheritedParents[bone];
            if (own >= 0) section.Links[bone] = new(section, own);
            else if (inherited >= 0)
            {
                BonePose parent = source.ParentInstance!.ModelPose.GetBone(inherited);
                section.Links[bone] = new(Section(parent.Instance), parent.BoneIndex);
            }
        }
        section.Rebound = true;
        return section;
    }

    private void Mark(SourceSection section)
    {
        if (section.Input == _input) return;
        section.Input = _input;
        if (section.Parent is not null) Mark(Section(section.Parent.Source));
    }

    public void Apply(RenderExtractionContext context)
    {
        Publish(context, retain: true);
        foreach (RenderMirror mirror in context.Mirrors)
        {
            if (!_meshSources.Contains(mirror.Source))
            {
                context.RemoveIfExists<RenderMeshSkin>(mirror.RenderEntity);
                context.RemoveIfExists<RenderSkinBinding>(mirror.RenderEntity);
            }
            if (!_targetSources.Contains(mirror.Source)) context.RemoveIfExists<RenderGpuPoseTarget>(mirror.RenderEntity);
        }
    }

    public void ApplyChanges(RenderExtractionContext context) => Publish(context, retain: false);

    private void Publish(RenderExtractionContext context, bool retain)
    {
        foreach (SourceSection section in _sections.Values)
            if (section.Input == _input && section.Entity == Entity.Null)
            {
                section.Entity = context.World.CreateEntity();
                context.Upsert(section.Entity, new RenderPoseSection());
            }
        foreach (SourceSection section in _sections.Values)
        {
            if (section.Input != _input) continue;
            RenderBoneSectionData data = section.Data;
            if (section.Rebound)
            {
                var parents = new RenderBoneParent[section.Links.Length];
                for (int bone = 0; bone < parents.Length; bone++)
                {
                    SourceJoint link = section.Links[bone];
                    parents[bone] = new(link.Section?.Entity ?? Entity.Null, link.Bone);
                }
                data = data with { Parents = parents };
                section.Rebound = false;
            }
            if (section.Copied)
            {
                (section.Current, section.Capture) = (section.Capture, section.Current);
                (section.CalculationValues, section.CalculationCapture) = (section.CalculationCapture, section.CalculationValues);
                section.CpuVersion = data.Version;
            }
            if (data.StateMachine is not null || data.Blend is not null)
                (section.ControlValues, section.ControlCapture) = (section.ControlCapture, section.ControlValues);
            if (data.Rig is not null) (section.GoalValues, section.GoalCapture) = (section.GoalCapture, section.GoalValues);
            bool hasCpu = !data.IsGpu || data.CpuBones.Length != 0;
            data = data with { Current = hasCpu ? section.Current : [], Calculations = hasCpu ? section.CalculationValues : [],
                Controls = section.ControlValues, Goals = section.GoalValues };
            context.Upsert(section.Entity, data);
            section.Data = data;
        }
        foreach ((Entity source, SourceMesh mesh) in _meshes)
        {
            if (!_meshSources.Contains(source)) continue;
            Entity entity = retain ? context.RetainMirror(source) : context.RequireMirror(source);
            if (!mesh.Changed) continue;
            var joints = new RenderSkinJoint[mesh.Joints.Length];
            for (int joint = 0; joint < joints.Length; joint++)
                joints[joint] = new(mesh.Joints[joint].Section!.Entity, mesh.Joints[joint].Bone);
            context.Upsert(entity, new RenderMeshSkin(mesh.Binding!.Binding, joints));
            context.Upsert(entity, new RenderSkinBinding());
            mesh.Changed = false;
        }
        foreach ((Entity source, SourceSection section) in _targets)
        {
            Entity entity = retain ? context.RetainMirror(source) : context.RequireMirror(source);
            context.Upsert(entity, new RenderGpuPoseTarget(section.Entity));
        }
        foreach (SkeletonInstance source in _removedSections)
        {
            SourceSection section = _sections[source];
            if (section.Entity != Entity.Null) context.World.DestroyEntity(section.Entity);
            _sections.Remove(source);
        }
        foreach (Entity source in _removedMeshes) _meshes.Remove(source);
    }

    public void Dispose()
    {
        _meshSources.Clear(); _targetSources.Clear(); _meshes.Clear(); _sections.Clear(); _targets.Clear();
        _gpuControls.Clear(); _removedSections.Clear(); _removedMeshes.Clear();
    }

    private readonly record struct SourceJoint(SourceSection? Section, int Bone);
    private sealed class SourceMesh
    {
        internal SkinBindingInstance? Binding;
        internal ulong BindingVersion;
        internal SourceJoint[] Joints = [];
        internal bool Changed;
    }
    private sealed class SourceSection(SkeletonInstance source)
    {
        internal Entity Target;
        internal readonly SkeletonInstance Source = source;
        internal Entity Entity;
        internal SourceSection? Parent;
        internal SourceJoint[] Links = [];
        internal RenderBoneSectionData Data;
        internal Matrix4x4[] Current = [];
        internal Matrix4x4[] Capture = [];
        internal PoseCalculation[] CalculationValues = [], CalculationCapture = [];
        internal float[] ControlValues = [], ControlCapture = [];
        internal GpuPoseGoal[] GoalValues = [], GoalCapture = [];
        internal ulong BindingVersion, CpuVersion, Input;
        internal bool Rebound, Copied;
    }
}
