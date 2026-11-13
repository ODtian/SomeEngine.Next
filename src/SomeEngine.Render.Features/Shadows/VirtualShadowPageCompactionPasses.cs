using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public readonly record struct VirtualShadowPageCompactionResources(GraphRegionId Region,
    GraphBufferId Parameters, GraphBufferCbvId ParametersView, GraphBufferId Prefixes,
    GraphBufferUavId PrefixesView, GraphBufferUavId PageListsView,
    ScreenBinningScanResources Scan, GraphPassId FirstPass, GraphPassId CompletePass);

/// <summary>Compacts cached pages in their existing request order and appends empty pages.</summary>
public static class VirtualShadowPageCompactionPasses
{
    public static VirtualShadowPageCompactionResources Add(ref RenderGraphEdit edit, RenderPipelines pipelines,
        Shader scanShader, Shader availablePagesShader, GraphBufferId pageLists, uint capacity,
        GraphPassId previous = default, GraphRegionId region = default)
    {
        GraphRegionId compactRegion = edit.AddRegion("Virtual shadow available pages", region);
        uint alignment = pipelines.Device.Capabilities.Limits.ConstantBufferAlignment;
        uint bytes = (16u + alignment - 1u) / alignment * alignment;
        GraphBufferId parameters = edit.CreatePersistentBuffer(new BufferDesc(bytes, BufferUsages.Constant,
            "Virtual shadow available page parameters"), MemoryType.Upload);
        GraphBufferCbvId parametersView = edit.CreateBufferCbv(parameters);
        GraphBufferId prefixes = edit.CreatePersistentBuffer(new BufferDesc((capacity + 2ul) * sizeof(uint),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Virtual shadow available page prefixes"));
        GraphBufferUavId prefixesView = edit.CreateBufferUav(prefixes, structureStride: sizeof(uint));
        GraphBufferUavId listsView = edit.CreateBufferUav(pageLists, structureStride: sizeof(uint));
        GraphParameterResourceBinding? Resolve(string name, ResourceBindingType _) => name switch
        {
            "AvailablePages" => GraphParameterResourceBinding.ConstantBuffer(parametersView, PipelineSync.ComputeShading),
            "ShadowPageLists" => GraphParameterResourceBinding.WritableBufferDescriptor(listsView),
            "ScreenBinningScanValues" => GraphParameterResourceBinding.WritableBufferDescriptor(prefixesView),
            _ => null,
        };
        GraphPassId count = AddPass(ref edit, pipelines, availablePagesShader, "CountAvailableShadowPages",
            capacity + 1u, compactRegion, Resolve);
        PassDefinition countAccess = edit.GetPass(count);
        countAccess.Read(pageLists, new(0, capacity * 4ul), PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        countAccess.Read(pageLists, new((2ul * (capacity + 1u) + capacity) * 4u, 4),
            PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        countAccess.Write(prefixes, BufferRange.Whole, PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        if (previous != default) edit.OrderAfter(count, previous);

        // The final value stores emptyCount and remains outside the scanned prefix.
        ScreenBinningScanResources scan = ScreenBinningScanPasses.Add(ref edit, pipelines, scanShader,
            prefixes, capacity + 1u, count, compactRegion);
        GraphPassId fill = AddPass(ref edit, pipelines, availablePagesShader, "FillAvailableShadowPages",
            capacity, compactRegion, Resolve);
        PassDefinition fillAccess = edit.GetPass(fill);
        fillAccess.Read(prefixes, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        fillAccess.Read(pageLists, new(0, capacity * 4ul), PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        fillAccess.Read(pageLists, new(2ul * (capacity + 1u) * 4u, capacity * 4ul), PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, contentScope: BufferContentScope.CountBoundedPrefix);
        fillAccess.Write(pageLists, new((capacity + 1ul) * 4u, capacity * 4ul), PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, WriteCoverage.Partial, ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        fillAccess.Write(pageLists, new(((capacity + 1ul) + capacity) * 4u, 4), PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        fillAccess.Write(pageLists, new((2ul * (capacity + 1u) + capacity) * 4u, 4), PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        edit.OrderAfter(fill, scan.CompletePass);
        return new(compactRegion, parameters, parametersView, prefixes, prefixesView, listsView, scan, count, fill);
    }

    public static void SetFrameParameters(ref RenderGraphFrame frame,
        in VirtualShadowPageCompactionResources resources, uint capacity)
    {
        var parameters = new Parameters(capacity);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(resources.Parameters), in parameters);
    }

    public static VirtualShadowPageCompactionResources ReplaceShader(ref RenderGraphEdit edit,
        in VirtualShadowPageCompactionResources resources, RenderPipelines pipelines, Shader previous, Shader next)
    {
        VirtualShadowPageCompactionResources values = resources;
        foreach (GraphPassId id in new[] { resources.FirstPass,resources.CompletePass })
        {
            PassDefinition pass = edit.GetPass(id);
            if (pipelines.CreateComputeWithShader(pass.Pipeline!,previous,next) is not { } replacement) continue;
            GraphParameterResourceBinding? Resolve(string name, ResourceBindingType _) => name switch
            {
                "AvailablePages" => GraphParameterResourceBinding.ConstantBuffer(values.ParametersView,PipelineSync.ComputeShading),
                "ShadowPageLists" => GraphParameterResourceBinding.WritableBufferDescriptor(values.PageListsView),
                "ScreenBinningScanValues" => GraphParameterResourceBinding.WritableBufferDescriptor(values.PrefixesView),
                _ => null,
            };
            pass.SetPipeline(replacement.Pipeline);
            pass.SetParameterBlock(replacement.ParameterLayout);
            edit.SetPassParameterBindings(id,ShaderEntryBindings.Resolve(next,replacement,Resolve));
        }
        ScreenBinningScanPasses.ReplaceShader(ref edit,resources.Scan,pipelines,previous,next);
        return resources;
    }

    public static void Remove(ref RenderGraphEdit edit, in VirtualShadowPageCompactionResources resources)
    {
        ScreenBinningScanPasses.Remove(ref edit, resources.Scan);
        edit.Remove(resources.Region);
        edit.Remove(resources.ParametersView);
        edit.Remove(resources.PrefixesView);
        edit.Remove(resources.PageListsView);
        edit.Remove(resources.Parameters);
        edit.Remove(resources.Prefixes);
    }

    private static GraphPassId AddPass(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, uint count, GraphRegionId region,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, entry);
        GraphParameterResourceBinding[] bindings = ShaderEntryBindings.Resolve(shader, pipeline, resolve);
        uint groups = (count + 255u) / 256u;
        var dispatch = new DispatchArguments(Math.Min(groups, 65535u), Math.Max(1u, (groups + 65534u) / 65535u), 1u);
        GraphPassId passId = edit.AddComputeFramePass(entry, PassQueueSelection.AnyCompatible(), dispatch,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data), region);
        PassDefinition pass = edit.GetPass(passId);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in bindings) pass.Bind(binding);
        return passId;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly record struct Parameters(uint MaxPages);
}
