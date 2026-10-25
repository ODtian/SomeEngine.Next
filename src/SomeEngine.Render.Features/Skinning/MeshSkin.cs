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
internal record struct RenderBoneSectionData(Skeleton Skeleton, RenderBoneParent[] Parents,
    Matrix4x4[] Current, ulong Version, ulong BindingVersion, QuantizedClipData? Clip,
    uint Starts, ulong InputNumber, double CalculationTime, float SampleTime,
    GpuPoseStateMachine? StateMachine, GpuPoseBlend? Blend, float[] Controls, float PlaybackTime, bool Playing,
    GpuPoseRig? Rig, GpuPoseGoal[] Goals, Vector3 ModelOffset, GpuPoseMovement Movement, Matrix4x4 ModelToWorld,
    ReadOnlyMemory<short> CpuBones, PoseCalculation[] Calculations, GpuPoseIdentity Identity,
    ReadOnlyMemory<GpuPoseReadbackRequest> Readbacks, GpuPoseEvaluation? Evaluation,
    bool ExtractRootMotion, BoneTransform RootCorrection) : IComponent
{
    internal readonly bool IsGpu => Clip is not null || StateMachine is not null || Blend is not null;
}

/// <summary>Copies pose controls and completed CPU sections through the shared extraction window.</summary>
public sealed class SkinRenderExtractor : IRenderExtractionSystem
{
    private readonly HashSet<Entity> _meshSources = [];
    private readonly HashSet<Entity> _targetSources = [];
    private readonly Dictionary<Entity, SourceMesh> _meshes = [];
    private readonly Dictionary<SkeletonInstance, SourceSection> _sections = [];
    private readonly Dictionary<Entity, SourceTarget> _targets = [];
    private readonly List<SkeletonInstance> _removedSections = [];
    private readonly List<Entity> _removedMeshes = [];
    private ulong _input;
    private bool _canApplyChangesDirect;

    public bool CanApplyChangesDirect => _canApplyChangesDirect;

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

    public void Reset() => _input++;

    public void Collect(QueryChunkView chunk)
    {
        ReadOnlySpan<Entity> entities = chunk.Entities;
        if (chunk.TryRead<MeshSkin>(out ReadOnlySpan<MeshSkin> meshes))
            for (int index = 0; index < entities.Length; index++)
            {
                Entity entity = entities[index];
                _meshSources.Add(entity);
                if (!_meshes.TryGetValue(entity, out SourceMesh? mesh)) _meshes.Add(entity, mesh = new());
                Bind(mesh, meshes[index].Joints);
            }
        if (chunk.TryRead<GpuPoseTarget>(out ReadOnlySpan<GpuPoseTarget> targets))
            for (int index = 0; index < entities.Length; index++)
            {
                Entity entity = entities[index];
                _targetSources.Add(entity);
                if (!_targets.TryGetValue(entity, out SourceTarget? target)) _targets.Add(entity, target = new());
                target.Data = targets[index];
            }
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        ReadOnlySpan<Entity> entities = chunk.Entities;
        if (chunk.TryRead<MeshSkin>(out ReadOnlySpan<MeshSkin> meshes) && chunk.HasChangedSinceLastSystemVersion<MeshSkin>())
            for (int index = 0; index < entities.Length; index++) Bind(_meshes[entities[index]], meshes[index].Joints);
        if (chunk.TryRead<GpuPoseTarget>(out ReadOnlySpan<GpuPoseTarget> targets) && chunk.HasChangedSinceLastSystemVersion<GpuPoseTarget>())
            for (int index = 0; index < entities.Length; index++) _targets[entities[index]].Data = targets[index];
        return false;
    }

    public bool CompleteSourceCollection(World world, bool fullCollection)
    {
        if (fullCollection)
        {
            _meshSources.RemoveWhere(entity => !world.IsAlive(entity) || !world.Has<MeshSkin>(entity));
            _targetSources.RemoveWhere(entity =>
            {
                bool removed = !world.IsAlive(entity) || !world.Has<GpuPoseTarget>(entity);
                if (removed) _targets.Remove(entity);
                return removed;
            });
        }
        _canApplyChangesDirect = true;
        foreach ((Entity entity, SourceTarget sourceTarget) in _targets)
        {
            ref readonly GpuPoseTarget target = ref sourceTarget.Data;
            SourceSection section = Section(target.Skeleton);
            sourceTarget.Section = section;
            _canApplyChangesDirect &= section.Entity != Entity.Null && sourceTarget.PublishedSection == section.Entity;
            section.GpuTarget = sourceTarget;
            section.GpuInput = _input;
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
        }
        foreach (Entity entity in _meshSources)
        {
            SourceMesh mesh = _meshes[entity];
            Bind(mesh, mesh.Binding!);
            _canApplyChangesDirect &= !mesh.Changed;
            foreach (SourceSection section in mesh.Sections) Mark(section);
        }
        _removedSections.Clear();
        GpuPoseTarget cpuTarget = default;
        foreach ((SkeletonInstance source, SourceSection section) in _sections)
        {
            if (section.Input != _input) { _removedSections.Add(source); continue; }
            _canApplyChangesDirect &= section.Entity != Entity.Null && !section.Rebound;
            ref readonly GpuPoseTarget gpu = ref (section.GpuInput == _input
                ? ref section.GpuTarget!.Data : ref cpuTarget);
            ModelPose model = source.ModelPose;
            bool isGpu = gpu.Clip is not null || gpu.StateMachine is not null || gpu.Blend is not null;
            bool copied = (!isGpu || source.CpuBones.Length != 0)
                && (section.CpuVersion != model.Version || section.Current.Length == 0 || !isGpu && section.Data.IsGpu);
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
                gpu.Rig, section.GoalValues, gpu.ModelOffset, gpu.Movement, gpu.ModelToWorld, source.CpuBones, section.CalculationValues,
                new(section.Target, source, gpu.Starts, gpu.InputNumber, gpu.AnimationStarts), gpu.Readbacks,
                gpu.Evaluation, gpu.ExtractRootMotion, gpu.RootCorrection);
        }
        _removedMeshes.Clear();
        if (fullCollection)
            foreach (Entity source in _meshes.Keys) if (!_meshSources.Contains(source)) _removedMeshes.Add(source);
        _canApplyChangesDirect &= _removedSections.Count == 0 && _removedMeshes.Count == 0;
        return _sections.Count != 0 || _removedMeshes.Count != 0;
    }

    private void Bind(SourceMesh mesh, SkinBindingInstance binding)
    {
        if (ReferenceEquals(mesh.Binding, binding) && mesh.BindingVersion == binding.SkeletonInstance.BindingVersion) return;
        mesh.Binding = binding;
        mesh.BindingVersion = binding.SkeletonInstance.BindingVersion;
        mesh.Joints = new SourceJoint[binding.JointCount];
        var sections = new HashSet<SourceSection>();
        for (int joint = 0; joint < mesh.Joints.Length; joint++)
        {
            BonePose bone = binding.GetJointPose(joint);
            SourceSection section = Section(bone.Instance);
            mesh.Joints[joint] = new(section, bone.BoneIndex);
            sections.Add(section);
        }
        mesh.Sections = [.. sections];
        mesh.Changed = true;
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

    public unsafe void ApplyChanges(RenderExtractionContext context)
    {
        if (!_canApplyChangesDirect)
        {
            Publish(context, retain: false);
            return;
        }
        SkinRenderExtractor extraction = this;
        context.World.ExecuteComponentWrite<RenderBoneSectionData, SkinRenderExtractor>(ref extraction, &WriteSections);
    }

    private static void WriteSections(ComponentWriteBatch<RenderBoneSectionData> destination, ref SkinRenderExtractor extraction)
    {
        foreach (SourceSection section in extraction._sections.Values)
        {
            section.SwapCapture();
            destination.Write(section.Entity) = section.Data;
        }
    }

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
            ref RenderBoneSectionData data = ref section.Data;
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
            section.SwapCapture();
            context.Upsert(section.Entity, data);
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
        foreach ((Entity source, SourceTarget target) in _targets)
        {
            SourceSection section = target.Section!;
            if (!retain && target.PublishedSection == section.Entity) continue;
            Entity entity = retain ? context.RetainMirror(source) : context.RequireMirror(source);
            context.Upsert(entity, new RenderGpuPoseTarget(section.Entity));
            target.PublishedSection = section.Entity;
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
        _removedSections.Clear(); _removedMeshes.Clear();
    }

    private readonly record struct SourceJoint(SourceSection? Section, int Bone);
    private sealed class SourceTarget
    {
        internal GpuPoseTarget Data;
        internal SourceSection? Section;
        internal Entity PublishedSection;
    }
    private sealed class SourceMesh
    {
        internal SkinBindingInstance? Binding;
        internal ulong BindingVersion;
        internal SourceJoint[] Joints = [];
        internal SourceSection[] Sections = [];
        internal bool Changed;
    }
    private sealed class SourceSection(SkeletonInstance source)
    {
        internal Entity Target;
        internal SourceTarget? GpuTarget;
        internal ulong GpuInput;
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

        internal void SwapCapture()
        {
            ref RenderBoneSectionData data = ref Data;
            if (Copied)
            {
                (Current, Capture) = (Capture, Current);
                (CalculationValues, CalculationCapture) = (CalculationCapture, CalculationValues);
                CpuVersion = data.Version;
            }
            if (data.StateMachine is not null || data.Blend is not null)
                (ControlValues, ControlCapture) = (ControlCapture, ControlValues);
            if (data.Rig is not null) (GoalValues, GoalCapture) = (GoalCapture, GoalValues);
            bool hasCpu = !data.IsGpu || data.CpuBones.Length != 0;
            data.Current = hasCpu ? Current : [];
            data.Calculations = hasCpu ? CalculationValues : [];
            data.Controls = ControlValues;
            data.Goals = GoalValues;
        }
    }
}
