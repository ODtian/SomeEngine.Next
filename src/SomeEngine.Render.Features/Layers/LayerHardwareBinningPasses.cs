using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Layers;

public readonly record struct LayerHardwareBinningResources(GraphRegionId Region,
    GraphBufferId Parameters, GraphBufferCbvId ParametersView, GraphBufferId Membership,
    GraphBufferId Clusters, GraphBufferId Ranges, GraphBufferId DrawArguments, GraphBufferId DispatchArguments,
    GraphBufferUavId ClustersView, GraphBufferUavId RangesView,
    ScreenBinningScanResources Scan, GraphPassId CompletePass,
    GraphBufferId[] Buffers, GraphBufferUavId[] Views)
{
    public GraphPassId CountPass { get; init; }
}

/// <summary>Registers each large-window visible cluster once in each geometry stage that consumes it.</summary>
public static class LayerHardwareBinningPasses
{
    public static LayerHardwareBinningResources Add(ref RenderGraphEdit edit, RenderPipelines pipelines,
        Shader sharedShader, Shader geometryShader, uint itemCapacity, uint rasterStageCount,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolveGeometry,
        GraphPassId geometryComplete = default, GraphRegionId region = default)
    {
        GraphRegionId hardware = edit.AddRegion("Layer hardware cluster lists", region);
        uint alignment = pipelines.Device.Capabilities.Limits.ConstantBufferAlignment;
        GraphBufferId parameters = edit.CreatePersistentBuffer(new((16u + alignment - 1u) / alignment * alignment,
            BufferUsages.Constant, "Layer hardware cluster parameters"), MemoryType.Upload);
        GraphBufferCbvId parametersView = edit.CreateBufferCbv(parameters);
        var buffers = new List<GraphBufferId> { parameters };
        var views = new List<GraphBufferUavId>();
        uint count = itemCapacity * rasterStageCount;
        GraphBufferId membership = Create(ref edit, "Layer hardware stage membership", count + 1u, 4, buffers, views,
            out GraphBufferUavId membershipView);
        GraphBufferId clusters = Create(ref edit, "Layer hardware stage clusters", count, 4, buffers, views,
            out GraphBufferUavId clustersView);
        GraphBufferId ranges = Create(ref edit, "Layer hardware stage ranges", rasterStageCount, 8, buffers, views,
            out GraphBufferUavId rangesView);
        GraphBufferId draws = Create(ref edit, "Layer hardware stage draws", rasterStageCount, 16, buffers, views,
            out GraphBufferUavId drawsView, BufferUsages.Indirect);
        GraphBufferId dispatch = Create(ref edit, "Layer hardware stage dispatch", rasterStageCount, 12, buffers, views,
            out GraphBufferUavId dispatchView, BufferUsages.Indirect);
        GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
        {
            "LayerHardwareBinning" => GraphParameterResourceBinding.ConstantBuffer(parametersView, PipelineSync.ComputeShading),
            "LayerHardwareMembership" => GraphParameterResourceBinding.WritableBufferDescriptor(membershipView),
            "LayerHardwareClusters" => GraphParameterResourceBinding.WritableBufferDescriptor(clustersView),
            "LayerHardwareRanges" => GraphParameterResourceBinding.WritableBufferDescriptor(rangesView),
            "LayerHardwareDrawArguments" => GraphParameterResourceBinding.WritableBufferDescriptor(drawsView),
            "LayerHardwareDispatchArguments" => GraphParameterResourceBinding.WritableBufferDescriptor(dispatchView),
            _ => resolveGeometry(name, type),
        };
        GraphPassId countPass = AddPass(ref edit, pipelines, geometryShader, "LayerCountHardwareClusters", hardware,
            count + 1u, Resolve);
        PassDefinition counter = edit.GetPass(countPass);
        counter.Write(membership, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        counter.Write(clusters, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        if (geometryComplete != default) edit.OrderAfter(countPass, geometryComplete);
        ScreenBinningScanResources scan = ScreenBinningScanPasses.Add(ref edit, pipelines, sharedShader,
            membership, count + 1u, countPass, hardware);
        GraphPassId complete = AddPass(ref edit, pipelines, geometryShader, "LayerFillHardwareClusters", hardware,
            Math.Max(count, rasterStageCount), Resolve);
        PassDefinition filler = edit.GetPass(complete);
        filler.Read(membership, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        filler.ReadWrite(clusters, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        filler.Write(ranges, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        filler.Write(draws, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        filler.Write(dispatch, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        edit.OrderAfter(complete, scan.CompletePass);
        return new(hardware, parameters, parametersView, membership, clusters, ranges, draws, dispatch,
            clustersView, rangesView, scan, complete, buffers.ToArray(), views.ToArray()) { CountPass = countPass };
    }

    public static void Record(ref RenderGraphFrame frame, in LayerHardwareBinningResources resources,
        uint itemCapacity, uint rasterStageCount)
    {
        var parameters = new Parameters(itemCapacity, rasterStageCount);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(resources.Parameters), in parameters);
    }

    public static void Remove(ref RenderGraphEdit edit, in LayerHardwareBinningResources resources)
    {
        edit.Remove(resources.Region);
        RemoveResources(ref edit, resources);
    }

    internal static void RemoveResources(ref RenderGraphEdit edit, in LayerHardwareBinningResources resources)
    {
        foreach (GraphBufferUavId view in resources.Scan.Views) edit.Remove(view);
        foreach (GraphBufferId buffer in resources.Scan.Buffers) edit.Remove(buffer);
        foreach (GraphBufferUavId view in resources.Views) edit.Remove(view);
        edit.Remove(resources.ParametersView);
        foreach (GraphBufferId buffer in resources.Buffers) edit.Remove(buffer);
    }

    private static GraphBufferId Create(ref RenderGraphEdit edit, string name, uint count, uint stride,
        List<GraphBufferId> buffers, List<GraphBufferUavId> views, out GraphBufferUavId view,
        BufferUsages extra = BufferUsages.None)
    {
        GraphBufferId buffer = edit.CreatePersistentBuffer(new((ulong)count * stride,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | extra, name));
        view = edit.CreateBufferUav(buffer, structureStride: stride);
        buffers.Add(buffer); views.Add(view);
        return buffer;
    }

    private static GraphPassId AddPass(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, GraphRegionId region, uint count,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, entry);
        uint groups = (count + 255u) / 256u;
        DispatchArguments dispatch = new(Math.Min(groups, 65535u), Math.Max((groups + 65534u) / 65535u, 1u), 1u);
        GraphPassId passId = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), dispatch,
            new PassOptions(RepeatCommands: true), static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments values) => commands.Dispatch(values), region);
        PassDefinition pass = edit.GetPass(passId);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in ShaderEntryBindings.Resolve(shader, pipeline, resolve)) pass.Bind(binding);
        return passId;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct Parameters(uint ItemCapacity, uint RasterStageCount);
}
