using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
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

internal sealed record RenderBonePose(Matrix4x4[] Current, Matrix4x4[] Previous, ulong Version);
internal readonly record struct RenderSkinJoint(RenderBonePose Pose, int Bone);
internal readonly record struct RenderMeshSkin(SkinBinding Binding, RenderSkinJoint[] Joints) : IComponent;

/// <summary>Copies completed model-pose sections through the shared Game-world extraction window.</summary>
public sealed class SkinRenderExtractor : IRenderExtractionSystem
{
    private readonly HashSet<Entity> _sources = [];
    private readonly Dictionary<Entity, RenderMeshSkin> _meshes = [];
    private Dictionary<SkeletonInstance, RenderBonePose> _previous = [];
    private Dictionary<SkeletonInstance, RenderBonePose> _current = [];

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<MeshSkin>();
        query.ReadOptional<PoseTarget>();
        query.Write<RenderMeshSkin>();
        query.Write<RenderSkinBinding>();
    }

    public void Reset()
    {
        _meshes.Clear();
        _current.Clear();
    }

    public void Collect(QueryChunkView chunk)
    {
        if (chunk.Has<MeshSkin>())
            foreach (Entity entity in chunk.Entities) _sources.Add(entity);
    }

    public bool CollectChanges(QueryChunkView chunk, RenderExtractionContext context)
    {
        Collect(chunk);
        return false;
    }

    public bool CompleteSourceCollection(World sourceWorld, bool fullCollection)
    {
        _sources.RemoveWhere(entity => !sourceWorld.IsAlive(entity) || !sourceWorld.Has<MeshSkin>(entity));
        foreach (Entity entity in _sources)
        {
            SkinBindingInstance binding = sourceWorld.Read<MeshSkin>(entity).Joints;
            ModelPose model = binding.SkeletonInstance.ModelPose;
            for (int sectionIndex = 0; sectionIndex < model.SectionCount; sectionIndex++)
            {
                ModelPoseSection section = model.GetSection(sectionIndex);
                if (_current.ContainsKey(section.Instance)) continue;
                _previous.TryGetValue(section.Instance, out RenderBonePose? previous);
                Matrix4x4[] current = previous?.Version == section.Version
                    ? previous.Current : section.Transforms.ToArray();
                _current.Add(section.Instance, new(current, previous?.Current ?? current, section.Version));
            }
            var joints = new RenderSkinJoint[binding.JointCount];
            for (int joint = 0; joint < joints.Length; joint++)
            {
                BonePose bone = binding.GetJointPose(joint);
                joints[joint] = new(_current[bone.Instance], bone.BoneIndex);
            }
            _meshes.Add(entity, new(binding.Binding, joints));
        }
        return _meshes.Count != 0;
    }

    public void Apply(RenderExtractionContext context)
    {
        foreach ((Entity source, RenderMeshSkin mesh) in _meshes)
        {
            Entity entity = context.RetainMirror(source);
            context.Upsert(entity, mesh);
            context.Upsert(entity, new RenderSkinBinding());
        }
        foreach (RenderMirror mirror in context.Mirrors)
            if (!_sources.Contains(mirror.Source))
            {
                context.RemoveIfExists<RenderMeshSkin>(mirror.RenderEntity);
                context.RemoveIfExists<RenderSkinBinding>(mirror.RenderEntity);
            }
        Accept();
    }

    public void ApplyChanges(RenderExtractionContext context)
    {
        foreach ((Entity source, RenderMeshSkin mesh) in _meshes)
            context.UpdateExisting(context.RequireMirror(source), mesh);
        Accept();
    }

    private void Accept() => (_previous, _current) = (_current, _previous);

    public void Dispose()
    {
        _sources.Clear();
        _meshes.Clear();
        _previous.Clear();
        _current.Clear();
    }
}
