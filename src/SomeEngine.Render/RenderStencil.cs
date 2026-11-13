using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render;

/// <summary>The shared integer stencil image and explicit transfers to hardware stencil attachments.</summary>
public sealed class RenderStencil
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly RenderPipelines _pipelines;
    private Shader _shader;
    private readonly LinkedComputePipeline _clearPipeline, _packPipeline, _unpackPipeline;
    private readonly byte[] _uniformBytes;
    private readonly GraphBufferId _uniforms;
    private readonly uint _width, _height, _views, _rowPitch, _viewBytes;
    private readonly GraphRegionId _clearRegion;
    private readonly List<RenderStencilAttachment> _attachments = [];
    private GraphPassId _sourceReady;

    public RenderStencil(IGraphicsBackend backend, Device device, RenderPipelines pipelines, global::SomeEngine.RenderGraph.RenderGraph graph, Shader shader,
        ref RenderGraphEdit edit, int width, int height, uint views, GraphRegionId clearRegion, GraphRegionId region)
    {
        _backend = backend; _device = device; _graph = graph; _pipelines = pipelines;
        _width = (uint)width; _height = (uint)height; _views = views; Region = region;
        _clearRegion = clearRegion;
        Values = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, _width, _height, 1, 1, views, 1,
            Format.R32UInt, TextureUsages.Sampled | TextureUsages.Storage, label: "stencil values"));
        var description = CreateAttachmentDescription();
        TextureCopyFootprint footprint = backend.GetTextureCopyFootprint(device, description,
            new(null!, 0, 0, 0, null!, 0, 0, TextureAspects.Stencil, 0, 0, 0, _width, _height, 1));
        _rowPitch = (footprint.RowPitch + 3u) & ~3u;
        uint alignment = Math.Max(512u, device.Capabilities.Limits.TextureDataPlacementAlignment);
        _viewBytes = ((_rowPitch * _height + alignment - 1u) / alignment) * alignment;
        _uniforms = edit.CreateTransientBuffer(new(256, BufferUsages.Constant), MemoryType.Upload);
        _uniformBytes = new byte[256];
        CapacityBytes = (ulong)_width * _height * _views * 4u + 256u;
        AllocationBytes = backend.GetTextureMemoryRequirements(device,
            new(TextureDimension.Texture2D, _width, _height, 1, 1, views, 1, Format.R32UInt,
                TextureUsages.Sampled | TextureUsages.Storage)).Size +
            backend.GetBufferMemoryRequirements(device, new(256, BufferUsages.Constant), MemoryType.Upload).Size;
        Span<uint> values = stackalloc uint[8] { _width, _height, views, _rowPitch, _viewBytes, 0, 0, 0 };
        MemoryMarshal.AsBytes(values).CopyTo(_uniformBytes);
        _shader = shader;
        _clearPipeline = LinkedComputePipeline.Create(pipelines, _shader, "ClearStencil", "Clear stencil");
        _packPipeline = LinkedComputePipeline.Create(pipelines, _shader, "PackStencil", "Pack stencil");
        _unpackPipeline = LinkedComputePipeline.Create(pipelines, _shader, "UnpackStencil", "Unpack stencil");
        ClearPass = AddCompute(ref edit, _clearPipeline, "ClearStencil", default);
        _sourceReady = ClearPass;
    }

    public void Upload(ref RenderGraphFrame frame) => frame.SetBufferData(_uniforms, _uniformBytes);

    public void ReplaceShader(Shader previous, Shader next)
    {
        if (!ReferenceEquals(_shader, previous)) return;
        var replacements = new Dictionary<Pipeline, LinkedComputePipeline>
        {
            [_clearPipeline.Pipeline] = _clearPipeline,
            [_packPipeline.Pipeline] = _packPipeline,
            [_unpackPipeline.Pipeline] = _unpackPipeline,
        };
        _clearPipeline.ReplaceShader(_pipelines, previous, next);
        _packPipeline.ReplaceShader(_pipelines, previous, next);
        _unpackPipeline.ReplaceShader(_pipelines, previous, next);
        using RenderGraphEdit edit = _graph.BeginEdit();
        foreach (GraphPassId id in edit.GetPasses(_clearRegion).Concat(edit.GetPasses(Region)).Distinct())
        {
            PassDefinition pass = edit.GetPass(id);
            if (pass.Pipeline is not { } pipeline || !replacements.TryGetValue(pipeline, out LinkedComputePipeline? replacement)) continue;
            pass.SetPipeline(replacement.Pipeline);
            pass.SetParameterBlock(replacement.ParameterLayout);
        }
        edit.Commit();
        _shader = next;
    }

    /// <summary>R32UInt, one eight-bit stencil value in each pixel, with one array layer per view.</summary>
    public GraphTextureId Values { get; }
    public IReadOnlyList<RenderStencilAttachment> Attachments => _attachments;
    /// <summary>Retained graph region for custom hardware and following integer-image stages.</summary>
    public GraphRegionId Region { get; }
    public GraphPassId ClearPass { get; }
    public ulong CapacityBytes { get; }
    public ulong AllocationBytes { get; }
    /// <summary>The latest completed integer-image stage in the declared chain.</summary>
    public GraphPassId CompletePass => _attachments.LastOrDefault()?.CompletePass ?? _sourceReady;

    /// <summary>Packs the integer image and copies its stencil plane into a D32FloatS8UInt attachment.</summary>
    /// <remarks>The returned attachment's depth is supplied by the caller's hardware stage.</remarks>
    public RenderStencilAttachment Export(ref RenderGraphEdit edit)
    {
        TextureDesc description = CreateAttachmentDescription();
        GraphTextureId texture = edit.CreateTexture(description);
        var bytes = new BufferDesc((ulong)_viewBytes * _views,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
            "stencil transfer bytes");
        GraphBufferId buffer = edit.CreateBuffer(bytes);
        GraphPassId pack = AddCompute(ref edit, _packPipeline, "PackStencil", buffer);
        edit.OrderAfter(pack, CompletePass);
        GraphPassId copy = AddCopy(ref edit, texture, buffer, export: true);
        edit.OrderAfter(copy, pack);
        var attachment = new RenderStencilAttachment(this, texture, buffer, pack, copy,
            bytes.Size + (ulong)_width * _height * _views * 8u,
            _backend.GetBufferMemoryRequirements(_device, bytes).Size + _backend.GetTextureMemoryRequirements(_device, description).Size);
        _attachments.Add(attachment);
        return attachment;
    }

    internal GraphPassId Import(ref RenderGraphEdit edit, RenderStencilAttachment attachment, GraphPassId hardwareComplete)
    {
        GraphPassId copy = AddCopy(ref edit, attachment.Texture, attachment.Bytes, export: false);
        edit.OrderAfter(copy, hardwareComplete);
        GraphPassId unpack = AddCompute(ref edit, _unpackPipeline, "UnpackStencil", attachment.Bytes);
        edit.OrderAfter(unpack, copy);
        attachment.ImportCopy = copy;
        attachment.CompletePass = unpack;
        return unpack;
    }

    public void SetSourceReady(ref RenderGraphEdit edit, GraphPassId pass)
    {
        _sourceReady = pass;
        foreach (RenderStencilAttachment attachment in _attachments) edit.OrderAfter(attachment.Pack, pass);
    }

    public void Remove(ref RenderGraphEdit edit)
    {
        foreach (RenderStencilAttachment attachment in _attachments)
        {
            edit.Remove(attachment.Pack); edit.Remove(attachment.ExportCopy);
            if (attachment.ImportCopy != default) { edit.Remove(attachment.ImportCopy); edit.Remove(attachment.CompletePass); }
            edit.Remove(attachment.Bytes); edit.Remove(attachment.Texture);
        }
        edit.Remove(ClearPass); edit.Remove(Values); edit.Remove(_uniforms);
        _graph.RetireAfterSubmittedFrames(_clearPipeline); _graph.RetireAfterSubmittedFrames(_packPipeline);
        _graph.RetireAfterSubmittedFrames(_unpackPipeline);
    }

    private TextureDesc CreateAttachmentDescription() => new(TextureDimension.Texture2D, _width, _height, 1, 1, _views, 1,
        Format.D32FloatS8UInt, TextureUsages.DepthStencilAttachment | TextureUsages.CopySource | TextureUsages.CopyDestination,
        label: "hardware stencil attachment");

    private GraphPassId AddCompute(ref RenderGraphEdit edit, LinkedComputePipeline pipeline, string entry, GraphBufferId bytes)
    {
        var dispatch = entry == "PackStencil"
            ? new DispatchArguments((_viewBytes / 4u * _views + 63u) / 64u, 1, 1)
            : new DispatchArguments((_width + 63u) / 64u, _height, _views);
        GraphPassId id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyOfType(QueueType.Graphics), dispatch,
            new PassOptions(RepeatCommands: true), pipeline.Pipeline, pipeline.ParameterLayout, default,
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments groups) => commands.Dispatch(groups),
            region: entry == "ClearStencil" ? _clearRegion : Region);
        PassDefinition pass = edit.GetPass(id);
        pass.BindParameterBlock(pipeline.ParameterLayout);
        int target = _shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        var resources = ShaderParameterResources.Create(pipeline.ParameterLayout,
            (category, space, register) => _shader.IsSlangParameterLocationUsed(target, entry, category, space, register));
        foreach (ShaderParameterResource resource in resources.Resources)
        {
            if (!resource.IsUsed) { pass.Bind(GraphParameterResourceBinding.Null(resource.Type)); continue; }
            switch (resource.Name)
            {
                case "StencilTransferUniforms": pass.Bind(edit.CreateBufferCbv(_uniforms), PipelineSync.ComputeShading); break;
                case "StencilRead": pass.Bind(edit.CreateTextureSrv(Values, format: Format.R32UInt,
                    dimension: TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading); break;
                case "StencilWrite": pass.Bind(edit.CreateTextureUav(Values, format: Format.R32UInt,
                    dimension: TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete); break;
                case "StencilBytesRead": pass.Bind(edit.CreateBufferSrv(bytes), PipelineSync.ComputeShading); break;
                case "StencilBytesWrite": pass.Bind(edit.CreateBufferUav(bytes), PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete); break;
            }
        }
        return id;
    }

    private GraphPassId AddCopy(ref RenderGraphEdit edit, GraphTextureId texture, GraphBufferId buffer, bool export)
    {
        var data = new StencilCopy(texture, buffer, _width, _height, _views, _rowPitch, _viewBytes, export);
        return edit.AddCopyFramePass(export ? "Export stencil" : "Import hardware stencil",
            PassQueueSelection.AnyOfType(QueueType.Graphics), data, new PassOptions(RepeatCommands: true),
            StencilCopy.Declare, StencilCopy.Record, region: Region);
    }

    private readonly record struct StencilCopy(GraphTextureId Texture, GraphBufferId Buffer,
        uint Width, uint Height, uint Views, uint RowPitch, uint ViewBytes, bool Export)
    {
        internal static void Declare(ref PassDefinition pass, ref StencilCopy data)
        {
            var range = new TextureSubresourceRange(0, 1, 0, data.Views, TextureAspects.Stencil);
            if (data.Export)
            {
                _ = pass.Read(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                _ = pass.Write(data.Texture, range, PipelineSync.Copy, ResourceAccess.CopyDestination,
                    TextureLayout.CopyDestination, WriteCoverage.Complete);
            }
            else
            {
                _ = pass.Read(data.Texture, range, PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                _ = pass.Write(data.Buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Partial);
            }
        }
        internal static void Record(ref CopyPassCommandScope commands, in StencilCopy data)
        {
            for (uint view = 0; view < data.Views; view++)
            {
                var copy = new BufferTextureCopy(commands.GetBuffer(data.Buffer), (ulong)view * data.ViewBytes,
                    data.RowPitch, data.Height, commands.GetTexture(data.Texture), 0, view, TextureAspects.Stencil,
                    0, 0, 0, data.Width, data.Height, 1);
                if (data.Export) commands.CopyBufferToTexture(copy); else commands.CopyTextureToBuffer(copy);
            }
        }
    }


}

/// <summary>A hardware attachment paired with explicit stencil export and import stages.</summary>
public sealed class RenderStencilAttachment
{
    private readonly RenderStencil _stencil;
    internal RenderStencilAttachment(RenderStencil stencil, GraphTextureId texture, GraphBufferId bytes,
        GraphPassId pack, GraphPassId exportCopy, ulong capacityBytes, ulong allocationBytes)
    {
        _stencil = stencil; Texture = texture; Bytes = bytes; Pack = pack; ExportCopy = exportCopy;
        CompletePass = exportCopy; CapacityBytes = capacityBytes; AllocationBytes = allocationBytes;
    }
    public GraphTextureId Texture { get; }
    /// <summary>Export completion before hardware use, then import completion after Import is declared.</summary>
    public GraphPassId CompletePass { get; internal set; }
    internal GraphBufferId Bytes { get; }
    internal GraphPassId Pack { get; }
    internal GraphPassId ExportCopy { get; }
    internal GraphPassId ImportCopy { get; set; }
    public ulong CapacityBytes { get; }
    public ulong AllocationBytes { get; }
    /// <summary>Copies the attachment stencil plane and unpacks it into the same integer image.</summary>
    public GraphPassId Import(ref RenderGraphEdit edit, GraphPassId hardwareComplete) => _stencil.Import(ref edit, this, hardwareComplete);
}
