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
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct ClusterSceneUniforms
    {
        internal uint InstanceCount;
        internal uint HasPreviousBounds;
        internal uint UseInstanceGroups;
    }

    private ClusterSceneUniforms _sceneValues;
    private RenderInstanceBatch? _sceneBatch;
    private ulong _sceneInstanceRevision;
    private ulong _sceneGeometryRevision;
    private Vector3[] _scenePositions = [];
    private uint[] _sceneRemap = [];
    private uint[] _sceneInstances = [];

    private void InstallSceneBounds(ref RenderGraphEdit graph)
    {
        uint capacity = checked((uint)_resources.InstanceResources.Options.RowCapacity);
        GraphData.SceneChunks = graph.CreatePersistentBuffer(new BufferDesc(
            _options.UseInstanceGroups ? (ulong)((capacity + 63u) / 64u) * 32u : 32u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
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
            ClusterBytes.Zero(_options.UseInstanceGroups ? checked((int)Math.Max(capacity, 1u) * sizeof(uint)) : sizeof(uint)),
            BufferUsages.ShaderRead, "Cluster spatial instance indices");
        GraphData.SceneUniforms = CreateParameters(ref graph, new byte[16], "Cluster scene bounds parameters");
        PassDefinition pass = graph.GetPass(GraphData.SceneBounds.Pass);
        _ = pass.Bind(GraphData.SceneUniforms, PipelineSync.ComputeShading);
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
            graph.CreateBufferSrv(GraphData.Resources.Bvh, structureStride: 64, label: "Cluster scene BVH")));
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.Resources.InstanceData), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferCbv(GraphData.Resources.InstanceProperties, GraphData.Resources.InstancePropertiesRange), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.SceneInstances, structureStride: 4), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(GraphData.SceneChunks, structureStride: 16), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(GraphData.InstanceWorldBounds, structureStride: 16), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _sceneBatch = null;
    }

    private void BindSceneBounds(ref RenderGraphFrame graph, RenderWorld world, RenderInstanceBatch batch, bool hasTargetViews)
    {
        bool hasPrevious = ReferenceEquals(_sceneBatch, batch);
        bool changed = !hasPrevious || _sceneInstanceRevision != batch.ContentRevision || _sceneGeometryRevision != GeometryRevision;
        // Movement changes bounds, not the instance membership of the spatial groups.
        // Keep their order until batch replacement changes the row mapping; the GPU refits
        // the existing groups below. Geometry residency changes also preserve that mapping.
        if (_options.UseInstanceGroups && !hasPrevious)
        {
            int count = batch.InstanceCount;
            if (_sceneInstances.Length != count)
            {
                _scenePositions = new Vector3[count];
                _sceneRemap = new uint[count];
                _sceneInstances = new uint[count];
            }
            var positions = new ScenePositions(_instances.Current!, _scenePositions);
            world.ExecuteQuery(_sceneQuery, ref positions,
                static (QueryCursor query, ref ScenePositions data) => data.Collect(query));
            if (count != 0)
                Meshopt.SpatialSortRemap(_sceneRemap, MemoryMarshal.Cast<Vector3, float>(_scenePositions), 12u);
            for (uint index = 0; index < count; index++)
                _sceneInstances[_sceneRemap[index]] = index;
            if (count != 0)
            {
                graph.WriteBuffer(GraphData.SceneInstanceData, 0, MemoryMarshal.AsBytes(_sceneInstances.AsSpan()));
            }
        }
        graph.SetBufferInput(GraphData.SceneInstances, new GraphBufferSlice(GraphData.SceneInstanceData,
            new BufferRange(0, _options.UseInstanceGroups ? checked((ulong)Math.Max(batch.InstanceCount, 1) * sizeof(uint)) : sizeof(uint))));
        _sceneValues = new ClusterSceneUniforms
        {
            InstanceCount = checked((uint)batch.InstanceCount), HasPreviousBounds = hasPrevious ? 1u : 0u,
            UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
        };
        graph.SetPassData(GraphData.SceneBounds.Pass, new ClusterDispatchParameters(new DispatchArguments(
            (checked((uint)batch.InstanceCount) + 63u) / 64u, 1, 1)));
        bool hasGeometry = batch.InstanceCount != 0 && GraphData.Outputs.Geometry.HasBvhData;
        graph.SetRegionEnabled(GraphData.SceneBoundsRegion, changed && hasGeometry);
        SetGeometryPassesEnabled(ref graph, hasGeometry, hasGeometry && hasTargetViews);
        graph.SetRegionEnabled(GraphData.SceneInitialization, _sceneBatch is null);
    }

    private struct ScenePositions(RenderInstanceBatches<RenderInstanceSingleGroup> batches, Vector3[] positions)
    {
        private int _entityIndex;

        internal void Collect(QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                Vector3 position = row.Read<RenderTransform>().Position;
                foreach (var address in batches.RowsForEntity(_entityIndex++))
                    positions[address.Row] = position;
            }
        }
    }

    private void SetGeometryPassesEnabled(ref RenderGraphFrame graph, bool hasGeometry, bool hasTargetGeometry)
    {
        bool scatter = hasGeometry && CalculateShadeBinItemCapacity(GraphData.Resources) == 0u;
        graph.SetRegionEnabled(GraphData.PrimaryGeometry, hasGeometry);
        graph.SetRegionEnabled(_regions.TargetGeometry, hasTargetGeometry);
        graph.SetRegionEnabled(GraphData.ShadeBinning, hasGeometry);
        graph.SetRegionEnabled(GraphData.ShadeGroups.Parent, hasGeometry);
        graph.SetRegionEnabled(GraphData.ShadeScatter, scatter);
    }

    private void BindSceneSeed(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.SceneInstances, structureStride: 4), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.SceneChunks, structureStride: 16), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.InstanceWorldBounds, structureStride: 16), PipelineSync.ComputeShading);
    }
}
