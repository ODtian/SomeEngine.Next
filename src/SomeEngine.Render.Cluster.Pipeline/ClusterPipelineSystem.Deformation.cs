using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Resources;
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
        GraphPassId reset = graph.AddCopyFramePass("Reset Cluster deformation capacity", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new ClusterTessellationResetParameters(frame.DeformAllocation), new PassOptions(RepeatCommands: true),
            static (ref PassDefinition pass, ref ClusterTessellationResetParameters data) =>
                pass.Write(data.Counters, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
            ClusterTessellationResetParameters.Record, region: _regions.PrimaryGeometry);
        graph.SetPassTimeGroup(reset, DeformationTimeGroup);
    }

    private void RecordDeformation(ref RenderGraphEdit graph, ClusterRasterInvocation material,
        DeformDrawData draw, GraphRegionId region)
    {
        ClusterDeformation deformation = material.Deformation!;
        GraphBufferSlice uniforms = UploadUniform(ref graph,
            new DeformUniforms(_options.MaxDeformCacheBytes, _options.MaxCandidates, material.BinIndex),
            "Cluster user deformation parameters");
        _rasterInputs?.Uniforms.Add(uniforms);
        for (int index = 0; index < deformation.Stages.Length; index++)
        {
            ClusterDeformStage stage = deformation.Stages[index];
            string name = $"{ClusterDeformation.Entries[index]} bin {material.BinIndex}";
            GraphPassId passId;
            if (index is 0 or 3)
                passId = AddComputePass(ref graph, name, stage.Pipeline,
                    new ClusterDispatchParameters(new DispatchArguments(1, 1, 1)), 0,
                    ClusterDispatchParameters.Record, region: region);
            else
            {
                var dispatch = new ClusterIndirectDispatchParameters(RequireDispatchIndirectLayout(),
                    GraphData.Resources.DeformArguments, index == 5 ? 12u : 0u);
                passId = AddComputePass(ref graph, name, stage.Pipeline, dispatch, 0,
                    static (ref PassDefinition pass, ref ClusterIndirectDispatchParameters data) =>
                        pass.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                            PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
                    ClusterIndirectDispatchParameters.Record, region: region);
            }
            PassDefinition pass = graph.GetPass(passId);
            BindDeformResources(ref graph, ref pass, stage, material.Material, uniforms, draw, index);
            graph.SetPassTimeGroup(passId, index == 5 ? DeformationEvaluationTimeGroup : DeformationTimeGroup);
        }
    }

    private void BindDeformResources(ref RenderGraphEdit graph, ref PassDefinition pass,
        ClusterDeformStage stage, Material material, GraphBufferSlice uniforms, DeformDrawData draw, int operation)
    {
        ReadOnlySpan<ClusterLinkedStageResource> resources = stage.Resources.Resources.Resources;
        FrameResources frame = GraphData.Resources;
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
                    pass.Bind(graph.CreateBufferSrv(GraphData.ShadeScalars), PipelineSync.ComputeShading); continue;
                case "MaterialScalarOffsets":
                    pass.Bind(graph.CreateBufferSrv(GraphData.ShadeScalarOffsets, structureStride: 4), PipelineSync.ComputeShading); continue;
                case "PageHeap":
                    pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(graph.CreateBufferSrv(frame.PageHeap)));
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
                            stage.Resources.MaterialBindings[index], material, PipelineSync.ComputeShading);
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
            graph.CreateBufferSrv(regions ? GraphData.Resources.DeformRegions : GraphData.Resources.DeformCache,
                structureStride: regions ? 8u : 0u), sync, BufferContentScope.CountBoundedRanges));
        return true;
    }
}
