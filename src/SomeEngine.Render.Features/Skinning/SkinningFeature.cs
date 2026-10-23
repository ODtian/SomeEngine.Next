using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.Skinning;

/// <summary>Uploads copied Rendering-world bones and binds them for user vertex evaluation.</summary>
public sealed class SkinningFeature : IGraphResourceProvider,
    ISystem<RenderPrepareSystemContext>, ISystem<RenderFrameSystemContext>
{
    private readonly GraphBufferInput _currentBones;
    private readonly GraphBufferInput _previousBones;
    private readonly GraphBufferInput _jointIndices;
    private readonly GraphBufferInput _inverseBind;
    private readonly List<Matrix4x4> _current = [];
    private readonly List<Matrix4x4> _previous = [];
    private readonly List<uint> _joints = [];
    private readonly List<Matrix4x4> _bindings = [];
    private readonly Dictionary<RenderBonePose, uint> _boneOffsets = [];
    private readonly Dictionary<SkinBinding, uint> _bindOffsets = [];
    private QueryHandle _query;

    public SkinningFeature(global::SomeEngine.RenderGraph.RenderGraph graph)
    {
        using RenderGraphEdit edit = graph.BeginEdit();
        _currentBones = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _previousBones = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _jointIndices = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _inverseBind = edit.CreateBufferInput(BufferUsages.ShaderRead);
        edit.Commit();
    }

    public void OnCreate(ref RenderPrepareSystemContext context) =>
        _query = context.World.Query(new QueryDefinitionBuilder().Read<RenderMeshSkin>().Write<RenderSkinBinding>());

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        _current.Clear();
        _previous.Clear();
        _joints.Clear();
        _bindings.Clear();
        _boneOffsets.Clear();
        _bindOffsets.Clear();
        SkinningFeature feature = this;
        context.World.ExecuteQuery(_query, ref feature, static (QueryCursor rows, ref SkinningFeature feature) =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                RenderMeshSkin mesh = row.Read<RenderMeshSkin>();
                uint jointOffset = (uint)feature._joints.Count;
                if (!feature._bindOffsets.TryGetValue(mesh.Binding, out uint bindOffset))
                {
                    bindOffset = (uint)feature._bindings.Count;
                    foreach (Matrix4x4 value in mesh.Binding.InverseBindMatrices) feature._bindings.Add(value);
                    feature._bindOffsets.Add(mesh.Binding, bindOffset);
                }
                foreach (RenderSkinJoint joint in mesh.Joints)
                {
                    if (!feature._boneOffsets.TryGetValue(joint.Pose, out uint boneOffset))
                    {
                        boneOffset = (uint)feature._current.Count;
                        feature._current.AddRange(joint.Pose.Current);
                        feature._previous.AddRange(joint.Pose.Previous);
                        feature._boneOffsets.Add(joint.Pose, boneOffset);
                    }
                    feature._joints.Add(boneOffset + (uint)joint.Bone);
                }
                row.Write<RenderSkinBinding>() = new(jointOffset, bindOffset);
            }
        });
        if (_current.Count == 0)
        {
            _current.Add(Matrix4x4.Identity);
            _previous.Add(Matrix4x4.Identity);
            _joints.Add(0);
            _bindings.Add(Matrix4x4.Identity);
        }
    }

    public void OnDestroy(ref RenderPrepareSystemContext context) => context.World.ReleaseQuery(_query);

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        RenderGraphFrame frame = context.Graph;
        frame.SetBufferInput(_currentBones, frame.Upload<Matrix4x4>(CollectionsMarshal.AsSpan(_current), BufferUsages.ShaderRead, "Skin current bones"));
        frame.SetBufferInput(_previousBones, frame.Upload<Matrix4x4>(CollectionsMarshal.AsSpan(_previous), BufferUsages.ShaderRead, "Skin previous bones"));
        frame.SetBufferInput(_jointIndices, frame.Upload<uint>(CollectionsMarshal.AsSpan(_joints), BufferUsages.ShaderRead, "Skin source joints"));
        frame.SetBufferInput(_inverseBind, frame.Upload<Matrix4x4>(CollectionsMarshal.AsSpan(_bindings), BufferUsages.ShaderRead, "Skin inverse bind matrices"));
    }

    public bool TryResolve(string name, ResourceBindingType type, out GraphResourceBinder binder)
    {
        binder = type == ResourceBindingType.BufferSrv ? name switch
        {
            "SkinCurrentBones" => BindCurrent,
            "SkinPreviousBones" => BindPrevious,
            "SkinJointIndices" => BindJoints,
            "SkinInverseBind" => BindInverse,
            _ => null!,
        } : null!;
        return binder is not null;
    }

    private void BindCurrent(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_currentBones, structureStride: 64), sync);
    private void BindPrevious(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_previousBones, structureStride: 64), sync);
    private void BindJoints(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_jointIndices, structureStride: 4), sync);
    private void BindInverse(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        pass.Bind(graph.CreateBufferSrv(_inverseBind, structureStride: 64), sync);
}
