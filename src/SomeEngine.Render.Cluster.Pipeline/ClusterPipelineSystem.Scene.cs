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
        internal uint UseInstanceGroups;
    }

    private GraphBufferId _sceneChunks;
    private GraphBufferId _instanceWorldBounds;
    private GraphBufferId _sceneInstanceData;
    private GraphBufferInput _frameSceneInstances = null!;
    private GraphBufferInput _sceneUniforms = null!;
    private ClusterSceneUniforms _sceneValues;
    private GraphPassId _sceneBoundsPass;
    private GraphPassId _sceneInitializePass;
    private RenderInstanceBatch? _sceneBatch;
    private ulong _sceneInstanceRevision;
    private ulong _sceneGeometryRevision;
    private RenderTransform[] _sceneTransforms = [];
    private Vector3[] _scenePositions = [];
    private uint[] _sceneRemap = [];
    private uint[] _sceneInstances = [];
    private (bool Geometry, bool Targets)? _geometryPassesEnabled;

    private void InstallSceneBounds(ref RenderGraphEdit graph)
    {
        uint capacity = checked((uint)_resources.InstanceResources.Options.RowCapacity);
        _sceneChunks = graph.CreatePersistentBuffer(new BufferDesc(
            _options.UseInstanceGroups ? (ulong)((capacity + 63u) / 64u) * 32u : 32u, BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
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
        if (_renderContext.BoundsConsumer != default)
            graph.OrderAfter(_renderContext.BoundsConsumer, _sceneBoundsPass);
        _frameSceneInstances = graph.CreateBufferInput(BufferUsages.ShaderRead);
        _sceneInstanceData = _uploads!.Declare(ref graph,
            ClusterUploadData.Zero(_options.UseInstanceGroups ? checked((int)Math.Max(capacity, 1u) * sizeof(uint)) : sizeof(uint)),
            BufferUsages.ShaderRead, "Cluster spatial instance indices");
        _sceneUniforms = _uploads.DeclareUniform(ref graph, new byte[16], "Cluster scene bounds parameters");
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

    private void BindSceneBounds(ref RenderGraphFrame graph, RenderInstanceBatch batch, bool hasTargetViews)
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
            if (count != 0)
            {
                byte[] indices = MemoryMarshal.AsBytes(_sceneInstances.AsSpan()).ToArray();
                int capacity = checked(_resources.InstanceResources.Options.RowCapacity * sizeof(uint));
                ClusterUploadData value = ClusterUploadData.Zero(capacity).WithChanges(capacity, [(0, indices)]);
                _uploads!.SetChangedValue(_sceneInstanceData, value, [new BufferRange(0, checked((ulong)indices.Length))]);
            }
        }
        graph.SetBufferInput(_frameSceneInstances, new GraphBufferSlice(_sceneInstanceData,
            new BufferRange(0, _options.UseInstanceGroups ? checked((ulong)Math.Max(batch.InstanceCount, 1) * sizeof(uint)) : sizeof(uint))));
        _sceneValues = new ClusterSceneUniforms
        {
            InstanceCount = checked((uint)batch.InstanceCount), HasPreviousBounds = hasPrevious ? 1u : 0u,
            UseInstanceGroups = _options.UseInstanceGroups ? 1u : 0u,
        };
        graph.SetPassData(_sceneBoundsPass, new ClusterDispatchParameters(new DispatchArguments(
            (checked((uint)batch.InstanceCount) + 63u) / 64u, 1, 1)));
        bool hasGeometry = batch.InstanceCount != 0 && _outputs.Geometry.HasBvhData;
        graph.SetPassEnabled(_sceneBoundsPass, changed && hasGeometry);
        SetGeometryPassesEnabled(ref graph, hasGeometry, hasGeometry && hasTargetViews);
        graph.SetPassEnabled(_sceneInitializePass, _sceneBatch is null);
    }

    private void SetGeometryPassesEnabled(ref RenderGraphFrame graph, bool hasGeometry, bool hasTargetGeometry)
    {
        if (_geometryPassesEnabled == (hasGeometry, hasTargetGeometry)) return;
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
            graph.SetPassEnabled(_continuationResetPass, hasTargetGeometry);
            graph.SetPassEnabled(_continuationDeferredResetPass, hasTargetGeometry);
            graph.SetPassEnabled(_continuationTraversalSeedPass, hasTargetGeometry);
            foreach (GraphPassId pass in _continuationTraversalLevelPasses)
                graph.SetPassEnabled(pass, hasTargetGeometry);
            foreach (GraphPassId pass in _continuationPostTraversalLevelPasses)
                graph.SetPassEnabled(pass, hasTargetGeometry);
            graph.SetPassEnabled(_continuationDeferredResumePass, hasTargetGeometry);
            graph.SetPassEnabled(_continuationCullPass, hasTargetGeometry);
            graph.SetPassEnabled(_continuationPostCullPass, hasTargetGeometry);
        }
        for (int index = 0; index < _rasterBinning.Length; index++)
        {
            RasterBinning? binning = _rasterBinning[index];
            if (binning is null) continue;
            bool enabled = index < 2 ? hasGeometry : hasTargetGeometry;
            graph.SetPassEnabled(binning.Count, enabled);
            graph.SetPassEnabled(binning.Scatter, enabled);
        }
        foreach (RasterEpoch epoch in _rasterEpochs.Values)
        {
            bool enabled = epoch.PolicyTarget ? hasTargetGeometry : hasGeometry;
            if (epoch.PolicyTarget)
                foreach (GraphPassId pass in epoch.Work.Passes)
                    graph.SetPassEnabled(pass, enabled);
            foreach (RasterBin bin in epoch.Bins.Values)
                foreach (GraphPassId pass in bin.Passes.Passes)
                    graph.SetPassEnabled(pass, enabled);
            foreach (GraphPassId pass in epoch.Patches.Passes)
                graph.SetPassEnabled(pass, enabled);
            foreach (TessellationGroup group in epoch.Splits.Values)
            {
                if (epoch.PolicyTarget)
                    foreach (GraphPassId pass in group.Reset.Passes)
                        graph.SetPassEnabled(pass, enabled);
                foreach (GraphPassId pass in group.Split.Passes)
                    graph.SetPassEnabled(pass, enabled);
                foreach (TessellationSeed seed in group.Seeds.Values)
                    foreach (GraphPassId pass in seed.Passes.Passes)
                        graph.SetPassEnabled(pass, enabled);
            }
        }
        graph.SetPassEnabled(_shadeCountPass, hasGeometry);
        graph.SetPassEnabled(_shadeScatterPass, scatter);
        foreach (ShadeGroup group in _shadeGroups.Values)
            graph.SetPassEnabled(group.Pass, hasGeometry);
        _geometryPassesEnabled = (hasGeometry, hasTargetGeometry);
    }

    private void BindSceneSeed(ref RenderGraphEdit graph, ref PassDefinition pass)
    {
        _ = pass.Bind(graph.CreateBufferSrv(_frameSceneInstances, structureStride: 4), PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(_sceneChunks, structureStride: 16), PipelineSync.ComputeShading, BufferContentScope.CountBoundedPrefix);
        _ = pass.Bind(graph.CreateBufferSrv(_instanceWorldBounds, structureStride: 16), PipelineSync.ComputeShading);
    }
}
