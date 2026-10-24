using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    public const string DeformationTimeGroup = "Cluster deformation";
    public const string DeformationEvaluationTimeGroup = "Cluster deformation evaluation";
    private readonly record struct DeformDrawData(
        GraphBufferId Count, GraphBufferId Previous0, GraphBufferId Previous1, GraphBufferId Previous2);

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    private readonly record struct DeformUniforms(uint Capacity, uint MaxVisibleClusters, uint CurrentBin, uint Padding = 0);

    private void RecordDeformReset(ref RenderGraphEdit graph, in FrameResources frame)
    {
        var clear = new ClusterTessellationResetParameters(frame.DeformAllocation);
        GraphPassId reset = TrackStagePass(graph.AddCopyFramePass("Reset Cluster deformation capacity",
            PassQueueSelection.AnyOfType(QueueType.Graphics), clear,
            new PassOptions(RepeatCommands: true),
            static (ref PassDefinition pass, ref ClusterTessellationResetParameters data) =>
                pass.Write(data.Counters, BufferRange.Whole, PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete),
            ClusterTessellationResetParameters.Record));
        graph.SetPassTimeGroup(reset, DeformationTimeGroup);
    }

    private RasterEpoch UpdateDeformation(ref RenderGraphEdit graph, RasterEpoch epoch, ClusterRasterStageSet stages)
    {
        RemoveRasterPasses(ref graph, epoch.Deformation);
        var passes = new RasterPassGroup();
        _rasterGroup = passes;
        try
        {
            foreach (ClusterRasterInvocation material in stages.Invocations)
            {
                if (material.Deformation is not { } deformation) continue;
                GraphBufferInput uniforms = UploadUniform(ref graph,
                    new DeformUniforms(_options.MaxDeformCacheBytes, _options.MaxCandidates, material.BinIndex),
                    "Cluster user deformation parameters");
                for (int index = 0; index < deformation.Stages.Length; index++)
                {
                    ClusterDeformStage stage = deformation.Stages[index];
                    string name = $"{ClusterDeformation.Entries[index]} bin {material.BinIndex}";
                    GraphPassId passId;
                    if (index is 0 or 3)
                    {
                        var dispatch = new ClusterDispatchParameters(new DispatchArguments(1, 1, 1));
                        passId = AddComputePass(ref graph, name, stage.Pipeline, dispatch, 0,
                            ClusterDispatchParameters.Record);
                    }
                    else
                    {
                        var dispatch = new ClusterIndirectDispatchParameters(RequireDispatchIndirectLayout(),
                            _frameResources.DeformArguments, index == 5 ? 12u : 0u);
                        passId = AddComputePass(ref graph, name, stage.Pipeline, dispatch, 0,
                            static (ref PassDefinition pass, ref ClusterIndirectDispatchParameters data) =>
                                pass.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
                            ClusterIndirectDispatchParameters.Record);
                    }
                    _materialInputsAreShade = false;
                    _materialInputBin = material.BinIndex;
                    PassDefinition pass = graph.GetPass(passId);
                    BindDeformResources(ref graph, ref pass, stage, uniforms, epoch.DrawData, index);
                    graph.SetPassTimeGroup(passId, index == 5 ? DeformationEvaluationTimeGroup : DeformationTimeGroup);
                }
            }
        }
        finally { _rasterGroup = null; }
        OrderRasterPasses(ref graph, passes, epoch.Work.Last);
        epoch = epoch with { Deformation = passes };
        foreach (RasterBin bin in epoch.Bins.Values)
            foreach (GraphPassId pass in bin.Passes.Passes) graph.OrderAfter(pass, epoch.WorkReady);
        foreach (TessellationGroup group in epoch.Splits.Values) graph.OrderAfter(group.Reset.First, epoch.WorkReady);
        if (epoch.Patches.Passes.Count != 0) graph.OrderAfter(epoch.Patches.First, epoch.WorkReady);
        foreach (GraphPassId consumer in epoch.Consumers) graph.OrderAfter(consumer, epoch.WorkReady);
        return epoch;
    }

    private void BindDeformResources(ref RenderGraphEdit graph, ref PassDefinition pass,
        ClusterDeformStage stage, GraphBufferInput uniforms, DeformDrawData draw, int operation)
    {
        ReadOnlySpan<ClusterLinkedStageResource> resources = stage.Resources.Resources.Resources;
        FrameResources frame = _frameResources;
        for (int index = 0; index < resources.Length; index++)
        {
            ref readonly ClusterLinkedStageResource resource = ref resources[index];
            if (BindInactiveStageResource(ref pass, resource)) continue;
            GraphBufferId buffer = default;
            uint stride = 0;
            GraphAccessMode mode = GraphAccessMode.Read;
            switch (resource.Name)
            {
                case "DeformUniforms": pass.Bind(uniforms, PipelineSync.ComputeShading); continue;
                case "InstanceProperties":
                    pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading);
                    continue;
                case "MaterialScalarRegion":
                    pass.Bind(graph.CreateBufferSrv(_shadeScalarsInput!), PipelineSync.ComputeShading); continue;
                case "MaterialScalarOffsets":
                    pass.Bind(graph.CreateBufferSrv(_shadeScalarOffsetsInput!, structureStride: 4), PipelineSync.ComputeShading); continue;
                case "PageHeap":
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(graph.CreateBufferSrv(frame.PageHeap)));
                    _pageHeapReaderPasses = _pageHeapReaderPasses.Add(pass.Id);
                    continue;
                case "InstanceData": buffer = frame.InstanceData; break;
                case "DeformDrawArgs": buffer = draw.Count; break;
                case "DeformReadOffset0": buffer = draw.Previous0; break;
                case "DeformReadOffset1": buffer = draw.Previous1; break;
                case "DeformReadOffset2": buffer = draw.Previous2; break;
                case "DeformRasterBins": buffer = frame.RasterSlots; stride = 4; break;
                case "DeformVisibleClusters": buffer = frame.VisibleClusters; stride = VisibleClusterStride; break;
                case "DeformCacheWrite": buffer = frame.DeformCache; mode = GraphAccessMode.Write; break;
                case "DeformRegionsWrite":
                    buffer = frame.DeformRegions; stride = 8;
                    mode = operation == 1 ? GraphAccessMode.Write : operation == 4 ? GraphAccessMode.ReadWrite : GraphAccessMode.Read;
                    break;
                case "DeformRequests":
                    buffer = frame.DeformRequests; stride = 8;
                    mode = operation == 1 ? GraphAccessMode.Write : GraphAccessMode.Read; break;
                case "DeformPrefixOffsets":
                    buffer = frame.DeformPrefixOffsets; stride = 4;
                    mode = operation == 2 ? GraphAccessMode.Write : GraphAccessMode.Read; break;
                case "DeformPrefixGroups":
                    buffer = frame.DeformPrefixGroups; stride = 8;
                    mode = operation == 2 ? GraphAccessMode.Write : operation == 3 ? GraphAccessMode.ReadWrite : GraphAccessMode.Read;
                    break;
                case "DeformAllocation": buffer = frame.DeformAllocation; stride = 4; mode = GraphAccessMode.ReadWrite; break;
                case "DeformArguments": buffer = frame.DeformArguments; mode = GraphAccessMode.Write; break;
                default:
                    if (stage.Resources.FeatureBinders[index] is { } feature)
                        feature(ref graph, ref pass, PipelineSync.ComputeShading);
                    else
                        BindStageMaterialResource(ref graph, ref pass, resource,
                            stage.Resources.MaterialBindings[index], PipelineSync.ComputeShading);
                    continue;
            }
            if (resource.Type == ResourceBindingType.BufferUav)
                pass.Bind(GraphParameterResourceBinding.WritableBuffer(
                    graph.CreateBufferUav(buffer, structureStride: stride), PipelineSync.ComputeShading,
                    mode, resource.Name == "DeformArguments" ? WriteCoverage.Complete : WriteCoverage.Partial,
                    ResourceContentState.Defined, resource.Name == "DeformArguments"
                        ? BufferContentScope.FullRange : BufferContentScope.CountBoundedRanges));
            else
                pass.Bind(GraphParameterResourceBinding.ReadOnlyBuffer(
                    graph.CreateBufferSrv(buffer, structureStride: stride), PipelineSync.ComputeShading,
                    BufferContentScope.CountBoundedRanges));
        }
    }

    private bool BindDeformRead(ref RenderGraphEdit graph, ref PassDefinition pass,
        in ClusterLinkedStageResource resource, PipelineSync sync)
    {
        if (!ClusterDeformResourceNames.IsRead(resource.Name)) return false;
        bool regions = resource.Name == "VertexDeformRegions";
        pass.Bind(GraphParameterResourceBinding.ReadOnlyBuffer(
            graph.CreateBufferSrv(regions ? _frameResources.DeformRegions : _frameResources.DeformCache,
                structureStride: regions ? 8u : 0u), sync, BufferContentScope.CountBoundedRanges));
        return true;
    }
}
