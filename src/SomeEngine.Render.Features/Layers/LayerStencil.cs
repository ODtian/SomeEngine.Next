using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Features.Layers;

/// <summary>The integer stencil image and explicit transfers to hardware stencil attachments.</summary>
public sealed class LayerStencil : IDisposable
{
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly RenderPipelines _pipelines;
    private readonly uint _width, _height, _views;
    private LayerStencilAttachment[] _attachments = [];

    internal LayerStencil(global::SomeEngine.RenderGraph.RenderGraph graph, RenderPipelines pipelines,
        GraphTextureId values, uint width, uint height, uint views)
    {
        (_graph, _pipelines, Values, _width, _height, _views) = (graph, pipelines, values, width, height, views);
    }

    public GraphTextureId Values { get; }

    /// <summary>Exports the integer image after sourceReady using an already loaded transfer shader.</summary>
    public LayerStencilAttachment Export(Shader transferShader, GraphPassId sourceReady, GraphRegionId region = default)
    {
        LinkedComputePipeline packPipeline = LinkedComputePipeline.Create(_pipelines, transferShader, "LayerPackStencil", "Pack layer stencil");
        LinkedComputePipeline unpackPipeline = LinkedComputePipeline.Create(_pipelines, transferShader, "LayerUnpackStencil", "Unpack layer stencil");
        TextureDesc textureDescription = new(TextureDimension.Texture2D, _width, _height, 1, 1, _views, 1,
            Format.D32FloatS8UInt, TextureUsages.DepthStencilAttachment | TextureUsages.CopySource | TextureUsages.CopyDestination,
            label: "Layer hardware stencil attachment");
        TextureCopyFootprint footprint = _graph.Backend.GetTextureCopyFootprint(_graph.Device, textureDescription,
            new(null!, 0, 0, 0, null!, 0, 0, TextureAspects.Stencil, 0, 0, 0, _width, _height, 1));
        uint rowPitch = (footprint.RowPitch + 3u) / 4u * 4u;
        uint placement = Math.Max(1u, _graph.Device.Capabilities.Limits.TextureDataPlacementAlignment);
        uint viewBytes = (rowPitch * _height + placement - 1u) / placement * placement;
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            GraphTextureId texture = edit.CreatePersistentTexture(textureDescription);
            GraphBufferId bytes = edit.CreatePersistentBuffer(new((ulong)viewBytes * _views,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
                "Layer stencil transfer bytes"));
            uint alignment = Math.Max(1u, _graph.Device.Capabilities.Limits.ConstantBufferAlignment);
            GraphBufferId parameters = edit.CreatePersistentBuffer(new((32u + alignment - 1u) / alignment * alignment,
                BufferUsages.Constant, "Layer stencil transfer parameters"), MemoryType.Upload);
            GraphBufferCbvId parametersView = edit.CreateBufferCbv(parameters);
            GraphBufferSrvId bytesSrv = edit.CreateBufferSrv(bytes);
            GraphBufferUavId bytesUav = edit.CreateBufferUav(bytes);
            GraphTextureSrvId stencilSrv = edit.CreateTextureSrv(Values, new(0, 1, 0, _views, TextureAspects.Color),
                Format.R32UInt, TextureViewDimension.Texture2DArray);
            GraphTextureUavId stencilUav = edit.CreateTextureUav(Values, new(0, 1, 0, _views, TextureAspects.Color),
                Format.R32UInt, TextureViewDimension.Texture2DArray);
            var data = new TransferData(texture, bytes, parameters, parametersView, bytesSrv, bytesUav, stencilSrv, stencilUav,
                rowPitch, viewBytes, region, packPipeline, unpackPipeline);
            GraphPassId pack = AddCompute(ref edit, data, packPipeline,
                FlatDispatch((uint)((ulong)viewBytes * _views / 4u)), GraphAccessMode.Write);
            edit.OrderAfter(pack, sourceReady);
            GraphPassId copy = AddCopy(ref edit, data, TransferDirection.ToAttachment);
            edit.OrderAfter(copy, pack);
            var attachment = new LayerStencilAttachment(this, data, pack, copy);
            LayerStencilAttachment[] attachments = [.. _attachments, attachment];
            edit.Commit();
            _attachments = attachments;
            return attachment;
        }
        finally { edit.Dispose(); }
    }

    internal GraphPassId Import(LayerStencilAttachment attachment, GraphPassId hardwareComplete)
    {
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            TransferData data = attachment.Data;
            GraphPassId copy = AddCopy(ref edit, data, TransferDirection.FromAttachment);
            edit.OrderAfter(copy, hardwareComplete);
            GraphPassId unpack = AddCompute(ref edit, data, data.Unpack,
                new((_width + 7u) / 8u, (_height + 7u) / 8u, _views), GraphAccessMode.Read);
            edit.OrderAfter(unpack, copy);
            GraphPassId[] imports = [.. attachment.Imports, copy, unpack];
            edit.Commit();
            attachment.Imports = imports;
            attachment.CompletePass = unpack;
            return unpack;
        }
        finally { edit.Dispose(); }
    }

    internal void Record(ref RenderGraphFrame frame)
    {
        foreach (LayerStencilAttachment attachment in _attachments)
        {
            var parameters = new TransferParameters(_width, _height, _views, attachment.Data.RowPitch, attachment.Data.ViewBytes);
            MemoryMarshal.Write(frame.GetBufferDataWriteSpan(attachment.Data.Parameters), in parameters);
        }
    }

    public void Remove(LayerStencilAttachment attachment)
    {
        RenderGraphEdit edit = _graph.BeginEdit();
        try
        {
            Remove(ref edit, attachment);
            LayerStencilAttachment[] attachments = _attachments.Where(value => !ReferenceEquals(value, attachment)).ToArray();
            edit.Commit();
            _attachments = attachments;
        }
        finally { edit.Dispose(); }
    }

    public void Dispose()
    {
        if (_attachments.Length == 0) return;
        if (!_graph.IsDisposed && !_graph.IsLost)
        {
            RenderGraphEdit edit = _graph.BeginEdit();
            try
            {
                foreach (LayerStencilAttachment attachment in _attachments) Remove(ref edit, attachment);
                edit.Commit();
            }
            finally { edit.Dispose(); }
        }
        _attachments = [];
    }

    private static void Remove(ref RenderGraphEdit edit, LayerStencilAttachment attachment)
    {
        TransferData data = attachment.Data;
        foreach (GraphPassId pass in attachment.Imports) edit.Remove(pass);
        edit.Remove(attachment.Pack);
        edit.Remove(attachment.ExportCopy);
        edit.Remove(data.ParametersView);
        edit.Remove(data.BytesSrv);
        edit.Remove(data.BytesUav);
        edit.Remove(data.StencilSrv);
        edit.Remove(data.StencilUav);
        edit.Remove(data.Parameters);
        edit.Remove(data.Bytes);
        edit.Remove(data.Texture);
    }

    private static GraphPassId AddCompute(ref RenderGraphEdit edit, TransferData data, LinkedComputePipeline pipeline,
        DispatchArguments dispatch, GraphAccessMode byteAccess)
    {
        GraphParameterResourceBinding[] bindings = ShaderEntryBindings.Resolve(pipeline.Shader, pipeline,
            (name, type) => name switch
            {
                "LayerStencilTransfer" => GraphParameterResourceBinding.ConstantBuffer(data.ParametersView, PipelineSync.ComputeShading),
                "LayerStencilRead" => GraphParameterResourceBinding.SampledTexture(data.StencilSrv, PipelineSync.ComputeShading),
                "LayerStencilWrite" => GraphParameterResourceBinding.StorageTexture(data.StencilUav, PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete),
                "LayerStencilBytesRead" => GraphParameterResourceBinding.ReadOnlyBuffer(data.BytesSrv, PipelineSync.ComputeShading),
                "LayerStencilBytesWrite" => GraphParameterResourceBinding.WritableBuffer(data.BytesUav, PipelineSync.ComputeShading,
                    byteAccess, WriteCoverage.Complete),
                _ => null,
            });
        GraphPassId passId = edit.AddComputeFramePass(pipeline.Label, PassQueueSelection.AnyCompatible(), dispatch,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments args) => commands.Dispatch(args), data.Region);
        PassDefinition pass = edit.GetPass(passId);
        pass.SetPipeline(pipeline.Pipeline);
        pass.SetParameterBlock(pipeline.ParameterLayout);
        foreach (GraphParameterResourceBinding binding in bindings) pass.Bind(binding);
        return passId;
    }

    private GraphPassId AddCopy(ref RenderGraphEdit edit, TransferData transfer, TransferDirection direction)
    {
        var data = new CopyData(transfer.Texture, transfer.Bytes, _width, _height, _views, transfer.RowPitch, transfer.ViewBytes, direction);
        return edit.AddCopyFramePass("Transfer layer stencil", PassQueueSelection.AnyCompatible(), data,
            new PassOptions(Culling: PassCullingMode.NeverCull), CopyData.Declare, CopyData.Record, transfer.Region);
    }

    private static DispatchArguments FlatDispatch(uint words) => new((words + 63u) / 64u, 1, 1);
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private readonly record struct TransferParameters(uint Width, uint Height, uint Views, uint RowPitch, uint ViewBytes);
    internal sealed record TransferData(GraphTextureId Texture, GraphBufferId Bytes, GraphBufferId Parameters,
        GraphBufferCbvId ParametersView, GraphBufferSrvId BytesSrv, GraphBufferUavId BytesUav,
        GraphTextureSrvId StencilSrv, GraphTextureUavId StencilUav, uint RowPitch, uint ViewBytes,
        GraphRegionId Region, LinkedComputePipeline Pack, LinkedComputePipeline Unpack);
    private enum TransferDirection { ToAttachment, FromAttachment }
    private readonly record struct CopyData(GraphTextureId Texture, GraphBufferId Bytes, uint Width, uint Height,
        uint Views, uint RowPitch, uint ViewBytes, TransferDirection Direction)
    {
        internal static void Declare(ref PassDefinition pass, ref CopyData data)
        {
            var range = new TextureSubresourceRange(0, 1, 0, data.Views, TextureAspects.Stencil);
            if (data.Direction == TransferDirection.ToAttachment)
            {
                pass.Read(data.Bytes, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                pass.Write(data.Texture, range, PipelineSync.Copy, ResourceAccess.CopyDestination,
                    TextureLayout.CopyDestination, WriteCoverage.Complete);
            }
            else
            {
                pass.Read(data.Texture, range, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                pass.Write(data.Bytes, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Partial);
            }
        }
        internal static void Record(ref CopyPassCommandScope commands, in CopyData data)
        {
            for (uint view = 0; view < data.Views; view++)
            {
                var copy = new BufferTextureCopy(commands.GetBuffer(data.Bytes), (ulong)view * data.ViewBytes,
                    data.RowPitch, data.Height, commands.GetTexture(data.Texture), 0, view, TextureAspects.Stencil,
                    0, 0, 0, data.Width, data.Height, 1);
                if (data.Direction == TransferDirection.ToAttachment) commands.CopyBufferToTexture(copy);
                else commands.CopyTextureToBuffer(copy);
            }
        }
    }
}

/// <summary>A hardware attachment whose export and import are committed by its stencil feature.</summary>
public sealed class LayerStencilAttachment
{
    private readonly LayerStencil _stencil;
    internal LayerStencilAttachment(LayerStencil stencil, LayerStencil.TransferData data, GraphPassId pack, GraphPassId exportCopy)
    {
        _stencil = stencil;
        Data = data;
        Pack = pack;
        ExportCopy = CompletePass = exportCopy;
    }
    public GraphTextureId Texture => Data.Texture;
    public GraphPassId CompletePass { get; internal set; }
    internal LayerStencil.TransferData Data { get; }
    internal GraphPassId Pack { get; }
    internal GraphPassId ExportCopy { get; }
    internal GraphPassId[] Imports { get; set; } = [];
    public GraphPassId Import(GraphPassId hardwareComplete) => _stencil.Import(this, hardwareComplete);
}
