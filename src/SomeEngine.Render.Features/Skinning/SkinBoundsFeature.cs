using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Render.Features.Skinning;

/// <summary>Produces per-instance LBS bounds from shared influence boxes and model-space bones.</summary>
public sealed class SkinBoundsFeature : IClusterBoundsSource, IDisposable,
    ISystem<RenderPrepareSystemContext>, ISystem<RenderFrameSystemContext>, IRenderGraphFrameExecutionObserver
{
    private const BufferUsages BoundsUsages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly SkinningFeature _skinning;
    private readonly LinkedComputePipeline _pipeline, _jointPipeline;
    private readonly GraphBufferId _current, _previous, _definitions, _jointTransforms;
    private readonly GraphBufferInput _controlsInput, _workInput, _boxesInput, _uploadInput;
    private readonly GraphPassId _boundsPass, _jointPass;
    private readonly Dictionary<Entity, MeshBounds> _instances = [];
    private readonly Dictionary<GeometryKey, GeometryBounds> _geometry = [];
    private readonly List<Entity> _removed = [];
    private readonly List<Control> _controls = [];
    private MeshBounds[] _order = [];
    private RenderWorld _world = null!;
    private QueryHandle _query;
    private GraphPassId _uploadPass;
    private byte[] _definitionBytes = [];
    private BufferRange _boxRange, _workRange;
    private ulong _collection;
    private bool _layoutChanged = true, _uploadRequired;

    public GraphBufferInput CurrentBounds { get; }
    public GraphBufferInput PreviousBounds { get; }
    public GraphPassId CompletePass { get; }
    public ulong Revision { get; private set; }

    public SkinBoundsFeature(IGraphicsBackend backend, Device device, global::SomeEngine.RenderGraph.RenderGraph graph,
        SkinningFeature skinning)
    {
        _graph = graph;
        _skinning = skinning;
        var shader = SkinBoundsShaders.Get(device.Capabilities.CompileTarget);
        _pipeline = LinkedComputePipeline.Create(backend, device, shader,
            "TransformSkinBounds", "Transform skin influence bounds");
        _jointPipeline = LinkedComputePipeline.Create(backend, device, shader,
            "TransformSkinJoints", "Transform skin bounds joints");
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            CurrentBounds = edit.CreateBufferInput(BoundsUsages);
            PreviousBounds = edit.CreateBufferInput(BoundsUsages);
            _controlsInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
            _workInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
            _boxesInput = edit.CreateBufferInput(BufferUsages.ShaderRead);
            _uploadInput = edit.CreateBufferInput(BufferUsages.CopySource);
            _current = edit.CreatePersistentBuffer(new BufferDesc(48, BoundsUsages, Label: "Current skin model bounds"));
            _previous = edit.CreatePersistentBuffer(new BufferDesc(48, BoundsUsages, Label: "Previous skin model bounds"));
            _jointTransforms = edit.CreatePersistentBuffer(new BufferDesc(144, BoundsUsages, Label: "Skin bounds joint transforms"));
            _definitions = edit.CreatePersistentBuffer(new BufferDesc(64, BufferUsages.ShaderRead | BufferUsages.CopyDestination, Label: "Skin bounds definitions"));
            _jointPass = edit.AddComputeFramePass("Transform skin bounds joints", PassQueueSelection.AnyCompatible(),
                new DispatchArguments(1, 1, 1), new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                _jointPipeline.Pipeline, _jointPipeline.ParameterLayout, default,
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data));
            _boundsPass = edit.AddComputeFramePass("Transform skin model bounds", PassQueueSelection.AnyCompatible(),
                new DispatchArguments(1, 1, 1), new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                _pipeline.Pipeline, _pipeline.ParameterLayout, default,
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data));
            foreach ((GraphPassId passId, LinkedComputePipeline pipeline, GraphAccessMode transformsAccess) in new[]
            {
                (_jointPass, _jointPipeline, GraphAccessMode.Write),
                (_boundsPass, _pipeline, GraphAccessMode.Read),
            })
            {
                PassDefinition pass = edit.GetPass(passId);
                Resource[] resources = ShaderResourceLayout.Resolve<Resource>(pipeline.ParameterLayout, static (name, _) => name switch
                {
                    "SkinBoundsControls" => Resource.Controls, "SkinBoundsWork" => Resource.Work, "SkinBoundsBoxes" => Resource.Boxes,
                    "SkinCurrentBounds" => Resource.Current, "SkinPreviousBounds" => Resource.Previous,
                    "SkinCurrentBones" => Resource.CurrentBones, "SkinPreviousBones" => Resource.PreviousBones,
                    "SkinJointIndices" => Resource.Joints, "SkinInverseBind" => Resource.InverseBind,
                    "SkinJointTransforms" => Resource.Transforms, _ => null,
                });
                foreach (Resource resource in resources)
                {
                    switch (resource)
                    {
                        case Resource.Controls: pass.Bind(edit.CreateBufferSrv(_controlsInput, structureStride: 32), PipelineSync.ComputeShading); break;
                        case Resource.Work: pass.Bind(edit.CreateBufferSrv(_workInput, structureStride: 32), PipelineSync.ComputeShading); break;
                        case Resource.Boxes: pass.Bind(edit.CreateBufferSrv(_boxesInput, structureStride: 48), PipelineSync.ComputeShading); break;
                        case Resource.Transforms: pass.Bind(edit.CreateBufferUav(_jointTransforms, structureStride: 144), PipelineSync.ComputeShading,
                            transformsAccess, WriteCoverage.Partial, ResourceContentState.Defined); break;
                        case Resource.Current: pass.Bind(edit.CreateBufferUav(CurrentBounds, structureStride: 48), PipelineSync.ComputeShading,
                            GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined); break;
                        case Resource.Previous: pass.Bind(edit.CreateBufferUav(PreviousBounds, structureStride: 48), PipelineSync.ComputeShading,
                            GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined); break;
                        default:
                            string name = resource switch { Resource.CurrentBones => "SkinCurrentBones", Resource.PreviousBones => "SkinPreviousBones",
                                Resource.Joints => "SkinJointIndices", _ => "SkinInverseBind" };
                            skinning.TryResolve(name, ResourceBindingType.BufferSrv, out GraphResourceBinder bind);
                            bind(ref edit, ref pass, PipelineSync.ComputeShading);
                            break;
                    }
                }
            }
            CompletePass = edit.AddCopyFramePass("Skin model bounds complete", PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition _, ref int _) => { }, static (ref CopyPassCommandScope _, in int _) => { });
            edit.OrderAfter(_jointPass, skinning.CompletePass);
            edit.OrderAfter(_boundsPass, _jointPass);
            edit.OrderAfter(CompletePass, _boundsPass);
            edit.Commit();
        }
        finally { edit.Dispose(); }
    }

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        _world = context.World;
        _query = context.World.Query(new QueryDefinitionBuilder().Read<RenderMesh>().Read<RenderMeshSkin>()
            .Read<RenderSkinBinding>().Read<RenderSkinBounds>().Write<RenderDeformBounds>());
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        _collection++;
        SkinBoundsFeature feature = this;
        _world.ExecuteQuery(_query, ref feature, static (QueryCursor rows, ref SkinBoundsFeature feature) =>
        {
            foreach (QueryRow row in rows.Rows) feature.ReadInstance(row);
        });
        _removed.Clear();
        foreach ((Entity entity, MeshBounds instance) in _instances)
            if (instance.Collection != _collection) _removed.Add(entity);
        foreach (Entity entity in _removed)
        {
            _instances.Remove(entity);
            if (_world.IsAlive(entity) && _world.Has<RenderDeformBounds>(entity)) _world.Replace(entity, new RenderDeformBounds());
            _layoutChanged = true;
        }
        if (_layoutChanged) Rebuild();
    }

    private void ReadInstance(QueryRow row)
    {
        if (!_instances.TryGetValue(row.Entity, out MeshBounds? instance))
        {
            _instances.Add(row.Entity, instance = new(row.Entity));
            _layoutChanged = true;
        }
        instance.Collection = _collection;
        Mesh mesh = row.Read<RenderMesh>().Mesh;
        var geometry = new GeometryKey(mesh.Bvh, mesh.ClusterGeometryIndices, row.Read<RenderSkinBounds>().Data);
        if (instance.Geometry != geometry)
        {
            instance.Geometry = geometry;
            instance.Mesh = mesh;
            _layoutChanged = true;
        }
        bool changed = false, geometryChanged = false;
        RenderSkinBinding binding = row.Read<RenderSkinBinding>();
        if (instance.Binding != binding) { instance.Binding = binding; changed = geometryChanged = _layoutChanged = true; }
        RenderSkinJoint[] joints = row.Read<RenderMeshSkin>().Joints;
        if (!ReferenceEquals(instance.Joints, joints))
        {
            instance.Joints = joints;
            instance.Sections = joints.Select(joint => joint.Section).Distinct().ToArray();
            instance.Inputs = new SkinningFeature.PoseInput[instance.Sections.Length];
            changed = geometryChanged = _layoutChanged = true;
        }
        for (int index = 0; index < instance.Sections.Length; index++)
        {
            SkinningFeature.PoseInput input = _skinning.ReadPoseInput(instance.Sections[index]);
            SkinningFeature.PoseInput before = instance.Inputs[index];
            if (before == input) continue;
            instance.Inputs[index] = input;
            changed = true;
            geometryChanged |= before.Starts != input.Starts || before.GeometryNumber != input.GeometryNumber || before.IsGpu != input.IsGpu;
        }
        if (changed) { instance.Input++; Revision++; }
        // The same range can hold new geometry. Publish its change through ordinary instance consumers.
        if (geometryChanged) _ = row.Write<RenderDeformBounds>();
    }

    private void Rebuild()
    {
        _order = [.. _instances.Values];
        var used = _order.Select(instance => instance.Geometry).ToHashSet();
        foreach (GeometryKey key in _geometry.Keys.Where(key => !used.Contains(key)).ToArray()) _geometry.Remove(key);
        var boxes = new List<Box>();
        var work = new List<Work>();
        var boxStarts = new Dictionary<GeometryKey, uint>();
        uint output = 0, jointCapacity = 1, maximumJoints = 0;
        for (int index = 0; index < _order.Length; index++)
        {
            MeshBounds instance = _order[index];
            uint jointCount = (uint)instance.Joints.Length;
            jointCapacity = Math.Max(jointCapacity, instance.Binding.JointOffset + jointCount);
            maximumJoints = Math.Max(maximumJoints, jointCount);
            if (!_geometry.TryGetValue(instance.Geometry, out GeometryBounds? geometry))
                _geometry.Add(instance.Geometry, geometry = BuildGeometry(instance.Mesh!, instance.Geometry.Data));
            if (!boxStarts.TryGetValue(instance.Geometry, out uint boxStart))
            {
                boxStarts.Add(instance.Geometry, boxStart = (uint)boxes.Count);
                boxes.AddRange(geometry.Boxes);
            }
            instance.First = output;
            instance.Input++;
            for (uint group = 0; group < geometry.Ranges.Length; group++)
            {
                BoundsGroup ranges = geometry.Ranges[group];
                work.Add(new((uint)index, boxStart + ranges.Cull.First, ranges.Cull.Count, group,
                    boxStart + ranges.Lod.First, ranges.Lod.Count, ranges.Error, 0));
            }
            _world.Replace(instance.Entity, new RenderDeformBounds(output, geometry.NodeCount));
            output += (uint)geometry.Ranges.Length;
        }
        if (boxes.Count == 0) boxes.Add(default);
        int workCount = work.Count;
        if (work.Count == 0) work.Add(default);
        using var stream = new MemoryStream();
        stream.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(boxes)));
        _boxRange = new(0, (ulong)stream.Position);
        stream.Position = (stream.Position + 31) / 32 * 32;
        _workRange = new((ulong)stream.Position, (ulong)(work.Count * 32));
        stream.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(work)));
        _definitionBytes = stream.ToArray();
        using RenderGraphEdit edit = _graph.BeginEdit();
        edit.SetBufferDescription(_definitions, new BufferDesc((ulong)_definitionBytes.Length,
            BufferUsages.ShaderRead | BufferUsages.CopyDestination, Label: "Skin bounds definitions"));
        edit.SetBufferDescription(_current, new BufferDesc(Math.Max(1u, output) * 48ul, BoundsUsages, Label: "Current skin model bounds"));
        edit.SetBufferDescription(_previous, new BufferDesc(Math.Max(1u, output) * 48ul, BoundsUsages, Label: "Previous skin model bounds"));
        edit.SetBufferDescription(_jointTransforms, new BufferDesc(jointCapacity * 144ul, BoundsUsages, Label: "Skin bounds joint transforms"));
        if (_uploadPass != default) edit.Remove(_uploadPass);
        _uploadPass = edit.AddCopyFramePass("Upload skin bounds definitions", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new DefinitionCopy(_uploadInput, _definitions, _current, _previous, _jointTransforms, (ulong)_definitionBytes.Length),
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true), DefinitionCopy.Declare, DefinitionCopy.Record);
        edit.OrderAfter(_boundsPass, _uploadPass);
        edit.OrderAfter(_jointPass, _uploadPass);
        edit.OrderAfter(CompletePass, _uploadPass);
        edit.Commit();
        _dispatch = new((uint)((workCount + 63) / 64), 1, 1);
        _jointDispatch = new((maximumJoints + 63) / 64, (uint)_order.Length, 1);
        _layoutChanged = false;
        _uploadRequired = true;
        Revision++;
    }

    private DispatchArguments _dispatch, _jointDispatch;

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        _controls.Clear();
        bool changed = false;
        foreach (MeshBounds instance in _order)
        {
            bool update = instance.Input != instance.AcceptedInput;
            changed |= update;
            _controls.Add(new(instance.Binding.JointOffset, instance.Binding.InverseBindOffset, instance.First,
                update ? 1u : 0u, (uint)instance.Joints.Length, 0, 0, 0));
        }
        if (_controls.Count == 0) _controls.Add(default);
        RenderGraphFrame frame = context.Graph;
        frame.SetBufferInput(CurrentBounds, new(_current, BufferRange.Whole));
        frame.SetBufferInput(PreviousBounds, new(_previous, BufferRange.Whole));
        frame.SetBufferInput(_boxesInput, new(_definitions, _boxRange));
        frame.SetBufferInput(_workInput, new(_definitions, _workRange));
        frame.SetBufferInput(_controlsInput, frame.Upload<Control>(CollectionsMarshal.AsSpan(_controls), BufferUsages.ShaderRead));
        if (_uploadRequired) frame.SetBufferInput(_uploadInput, frame.Upload(_definitionBytes, BufferUsages.CopySource));
        frame.SetPassEnabled(_uploadPass, _uploadRequired);
        frame.SetPassEnabled(_boundsPass, changed);
        frame.SetPassEnabled(_jointPass, changed);
        frame.SetPassData(_boundsPass, _dispatch);
        frame.SetPassData(_jointPass, _jointDispatch);
        frame.ObserveExecution(this);
    }

    void IRenderGraphFrameExecutionObserver.OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        if (result.TryGetPassCompletion(_uploadPass, out _)) _uploadRequired = false;
        if (result.TryGetPassCompletion(_boundsPass, out _))
            foreach (MeshBounds instance in _order) instance.AcceptedInput = instance.Input;
    }

    private static GeometryBounds BuildGeometry(Mesh mesh, ClusterSkinBounds data)
    {
        ReadOnlySpan<ClusterBVHNode> nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(mesh.Bvh.Span);
        var sourceBoxes = new SkinJointBounds[data.ClusterCount][];
        var sourceLod = new SkinClusterLod[data.ClusterCount];
        float referencePositionError = 0;
        for (int index = 0; index < data.ClusterCount; index++)
        {
            sourceBoxes[index] = data.GetCluster(index).ToArray();
            sourceLod[index] = data.GetLod(index);
            if (sourceLod[index].Level == 0) referencePositionError = Math.Max(referencePositionError, sourceLod[index].PositionError);
        }
        var lodGroups = new Dictionary<int, LodGroup>();
        for (int index = 0; index < sourceLod.Length; index++)
        {
            SkinClusterLod lod = sourceLod[index];
            if (lod.Level == 0) continue;
            if (!lodGroups.TryGetValue(lod.Group, out LodGroup? group)) lodGroups.Add(lod.Group, group = new());
            group.Boxes.Add(sourceBoxes[index]);
            group.Error = Math.Max(group.Error, lod.Error + lod.PositionError + referencePositionError);
        }
        for (int index = 0; index < sourceLod.Length; index++)
            if (sourceLod[index].ParentGroup >= 0) lodGroups[sourceLod[index].ParentGroup].Boxes.Add(sourceBoxes[index]);
        var lodBoxes = lodGroups.ToDictionary(pair => pair.Key, pair => MergeBoxes(pair.Value.Boxes.SelectMany(boxes => boxes)));
        var groups = new SkinJointBounds[nodes.Length + mesh.ClusterGeometryIndices.Length][];
        var selection = new SkinJointBounds[groups.Length][];
        var errors = new float[groups.Length];
        var pageStarts = new uint[mesh.PageCount];
        for (int page = 1; page < pageStarts.Length; page++) pageStarts[page] = pageStarts[page - 1] + mesh.PageDigests[page - 1].ClusterCount;
        for (int cluster = 0; cluster < mesh.ClusterGeometryIndices.Length; cluster++)
        {
            uint source = mesh.ClusterGeometryIndices[cluster];
            int output = nodes.Length + cluster;
            groups[output] = sourceBoxes[source];
            SkinClusterLod lod = sourceLod[source];
            selection[output] = lod.Level == 0 ? groups[output] : lodBoxes[lod.Group];
            errors[output] = lod.Level == 0 ? 0 : lodGroups[lod.Group].Error;
        }
        for (int index = 0; index < nodes.Length; index++)
        {
            ClusterBVHNode node = nodes[index];
            uint first = node.ChildPointer, count = node.ChildCount;
            if (node.NodeType != 0)
            {
                node.GetLeafData(out uint start, out count);
                first = (uint)nodes.Length + pageStarts[node.ChildPointer] + start;
            }
            groups[index] = MergeBoxes(groups.Skip((int)first).Take((int)count).SelectMany(boxes => boxes));
            if (node.NodeType == 0)
            {
                selection[index] = MergeBoxes(selection.Skip((int)first).Take((int)count).SelectMany(boxes => boxes));
                for (uint child = 0; child < count; child++) errors[index] = Math.Max(errors[index], errors[first + child]);
            }
            else
            {
                int parent = (int)node.BoundMax.W - 1;
                selection[index] = parent >= 0 ? lodBoxes[parent] : groups[index];
                errors[index] = parent >= 0 ? lodGroups[parent].Error : node.LODError;
            }
        }
        var boxes = new List<Box>();
        var locations = new Dictionary<SkinJointBounds[], InfluenceRange>();
        var ranges = new BoundsGroup[groups.Length];
        for (int group = 0; group < groups.Length; group++)
            ranges[group] = new(AddBoxes(groups[group]), AddBoxes(selection[group]), errors[group]);
        return new((uint)nodes.Length, ranges, [.. boxes]);

        InfluenceRange AddBoxes(SkinJointBounds[] source)
        {
            if (locations.TryGetValue(source, out InfluenceRange range)) return range;
            range = new((uint)boxes.Count, (uint)source.Length);
            foreach (SkinJointBounds box in source) boxes.Add(new(new(box.Minimum, 0), new(box.Maximum, 0), box.Joint, 0, 0, 0));
            locations.Add(source, range);
            return range;
        }
    }

    private static SkinJointBounds[] MergeBoxes(IEnumerable<SkinJointBounds> boxes)
    {
        var joints = new Dictionary<uint, SkinJointBounds>();
        foreach (SkinJointBounds box in boxes)
            joints[box.Joint] = joints.TryGetValue(box.Joint, out SkinJointBounds before)
                ? new(box.Joint, Vector3.Min(before.Minimum, box.Minimum), Vector3.Max(before.Maximum, box.Maximum)) : box;
        return joints.OrderBy(value => value.Key).Select(value => value.Value).ToArray();
    }

    public void OnDestroy(ref RenderPrepareSystemContext context) => context.World.ReleaseQuery(_query);
    public void Dispose() { _pipeline.Dispose(); _jointPipeline.Dispose(); }

    private enum Resource { Controls, Work, Boxes, Current, Previous, CurrentBones, PreviousBones, Joints, InverseBind, Transforms }
    private readonly record struct GeometryKey(ReadOnlyMemory<byte> Bvh, ImmutableArray<uint> Clusters, ClusterSkinBounds Data);
    private readonly record struct InfluenceRange(uint First, uint Count);
    private readonly record struct BoundsGroup(InfluenceRange Cull, InfluenceRange Lod, float Error);
    private sealed record GeometryBounds(uint NodeCount, BoundsGroup[] Ranges, Box[] Boxes);
    private sealed class LodGroup
    {
        internal readonly List<SkinJointBounds[]> Boxes = [];
        internal float Error;
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Control(uint Joints, uint InverseBind, uint Output,
        uint Update, uint JointCount, uint Pad0, uint Pad1, uint Pad2);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Work(uint Instance, uint FirstBox, uint BoxCount, uint Output,
        uint FirstLodBox, uint LodBoxCount, float Error, uint Pad);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Box(Vector4 Minimum, Vector4 Maximum, uint Joint, uint Pad0, uint Pad1, uint Pad2);
    private sealed class MeshBounds(Entity entity)
    {
        internal readonly Entity Entity = entity;
        internal GeometryKey Geometry;
        internal Mesh? Mesh;
        internal RenderSkinBinding Binding;
        internal RenderSkinJoint[] Joints = [];
        internal Entity[] Sections = [];
        internal SkinningFeature.PoseInput[] Inputs = [];
        internal uint First;
        internal ulong Collection, Input, AcceptedInput;
    }
    private readonly record struct DefinitionCopy(GraphBufferInput Upload, GraphBufferId Definitions, GraphBufferId Current,
        GraphBufferId Previous, GraphBufferId Transforms, ulong Bytes)
    {
        internal static void Declare(ref PassDefinition pass, ref DefinitionCopy data)
        {
            pass.Read(data.Upload, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
            pass.Write(data.Definitions, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            pass.Write(data.Current, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            pass.Write(data.Previous, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            pass.Write(data.Transforms, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }
        internal static void Record(ref CopyPassCommandScope commands, in DefinitionCopy data)
        {
            BufferRegion upload = commands.GetBufferRegion(data.Upload);
            commands.CopyBuffer(new(upload.Buffer, upload.Range.Offset, commands.GetBuffer(data.Definitions), 0, data.Bytes));
            commands.ClearBuffer(commands.GetBuffer(data.Current), BufferRange.Whole, 0);
            commands.ClearBuffer(commands.GetBuffer(data.Previous), BufferRange.Whole, 0);
            commands.ClearBuffer(commands.GetBuffer(data.Transforms), BufferRange.Whole, 0);
        }
    }
}
