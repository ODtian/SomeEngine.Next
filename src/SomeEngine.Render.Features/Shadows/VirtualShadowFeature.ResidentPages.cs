using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Shadows;

public sealed partial class VirtualShadowFeature
{
    private GraphBufferId _residentPages;
    private GraphBufferInput _residentUniforms = null!;
    private GraphPassId _residentPropagationPass;
    private GraphPassId _residentInitializePass;
    private LinkedComputePipeline? _residentPipeline;

    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
    private struct ResidentUniforms
    {
        public uint ViewCount;
        public uint EntriesPerView;
        public uint EntryCount;
        public uint Pad0;
    }

    private void InstallResidentPages(ref RenderGraphEdit edit, VirtualShadowMapShaders shaders, uint entryCapacity,
        GraphRegionId finish)
    {
        _residentUniforms = edit.CreateBufferInput(BufferUsages.Constant);
        _residentPages = edit.CreatePersistentBuffer(new BufferDesc(checked((ulong)entryCapacity * 2u * sizeof(uint)),
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Virtual shadow completed resident ancestors"));
        _residentInitializePass = edit.AddCopyFramePass("Initialize virtual shadow resident ancestors",
            PassQueueSelection.AnyOfType(QueueType.Graphics), _residentPages,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref GraphBufferId buffer) =>
                pass.Write(buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete),
            static (ref CopyPassCommandScope commands, in GraphBufferId buffer) =>
                commands.ClearBuffer(commands.GetBuffer(buffer), BufferRange.Whole, 0), region: finish);
        _residentPipeline = LinkedComputePipeline.Create(_backend, _device, shaders.ResidentPagesShader,
            shaders.ResidentPages, "Virtual shadow resident ancestor propagation");
        _residentPropagationPass = AddComputePass(ref edit, "Propagate virtual shadow resident ancestors",
            _residentPipeline, new VirtualShadowDispatchPassData(default), finish);
        PassDefinition pass = edit.GetPass(_residentPropagationPass);
        _ = pass.Bind(_residentUniforms, PipelineSync.ComputeShading);
        _ = pass.Bind(edit.CreateBufferSrv(_frameShadowViews, structureStride: global::SomeEngine.Render.Shadows.VirtualShadowView.SizeInBytes), PipelineSync.ComputeShading);
        _ = pass.Bind(edit.CreateBufferSrv(_framePageTable, structureStride: 16), PipelineSync.ComputeShading);
        _ = pass.Bind(edit.CreateBufferUav(_residentPages, structureStride: 8), PipelineSync.ComputeShading,
            GraphAccessMode.Write, WriteCoverage.Partial, resultContents: ResourceContentState.Defined,
            contentScope: BufferContentScope.CountBoundedPrefix);
        edit.OrderAfter(_residentPropagationPass, _rasterCompletePass);
        edit.OrderAfter(_frameCompletePass, _residentPropagationPass);
    }

    private void UpdateResidentPages(ref RenderGraphFrame graph, uint viewCount)
    {
        var data = new ResidentUniforms
        {
            ViewCount = viewCount,
            EntriesPerView = _shadowMap.Settings.PageTableEntriesPerView,
            EntryCount = checked(viewCount * _shadowMap.Settings.PageTableEntriesPerView),
        };
        _uniformData.Write(ref graph, _residentUniforms, in data);
        graph.SetPassDataIfChanged(_residentPropagationPass,
            new VirtualShadowDispatchPassData(new DispatchArguments(DivideRoundUp(data.EntryCount, 64u), 1, 1)));
        graph.SetPassEnabled(_residentPropagationPass, viewCount != 0u);
    }

    private void BindResidentPages(ref RenderGraphEdit graph, ref PassDefinition pass, PipelineSync sync) =>
        _ = pass.Bind(graph.CreateBufferSrv(_residentPages, structureStride: 8), sync, BufferContentScope.CountBoundedPrefix);
}
