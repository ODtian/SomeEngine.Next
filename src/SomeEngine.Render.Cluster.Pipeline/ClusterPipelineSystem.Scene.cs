using System.Numerics;
using System.Runtime.InteropServices;
using MeshOptimizer;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct ClusterSceneUniforms
    {
        internal uint InstanceCount;
        internal uint HasPreviousBounds;
    }

    private GraphBufferId _sceneChunks;
    private GraphBufferId _instanceWorldBounds;
    private GraphBufferInput _frameSceneInstances = null!;
    private GraphBufferInput _sceneUniforms = null!;
    private GraphPassId _sceneBoundsPass;
    private GraphPassId _sceneInitializePass;
    private RenderInstanceBatch? _sceneBatch;
    private ulong _sceneInstanceRevision;
    private ulong _sceneGeometryRevision;
    private RenderTransform[] _sceneTransforms = [];
    private Vector3[] _scenePositions = [];
    private uint[] _sceneRemap = [];
    private uint[] _sceneInstances = [];
    private bool? _geometryPassesEnabled;

    private void InstallSceneBounds(ref RenderGraphEdit graph)
    {
        uint capacity = checked((uint)_resources.InstanceResources.Options.RowCapacity);
        _sceneChunks = graph.CreatePersistentBuffer(new BufferDesc(
            (ulong)((capacity + 63u) / 64u) * 32u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
            "Cluster spatial chunk bounds"));
        _instanceWorldBounds = graph.CreatePersistentBuffer(new BufferDesc(
            (ulong)capacity * 64u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination,
            "Cluster current and previous instance bounds"));
        var clear = new ClusterBufferClearParameters(_instanceWorldBounds);
        _sceneInitializePass = graph.AddCopyFramePass("Initialize Cluster instance bounds",
            PassQueueSelection.AnyOfType(QueueType.Copy), clear, default,
            static (ref PassDefinition pass, ref ClusterBufferClearParameters data) =>
                _ = pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete), ClusterBufferClearParameters.Record);
        _sceneBoundsPass = AddComputePass(ref graph, "Update Cluster spatial scene bounds", _pipelines!.SceneBounds,
            new ClusterDispatchParameters(new DispatchArguments(1, 1, 1)), 0, ClusterDispatchParameters.Record);
        graph.OrderAfter(_sceneBoundsPass, _sceneInitializePass);
        _frameSceneInstances = graph.CreateBufferInput(BufferUsages.ShaderRead);
        _sceneUniforms = graph.CreateBufferInput(BufferUsages.Constant);
        PassDefinition pass = graph.GetPass(_sceneBoundsPass);
        _ = pass.Bind(_sceneUniforms, PipelineSync.ComputeShading);
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(
            graph.CreateBufferSrv(_frameResources.Bvh, structureStride: 64, label: "Cluster scene BVH")));
        _ = pass.Bind(graph.CreateBufferSrv(_frameResources.InstanceData), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferCbv(_frameResources.InstanceProperties, _frameResources.InstancePropertiesRange), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_frameSceneInstances, structureStride: 4), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferUav(_sceneChunks, structureStride: 16), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined, BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferUav(_instanceWorldBounds, structureStride: 16), PipelineSync.ComputeShading,
            GraphAccessMode.ReadWrite, WriteCoverage.Partial);
        _sceneBatch = null;
        _geometryPassesEnabled = null;
    }

    private void BindSceneBounds(ref RenderGraphFrame graph, RenderInstanceBatch batch)
    {
        bool hasPrevious = ReferenceEquals(_sceneBatch, batch);
        bool changed = !hasPrevious || _sceneInstanceRevision != batch.ContentRevision || _sceneGeometryRevision != GeometryRevision;
        if (changed)
        {
            int count = batch.InstanceCount;
            if (_sceneInstances.Length != count)
            {
                _sceneTransforms = new RenderTransform[count];
                _scenePositions = new Vector3[count];
                _sceneRemap = new uint[count];
                _sceneInstances = new uint[count];
            }
            _resources.InstanceResources.ReadInstances(batch,
                _instanceLayout.Resolve<RenderTransform>(RenderInstanceTransformProperties.CurrentTransformKey), _sceneTransforms.AsSpan());
            for (int index = 0; index < count; index++)
                _scenePositions[index] = _sceneTransforms[index].Position;
            if (count != 0)
                Meshopt.SpatialSortRemap(_sceneRemap, MemoryMarshal.Cast<Vector3, float>(_scenePositions), 12u);
            for (uint index = 0; index < count; index++)
                _sceneInstances[_sceneRemap[index]] = index;
        }
        graph.SetBufferInput(_frameSceneInstances, graph.Upload<uint>(
            _sceneInstances.Length == 0 ? [0u] : _sceneInstances, BufferUsages.ShaderRead, "Cluster spatial instance indices"));
        Span<byte> constants = stackalloc byte[256];
        constants.Clear();
        var uniforms = new ClusterSceneUniforms { InstanceCount = checked((uint)batch.InstanceCount), HasPreviousBounds = hasPrevious ? 1u : 0u };
        MemoryMarshal.Write(constants, in uniforms);
        graph.SetBufferInput(_sceneUniforms, graph.Upload<byte>(constants, BufferUsages.Constant, "Cluster scene bounds parameters"));
        graph.SetPassData(_sceneBoundsPass, new ClusterDispatchParameters(new DispatchArguments(
            (checked((uint)batch.InstanceCount) + 63u) / 64u, 1, 1)));
        bool hasGeometry = batch.InstanceCount != 0 && _outputs.Geometry.HasBvhData;
        graph.SetPassEnabled(_sceneBoundsPass, changed && hasGeometry);
        SetGeometryPassesEnabled(ref graph, hasGeometry);
        graph.SetPassEnabled(_sceneInitializePass, _sceneBatch is null);
    }

    private void SetGeometryPassesEnabled(ref RenderGraphFrame graph, bool hasGeometry)
    {
        if (_geometryPassesEnabled == hasGeometry) return;
        graph.SetPassEnabled(_traversalSeedPass, hasGeometry);
        foreach (GraphPassId pass in _traversalLevelPasses)
            graph.SetPassEnabled(pass, hasGeometry);
        foreach (GraphPassId pass in _primaryPostTraversalLevelPasses)
            graph.SetPassEnabled(pass, hasGeometry);
        graph.SetPassEnabled(_primaryDeferredResumePass, hasGeometry);
        graph.SetPassEnabled(_phaseOneCullPass, hasGeometry);
        graph.SetPassEnabled(_phaseTwoCullPass, hasGeometry);
        if (_continuationTraversalSeedPass != default)
        {
            graph.SetPassEnabled(_continuationTraversalSeedPass, hasGeometry);
            foreach (GraphPassId pass in _continuationTraversalLevelPasses)
                graph.SetPassEnabled(pass, hasGeometry);
            foreach (GraphPassId pass in _continuationPostTraversalLevelPasses)
                graph.SetPassEnabled(pass, hasGeometry);
            graph.SetPassEnabled(_continuationDeferredResumePass, hasGeometry);
            graph.SetPassEnabled(_continuationCullPass, hasGeometry);
            graph.SetPassEnabled(_continuationPostCullPass, hasGeometry);
        }
        foreach (RasterBinning? binning in _rasterBinning)
        {
            if (binning is null) continue;
            graph.SetPassEnabled(binning.Count, hasGeometry);
            graph.SetPassEnabled(binning.Scatter, hasGeometry);
        }
        foreach (RasterEpoch epoch in _rasterEpochs.Values)
        {
            foreach (RasterBin bin in epoch.Bins.Values)
                foreach (GraphPassId pass in bin.Passes.Passes)
                    graph.SetPassEnabled(pass, hasGeometry);
            foreach (GraphPassId pass in epoch.Patches.Passes)
                graph.SetPassEnabled(pass, hasGeometry);
            foreach (TessellationGroup group in epoch.Splits.Values)
            {
                foreach (GraphPassId pass in group.Split.Passes)
                    graph.SetPassEnabled(pass, hasGeometry);
                foreach (TessellationSeed seed in group.Seeds.Values)
                    foreach (GraphPassId pass in seed.Passes.Passes)
                        graph.SetPassEnabled(pass, hasGeometry);
            }
        }
        graph.SetPassEnabled(_shadeCountPass, hasGeometry);
        graph.SetPassEnabled(_shadeScatterPass, hasGeometry);
        foreach (ShadeGroup group in _shadeGroups.Values)
            graph.SetPassEnabled(group.Pass, hasGeometry);
        _geometryPassesEnabled = hasGeometry;
    }

    private void BindSceneSeed(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_frameSceneInstances, structureStride: 4), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_sceneChunks, structureStride: 16), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(_instanceWorldBounds, structureStride: 16), PipelineSync.ComputeShading);
    }
}
