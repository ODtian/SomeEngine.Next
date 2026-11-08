using System.Numerics;
using System.Runtime.InteropServices;
using MeshOptimizer;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.RenderGraph;
using SomeEngine.Render.Systems;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct ClusterSceneUniforms
    {
        internal uint InstanceCount;
        internal uint HasPreviousBounds;
        internal uint UseInstanceGroups;
        private uint Padding;
        internal Vector3 PreviousWorldTranslation;
        private float TranslationPadding;
    }

    private ClusterSceneUniforms _sceneValues;
    private RenderInstanceBatch? _sceneBatch;
    private ulong _sceneInstanceRevision;
    private ulong _sceneGeometryRevision;
    private ulong _sceneRasterRevision;
    private WorldOrigin _sceneOrigin;
    private WorldOrigin _frameSceneOrigin;
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct SceneInstanceGroup(uint FirstInstance, uint InstanceCount, uint InstanceSet, uint Padding = 0);

    private uint[] _sceneInstances = [];
    private readonly List<uint> _viewInstanceSets = [];
    private SceneInstanceGroup[] _sceneGroups = [];
    private RenderInstanceBatch? _sceneGroupBatch;
    private uint _sceneSetVersion;
    private bool _sceneGroupsChanged;

    public uint CurrentInstanceGroupCount => checked((uint)_sceneGroups.Length);

    private void RefreshSceneGroups()
    {
        RenderInstanceBatch batch = _instances.Current!.Groups[0].Batch;
        if (ReferenceEquals(batch, _sceneGroupBatch) && _sceneSetVersion == _resources.World.CurrentTick) return;
        var inputs = new SceneSetInputs(_instances.Current!, !ReferenceEquals(batch, _sceneGroupBatch));
        _resources.World.ExecuteQuery(_sceneQuery, _sceneSetVersion, ref inputs,
            static (QueryCursor query, ref SceneSetInputs data) => data.Collect(query));
        _sceneSetVersion = inputs.Version;
        if (!inputs.Changed) return;
        var groups = new List<SceneInstanceGroup>();
        var rows = new List<uint>(batch.InstanceCount);
        foreach ((uint set, List<(uint Row, Vector3 Position)> instances) in inputs.Sets)
        {
            uint[] remap = new uint[instances.Count];
            if (_options.UseInstanceGroups)
            {
                Vector3[] positions = instances.Select(value => value.Position).ToArray();
                Meshopt.SpatialSortRemap(remap, MemoryMarshal.Cast<Vector3, float>(positions), 12u);
            }
            else
            {
                for (uint index = 0; index < remap.Length; index++) remap[index] = index;
            }
            uint[] sorted = new uint[instances.Count];
            for (int index = 0; index < instances.Count; index++) sorted[remap[index]] = instances[index].Row;
            for (int first = 0; first < sorted.Length; first += 64)
                groups.Add(new(checked((uint)(rows.Count + first)), checked((uint)Math.Min(64, sorted.Length - first)), set));
            rows.AddRange(sorted);
        }
        _sceneGroups = groups.ToArray();
        _sceneInstances = rows.ToArray();
        _sceneGroupBatch = batch;
        _sceneGroupsChanged = true;
    }

    private struct SceneSetInputs(RenderInstanceBatches<RenderInstanceSingleGroup> batches, bool rebuild)
    {
        internal readonly Dictionary<uint, List<(uint Row, Vector3 Position)>> Sets = [];
        internal bool Changed = rebuild;
        internal uint Version;
        private int _entityIndex;

        internal void Collect(QueryCursor query)
        {
            Version = query.CurrentSystemVersion;
            foreach (QueryChunkView chunk in query.Chunks)
                Changed |= chunk.Has<InstanceSet>() && chunk.HasChangedSinceLastSystemVersion<InstanceSet>();
            if (!Changed) return;
            foreach (QueryRow row in query.Rows)
            {
                uint set = row.TryRead<InstanceSet>(out var collection) ? collection.Value : 0;
                if (!Sets.TryGetValue(set, out var instances)) Sets.Add(set, instances = []);
                Vector3 position = row.Read<RenderTransform>().Position;
                foreach (var address in batches.RowsForEntity(_entityIndex++))
                    instances.Add((checked((uint)address.Row), position));
            }
        }
    }

    private void BindSceneSelections(ref RenderGraphFrame graph, scoped ReadOnlySpan<RenderView> views,
        scoped Span<ClusterViewUniforms> packedViews)
    {
        List<uint> selections = _viewInstanceSets;
        selections.Clear();
        for (int index = 0; index < views.Length; index++)
        {
            packedViews[index].InstanceSelectionOffset = checked((uint)selections.Count);
            packedViews[index].InstanceSelectionCount = checked((uint)views[index].InstanceSets.Length);
            selections.AddRange(views[index].InstanceSets.Span);
        }
        if (selections.Count == 0) selections.Add(0);
        graph.SetBufferInput(GraphData.SceneSelections, graph.Upload<uint>(CollectionsMarshal.AsSpan(selections),
            BufferUsages.ShaderRead, "Cluster view instance sets"));
    }

    private void ReconcileSceneGroups()
    {
        if (_instances.Current is not { GroupCount: 1 }) return;
        RefreshSceneGroups();
        if (!_graphInstalled || (ulong)_sceneGroups.Length * 16u <= GraphData.BufferDescriptions[GraphData.SceneGroups].Description.Size) return;
        EditGraph((ref RenderGraphEdit edit) =>
        {
            int count = _sceneGroups.Length;
            UpdateBufferData(ref edit, GraphData.SceneGroups, ClusterBytes.Zero(checked(count * 16)),
                [new(0, checked((ulong)count * 16u))]);
            edit.SetBufferDescription(GraphData.SceneChunks, new BufferDesc(
                _options.UseInstanceGroups ? checked((ulong)count * 32u) : 32u,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Cluster spatial chunk bounds"));
            _sceneGroupsChanged = true;
            _sceneBatch = null;
        });
    }

    private void InstallSceneBounds(ref RenderGraphEdit graph)
    {
        uint capacity = checked((uint)_resources.InstanceResources.Options.RowCapacity);
        int groupCapacity = Math.Max(_sceneGroups.Length, 1);
        GraphData.SceneChunks = graph.CreatePersistentBuffer(new BufferDesc(
            _options.UseInstanceGroups ? (ulong)groupCapacity * 32u : 32u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
            "Cluster spatial chunk bounds"));
        GraphData.InstanceWorldBounds = graph.CreatePersistentBuffer(new BufferDesc(
            (ulong)capacity * 64u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
            "Cluster current and previous instance bounds"));
        var clear = new ClusterBufferClearParameters(GraphData.InstanceWorldBounds);
        GraphData.SceneInitialization = graph.AddRegion("Cluster instance bounds initialization", _regions.PrimaryGeometry);
        graph.AddCopyFramePass("Initialize Cluster instance bounds",
            PassQueueSelection.AnyOfType(QueueType.Copy), clear, default,
            static (ref PassDefinition pass, ref ClusterBufferClearParameters data) =>
                _ = pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete), ClusterBufferClearParameters.Record, region: GraphData.SceneInitialization);
        GraphData.SceneBoundsRegion = graph.AddRegion("Cluster scene bounds", _regions.PrimaryGeometry);
        GraphPassId bounds = AddComputePass(ref graph, "Update Cluster spatial scene bounds", _pipelines!.SceneBounds,
            new ClusterDispatchParameters(new DispatchArguments(1, 1, 1)), 0, ClusterDispatchParameters.Record, region: GraphData.SceneBoundsRegion);
        GraphData.SceneBounds = new ClusterLayerDispatch(bounds, 1, 1);
        GraphData.SceneInstances = graph.CreateBufferInput(BufferUsages.ShaderRead);
        GraphData.SceneInstanceData = CreateBufferData(ref graph,
            ClusterBytes.Zero(checked((int)Math.Max(capacity, 1u) * sizeof(uint))),
            BufferUsages.ShaderRead, "Cluster spatial instance indices");
        GraphData.SceneGroups = CreateBufferData(ref graph, ClusterBytes.Zero(checked(groupCapacity * 16)),
            BufferUsages.ShaderRead, "Cluster instance set groups");
        GraphData.SceneSelections = graph.CreateBufferInput(BufferUsages.ShaderRead);
        GraphData.SceneUniforms = CreateParameters(ref graph, new byte[32], "Cluster scene bounds parameters");
        PassDefinition pass = graph.GetPass(GraphData.SceneBounds.Pass);
        _ = pass.Bind(GraphData.SceneUniforms, PipelineSync.ComputeShading);
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
            graph.CreateBufferSrv(GraphData.Resources.Bvh, label: "Cluster scene BVH")));
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.Resources.InstanceData), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferCbv(GraphData.Resources.InstanceProperties, GraphData.Resources.InstancePropertiesRange), PipelineSync.ComputeShading);
        BindDeformBounds(ref graph, ref pass);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.SceneInstances, structureStride: 4), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.SceneGroups, structureStride: 16), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(GraphData.SceneChunks, structureStride: 16), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(GraphData.InstanceWorldBounds, structureStride: 16), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.Resources.MaterialBounds, structureStride: sizeof(uint)), PipelineSync.ComputeShading);
        _sceneBatch = null;
        _sceneGroupsChanged = true;
    }

    private void BindSceneBounds(ref RenderGraphFrame graph, RenderInstanceBatch batch, bool hasTargetViews,
        WorldOrigin worldOrigin)
    {
        bool hasPrevious = ReferenceEquals(_sceneBatch, batch);
        bool changed = !hasPrevious || _sceneInstanceRevision != batch.ContentRevision ||
            _sceneGeometryRevision != GeometryRevision || _sceneRasterRevision != RasterRevision || _sceneOrigin != worldOrigin || !_resources.GeometryBuilds(graph.FrameIdentity).IsEmpty || _sceneDeformRevision != (_deformBounds?.Revision ?? 0);
        _frameSceneOrigin = worldOrigin;
        if (_sceneGroupsChanged)
        {
            graph.WriteBuffer(GraphData.SceneInstanceData, 0, MemoryMarshal.AsBytes(_sceneInstances.AsSpan()));
            graph.WriteBuffer(GraphData.SceneGroups, 0, MemoryMarshal.AsBytes(_sceneGroups.AsSpan()));
            changed = true;
        }
        graph.SetBufferInput(GraphData.SceneInstances, new GraphBufferSlice(GraphData.SceneInstanceData,
            new BufferRange(0, checked((ulong)Math.Max(batch.InstanceCount, 1) * sizeof(uint)))));
        _sceneValues = new ClusterSceneUniforms
        {
            InstanceCount = checked((uint)batch.InstanceCount), HasPreviousBounds = hasPrevious ? 1u : 0u,
            UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
            PreviousWorldTranslation = hasPrevious ? _sceneOrigin.RelativeTo(worldOrigin) : Vector3.Zero,
        };
        graph.SetPassData(GraphData.SceneBounds.Pass, new ClusterDispatchParameters(new DispatchArguments(
            checked((uint)_sceneGroups.Length), 1, 1)));
        bool hasGeometry = batch.InstanceCount != 0 && GraphData.Outputs.Geometry.HasBvhData;
        graph.SetRegionEnabled(GraphData.SceneBoundsRegion, changed && hasGeometry);
        SetGeometryPassesEnabled(ref graph, hasGeometry, hasGeometry && hasTargetViews);
        graph.SetRegionEnabled(GraphData.SceneInitialization, _sceneBatch is null);
    }

    private void SetGeometryPassesEnabled(ref RenderGraphFrame graph, bool hasGeometry, bool hasTargetGeometry)
    {
        CullWork work = GraphData.Resources.Work;
        work.FramePhases = hasGeometry ? hasTargetGeometry ? 15u : 3u : 0u;
        graph.SetRegionEnabled(work.Initialization, !work.Initialized);
        bool scatter = hasGeometry && CalculateShadeBinItemCapacity(GraphData.Resources) == 0u;
        graph.SetRegionEnabled(GraphData.PrimaryGeometry, hasGeometry);
        graph.SetRegionEnabled(_regions.TargetGeometry, hasTargetGeometry);
        graph.SetRegionEnabled(GraphData.ShadeBinning, hasGeometry);
        graph.SetRegionEnabled(GraphData.ShadeGroups.Parent, hasGeometry);
        graph.SetRegionEnabled(GraphData.ShadowRequestGroups.Parent, hasGeometry);
        graph.SetRegionEnabled(GraphData.ShadeScatter, scatter);
    }

    private void BindSceneSeed(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.SceneInstances, structureStride: 4), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.SceneGroups, structureStride: 16), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.SceneSelections, structureStride: 4), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.SceneChunks, structureStride: 16), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.InstanceWorldBounds, structureStride: 16), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.Resources.MaterialBounds, structureStride: sizeof(uint)), PipelineSync.ComputeShading);
    }
}
