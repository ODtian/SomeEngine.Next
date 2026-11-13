using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Layers;

public readonly record struct LayerTileBinningResources(
    GraphRegionId Region,
    ScreenBinningResources Binning,
    GraphBufferId Parameters,
    GraphBufferCbvId ParametersView,
    GraphBufferId StageClusters,
    GraphBufferId StageTileRanges,
    GraphBufferId StageWorks,
    GraphBufferId StageWorkRanges,
    GraphBufferId StageDispatch,
    GraphBufferUavId StageClustersView,
    GraphBufferUavId StageWorksView,
    GraphBufferUavId StageWorkRangesView,
    GraphPassId CompletePass,
    ScreenBinningScanResources MembershipScan,
    ScreenBinningScanResources WorkScan,
    GraphBufferId[] Buffers,
    GraphBufferUavId[] Views)
{
    public GraphBufferSrvId[] InputViews { get; init; } = [];
    public GraphBufferId Membership { get; init; }
    public GraphPassId MarkPass { get; init; }
    public GraphPassId RangesPass { get; init; }
    public GraphPassId FillPass { get; init; }
    public uint RasterStageCount { get; init; }
}

/// <summary>Registers tile lists from culling output and groups them by the active geometry stages.</summary>
public static class LayerTileBinningPasses
{
    private const BufferUsages WorkUsages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource;

    public static LayerTileBinningResources Add(ref RenderGraphEdit edit, RenderPipelines pipelines,
        Shader binningShader, Shader tileShader, Shader geometryShader, IndirectCommandLayout dispatchLayout,
        uint itemCapacity, uint tileCount, uint rasterStageCount, uint listCapacity,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolveGeometry,
        scoped ReadOnlySpan<GraphBufferSrvId> inputViews,
        IGraphReadbackReceiver diagnosticsReceiver, GraphRegionId region = default)
    {
        GraphRegionId tileRegion = edit.AddRegion("Layer tile binning", region);
        var buffers = new List<GraphBufferId>();
        var views = new List<GraphBufferUavId>();
        uint alignment = pipelines.Device.Capabilities.Limits.ConstantBufferAlignment;
        uint size = (16u + alignment - 1u) / alignment * alignment;
        GraphBufferId parameters = edit.CreatePersistentBuffer(new BufferDesc(size, BufferUsages.Constant,
            "Layer tile binning parameters"), MemoryType.Upload);
        buffers.Add(parameters);
        GraphBufferCbvId parametersView = edit.CreateBufferCbv(parameters);
        ScreenBinningResources binning = ScreenBinningPasses.Add(ref edit, pipelines, binningShader,
            geometryShader, dispatchLayout, itemCapacity, tileCount, listCapacity,
            (name, type) => name == "LayerTileBinning"
                ? GraphParameterResourceBinding.ConstantBuffer(parametersView, PipelineSync.ComputeShading)
                : resolveGeometry(name, type), inputViews, diagnosticsReceiver, tileRegion);
        foreach (GraphPassId passId in new[] { binning.CountPass, binning.ExpandPass, binning.CompletePass })
            edit.GetPass(passId).Read(parametersView, PipelineSync.ComputeShading);
        uint entries = rasterStageCount * listCapacity;
        uint cells = rasterStageCount * tileCount;
        GraphBufferId membership = Create(ref edit, "Layer stage membership", entries + 1u, 4, buffers, views,
            out GraphBufferUavId membershipView);
        GraphBufferId stageClusters = Create(ref edit, "Layer stage clusters", entries, 4, buffers, views,
            out GraphBufferUavId stageClustersView);
        GraphBufferId stageRanges = Create(ref edit, "Layer stage tile ranges", cells, 8, buffers, views,
            out GraphBufferUavId stageRangesView);
        GraphBufferId workOffsets = Create(ref edit, "Layer stage work offsets", cells + 1u, 4, buffers, views,
            out GraphBufferUavId workOffsetsView);
        GraphBufferId stageWorks = Create(ref edit, "Layer stage work", cells, 16, buffers, views,
            out GraphBufferUavId stageWorksView);
        GraphBufferId workRanges = Create(ref edit, "Layer stage work ranges", rasterStageCount, 8, buffers, views,
            out GraphBufferUavId workRangesView);
        GraphBufferId stageDispatch = Create(ref edit, "Layer stage dispatch", rasterStageCount, 12, buffers, views,
            out GraphBufferUavId stageDispatchView, BufferUsages.Indirect);
        GraphBufferUavId tileRangesView = edit.CreateBufferUav(binning.Cells, structureStride: 8);
        GraphBufferUavId indicesView = edit.CreateBufferUav(binning.Indices, structureStride: 4);
        views.Add(tileRangesView);
        views.Add(indicesView);
        GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
        {
            "ScreenBinningCells" => GraphParameterResourceBinding.WritableBufferDescriptor(tileRangesView),
            "ScreenBinningIndices" => GraphParameterResourceBinding.WritableBufferDescriptor(indicesView),
            "LayerStageMembership" => GraphParameterResourceBinding.WritableBufferDescriptor(membershipView),
            "LayerStageClusters" => GraphParameterResourceBinding.WritableBufferDescriptor(stageClustersView),
            "LayerStageTileRanges" => GraphParameterResourceBinding.WritableBufferDescriptor(stageRangesView),
            "LayerStageWorkOffsets" => GraphParameterResourceBinding.WritableBufferDescriptor(workOffsetsView),
            "LayerStageWorks" => GraphParameterResourceBinding.WritableBufferDescriptor(stageWorksView),
            "LayerStageWorkRanges" => GraphParameterResourceBinding.WritableBufferDescriptor(workRangesView),
            "LayerStageDispatch" => GraphParameterResourceBinding.WritableBufferDescriptor(stageDispatchView),
            _ => resolveGeometry(name, type),
        };
        GraphPassId mark = AddPass(ref edit, pipelines, geometryShader, "LayerMarkRasterStages", parametersView,
            tileRegion, Resolve, entries + 1u,
            [new(binning.Indices, GraphAccessMode.Read), new(membership, GraphAccessMode.Write),
                new(stageClusters, GraphAccessMode.Write)], binning.CompletePass);
        foreach (GraphBufferSrvId view in inputViews)
            edit.GetPass(mark).Read(view, PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges);
        ScreenBinningScanResources membershipScan = ScreenBinningScanPasses.Add(ref edit, pipelines, binningShader,
            membership, entries + 1u, mark, tileRegion);
        GraphPassId ranges = AddPass(ref edit, pipelines, tileShader, "LayerBuildStageTileRanges", parametersView,
            tileRegion, Resolve, cells + 1u,
            [new(binning.Cells, GraphAccessMode.Read), new(membership, GraphAccessMode.Read),
                new(stageRanges, GraphAccessMode.Write), new(workOffsets, GraphAccessMode.Write),
                new(stageWorks, GraphAccessMode.Write)], membershipScan.CompletePass);
        GraphPassId fill = AddPass(ref edit, pipelines, tileShader, "LayerFillStageClusters", parametersView,
            tileRegion, Resolve, entries,
            [new(membership, GraphAccessMode.Read), new(binning.Indices, GraphAccessMode.Read),
                new(stageClusters, GraphAccessMode.ReadWrite)], ranges);
        ScreenBinningScanResources workScan = ScreenBinningScanPasses.Add(ref edit, pipelines, binningShader,
            workOffsets, cells + 1u, fill, tileRegion);
        GraphPassId complete = AddPass(ref edit, pipelines, tileShader, "LayerBuildStageWork", parametersView,
            tileRegion, Resolve, Math.Max(cells, rasterStageCount),
            [new(workOffsets, GraphAccessMode.Read), new(stageRanges, GraphAccessMode.Read),
                new(stageWorks, GraphAccessMode.ReadWrite), new(workRanges, GraphAccessMode.Write),
                new(stageDispatch, GraphAccessMode.Write)], workScan.CompletePass);
        return new(tileRegion, binning, parameters, parametersView, stageClusters, stageRanges, stageWorks,
            workRanges, stageDispatch, stageClustersView, stageWorksView, workRangesView, complete,
            membershipScan, workScan, buffers.ToArray(), views.ToArray())
        {
            Membership = membership,
            MarkPass = mark,
            RangesPass = ranges,
            FillPass = fill,
            RasterStageCount = rasterStageCount,
        };
    }

    public static void SetFrameParameters(ref RenderGraphFrame frame, in LayerTileBinningResources resources,
        uint itemCount, uint itemCapacity, uint tileCount, uint rasterStageCount, uint listCapacity)
    {
        ScreenBinningPasses.SetFrameParameters(ref frame, resources.Binning, itemCount, itemCapacity, tileCount);
        var parameters = new Parameters(tileCount, rasterStageCount, listCapacity);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(resources.Parameters), in parameters);
        uint entries = rasterStageCount * listCapacity;
        frame.SetPassData(resources.MarkPass, FlatDispatch(entries + 1u));
        frame.SetPassData(resources.FillPass, FlatDispatch(entries));
    }

    public static LayerTileBinningResources ResizeLists(ref RenderGraphEdit edit,
        in LayerTileBinningResources resources, RenderPipelines pipelines, Shader binningShader, uint listCapacity)
    {
        uint entries = checked(resources.RasterStageCount * listCapacity);
        ScreenBinningPasses.SetListCapacity(ref edit, resources.Binning, listCapacity);
        edit.SetBufferDescription(resources.Membership, new((entries + 1ul) * 4u, WorkUsages, "Layer stage membership"));
        edit.SetBufferDescription(resources.StageClusters, new((ulong)entries * 4u, WorkUsages, "Layer stage clusters"));
        ScreenBinningScanPasses.Remove(ref edit, resources.MembershipScan);
        ScreenBinningScanResources scan = ScreenBinningScanPasses.Add(ref edit, pipelines, binningShader,
            resources.Membership, entries + 1u, resources.MarkPass, resources.Region);
        edit.OrderAfter(resources.RangesPass, scan.CompletePass);
        return resources with { MembershipScan = scan };
    }

    private static DispatchArguments FlatDispatch(uint count)
    {
        uint groups = (count + 255u) / 256u;
        return new(Math.Min(groups, 65535u), Math.Max((groups + 65534u) / 65535u, 1u), 1u);
    }

    public static void Remove(ref RenderGraphEdit edit, in LayerTileBinningResources resources)
    {
        foreach (GraphBufferSrvId view in resources.InputViews) edit.Remove(view);
        foreach (GraphBufferUavId view in resources.Views) edit.Remove(view);
        edit.Remove(resources.ParametersView);
        ScreenBinningPasses.Remove(ref edit, resources.Binning);
        ScreenBinningScanPasses.Remove(ref edit, resources.MembershipScan);
        ScreenBinningScanPasses.Remove(ref edit, resources.WorkScan);
        edit.Remove(resources.Region);
        foreach (GraphBufferId buffer in resources.Buffers) edit.Remove(buffer);
    }

    private static GraphBufferId Create(ref RenderGraphEdit edit, string label, uint count, uint stride,
        List<GraphBufferId> buffers, List<GraphBufferUavId> views, out GraphBufferUavId view,
        BufferUsages extraUsages = BufferUsages.None)
    {
        GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc((ulong)count * stride, WorkUsages | extraUsages, label));
        view = edit.CreateBufferUav(buffer, structureStride: stride);
        buffers.Add(buffer);
        views.Add(view);
        return buffer;
    }

    private static GraphPassId AddPass(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, GraphBufferCbvId parameters, GraphRegionId region,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve, uint count,
        Access[] accesses, GraphPassId previous)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, entry);
        GraphParameterResourceBinding[] bindings = ShaderEntryBindings.Resolve(shader, pipeline,
            (name, type) => name == "LayerTileBinning"
                ? GraphParameterResourceBinding.ConstantBuffer(parameters, PipelineSync.ComputeShading)
                : resolve(name, type));
        uint groups = (count + 255u) / 256u;
        var dispatch = new DispatchArguments(Math.Min(groups, 65535u), Math.Max((groups + 65534u) / 65535u, 1u), 1u);
        GraphPassId id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), dispatch,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data), region);
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in bindings) pass.Bind(binding);
        foreach (Access access in accesses)
        {
            if (access.Mode == GraphAccessMode.Read)
                pass.Read(access.Buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            else if (access.Mode == GraphAccessMode.Write)
                pass.Write(access.Buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess,
                    WriteCoverage.Complete);
            else
                pass.ReadWrite(access.Buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        }
        edit.OrderAfter(id, previous);
        return id;
    }

    private readonly record struct Access(GraphBufferId Buffer, GraphAccessMode Mode);
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct Parameters(uint TileCount, uint RasterStageCount, uint ListCapacity);
}
