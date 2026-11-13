using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render;

public readonly record struct ScreenBinningResources(GraphRegionId Region, GraphBufferId Parameters,
    GraphBufferId Cells, GraphBufferId Indices, GraphBufferId Diagnostics, GraphPassId CompletePass,
    GraphReadbackId DiagnosticsReadback, GraphBufferCbvId ParametersView, GraphBufferId[] Buffers,
    GraphBufferUavId[] Views, GraphPassId CountPass, GraphPassId ExpandPass,
    ScreenBinningWorkViews WorkViews, ScreenBinningScanResources ItemScan, ScreenBinningScanResources CellScan);

public readonly record struct ScreenBinningWorkViews(GraphBufferUavId ItemOffsets, GraphBufferUavId CellCounts,
    GraphBufferUavId CellOffsets, GraphBufferUavId Cells, GraphBufferUavId Indices,
    GraphBufferUavId Diagnostics, GraphBufferUavId Dispatch);

/// <summary>Registers rectangle expansion and ordered cell lists in the caller's persistent graph edit.</summary>
public static class ScreenBinningPasses
{
    private const BufferUsages WorkUsages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite;

    public static ScreenBinningResources Add(ref RenderGraphEdit edit, RenderPipelines pipelines,
        Shader sharedShader, Shader itemShader, IndirectCommandLayout dispatchLayout,
        uint itemCapacity, uint cellCount, uint listCapacity,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolveResource,
        scoped ReadOnlySpan<GraphBufferSrvId> inputViews,
        IGraphReadbackReceiver diagnosticsReceiver, GraphRegionId region = default) =>
        AddCore(ref edit, pipelines, sharedShader, itemShader, dispatchLayout, itemCapacity, cellCount,
            listCapacity, resolveResource, inputViews, diagnosticsReceiver, region, default, default, default);

    public static ScreenBinningResources Rebuild(ref RenderGraphEdit edit, in ScreenBinningResources resources,
        RenderPipelines pipelines, Shader sharedShader, Shader itemShader, IndirectCommandLayout dispatchLayout,
        uint itemCapacity, uint cellCount, uint listCapacity,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolveResource,
        scoped ReadOnlySpan<GraphBufferSrvId> inputViews, IGraphReadbackReceiver diagnosticsReceiver,
        GraphRegionId region = default)
    {
        edit.Remove(resources.Region);
        foreach (GraphBufferUavId view in resources.Views) edit.Remove(view);
        edit.Remove(resources.ParametersView);
        foreach (GraphBufferId buffer in resources.Buffers)
            if (buffer != resources.Cells && buffer != resources.Indices && buffer != resources.Diagnostics)
                edit.Remove(buffer);
        return AddCore(ref edit, pipelines, sharedShader, itemShader, dispatchLayout, itemCapacity, cellCount,
            listCapacity, resolveResource, inputViews, diagnosticsReceiver, region,
            resources.Cells, resources.Indices, resources.Diagnostics);
    }

    private static ScreenBinningResources AddCore(ref RenderGraphEdit edit, RenderPipelines pipelines,
        Shader sharedShader, Shader itemShader, IndirectCommandLayout dispatchLayout,
        uint itemCapacity, uint cellCount, uint listCapacity,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolveResource,
        scoped ReadOnlySpan<GraphBufferSrvId> inputViews, IGraphReadbackReceiver diagnosticsReceiver,
        GraphRegionId region, GraphBufferId cellOutput, GraphBufferId indexOutput, GraphBufferId diagnosticsOutput)
    {
        GraphRegionId binningRegion = edit.AddRegion("Screen binning", region);
        var buffers = new List<GraphBufferId>();
        var views = new Dictionary<string, GraphBufferUavId>();
        var uavs = new List<GraphBufferUavId>();
        uint alignment = Math.Max(1u, pipelines.Device.Capabilities.Limits.ConstantBufferAlignment);
        uint parameterSize = (ScreenBinningParameters.SizeInBytes + alignment - 1u) / alignment * alignment;
        GraphBufferId uniforms = edit.CreatePersistentBuffer(new BufferDesc(parameterSize,
            BufferUsages.Constant, "Screen binning parameters"), MemoryType.Upload);
        buffers.Add(uniforms);
        GraphBufferId itemOffsets = Create(ref edit, "ScreenBinningItemOffsets", itemCapacity + 1u, buffers, views, uavs);
        GraphBufferId cellCounts = Create(ref edit, "ScreenBinningCellCounts", cellCount, buffers, views, uavs);
        GraphBufferId cellOffsets = Create(ref edit, "ScreenBinningCellOffsets", cellCount + 1u, buffers, views, uavs);
        GraphBufferId cells = Create(ref edit, "ScreenBinningCells", cellCount, buffers, views, uavs, 8, BufferUsages.CopySource, cellOutput);
        GraphBufferId indices = Create(ref edit, "ScreenBinningIndices", listCapacity, buffers, views, uavs,
            extraUsages: BufferUsages.CopySource, existing: indexOutput);
        GraphBufferId diagnostics = Create(ref edit, "ScreenBinningDiagnostics", 5, buffers, views, uavs,
            extraUsages: BufferUsages.CopySource, existing: diagnosticsOutput);
        GraphBufferId dispatch = Create(ref edit, "ScreenBinningDispatch", 3, buffers, views, uavs, extraUsages: BufferUsages.Indirect);
        GraphBufferCbvId parameters = edit.CreateBufferCbv(uniforms);
        GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
        {
            "ScreenBinningUniforms" => GraphParameterResourceBinding.ConstantBuffer(parameters, PipelineSync.ComputeShading),
            "ScreenBinningScanValues" => GraphParameterResourceBinding.WritableBufferDescriptor(views["ScreenBinningItemOffsets"]),
            "ScreenBinningScanSums" => GraphParameterResourceBinding.WritableBufferDescriptor(views["ScreenBinningCellOffsets"]),
            _ => views.TryGetValue(name, out GraphBufferUavId view)
                ? GraphParameterResourceBinding.WritableBufferDescriptor(view) : resolveResource(name, type),
        };
        GraphPassId count = AddPass(ref edit, pipelines, itemShader, "CountScreenItems", binningRegion, Resolve, parameters,
            new(Dispatch(Math.Max(itemCapacity + 1u, cellCount)), default),
            [new(itemOffsets, GraphAccessMode.Write), new(cellCounts, GraphAccessMode.Write),
                new(indices, GraphAccessMode.Write), new(diagnostics, GraphAccessMode.Write)], inputViews: inputViews);
        ScreenBinningScanResources itemScan = ScreenBinningScanPasses.Add(ref edit, pipelines, sharedShader,
            itemOffsets, itemCapacity + 1u, count, binningRegion, Resolve);
        buffers.AddRange(itemScan.Buffers);
        uavs.AddRange(itemScan.Views);
        GraphPassId previous = AddPass(ref edit, pipelines, sharedShader, "ScreenBinningFinishItems", binningRegion, Resolve, parameters,
            new(new(1, 1, 1), default),
            [new(itemOffsets, GraphAccessMode.Read), new(dispatch, GraphAccessMode.Write),
                new(diagnostics, GraphAccessMode.ReadWrite)], itemScan.CompletePass);
        GraphPassId expand = AddPass(ref edit, pipelines, itemShader, "ExpandScreenItems", binningRegion, Resolve, parameters,
            new(default, dispatch, dispatchLayout),
            [new(itemOffsets, GraphAccessMode.Read), new(cellCounts, GraphAccessMode.ReadWrite)], previous, inputViews: inputViews);
        previous = AddPass(ref edit, pipelines, sharedShader, "ScreenBinningScanCells", binningRegion, Resolve, parameters,
            new(Dispatch(cellCount + 1u), default),
            [new(cellCounts, GraphAccessMode.Read), new(cellOffsets, GraphAccessMode.Write)], expand);
        ScreenBinningScanResources cellScan = ScreenBinningScanPasses.Add(ref edit, pipelines, sharedShader,
            cellOffsets, cellCount + 1u, previous, binningRegion, Resolve);
        buffers.AddRange(cellScan.Buffers);
        uavs.AddRange(cellScan.Views);
        previous = AddPass(ref edit, pipelines, sharedShader, "ScreenBinningFinishCells", binningRegion, Resolve, parameters,
            new(Dispatch(cellCount), default),
            [new(cellOffsets, GraphAccessMode.Read), new(cellCounts, GraphAccessMode.Read), new(cells, GraphAccessMode.Write),
                new(indices, GraphAccessMode.Read), new(diagnostics, GraphAccessMode.ReadWrite)], cellScan.CompletePass);
        GraphPassId complete = AddPass(ref edit, pipelines, itemShader, "FillScreenItems", binningRegion, Resolve, parameters,
            new(default, dispatch, dispatchLayout),
            [new(itemOffsets, GraphAccessMode.Read), new(cells, GraphAccessMode.Read), new(indices, GraphAccessMode.ReadWrite)], previous,
            cellCounts, inputViews);
        GraphReadbackId readback = edit.AddReadback(diagnostics, new(0, 20), diagnosticsReceiver,
            "Screen binning overflow feedback", binningRegion);
        edit.OrderAfter(readback, complete);
        var workViews = new ScreenBinningWorkViews(views["ScreenBinningItemOffsets"], views["ScreenBinningCellCounts"],
            views["ScreenBinningCellOffsets"], views["ScreenBinningCells"], views["ScreenBinningIndices"],
            views["ScreenBinningDiagnostics"], views["ScreenBinningDispatch"]);
        return new(binningRegion, uniforms, cells, indices, diagnostics, complete, readback, parameters,
            buffers.ToArray(), uavs.ToArray(), count, expand, workViews, itemScan, cellScan);
    }

    /// <summary>Replaces shader layouts and bindings while keeping the registered algorithm's resources and pass identities.</summary>
    public static ScreenBinningResources ReplaceShader(ref RenderGraphEdit edit, in ScreenBinningResources resources,
        RenderPipelines pipelines, Shader previous, Shader next,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?>? resolveResource = null)
    {
        ScreenBinningResources values = resources;
        foreach (GraphPassId id in edit.GetPasses(resources.Region))
        {
            if (IsScanPass(resources.ItemScan, id) || IsScanPass(resources.CellScan, id)) continue;
            PassDefinition pass = edit.GetPass(id);
            if (pass.Pipeline is not { } pipeline || pipelines.CreateComputeWithShader(pipeline, previous, next) is not { } replacement)
                continue;
            GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
            {
                "ScreenBinningUniforms" => GraphParameterResourceBinding.ConstantBuffer(values.ParametersView, PipelineSync.ComputeShading),
                "ScreenBinningItemOffsets" or "ScreenBinningScanValues" => GraphParameterResourceBinding.WritableBufferDescriptor(values.WorkViews.ItemOffsets),
                "ScreenBinningCellCounts" => GraphParameterResourceBinding.WritableBufferDescriptor(values.WorkViews.CellCounts),
                "ScreenBinningCellOffsets" or "ScreenBinningScanSums" => GraphParameterResourceBinding.WritableBufferDescriptor(values.WorkViews.CellOffsets),
                "ScreenBinningCells" => GraphParameterResourceBinding.WritableBufferDescriptor(values.WorkViews.Cells),
                "ScreenBinningIndices" => GraphParameterResourceBinding.WritableBufferDescriptor(values.WorkViews.Indices),
                "ScreenBinningDiagnostics" => GraphParameterResourceBinding.WritableBufferDescriptor(values.WorkViews.Diagnostics),
                "ScreenBinningDispatch" => GraphParameterResourceBinding.WritableBufferDescriptor(values.WorkViews.Dispatch),
                _ => resolveResource?.Invoke(name, type),
            };
            pass.SetPipeline(replacement.Pipeline);
            pass.SetParameterBlock(replacement.ParameterLayout);
            edit.SetPassParameterBindings(id, ShaderEntryBindings.Resolve(next, replacement, Resolve));
        }
        ScreenBinningScanPasses.ReplaceShader(ref edit, resources.ItemScan, pipelines, previous, next, resolveResource);
        ScreenBinningScanPasses.ReplaceShader(ref edit, resources.CellScan, pipelines, previous, next, resolveResource);
        return resources;
    }

    private static bool IsScanPass(in ScreenBinningScanResources resources, GraphPassId pass) =>
        resources.Steps.Any(step => step.ScanPass == pass || step.AddPrefixesPass == pass);

    public static void SetFrameParameters(ref RenderGraphFrame frame, in ScreenBinningResources resources,
        uint itemCount, uint itemCapacity, uint cellCount, uint maxItemsPerCell = uint.MaxValue)
    {
        DispatchArguments dispatch = Dispatch(Math.Max(itemCapacity + 1u, cellCount));
        var parameters = new ScreenBinningParameters(itemCount, itemCapacity, cellCount, dispatch.X * dispatch.Y * 256u, maxItemsPerCell);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(resources.Parameters), in parameters);
        frame.SetReadbackTag(resources.DiagnosticsReadback, frame.FrameIdentity);
    }

    public static void SetListCapacity(ref RenderGraphEdit edit, in ScreenBinningResources resources, uint listCapacity) =>
        edit.SetBufferDescription(resources.Indices, new BufferDesc((ulong)listCapacity * sizeof(uint),
            WorkUsages | BufferUsages.CopySource, "ScreenBinningIndices"));

    public static void Remove(ref RenderGraphEdit edit, in ScreenBinningResources resources)
    {
        edit.Remove(resources.Region);
        foreach (GraphBufferUavId view in resources.Views) edit.Remove(view);
        edit.Remove(resources.ParametersView);
        foreach (GraphBufferId buffer in resources.Buffers) edit.Remove(buffer);
    }

    private static GraphBufferId Create(ref RenderGraphEdit edit, string name, uint count,
        List<GraphBufferId> buffers, Dictionary<string, GraphBufferUavId> views, List<GraphBufferUavId> uavs,
        uint stride = 4, BufferUsages extraUsages = BufferUsages.None, GraphBufferId existing = default)
    {
        var description = new BufferDesc(Math.Max((ulong)count * stride, stride), WorkUsages | extraUsages, name);
        GraphBufferId buffer = existing;
        if (buffer == default) buffer = edit.CreatePersistentBuffer(description);
        else edit.SetBufferDescription(buffer, description);
        buffers.Add(buffer);
        GraphBufferUavId view = edit.CreateBufferUav(buffer, structureStride: stride);
        views.Add(name, view);
        uavs.Add(view);
        return buffer;
    }

    private static GraphPassId AddPass(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, GraphRegionId region, Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve,
        GraphBufferCbvId parameters, DispatchData data, Access[] accesses, GraphPassId previous = default,
        GraphBufferId fillCounts = default, scoped ReadOnlySpan<GraphBufferSrvId> inputViews = default)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, "Screen binning " + entry);
        GraphParameterResourceBinding[] resources = ShaderEntryBindings.Resolve(shader, pipeline, resolve);
        GraphPassId passId = edit.AddComputeFramePass("Screen binning " + entry, PassQueueSelection.AnyCompatible(), data,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchData _) => { }, DispatchData.Record, region);
        PassDefinition pass = edit.GetPass(passId);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        edit.SetPassParameterBindings(passId, resources);
        pass.Read(parameters, PipelineSync.ComputeShading);
        foreach (GraphBufferSrvId view in inputViews)
            pass.Read(view, PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges);
        foreach (Access access in accesses)
        {
            if (access.Mode == GraphAccessMode.Read)
                pass.Read(access.Buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            else if (access.Mode == GraphAccessMode.Write)
                pass.Write(access.Buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
            else
                pass.ReadWrite(access.Buffer, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        }
        if (fillCounts != default)
            pass.ReadWrite(fillCounts, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        if (data.Indirect != default) pass.Read(data.Indirect, new(0, 12), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
        if (previous != default) edit.OrderAfter(passId, previous);
        return passId;
    }

    private static DispatchArguments Dispatch(uint count)
    {
        uint groups = (count + 255u) / 256u;
        return new(Math.Min(groups, 65535u), Math.Max((groups + 65534u) / 65535u, 1u), 1u);
    }

    private readonly record struct Access(GraphBufferId Buffer, GraphAccessMode Mode);
    private readonly record struct DispatchData(DispatchArguments Arguments, GraphBufferId Indirect, IndirectCommandLayout? Layout = null)
    {
        internal static void Record(ref ComputePassCommandScope commands, in DispatchData data)
        {
            if (data.Indirect == default) commands.Dispatch(data.Arguments);
            else commands.ExecuteIndirect(data.Layout!, new BufferRegion(commands.GetBuffer(data.Indirect), new(0, 12)), 1);
        }
    }
}
