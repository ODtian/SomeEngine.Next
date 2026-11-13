using System.Collections.Frozen;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;

namespace SomeEngine.Render.Features.Shadows;

internal sealed class VirtualShadowPrefilter(VirtualShadowFeature feature)
{
    private readonly VirtualShadowFeature _feature = feature;

    internal LinkedComputePipeline? _prefilterPipeline;
    internal LinkedComputePipeline? _prefilterRequestPipeline;
    internal LinkedComputePipeline? _prefilterRequestCapturePipeline;
    internal LinkedComputePipeline? _prefilterResetPipeline, _prefilterSelectPipeline;
    internal GraphPassId _prefilterRequestPass;
    internal GraphPassId _prefilterRequestCapturePass;
    internal GraphBufferId _prefilterRequestArguments;
    internal GraphPassId _prefilterSelectPass, _prefilterResetPass;
    internal GraphBufferId _prefilterPages, _prefilterArguments;
    internal GraphTextureId _prefilterAxisX;
    internal GraphTextureId _prefilterSeparate;
    internal GraphTextureId _prefilterMerged;
    internal GraphBufferId _prefilterPreviousPages;
    internal GraphTextureId _prefilterPreviousMasks;
    internal GraphBufferInput _prefilterPreviousViews = null!;
    internal GraphBufferInput _prefilterProjections = null!;
    internal GraphBufferInput _prefilterAxisUniforms = null!;
    internal GraphBufferInput _prefilterColumnUniforms = null!;
    internal GraphBufferInput _prefilterBorderUniforms = null!;
    internal GraphBufferInput _prefilterMergeUniforms = null!;
    internal GraphBufferInput _prefilterMergedBorderUniforms = null!;
    internal GraphBufferInput _prefilterHistoryUniforms = null!;
    internal GraphBufferInput _prefilterRequestUniforms = null!;
    internal GraphBufferInput _prefilterResetUniforms = null!;
    internal GraphBufferInput _prefilterSelectUniforms = null!;

    private GraphBufferInput UniformsForMode(int mode) => mode switch
    {
        0 => _prefilterAxisUniforms,
        1 => _prefilterColumnUniforms,
        2 => _prefilterBorderUniforms,
        3 => _prefilterMergeUniforms,
        4 => _prefilterMergedBorderUniforms,
        _ => _prefilterHistoryUniforms,
    };
    internal GraphPassId[] _prefilterPasses = [];
    internal GraphPassId _prefilterInitialize;
    internal uint _prefilterHistoryLayer;
    internal bool _prefilterHistoryReady;
    internal uint _prefilterEntryCapacity;

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
    internal struct PrefilterUniforms
    {
        public uint ViewCount, EntriesPerView, EntryCapacity, FrameIndex;
        public uint PageSize, PagesPerRow, Mode, WriteLayer;
        public uint HasHistory, PreviousViewCount;
        public float HistoryWeight;
        public uint EntryCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 80)]
    internal struct PrefilterProjection
    {
        public Matrix4x4 ShadowToWorld;
        public uint PreviousFirstView;
        public uint Pad0, Pad1, Pad2;
    }

    internal enum PrefilterResource
    {
        Uniforms, Views, RangeViews, PreviousViews, Projections, PageReferences, Pages, Depth,
        AxisX, Separate, Merged, ReceiverMasks, PreviousPages, PreviousMasks, RequestFlags, WorkPages, PageCount, SelectedPages, Arguments,
    }

    internal IEnumerable<GraphBufferInput> UniformInputs =>
        [_prefilterAxisUniforms, _prefilterColumnUniforms, _prefilterBorderUniforms, _prefilterMergeUniforms,
         _prefilterMergedBorderUniforms, _prefilterHistoryUniforms, _prefilterRequestUniforms,
         _prefilterResetUniforms, _prefilterSelectUniforms];

    private static readonly System.Collections.Frozen.FrozenDictionary<string, PrefilterResource> ResourceNames =
        new Dictionary<string, PrefilterResource>
        {
            ["Prefilter"] = PrefilterResource.Uniforms,
            ["FilterViews"] = PrefilterResource.Views,
            ["ShadowRanges"] = PrefilterResource.RangeViews,
            ["PreviousFilterViews"] = PrefilterResource.PreviousViews,
            ["FilterProjections"] = PrefilterResource.Projections,
            ["FilterPageReferences"] = PrefilterResource.PageReferences,
            ["FilterPageTable"] = PrefilterResource.Pages,
            ["FilterDepthAtlas"] = PrefilterResource.Depth,
            ["FilterAxisX"] = PrefilterResource.AxisX,
            ["FilterSeparate"] = PrefilterResource.Separate,
            ["FilterMerged"] = PrefilterResource.Merged,
            ["FilterReceiverMasks"] = PrefilterResource.ReceiverMasks,
            ["FilterPreviousPageTable"] = PrefilterResource.PreviousPages,
            ["FilterPreviousReceiverMasks"] = PrefilterResource.PreviousMasks,
            ["FilterRequestFlags"] = PrefilterResource.RequestFlags,
            ["FilterPages"] = PrefilterResource.WorkPages,
            ["FilterPageCount"] = PrefilterResource.PageCount,
            ["SelectedFilterPages"] = PrefilterResource.SelectedPages,
            ["FilterArguments"] = PrefilterResource.Arguments,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    internal void InstallPrefilter(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders, uint entryCapacity,
        GraphRegionId prepare, GraphRegionId finish)
    {
        _prefilterEntryCapacity = entryCapacity;
        var settings = _feature._shadowMap.Settings;
        _prefilterPipeline = LinkedComputePipeline.Create(_feature._renderPipelines,shaders.PrefilterShader,            shaders.Prefilter,"Virtual shadow distant prefilter");
        _prefilterResetPipeline = LinkedComputePipeline.Create(_feature._renderPipelines,shaders.PrefilterResetShader,            shaders.PrefilterReset,"Virtual shadow filter work reset");
        _prefilterSelectPipeline = LinkedComputePipeline.Create(_feature._renderPipelines,shaders.PrefilterSelectShader,            shaders.PrefilterSelect,"Virtual shadow filter page selection");
        uint paddedSize = checked(settings.AtlasPagesPerRow * (settings.PageSize + 2u));
        uint paddedHeight = checked(settings.MaxPages / settings.AtlasPagesPerRow * (settings.PageSize + 2u));
        TextureUsages textureUses = TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopyDestination;
        _prefilterAxisX = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
            settings.AtlasSize, settings.AtlasHeight, 1, 1, 1, 1, Format.R8UNorm, textureUses,
            label: "Virtual shadow horizontal prefilter"));
        _prefilterSeparate = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
            paddedSize, paddedHeight, 1, 1, 2, 1, Format.R8UNorm, textureUses,
            label: "Virtual shadow separate prefilter histories"));
        _prefilterMerged = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
            paddedSize, paddedHeight, 1, 1, 1, 1, Format.R8UNorm, textureUses,
            label: "Virtual shadow merged prefilter"));
        BufferUsages bufferUses = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination;
        _prefilterPreviousPages = edit.CreatePersistentBuffer(new BufferDesc(checked((ulong)entryCapacity * 2u * sizeof(uint)), bufferUses,
            "Virtual shadow prefilter page histories"));
        _prefilterPreviousMasks = edit.CreatePersistentTexture(PrefilterReceiverHistoryDescription);
        _prefilterPreviousViews = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _prefilterProjections = edit.CreateBufferInput(BufferUsages.ShaderRead);
        _prefilterAxisUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterColumnUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterBorderUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterMergeUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterMergedBorderUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterHistoryUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterPasses = new GraphPassId[6];
        _prefilterRequestUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterPages = edit.CreatePersistentBuffer(new BufferDesc(
            checked((ulong)settings.MaxPages * sizeof(uint)), bufferUses, "Virtual shadow filtered pages"));
        _prefilterArguments = edit.CreatePersistentBuffer(new BufferDesc(96,
            bufferUses | BufferUsages.Indirect, "Virtual shadow filter arguments"));
        _prefilterRequestPipeline = LinkedComputePipeline.Create(_feature._renderPipelines,shaders.PrefilterRequestsShader,            shaders.PrefilterRequests,"Virtual shadow secondary receiver propagation");
        _prefilterRequestCapturePipeline = LinkedComputePipeline.Create(_feature._renderPipelines,
            _prefilterRequestPipeline.Shader,"CaptureVirtualShadowFilterRequests","Virtual shadow filter request count");
        _prefilterRequestArguments = edit.CreatePersistentBuffer(new BufferDesc(16,
            bufferUses | BufferUsages.Indirect,"Virtual shadow filter request arguments"));
        _prefilterRequestCapturePass = _feature.AddComputePass(ref edit,"Capture virtual shadow filter requests",
            _prefilterRequestCapturePipeline,new VirtualShadowDispatchPassData(new(1,1,1)),prepare);
        _prefilterRequestPass = _feature.AddIndirectPass(ref edit, "Propagate virtual shadow filtered receiver requests",
            _prefilterRequestPipeline,_prefilterRequestArguments,region:prepare);
        BindRequestPass(ref edit,_prefilterRequestCapturePass,_prefilterRequestCapturePipeline);
        BindRequestPass(ref edit,_prefilterRequestPass,_prefilterRequestPipeline);
        edit.OrderAfter(_prefilterRequestCapturePass, _feature._coarseMarkPass);
        edit.OrderAfter(_prefilterRequestPass,_prefilterRequestCapturePass);
        edit.OrderAfter(_feature._poolPreparePass, _prefilterRequestPass);
        var initialize = new PrefilterInitializeData(_prefilterAxisX, _prefilterSeparate, _prefilterMerged,
            _prefilterPreviousPages, _prefilterPreviousMasks, _prefilterPages, _prefilterArguments);
        _prefilterInitialize = edit.AddCopyFramePass("Initialize virtual shadow prefilter histories",
            PassQueueSelection.AnyOfType(QueueType.Graphics), initialize,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            PrefilterInitializeData.Declare, PrefilterInitializeData.Record, region: prepare);
        PrefilterResource[] bindings = ResolvePrefilterBindings(_prefilterPipeline);

        static PrefilterResource[] ResolvePrefilterBindings(LinkedComputePipeline pipeline) =>
            ShaderResourceLayout.Resolve<PrefilterResource>(pipeline.ParameterLayout,
                static (name, _) => ResourceNames.TryGetValue(name, out PrefilterResource resource) ? resource : null);
        string[] labels = ["Filter virtual shadow rows", "Filter virtual shadow columns",
            "Gather virtual shadow filtered borders", "Merge virtual shadow filtered clipmaps",
            "Gather virtual shadow merged borders", "Carry virtual shadow filtered page history"];
        for (int mode = 0; mode < _prefilterPasses.Length; mode++)
        {
            _prefilterPasses[mode] = _feature.AddIndirectPass(ref edit, labels[mode], _prefilterPipeline, _prefilterArguments,
                checked((ulong)mode * 16u), finish);
            PassDefinition pass = edit.GetPass(_prefilterPasses[mode]);
            foreach (PrefilterResource resource in bindings)
                BindPrefilterResource(ref edit, ref pass, resource, UniformsForMode(mode));
            edit.OrderAfter(_prefilterPasses[mode], mode == 0 ? _feature._rasterCompletePass : _prefilterPasses[mode - 1]);
        }
        _prefilterResetUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterSelectUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterResetPass = _feature.AddComputePass(ref edit, "Reset virtual shadow filter work", _prefilterResetPipeline,
            new VirtualShadowDispatchPassData(default), finish);
        _prefilterSelectPass = _feature.AddComputePass(ref edit, "Select virtual shadow filter pages", _prefilterSelectPipeline,
            new VirtualShadowDispatchPassData(new DispatchArguments(VirtualShadowFeature.DivideRoundUp(settings.MaxPages, 64u), 1, 1)), finish);
        PassDefinition reset = edit.GetPass(_prefilterResetPass);
        foreach (PrefilterResource resource in ResolvePrefilterBindings(_prefilterResetPipeline))
            BindPrefilterResource(ref edit, ref reset, resource, _prefilterResetUniforms);
        PassDefinition select = edit.GetPass(_prefilterSelectPass);
        foreach (PrefilterResource resource in ResolvePrefilterBindings(_prefilterSelectPipeline))
            BindPrefilterResource(ref edit, ref select, resource, _prefilterSelectUniforms);
        edit.OrderAfter(_prefilterResetPass, _feature._rasterCompletePass);
        edit.OrderAfter(_prefilterResetPass, _prefilterInitialize);
        edit.OrderAfter(_prefilterSelectPass, _prefilterResetPass);
        edit.OrderAfter(_prefilterPasses.First(), _prefilterSelectPass);
        edit.OrderAfter(_prefilterPasses.First(), _prefilterInitialize);
        edit.OrderAfter(_feature._frameCompletePass, _prefilterPasses[^1]);
        edit.OrderAfter(_feature._receiverMaskResetPass, _prefilterPasses[^1]);
    }

    private void BindRequestPass(ref RenderGraphEdit edit, GraphPassId id, LinkedComputePipeline pipeline)
    {
        edit.SetPassParameterBindings(id,[]);
        PassDefinition pass = edit.GetPass(id);
        foreach (ShaderParameterResource resource in ShaderParameterResources.Create(pipeline.Shader,
            _feature._renderPipelines.Device.Capabilities.CompileTarget,pipeline.EntryPoint.Name).Resources)
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
            GraphParameterResourceBinding? binding = resource.Name switch
            {
                "FilterRequests" => GraphParameterResourceBinding.ConstantBuffer(_prefilterRequestUniforms,PipelineSync.ComputeShading),
                "ShadowViews" => GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(_feature._frameShadowViews,
                    structureStride:VirtualShadowView.SizeInBytes),PipelineSync.ComputeShading),
                "FilterRequestPages" => GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(
                    _feature._requests._requestedPageIndices,structureStride:sizeof(uint)),PipelineSync.ComputeShading,
                    GraphAccessMode.Read,contentScope:BufferContentScope.CountBoundedPrefix),
                "FilterRequestCount" => GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(
                    _feature._requests._requestedPageCount,structureStride:sizeof(uint)),PipelineSync.ComputeShading,GraphAccessMode.Read),
                "FilterRequestDispatchArgs" => GraphParameterResourceBinding.WritableBuffer(edit.CreateBufferUav(
                    _prefilterRequestArguments),PipelineSync.ComputeShading,GraphAccessMode.Write,WriteCoverage.Complete),
                "FilterRequestArguments" => GraphParameterResourceBinding.ReadOnlyBuffer(edit.CreateBufferSrv(
                    _prefilterRequestArguments),PipelineSync.ComputeShading),
                _ => null,
            };
            if (binding is { } value) pass.Bind(value);
            else if (_feature.TryResolve(resource.Name,resource.Type,out GraphResourceBinder binder))
                binder(ref edit,ref pass,PipelineSync.ComputeShading);
            else throw new InvalidDataException($"Virtual shadow filter request resource '{resource.Name}' has no binding.");
        }
    }

    internal void ReplaceRequestBindings(ref RenderGraphEdit edit)
    {
        BindRequestPass(ref edit,_prefilterRequestCapturePass,_prefilterRequestCapturePipeline!);
        BindRequestPass(ref edit,_prefilterRequestPass,_prefilterRequestPipeline!);
    }

    internal void BindPrefilterResource(ref RenderGraphEdit edit, ref PassDefinition pass, PrefilterResource resource,
        GraphBufferInput uniforms)
    {
        const PipelineSync sync = PipelineSync.ComputeShading;
        switch (resource)
        {
            case PrefilterResource.RequestFlags: _ = pass.Bind(edit.CreateBufferSrv(_feature._requests._requestedPageFlags, structureStride: sizeof(uint)), sync); break;
            case PrefilterResource.WorkPages: _ = pass.Bind(edit.CreateBufferSrv(_prefilterPages, structureStride: sizeof(uint)), sync, BufferContentScope.CountBoundedPrefix); break;
            case PrefilterResource.PageCount: _ = pass.Bind(edit.CreateBufferSrv(_prefilterArguments), sync); break;
            case PrefilterResource.SelectedPages: _ = pass.Bind(edit.CreateBufferUav(_prefilterPages, structureStride: sizeof(uint)), sync, GraphAccessMode.Write, WriteCoverage.Partial, contentScope: BufferContentScope.CountBoundedPrefix); break;
            case PrefilterResource.Arguments: _ = pass.Bind(edit.CreateBufferUav(_prefilterArguments), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.Uniforms: _ = pass.Bind(uniforms, sync); break;
            case PrefilterResource.Views: _ = pass.Bind(edit.CreateBufferSrv(_feature._frameShadowViews, structureStride: VirtualShadowView.SizeInBytes), sync); break;
            case PrefilterResource.RangeViews: _feature.BindRangeViews(ref edit, ref pass, sync); break;
            case PrefilterResource.PreviousViews: _ = pass.Bind(edit.CreateBufferSrv(_prefilterPreviousViews, structureStride: VirtualShadowView.SizeInBytes), sync); break;
            case PrefilterResource.Projections: _ = pass.Bind(edit.CreateBufferSrv(_prefilterProjections, structureStride: 80), sync); break;
            case PrefilterResource.PageReferences: _ = pass.Bind(edit.CreateBufferSrv(_feature._shadowMap.PageAssignments, structureStride: 4), sync); break;
            case PrefilterResource.Pages: _ = pass.Bind(edit.CreateBufferSrv(_feature._shadowMap.PageTable, structureStride: sizeof(uint)), sync); break;
            case PrefilterResource.ReceiverMasks: _ = pass.Bind(edit.CreateTextureSrv(_feature._requests._pageReceiverMasks,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D), sync); break;
            case PrefilterResource.Depth: _ = pass.Bind(edit.CreateTextureSrv(_feature._shadowMap.Atlas, format: Format.R32UInt, dimension: TextureViewDimension.Texture2DArray), sync); break;
            case PrefilterResource.AxisX: _ = pass.Bind(edit.CreateTextureUav(_prefilterAxisX, format: Format.R8UNorm, dimension: TextureViewDimension.Texture2D), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.Separate: _ = pass.Bind(edit.CreateTextureUav(_prefilterSeparate, format: Format.R8UNorm, dimension: TextureViewDimension.Texture2DArray), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.Merged: _ = pass.Bind(edit.CreateTextureUav(_prefilterMerged, format: Format.R8UNorm, dimension: TextureViewDimension.Texture2D), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.PreviousPages: _ = pass.Bind(edit.CreateBufferUav(_prefilterPreviousPages, structureStride: sizeof(uint)), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.PreviousMasks: _ = pass.Bind(edit.CreateTextureUav(_prefilterPreviousMasks,
                new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2DArray),
                sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
        }
    }

    internal TextureDesc PrefilterReceiverHistoryDescription => CreateReceiverHistoryDescription(_feature._sourceViewCapacity);

    internal TextureDesc CreateReceiverHistoryDescription(uint sourceViews) =>
        CreateReceiverHistoryDescription(sourceViews, _feature._shadowMap.Settings);

    internal TextureDesc CreateReceiverHistoryDescription(uint sourceViews, VirtualShadowMapSettings settings)
    {
        TextureDesc masks = _feature._requests.CreateReceiverMaskDescription(sourceViews, settings);
        return new(TextureDimension.Texture2D, masks.Width, masks.Height, 1, 1, 2, 1, Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopyDestination,
            label: "Virtual shadow prefilter receiver histories");
    }

    internal void StagePrefilter(ref RenderGraphFrame graph, scoped ReadOnlySpan<VirtualShadowView> views)
    {
        VirtualShadowFrame data = _feature.GetFrame(ref graph);
        bool requestsEnabled = _feature._shadowMap.Settings.PrefilteredDistantEnabled && views.Length != 0;
        graph.SetPassEnabled(_prefilterRequestCapturePass,requestsEnabled);
        graph.SetPassEnabled(_prefilterRequestPass,requestsEnabled);
        if (!_feature._shadowMap.Settings.PrefilteredDistantEnabled)
        {
            graph.SetPassEnabled(_prefilterInitialize, !_prefilterHistoryReady || _feature._shadowMap.RequiresInitialization);
            graph.SetPassEnabled(_prefilterResetPass, false);
            graph.SetPassEnabled(_prefilterSelectPass, false);
            foreach (GraphPassId pass in _prefilterPasses) graph.SetPassEnabled(pass, false);
            return;
        }
        var projections = new PrefilterProjection[Math.Max(views.Length, 1)];
        for (int index = 0; index < views.Length; index++)
        {
            Matrix4x4.Invert(views[index].LightViewProjection, out projections[index].ShadowToWorld);
            projections[index].PreviousFirstView = uint.MaxValue;
            VirtualShadowQuality.ShadowWorkView key = data._frameWorkViews[index];
            if (data._frameHasPreviousHistory && (views[index].Flags & VirtualShadowViewFlags.PreviousProjectionValid) != 0u &&
                _feature._quality._historyWorkViews.TryGetValue(key, out uint previous) && previous < (uint)data._previousViewCount)
            {
                VirtualShadowView old = data._previousShadowViews.Span[(int)previous];
                projections[index].PreviousFirstView = previous - checked((uint)(old.ClipmapLevel - old.FirstClipmapLevel));
            }
        }
        graph.SetBufferInput(_prefilterProjections, graph.Upload<PrefilterProjection>(projections, BufferUsages.ShaderRead,
            "Virtual shadow filter inverse projections"));
        graph.SetBufferInput(_prefilterPreviousViews, graph.Upload<VirtualShadowView>(
            data._frameHasPreviousHistory && data._previousViewCount != 0 ? data._previousShadowViews.Span : [default],
            BufferUsages.ShaderRead, "Previous virtual shadow filtered views"));
        var settings = _feature._shadowMap.Settings;
        var requests = new PrefilterRequestUniforms
        {
            ViewCount = checked((uint)views.Length), PageTableEntryCount = data.Views.PageTableEntryCount,
            FrameIndex = data._frameIndex, EntriesPerView = settings.PageTableEntriesPerView,
        };
        _feature._uniformData.Write(ref graph, _prefilterRequestUniforms, in requests);
        for (int mode = 0; mode < _prefilterPasses.Length; mode++)
        {
            var uniforms = new PrefilterUniforms
            {
                ViewCount = checked((uint)views.Length), EntriesPerView = settings.PageTableEntriesPerView,
                EntryCapacity = _prefilterEntryCapacity, FrameIndex = data._frameIndex,
                EntryCount = data.Views.PageTableEntryCount,
                PageSize = settings.PageSize, PagesPerRow = settings.AtlasPagesPerRow,
                Mode = checked((uint)mode), WriteLayer = _prefilterHistoryLayer ^ 1u,
                HasHistory = _prefilterHistoryReady && data._frameHasPreviousHistory ? 1u : 0u,
                PreviousViewCount = data._frameHasPreviousHistory ? checked((uint)data._previousViewCount) : 0u,
                HistoryWeight = Math.Clamp(settings.PrefilteredDistantHistoryWeight, 0f, 1f),
            };
            _feature._uniformData.Write(ref graph, UniformsForMode(mode), in uniforms);
            graph.SetPassEnabled(_prefilterPasses[mode], views.Length != 0);
        }
        for (int workMode = 6; workMode <= 7; workMode++)
        {
            var work = new PrefilterUniforms
            {
                ViewCount = checked((uint)views.Length), EntriesPerView = settings.PageTableEntriesPerView,
                EntryCapacity = _prefilterEntryCapacity, FrameIndex = data._frameIndex,
                EntryCount = data.Views.PageTableEntryCount, PageSize = settings.PageSize,
                PagesPerRow = settings.AtlasPagesPerRow, Mode = checked((uint)workMode),
                WriteLayer = _prefilterHistoryLayer ^ 1u,
            };
            _feature._uniformData.Write(ref graph, workMode == 6 ? _prefilterResetUniforms : _prefilterSelectUniforms, in work);
        }
        uint resetGroups = VirtualShadowFeature.DivideRoundUp(_prefilterEntryCapacity, 64u);
        graph.SetPassDataIfChanged(_prefilterResetPass, new VirtualShadowDispatchPassData(
            new DispatchArguments(Math.Min(resetGroups, 65535u), VirtualShadowFeature.DivideRoundUp(resetGroups, 65535u), 1)));
        graph.SetPassEnabled(_prefilterResetPass, views.Length != 0u);
        graph.SetPassEnabled(_prefilterSelectPass, views.Length != 0u);
        graph.SetPassEnabled(_prefilterInitialize, !_prefilterHistoryReady || _feature._shadowMap.RequiresInitialization);
    }

    internal void Resize(ref RenderGraphEdit edit, uint entries, uint sourceViews, VirtualShadowMapSettings settings)
    {
        uint width = checked(settings.AtlasPagesPerRow * (settings.PageSize + 2u));
        uint height = checked(settings.MaxPages / settings.AtlasPagesPerRow * (settings.PageSize + 2u));
        TextureUsages uses = TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopyDestination;
        edit.SetTextureDescription(_prefilterAxisX, new TextureDesc(TextureDimension.Texture2D,
            settings.AtlasSize, settings.AtlasHeight, 1, 1, 1, 1, Format.R8UNorm, uses, label: "Virtual shadow horizontal prefilter"));
        edit.SetTextureDescription(_prefilterSeparate, new TextureDesc(TextureDimension.Texture2D,
            width, height, 1, 1, 2, 1, Format.R8UNorm, uses, label: "Virtual shadow separate prefilter histories"));
        edit.SetTextureDescription(_prefilterMerged, new TextureDesc(TextureDimension.Texture2D,
            width, height, 1, 1, 1, 1, Format.R8UNorm, uses, label: "Virtual shadow merged prefilter"));
        VirtualShadowFeature.GrowLightBuffer(ref edit, _prefilterPages, (ulong)settings.MaxPages * sizeof(uint), "Virtual shadow filtered pages");
        VirtualShadowFeature.GrowLightBuffer(ref edit, _prefilterPreviousPages, (ulong)entries * 2u * sizeof(uint), "Virtual shadow prefilter page histories");
        edit.SetTextureDescription(_prefilterPreviousMasks, CreateReceiverHistoryDescription(sourceViews, settings));
    }

    internal void CommitPrefilter()
    {
        _prefilterHistoryReady = true;
        if (_prefilterPipeline is null) return;
        _prefilterHistoryLayer ^= 1u;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    internal struct PrefilterRequestUniforms
    {
        public uint ViewCount, PageTableEntryCount, FrameIndex, EntriesPerView;
    }

    internal void BindPrefilteredAtlas(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(edit.CreateTextureSrv(_prefilterMerged, format: Format.R8UNorm, dimension: TextureViewDimension.Texture2D), sync);
    }

    internal readonly record struct PrefilterInitializeData(GraphTextureId AxisX, GraphTextureId Separate, GraphTextureId Merged,
        GraphBufferId Pages, GraphTextureId Masks, GraphBufferId WorkPages, GraphBufferId Arguments)
    {
        public static void Declare(ref PassDefinition pass, ref PrefilterInitializeData data)
        {
            _ = pass.Write(data.AxisX, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), PipelineSync.RenderTarget,
                ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete);
            _ = pass.Write(data.Separate, new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color), PipelineSync.RenderTarget,
                ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete);
            _ = pass.Write(data.Merged, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), PipelineSync.RenderTarget,
                ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete);
            _ = pass.Write(data.Pages, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.Masks, new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color), PipelineSync.RenderTarget,
                ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete);
            _ = pass.Write(data.WorkPages, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.Arguments, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
        }
        public static void Record(ref CopyPassCommandScope commands, in PrefilterInitializeData data)
        {
            commands.ClearTexture(commands.GetTexture(data.AxisX), new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), default);
            commands.ClearTexture(commands.GetTexture(data.Separate), new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color), default);
            commands.ClearTexture(commands.GetTexture(data.Merged), new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), default);
            var pages = commands.GetBuffer(data.Pages);
            commands.ClearBuffer(pages, new BufferRange(0, pages.Info.Size));
            commands.ClearTexture(commands.GetTexture(data.Masks),
                new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color), default);
            var workPages = commands.GetBuffer(data.WorkPages);
            var arguments = commands.GetBuffer(data.Arguments);
            commands.ClearBuffer(workPages, new BufferRange(0, workPages.Info.Size));
            commands.ClearBuffer(arguments, new BufferRange(0, arguments.Info.Size));
        }
    }

}
