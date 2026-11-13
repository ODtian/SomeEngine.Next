using System.Runtime.InteropServices;
using System.Globalization;
using System.Collections.Immutable;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Layers;

public readonly record struct LayerShadingResources(GraphRegionId Region, GraphBufferId Parameters,
    GraphBufferCbvId ParametersView, GraphBufferId BinCounts, GraphBufferId BinOffsets,
    GraphBufferId ScatterCounts, GraphBufferId ReserveCounters, GraphBufferId IndirectArguments,
    GraphBufferId PixelCoordinates, GraphBufferSrvId BinCountsSrv, GraphBufferSrvId BinOffsetsSrv,
    GraphBufferSrvId PixelCoordinatesSrv, GraphPassId ResetPass, GraphPassId CountPass,
    GraphPassId ReservePass, GraphPassId ScatterPass, GraphBufferId[] Buffers,
    GraphBufferUavId[] Uavs, GraphBufferSrvId[] Srvs);

/// <summary>Uniform and pass IDs indexed by the representative material address of each bin.</summary>
public readonly record struct LayerMaterialShadingResources(GraphRegionId Region, GraphBufferId[] Parameters,
    GraphBufferCbvId[] ParameterViews, GraphPassId[] Passes, GraphPassId CompletePass);

/// <summary>Registers material classification for physical fragment indices in the shared shade-work format.</summary>
public static class LayerShadingPasses
{
    private const BufferUsages WorkUsages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource;

    public static LayerShadingResources Add(ref RenderGraphEdit edit, RenderPipelines pipelines,
        in LayerStageResources stages, RenderGeometryResources geometry, IRenderMaterialResources materials,
        Shader resetShader, Shader countShader, Shader reserveShader, Shader scatterShader,
        uint tileCount, uint fragmentCapacity,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?>? resolveResource = null)
    {
        uint binCount = GetBinCount(materials);
        GraphRegionId region = edit.AddRegion("Layer material classification", stages.ShadingRegion);
        uint alignment = Math.Max(1u, pipelines.Device.Capabilities.Limits.ConstantBufferAlignment);
        GraphBufferId parameters = edit.CreatePersistentBuffer(new((32u + alignment - 1u) / alignment * alignment,
            BufferUsages.Constant, "Layer shade bin parameters"), MemoryType.Upload);
        GraphBufferCbvId parametersView = edit.CreateBufferCbv(parameters);
        GraphBufferId counts = edit.CreatePersistentBuffer(new((ulong)binCount * 8u, WorkUsages, "Layer shade bin counts"));
        GraphBufferId offsets = edit.CreatePersistentBuffer(new((ulong)binCount * 8u, WorkUsages, "Layer shade bin offsets"));
        GraphBufferId scatterCounts = edit.CreatePersistentBuffer(new((ulong)binCount * 8u, WorkUsages, "Layer shade scatter counts"));
        GraphBufferId reserveCounters = edit.CreatePersistentBuffer(new(4, WorkUsages, "Layer shade reserve count"));
        GraphBufferId arguments = edit.CreatePersistentBuffer(new((ulong)binCount * 12u, WorkUsages | BufferUsages.Indirect, "Layer material dispatch arguments"));
        GraphBufferId coordinates = edit.CreatePersistentBuffer(CoordinateDescription(fragmentCapacity));
        GraphBufferUavId countsUav = edit.CreateBufferUav(counts, structureStride: 4);
        GraphBufferUavId offsetsUav = edit.CreateBufferUav(offsets, structureStride: 4);
        GraphBufferUavId scatterUav = edit.CreateBufferUav(scatterCounts, structureStride: 4);
        GraphBufferUavId reserveUav = edit.CreateBufferUav(reserveCounters, structureStride: 4);
        GraphBufferUavId argumentsUav = edit.CreateBufferUav(arguments);
        GraphBufferUavId coordinatesUav = edit.CreateBufferUav(coordinates, structureStride: 8);
        GraphBufferSrvId countsSrv = edit.CreateBufferSrv(counts, structureStride: 4);
        GraphBufferSrvId offsetsSrv = edit.CreateBufferSrv(offsets, structureStride: 4);
        GraphBufferSrvId coordinatesSrv = edit.CreateBufferSrv(coordinates, structureStride: 8);
        LayerStageResources stageResources = stages;
        GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
        {
            "Uniforms" => GraphParameterResourceBinding.ConstantBuffer(parametersView, PipelineSync.ComputeShading),
            "BinCounts" => GraphParameterResourceBinding.WritableBufferDescriptor(countsUav),
            "BinOffsets" => GraphParameterResourceBinding.WritableBufferDescriptor(offsetsUav),
            "ScatterCounts" => GraphParameterResourceBinding.WritableBufferDescriptor(scatterUav),
            "ReserveCounters" => GraphParameterResourceBinding.WritableBufferDescriptor(reserveUav),
            "IndirectArgs" => GraphParameterResourceBinding.WritableBufferDescriptor(argumentsUav),
            "PixelCoordinates" => GraphParameterResourceBinding.WritableBufferDescriptor(coordinatesUav),
            _ => stageResources.ResolveShading(name, type) ?? resolveResource?.Invoke(name, type),
        };
        DispatchArguments binDispatch = FlatDispatch(binCount, 128);
        DispatchArguments fragmentDispatch = FlatDispatch(tileCount, 1);
        GraphPassId reset = AddPass(ref edit, pipelines, resetShader, "ResetShadeBins", region, binDispatch,
            Resolve, geometry, materials, parameters);
        PassDefinition resetPass = edit.GetPass(reset);
        resetPass.Write(counts, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        resetPass.Write(scatterCounts, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        resetPass.Write(reserveCounters, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        resetPass.Write(coordinates, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess,
            WriteCoverage.Partial, ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedRanges);
        if (stages.ShadingBegin != default) edit.OrderAfter(reset, stages.ShadingBegin);
        GraphPassId count = AddPass(ref edit, pipelines, countShader, "CountLayerShadeBins", region, fragmentDispatch,
            Resolve, geometry, materials, parameters);
        PassDefinition countPass = edit.GetPass(count);
        countPass.ReadWrite(counts, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        edit.OrderAfter(count, reset);
        GraphPassId reserve = AddPass(ref edit, pipelines, reserveShader, "ReserveShadeBins", region, binDispatch,
            Resolve, geometry, materials, parameters);
        PassDefinition reservePass = edit.GetPass(reserve);
        reservePass.Read(counts, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        reservePass.Write(offsets, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        reservePass.Write(arguments, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        reservePass.Write(scatterCounts, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess, WriteCoverage.Complete);
        reservePass.ReadWrite(reserveCounters, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        edit.OrderAfter(reserve, count);
        GraphPassId scatter = AddPass(ref edit, pipelines, scatterShader, "ScatterLayerShadeBins", region, fragmentDispatch,
            Resolve, geometry, materials, parameters);
        PassDefinition scatterPass = edit.GetPass(scatter);
        scatterPass.Read(offsets, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        scatterPass.ReadWrite(scatterCounts, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
        scatterPass.Write(coordinates, BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess,
            WriteCoverage.Partial, ResourceContentState.Defined, contentScope: BufferContentScope.CountBoundedRanges);
        edit.OrderAfter(scatter, reserve);
        return new(region, parameters, parametersView, counts, offsets, scatterCounts, reserveCounters, arguments, coordinates,
            countsSrv, offsetsSrv, coordinatesSrv, reset, count, reserve, scatter,
            [parameters, counts, offsets, scatterCounts, reserveCounters, arguments, coordinates],
            [countsUav, offsetsUav, scatterUav, reserveUav, argumentsUav, coordinatesUav],
            [countsSrv, offsetsSrv, coordinatesSrv]);
    }

    public static uint GetBinCount(IRenderMaterialResources materials)
    {
        uint count = 1;
        foreach (RenderMaterialEntry material in materials.Materials) count = Math.Max(count, material.BinIndex + 1u);
        return count;
    }

    public static LayerMaterialShadingResources AddMaterials(ref RenderGraphEdit edit, RenderPipelines pipelines,
        in LayerStageResources stages, in LayerShadingResources binning, RenderGeometryResources geometry,
        IRenderMaterialResources materials, IndirectCommandLayout dispatchLayout,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?>? resolveResource = null)
    {
        GraphRegionId region = edit.AddRegion("Layer material shading", stages.ShadingRegion);
        int capacity = checked((int)materials.MaterialCapacity);
        var parameters = new GraphBufferId[capacity];
        var parameterViews = new GraphBufferCbvId[capacity];
        var passes = new GraphPassId[capacity];
        var bins = new HashSet<uint>();
        LayerStageResources stageResources = stages;
        LayerShadingResources work = binning;
        uint alignment = Math.Max(1u, pipelines.Device.Capabilities.Limits.ConstantBufferAlignment);
        string[] arguments = [materials.DebugMode.ToString(CultureInfo.InvariantCulture)];
        foreach (RenderMaterialEntry material in materials.Materials)
        {
            if (!bins.Add(material.BinIndex)) continue;
            Shader shader = material.Material.Shader!.RequireSpecialization("LayerShadeMode", arguments);
            LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, "LayerShadeMode",
                "Shade layer material bin " + material.BinIndex);
            GraphBufferId values = edit.CreatePersistentBuffer(new((48u + alignment - 1u) / alignment * alignment,
                BufferUsages.Constant, "Layer material shade parameters"), MemoryType.Upload);
            GraphBufferCbvId view = edit.CreateBufferCbv(values);
            var dispatch = new MaterialDispatch(dispatchLayout, work.IndirectArguments, (ulong)material.BinIndex * 12u);
            GraphPassId passId = edit.AddComputeFramePass(pipeline.Label, PassQueueSelection.AnyCompatible(), dispatch,
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                static (ref PassDefinition pass, ref MaterialDispatch data) =>
                    pass.Read(data.Arguments, new(data.Offset, 12), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
                MaterialDispatch.Record, region);
            PassDefinition pass = edit.GetPass(passId);
            pass.SetPipeline(pipeline.Pipeline);
            pass.SetParameterBlock(pipeline.ParameterLayout);
            GraphParameterResourceBinding? Resolve(string name, ResourceBindingType type) => name switch
            {
                "Uniforms" => GraphParameterResourceBinding.ConstantBuffer(view, PipelineSync.ComputeShading),
                "PixelCoordBuffer" => GraphParameterResourceBinding.ReadOnlyBuffer(work.PixelCoordinatesSrv,
                    PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges),
                "BinCounts" => GraphParameterResourceBinding.ReadOnlyBuffer(work.BinCountsSrv, PipelineSync.ComputeShading),
                "BinOffsets" => GraphParameterResourceBinding.ReadOnlyBuffer(work.BinOffsetsSrv, PipelineSync.ComputeShading),
                "LayerFragmentPayloads" => GraphParameterResourceBinding.WritableBuffer(stageResources.Fragments.PayloadsUav,
                    PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Partial, ResourceContentState.Defined,
                    BufferContentScope.CountBoundedRanges, UnorderedAccessHazardMode.DisjointWrites),
                _ => stageResources.ResolveShading(name, type) ?? resolveResource?.Invoke(name, type),
            };
            BindResources(ref edit, ref pass, pipeline, Resolve, geometry, materials, in material,
                new(values, new(0, 48)));
            ImmutableArray<RenderMaterialTextureRead> textures = materials.Materials
                .Where(value => value.BinIndex == material.BinIndex).SelectMany(value => value.TextureReads).Distinct().ToImmutableArray();
            materials.ReadTextures(ref edit, ref pass, textures, PipelineSync.ComputeShading);
            edit.OrderAfter(passId, work.ScatterPass);
            parameters[material.Address] = values;
            parameterViews[material.Address] = view;
            passes[material.Address] = passId;
        }
        GraphPassId complete = edit.AddComputeFramePass("Layer material shading complete", PassQueueSelection.AnyCompatible(), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull), static (ref PassDefinition _, ref int _) => { },
            static (ref ComputePassCommandScope _, in int _) => { }, region);
        edit.OrderAfter(complete, work.ScatterPass);
        foreach (GraphPassId pass in passes) if (pass != default) edit.OrderAfter(complete, pass);
        return new(region, parameters, parameterViews, passes, complete);
    }

    public static void RecordMaterials(ref RenderGraphFrame frame, in LayerMaterialShadingResources resources,
        RenderGeometryResources geometry, IRenderMaterialResources materials, uint width, uint height, uint views)
    {
        uint bins = GetBinCount(materials);
        foreach (RenderMaterialEntry material in materials.Materials)
        {
            GraphBufferId buffer = resources.Parameters[material.Address];
            if (buffer == default) continue;
            var values = new ShadeParameters(materials.DebugMode, width, height, material.BinIndex, bins,
                materials.MaterialCapacity, views, 0, material.VisibilityDepth);
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(buffer), in values);
            PassDefinition pass = frame.GetPass(resources.Passes[material.Address]);
            geometry.ReadPageHeap(ref frame, ref pass, PipelineSync.ComputeShading);
        }
    }

    public static void RemoveMaterials(ref RenderGraphEdit edit, in LayerMaterialShadingResources resources)
    {
        edit.Remove(resources.Region);
        RemoveMaterialResources(ref edit, resources);
    }

    internal static void RemoveMaterialResources(ref RenderGraphEdit edit, in LayerMaterialShadingResources resources)
    {
        foreach (GraphBufferCbvId view in resources.ParameterViews) if (view != default) edit.Remove(view);
        foreach (GraphBufferId buffer in resources.Parameters) if (buffer != default) edit.Remove(buffer);
    }

    public static void Record(ref RenderGraphFrame frame, in LayerShadingResources resources,
        RenderGeometryResources geometry, IRenderMaterialResources materials, uint tileCount)
    {
        var parameters = new BinParameters(GetBinCount(materials), materials.MaterialCapacity);
        MemoryMarshal.Write(frame.GetBufferDataWriteSpan(resources.Parameters), in parameters);
        DispatchArguments dispatch = FlatDispatch(tileCount, 1);
        frame.SetPassData(resources.CountPass, dispatch);
        frame.SetPassData(resources.ScatterPass, dispatch);
        PassDefinition count = frame.GetPass(resources.CountPass);
        geometry.ReadPageHeap(ref frame, ref count, PipelineSync.ComputeShading);
        PassDefinition scatter = frame.GetPass(resources.ScatterPass);
        geometry.ReadPageHeap(ref frame, ref scatter, PipelineSync.ComputeShading);
    }

    public static void ResizeWork(ref RenderGraphEdit edit, in LayerShadingResources resources, uint fragmentCapacity) =>
        edit.SetBufferDescription(resources.PixelCoordinates, CoordinateDescription(fragmentCapacity));

    public static void Remove(ref RenderGraphEdit edit, in LayerShadingResources resources)
    {
        edit.Remove(resources.Region);
        RemoveResources(ref edit, resources);
    }

    internal static void RemoveResources(ref RenderGraphEdit edit, in LayerShadingResources resources)
    {
        edit.Remove(resources.ParametersView);
        foreach (GraphBufferSrvId view in resources.Srvs) edit.Remove(view);
        foreach (GraphBufferUavId view in resources.Uavs) edit.Remove(view);
        foreach (GraphBufferId buffer in resources.Buffers) edit.Remove(buffer);
    }

    private static GraphPassId AddPass(ref RenderGraphEdit edit, RenderPipelines pipelines, Shader shader,
        string entry, GraphRegionId region, DispatchArguments dispatch,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve,
        RenderGeometryResources geometry, IRenderMaterialResources materials, GraphBufferId parameters)
    {
        LinkedComputePipeline pipeline = LinkedComputePipeline.Create(pipelines, shader, entry, "Layer " + entry);
        GraphPassId id = edit.AddComputeFramePass("Layer " + entry, PassQueueSelection.AnyCompatible(), dispatch,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data), region);
        PassDefinition pass = edit.GetPass(id);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        RenderMaterialEntry material = default;
        BindResources(ref edit, ref pass, pipeline, resolve, geometry, materials, in material, new(parameters, new(0, 32)));
        return id;
    }

    private static void BindResources(ref RenderGraphEdit edit, ref PassDefinition pass, LinkedComputePipeline pipeline,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve, RenderGeometryResources geometry,
        IRenderMaterialResources materials, in RenderMaterialEntry material, GraphBufferSlice uniforms)
    {
        foreach (ShaderParameterResource resource in ShaderParameterResources.Create(pipeline.Shader,
            pipeline.Pipeline.Device.Capabilities.CompileTarget, pipeline.EntryPoint.Name).Resources)
        {
            if (resource.ParameterLayout != VariableLayoutReflection.Null)
            {
                pass.BindParameterBlock(resource.ParameterLayout);
                continue;
            }
            if (!resource.IsUsed)
            {
                pass.Bind(GraphParameterResourceBinding.Null(resource.Type));
                continue;
            }
            if (resolve(resource.Name, resource.Type) is { } binding)
            {
                pass.Bind(binding);
                continue;
            }
            if (resource.Name == "PageHeap")
            {
                geometry.BindPageHeap(edit, ref pass, PipelineSync.ComputeShading);
                continue;
            }
            if (resource.Name == "InstanceProperties")
            {
                pass.Bind(GraphParameterResourceBinding.ConstantBuffer(geometry.InstanceProperties, PipelineSync.ComputeShading));
                continue;
            }
            GraphParameterResourceBinding? geometryBinding = resource.Name switch
            {
                "Views" => GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(geometry.PackedViews,
                    structureStride: geometry.PackedViewStride), PipelineSync.ComputeShading),
                "VisibleClusters" => GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(geometry.VisibleClusters,
                    structureStride: 16), PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges),
                "InstanceData" => GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(geometry.InstanceData),
                    PipelineSync.ComputeShading, BufferContentScope.CountBoundedRanges),
                _ => null,
            };
            if (geometryBinding is { } geometryValue)
            {
                pass.Bind(geometryValue);
                continue;
            }
            materials.BindResource(ref edit, ref pass, in resource, in material, uniforms);
        }
    }

    private static BufferDesc CoordinateDescription(uint fragmentCapacity) =>
        new((ulong)fragmentCapacity * 8u, WorkUsages, "Layer shade pixel work");
    private static DispatchArguments FlatDispatch(uint count, uint threads)
    {
        uint groups = (count + threads - 1u) / threads;
        return new(Math.Min(groups, 65535u), Math.Max((groups + 65534u) / 65535u, 1u), 1);
    }
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private readonly record struct BinParameters
    {
        private readonly uint ScreenWidth, ScreenHeight;
        private readonly uint ShadeBinCount, MaterialCapacity, BinItemCapacity, Padding0, ViewCount, Padding1;
        internal BinParameters(uint count, uint capacity)
        {
            this = default;
            ShadeBinCount = count;
            MaterialCapacity = capacity;
        }
    }

    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private readonly record struct ShadeParameters(uint DebugMode, uint ScreenWidth, uint ScreenHeight,
        uint ShadingBin, uint ShadeBinCount, uint MaterialCapacity, uint ViewCount, uint Padding, uint VisibilityDepth);

    private readonly record struct MaterialDispatch(IndirectCommandLayout Layout, GraphBufferId Arguments, ulong Offset)
    {
        internal static void Record(ref ComputePassCommandScope commands, in MaterialDispatch data) =>
            commands.ExecuteIndirect(data.Layout, new BufferRegion(commands.GetBuffer(data.Arguments), new(data.Offset, 12)), 1);
    }
}
