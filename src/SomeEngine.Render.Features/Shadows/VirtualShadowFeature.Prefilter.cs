using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private LinkedComputePipeline? _prefilterPipeline;
    private LinkedComputePipeline? _prefilterRequestPipeline;
    private LinkedComputePipeline? _prefilterResetPipeline, _prefilterSelectPipeline;
    private GraphPassId _prefilterRequestPass;
    private GraphPassId _prefilterSelectPass, _prefilterResetPass;
    private GraphBufferId _prefilterPages, _prefilterArguments;
    private GraphTextureId _prefilterAxisX;
    private GraphTextureId _prefilterSeparate;
    private GraphTextureId _prefilterMerged;
    private GraphBufferId _prefilterPreviousPages;
    private GraphTextureId _prefilterPreviousMasks;
    private GraphBufferInput _prefilterPreviousViews = null!;
    private GraphBufferInput _prefilterProjections = null!;
    private GraphBufferInput[] _prefilterUniforms = [];
    private GraphPassId[] _prefilterPasses = [];
    private GraphPassId _prefilterInitialize;
    private uint _prefilterHistoryLayer;
    private bool _prefilterHistoryReady;
    private uint _prefilterEntryCapacity;

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 48)]
    private struct PrefilterUniforms
    {
        public uint ViewCount, EntriesPerView, EntryCapacity, FrameIndex;
        public uint PageSize, PagesPerRow, Mode, WriteLayer;
        public uint HasHistory, PreviousViewCount;
        public float HistoryWeight;
        public uint EntryCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 80)]
    private struct PrefilterProjection
    {
        public Matrix4x4 ShadowToWorld;
        public uint PreviousFirstView;
        public uint Pad0, Pad1, Pad2;
    }

    private enum PrefilterResource
    {
        Uniforms, Views, PreviousViews, Projections, PageReferences, Pages, Depth,
        AxisX, Separate, Merged, ReceiverMasks, PreviousPages, PreviousMasks, RequestFlags, RequestFrames, WorkPages, PageCount, SelectedPages, Arguments,
    }

    private void InstallPrefilter(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders, uint entryCapacity,
        GraphRegionId prepare, GraphRegionId finish)
    {
        _prefilterEntryCapacity = entryCapacity;
        var settings = _shadowMap.Settings;
        if (!settings.PrefilteredDistantEnabled)
        {
            _prefilterMerged = edit.CreatePersistentTexture(new TextureDesc(TextureDimension.Texture2D,
                1, 1, 1, 1, 1, 1, Format.R8UNorm,
                TextureUsages.Sampled | TextureUsages.ColorAttachment | TextureUsages.CopyDestination,
                label: "Virtual shadow filtered fallback"));
            var empty = new VirtualShadowAtlasInitializePassData(_prefilterMerged);
            _prefilterInitialize = edit.AddCopyFramePass("Initialize virtual shadow filtered fallback",
                PassQueueSelection.AnyOfType(QueueType.Graphics), empty,
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                VirtualShadowAtlasInitializePassData.Declare, VirtualShadowAtlasInitializePassData.Record, region: prepare);
            return;
        }
        _prefilterPipeline = LinkedComputePipeline.Create(_backend, _device, shaders.PrefilterShader,
            shaders.Prefilter, "Virtual shadow distant prefilter");
        _prefilterResetPipeline = LinkedComputePipeline.Create(_backend, _device, shaders.PrefilterResetShader,
            shaders.PrefilterReset, "Virtual shadow filter work reset");
        _prefilterSelectPipeline = LinkedComputePipeline.Create(_backend, _device, shaders.PrefilterSelectShader,
            shaders.PrefilterSelect, "Virtual shadow filter page selection");
        uint paddedSize = checked(settings.PhysicalPagesPerRow * (settings.PageSize + 2u));
        uint paddedHeight = checked(settings.MaxPhysicalPages / settings.PhysicalPagesPerRow * (settings.PageSize + 2u));
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
        _prefilterUniforms = new GraphBufferInput[9];
        _prefilterPasses = new GraphPassId[6];
        _prefilterUniforms[6] = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterPages = edit.CreatePersistentBuffer(new BufferDesc(
            checked((ulong)settings.MaxPhysicalPages * sizeof(uint)), bufferUses, "Virtual shadow filtered pages"));
        _prefilterArguments = edit.CreatePersistentBuffer(new BufferDesc(96,
            bufferUses | BufferUsages.Indirect, "Virtual shadow filter arguments"));
        _prefilterRequestPipeline = LinkedComputePipeline.Create(_backend, _device, shaders.PrefilterRequestsShader,
            shaders.PrefilterRequests, "Virtual shadow secondary receiver propagation");
        _prefilterRequestPass = AddComputePass(ref edit, "Propagate virtual shadow filtered receiver requests",
            _prefilterRequestPipeline, new VirtualShadowDispatchPassData(default), prepare);
        BindCoarseMark(ref edit, _prefilterRequestPass, _prefilterUniforms[6]);
        edit.OrderAfter(_prefilterRequestPass, _markPass);
        edit.OrderAfter(_prefilterRequestPass, _coarseMarkPass);
        edit.OrderAfter(_poolPreparePass, _prefilterRequestPass);
        var initialize = new PrefilterInitializeData(_prefilterAxisX, _prefilterSeparate, _prefilterMerged,
            _prefilterPreviousPages, _prefilterPreviousMasks, _prefilterPages, _prefilterArguments);
        _prefilterInitialize = edit.AddCopyFramePass("Initialize virtual shadow prefilter histories",
            PassQueueSelection.AnyOfType(QueueType.Graphics), initialize,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            PrefilterInitializeData.Declare, PrefilterInitializeData.Record, region: prepare);
        PrefilterResource[] bindings = ResolvePrefilterBindings(_prefilterPipeline);

        static PrefilterResource[] ResolvePrefilterBindings(LinkedComputePipeline pipeline) =>
            ShaderResourceLayout.Resolve<PrefilterResource>(pipeline.ParameterLayout,
            static (name, _) => name switch
            {
                "Prefilter" => PrefilterResource.Uniforms,
                "FilterViews" => PrefilterResource.Views,
                "PreviousFilterViews" => PrefilterResource.PreviousViews,
                "FilterProjections" => PrefilterResource.Projections,
                "FilterPageReferences" => PrefilterResource.PageReferences,
                "FilterPageTable" => PrefilterResource.Pages,
                "FilterDepthAtlas" => PrefilterResource.Depth,
                "FilterAxisX" => PrefilterResource.AxisX,
                "FilterSeparate" => PrefilterResource.Separate,
                "FilterMerged" => PrefilterResource.Merged,
                "FilterReceiverMasks" => PrefilterResource.ReceiverMasks,
                "FilterPreviousPageTable" => PrefilterResource.PreviousPages,
                "FilterPreviousReceiverMasks" => PrefilterResource.PreviousMasks,
                "FilterRequestFlags" => PrefilterResource.RequestFlags,
                "FilterRequestFrames" => PrefilterResource.RequestFrames,
                "FilterPages" => PrefilterResource.WorkPages,
                "FilterPageCount" => PrefilterResource.PageCount,
                "SelectedFilterPages" => PrefilterResource.SelectedPages,
                "FilterArguments" => PrefilterResource.Arguments,
                _ => null,
            });
        string[] labels = ["Filter virtual shadow rows", "Filter virtual shadow columns",
            "Gather virtual shadow filtered borders", "Merge virtual shadow filtered clipmaps",
            "Gather virtual shadow merged borders", "Carry virtual shadow filtered page history"];
        for (int mode = 0; mode < _prefilterPasses.Length; mode++)
        {
            _prefilterUniforms[mode] = edit.CreateBufferInput(BufferUsages.Constant);
            _prefilterPasses[mode] = AddIndirectPass(ref edit, labels[mode], _prefilterPipeline, _prefilterArguments,
                checked((ulong)mode * 16u), finish);
            PassDefinition pass = edit.GetPass(_prefilterPasses[mode]);
            foreach (PrefilterResource resource in bindings)
                BindPrefilterResource(ref edit, ref pass, resource, _prefilterUniforms[mode]);
            edit.OrderAfter(_prefilterPasses[mode], mode == 0 ? _rasterCompletePass : _prefilterPasses[mode - 1]);
        }
        _prefilterUniforms[7] = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterUniforms[8] = edit.CreateBufferInput(BufferUsages.Constant);
        _prefilterResetPass = AddComputePass(ref edit, "Reset virtual shadow filter work", _prefilterResetPipeline,
            new VirtualShadowDispatchPassData(default), finish);
        _prefilterSelectPass = AddComputePass(ref edit, "Select virtual shadow filter pages", _prefilterSelectPipeline,
            new VirtualShadowDispatchPassData(new DispatchArguments(DivideRoundUp(settings.MaxPhysicalPages, 64u), 1, 1)), finish);
        PassDefinition reset = edit.GetPass(_prefilterResetPass);
        foreach (PrefilterResource resource in ResolvePrefilterBindings(_prefilterResetPipeline))
            BindPrefilterResource(ref edit, ref reset, resource, _prefilterUniforms[7]);
        PassDefinition select = edit.GetPass(_prefilterSelectPass);
        foreach (PrefilterResource resource in ResolvePrefilterBindings(_prefilterSelectPipeline))
            BindPrefilterResource(ref edit, ref select, resource, _prefilterUniforms[8]);
        edit.OrderAfter(_prefilterResetPass, _rasterCompletePass);
        edit.OrderAfter(_prefilterResetPass, _prefilterInitialize);
        edit.OrderAfter(_prefilterSelectPass, _prefilterResetPass);
        edit.OrderAfter(_prefilterPasses[0], _prefilterSelectPass);
        edit.OrderAfter(_prefilterPasses[0], _prefilterInitialize);
        edit.OrderAfter(_frameCompletePass, _prefilterPasses[^1]);
        edit.OrderAfter(_receiverMaskResetPass, _prefilterPasses[^1]);
    }

    private void BindPrefilterResource(ref RenderGraphEdit edit, ref PassDefinition pass, PrefilterResource resource,
        GraphBufferInput uniforms)
    {
        const PipelineSync sync = PipelineSync.ComputeShading;
        switch (resource)
        {
            case PrefilterResource.RequestFlags: _ = pass.Bind(edit.CreateBufferSrv(_requestedPageFlags, structureStride: sizeof(uint)), sync); break;
            case PrefilterResource.RequestFrames: _ = pass.Bind(edit.CreateBufferSrv(_requestedPages, structureStride: sizeof(uint)), sync); break;
            case PrefilterResource.WorkPages: _ = pass.Bind(edit.CreateBufferSrv(_prefilterPages, structureStride: sizeof(uint)), sync, BufferContentScope.CountBoundedPrefix); break;
            case PrefilterResource.PageCount: _ = pass.Bind(edit.CreateBufferSrv(_prefilterArguments), sync); break;
            case PrefilterResource.SelectedPages: _ = pass.Bind(edit.CreateBufferUav(_prefilterPages, structureStride: sizeof(uint)), sync, GraphAccessMode.Write, WriteCoverage.Partial, contentScope: BufferContentScope.CountBoundedPrefix); break;
            case PrefilterResource.Arguments: _ = pass.Bind(edit.CreateBufferUav(_prefilterArguments), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.Uniforms: _ = pass.Bind(uniforms, sync); break;
            case PrefilterResource.Views: _ = pass.Bind(edit.CreateBufferSrv(_frameShadowViews, structureStride: VirtualShadowView.SizeInBytes), sync); break;
            case PrefilterResource.PreviousViews: _ = pass.Bind(edit.CreateBufferSrv(_prefilterPreviousViews, structureStride: VirtualShadowView.SizeInBytes), sync); break;
            case PrefilterResource.Projections: _ = pass.Bind(edit.CreateBufferSrv(_prefilterProjections, structureStride: 80), sync); break;
            case PrefilterResource.PageReferences: _ = pass.Bind(edit.CreateBufferSrv(_pageOwners, structureStride: 4), sync); break;
            case PrefilterResource.Pages: _ = pass.Bind(edit.CreateBufferSrv(_framePageTable, structureStride: sizeof(uint)), sync); break;
            case PrefilterResource.ReceiverMasks: _ = pass.Bind(edit.CreateTextureSrv(_pageReceiverMasks,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2D), sync); break;
            case PrefilterResource.Depth: _ = pass.Bind(edit.CreateTextureSrv(_atlas, format: Format.R32UInt, dimension: TextureViewDimension.Texture2DArray), sync); break;
            case PrefilterResource.AxisX: _ = pass.Bind(edit.CreateTextureUav(_prefilterAxisX, format: Format.R8UNorm, dimension: TextureViewDimension.Texture2D), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.Separate: _ = pass.Bind(edit.CreateTextureUav(_prefilterSeparate, format: Format.R8UNorm, dimension: TextureViewDimension.Texture2DArray), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.Merged: _ = pass.Bind(edit.CreateTextureUav(_prefilterMerged, format: Format.R8UNorm, dimension: TextureViewDimension.Texture2D), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.PreviousPages: _ = pass.Bind(edit.CreateBufferUav(_prefilterPreviousPages, structureStride: sizeof(uint)), sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
            case PrefilterResource.PreviousMasks: _ = pass.Bind(edit.CreateTextureUav(_prefilterPreviousMasks,
                new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color), Format.R32UInt, TextureViewDimension.Texture2DArray),
                sync, GraphAccessMode.ReadWrite, WriteCoverage.Partial); break;
        }
    }

    private TextureDesc PrefilterReceiverHistoryDescription => new(TextureDimension.Texture2D,
        ReceiverMaskWidth, ReceiverMaskHeight, 1, 1, 2, 1, Format.R32UInt,
        TextureUsages.Sampled | TextureUsages.Storage | TextureUsages.ColorAttachment | TextureUsages.CopyDestination,
        label: "Virtual shadow prefilter receiver histories");

    private void StagePrefilter(ref RenderGraphFrame graph, scoped ReadOnlySpan<VirtualShadowView> views)
    {
        if (_prefilterPipeline is null)
        {
            graph.SetPassEnabled(_prefilterInitialize, !_prefilterHistoryReady);
            return;
        }
        var projections = new PrefilterProjection[Math.Max(views.Length, 1)];
        for (int index = 0; index < views.Length; index++)
        {
            Matrix4x4.Invert(views[index].LightViewProjection, out projections[index].ShadowToWorld);
            projections[index].PreviousFirstView = uint.MaxValue;
            ShadowWorkView key = _frameWorkViews[index];
            if (_committedWorkViews.TryGetValue(key, out uint previous) && previous < _previousShadowViews.Length)
            {
                VirtualShadowView old = _previousShadowViews[previous];
                projections[index].PreviousFirstView = previous - checked((uint)(old.ClipmapLevel - old.FirstClipmapLevel));
            }
        }
        graph.SetBufferInput(_prefilterProjections, graph.Upload<PrefilterProjection>(projections, BufferUsages.ShaderRead,
            "Virtual shadow filter inverse projections"));
        graph.SetBufferInput(_prefilterPreviousViews, graph.Upload<VirtualShadowView>(
            _frameHasPreviousHistory && _previousShadowViews.Length != 0 ? _previousShadowViews : [default],
            BufferUsages.ShaderRead, "Previous virtual shadow filtered views"));
        var settings = _shadowMap.Settings;
        var requests = new PrefilterRequestUniforms
        {
            ViewCount = checked((uint)views.Length), PageTableEntryCount = ActivePageTableEntryCount,
            FrameIndex = _frameIndex, EntriesPerView = settings.PageTableEntriesPerView,
        };
        _uniformData.Write(ref graph, _prefilterUniforms[6], in requests);
        graph.SetPassDataIfChanged(_prefilterRequestPass, new VirtualShadowDispatchPassData(
            new DispatchArguments(DivideRoundUp(requests.PageTableEntryCount, 64u), 1, 1)));
        graph.SetPassEnabled(_prefilterRequestPass, views.Length != 0);
        for (int mode = 0; mode < _prefilterPasses.Length; mode++)
        {
            var data = new PrefilterUniforms
            {
                ViewCount = checked((uint)views.Length), EntriesPerView = settings.PageTableEntriesPerView,
                EntryCapacity = _prefilterEntryCapacity, FrameIndex = _frameIndex,
                EntryCount = ActivePageTableEntryCount,
                PageSize = settings.PageSize, PagesPerRow = settings.PhysicalPagesPerRow,
                Mode = checked((uint)mode), WriteLayer = _prefilterHistoryLayer ^ 1u,
                HasHistory = _prefilterHistoryReady && _frameHasPreviousHistory ? 1u : 0u,
                PreviousViewCount = _frameHasPreviousHistory ? checked((uint)_previousShadowViews.Length) : 0u,
                HistoryWeight = Math.Clamp(settings.PrefilteredDistantHistoryWeight, 0f, 1f),
            };
            _uniformData.Write(ref graph, _prefilterUniforms[mode], in data);
            graph.SetPassEnabled(_prefilterPasses[mode], views.Length != 0);
        }
        for (int workMode = 6; workMode <= 7; workMode++)
        {
            var work = new PrefilterUniforms
            {
                ViewCount = checked((uint)views.Length), EntriesPerView = settings.PageTableEntriesPerView,
                EntryCapacity = _prefilterEntryCapacity, FrameIndex = _frameIndex,
                EntryCount = ActivePageTableEntryCount, PageSize = settings.PageSize,
                PagesPerRow = settings.PhysicalPagesPerRow, Mode = checked((uint)workMode),
                WriteLayer = _prefilterHistoryLayer ^ 1u,
            };
            _uniformData.Write(ref graph, _prefilterUniforms[workMode + 1], in work);
        }
        uint resetGroups = DivideRoundUp(_prefilterEntryCapacity, 64u);
        graph.SetPassDataIfChanged(_prefilterResetPass, new VirtualShadowDispatchPassData(
            new DispatchArguments(Math.Min(resetGroups, 65535u), DivideRoundUp(resetGroups, 65535u), 1)));
        graph.SetPassEnabled(_prefilterResetPass, views.Length != 0u);
        graph.SetPassEnabled(_prefilterSelectPass, views.Length != 0u);
        graph.SetPassEnabled(_prefilterInitialize, !_prefilterHistoryReady || _forceStateReset);
    }

    private void CommitPrefilter()
    {
        _prefilterHistoryReady = true;
        if (_prefilterPipeline is null) return;
        _prefilterHistoryLayer ^= 1u;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    private struct PrefilterRequestUniforms
    {
        public uint ViewCount, PageTableEntryCount, FrameIndex, EntriesPerView;
    }

    private void BindPrefilteredAtlas(ref RenderGraphEdit edit, ref PassDefinition pass, PipelineSync sync)
    {
        _ = pass.Bind(edit.CreateTextureSrv(_prefilterMerged, format: Format.R8UNorm, dimension: TextureViewDimension.Texture2D), sync);
    }

    private readonly record struct PrefilterInitializeData(GraphTextureId AxisX, GraphTextureId Separate, GraphTextureId Merged,
        GraphBufferId Pages, GraphTextureId Masks, GraphBufferId WorkPages, GraphBufferId Arguments)
    {
        public static void Declare(ref PassDefinition pass, ref PrefilterInitializeData data)
        {
            _ = pass.Write(data.AxisX, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), PipelineSync.Copy,
                ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.Separate, new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color), PipelineSync.Copy,
                ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.Merged, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color), PipelineSync.Copy,
                ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.Pages, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            _ = pass.Write(data.Masks, new TextureSubresourceRange(0, 1, 0, 2, TextureAspects.Color), PipelineSync.Copy,
                ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete);
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
