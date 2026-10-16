using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>The pipeline's integer stencil image and explicit transfers to hardware stencil attachments.</summary>
public sealed class ClusterStencil
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly Shader _shader;
    private readonly LinkedComputePipeline _clearPipeline, _packPipeline, _unpackPipeline;
    private readonly Buffer _uniformBuffer;
    private readonly GraphBufferId _uniforms;
    private readonly uint _width, _height, _views, _rowPitch, _viewBytes;
    private readonly GraphRegionId _clearRegion;
    private readonly List<ClusterStencilAttachment> _attachments = [];
    private GraphPassId _sourceReady;
    private ClusterBlendMeasurements? _measurements;

    internal ClusterStencil(IGraphicsBackend backend, Device device, global::SomeEngine.RenderGraph.RenderGraph graph,
        ref RenderGraphEdit edit, int width, int height, uint views, GraphRegionId clearRegion, GraphRegionId region)
    {
        _backend = backend; _device = device; _graph = graph;
        _width = (uint)width; _height = (uint)height; _views = views; Region = region;
        _clearRegion = clearRegion;
        Values = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, _width, _height, 1, 1, views, 1,
            Format.R32UInt, TextureUsages.Sampled | TextureUsages.Storage, label: "Cluster stencil values"));
        var description = CreateAttachmentDescription();
        TextureCopyFootprint footprint = backend.GetTextureCopyFootprint(device, description,
            new(null!, 0, 0, 0, null!, 0, 0, TextureAspects.Stencil, 0, 0, 0, _width, _height, 1));
        _rowPitch = (footprint.RowPitch + 3u) & ~3u;
        uint alignment = Math.Max(512u, device.Capabilities.Limits.TextureDataPlacementAlignment);
        _viewBytes = ((_rowPitch * _height + alignment - 1u) / alignment) * alignment;
        _uniformBuffer = backend.CreateBuffer(device, new(256, BufferUsages.Constant), MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(_uniformBuffer, MapType.Write, BufferRange.Whole))
        {
            Span<uint> values = stackalloc uint[8] { _width, _height, views, _rowPitch, _viewBytes, 0, 0, 0 };
            MemoryMarshal.AsBytes(values).CopyTo(mapped.Bytes);
            mapped.Flush(mapped.Range);
        }
        _uniforms = edit.RegisterExternalBuffer(_uniformBuffer,
            [new(BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.ConstantBuffer, ResourceContentState.Defined)]);
        _shader = CreateShader(device.Capabilities.CompileTarget);
        _clearPipeline = LinkedComputePipeline.Create(backend, device, _shader, "ClearStencil", "Clear Cluster stencil");
        _packPipeline = LinkedComputePipeline.Create(backend, device, _shader, "PackStencil", "Pack Cluster stencil");
        _unpackPipeline = LinkedComputePipeline.Create(backend, device, _shader, "UnpackStencil", "Unpack Cluster stencil");
        ClearPass = AddCompute(ref edit, _clearPipeline, "ClearStencil", default);
        _sourceReady = ClearPass;
    }

    /// <summary>R32UInt, one eight-bit stencil value in each pixel, with one array layer per view.</summary>
    public GraphTextureId Values { get; }
    /// <summary>Retained graph region for custom hardware and following integer-image stages.</summary>
    public GraphRegionId Region { get; }
    internal GraphPassId ClearPass { get; }
    /// <summary>The latest completed integer-image stage in the declared chain.</summary>
    public GraphPassId CompletePass => _attachments.LastOrDefault()?.CompletePass ?? _sourceReady;

    /// <summary>Packs the integer image and copies its stencil plane into a D32FloatS8UInt attachment.</summary>
    /// <remarks>The returned attachment's depth is supplied by the caller's hardware stage.</remarks>
    public ClusterStencilAttachment Export(ref RenderGraphEdit edit)
    {
        TextureDesc description = CreateAttachmentDescription();
        GraphTextureId texture = edit.CreateTexture(description);
        var bytes = new BufferDesc((ulong)_viewBytes * _views,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
            "Cluster stencil transfer bytes");
        GraphBufferId buffer = edit.CreateBuffer(bytes);
        GraphPassId pack = AddCompute(ref edit, _packPipeline, "PackStencil", buffer);
        edit.OrderAfter(pack, CompletePass);
        GraphPassId copy = AddCopy(ref edit, texture, buffer, export: true);
        edit.OrderAfter(copy, pack);
        var attachment = new ClusterStencilAttachment(this, texture, buffer, pack, copy,
            bytes.Size + (ulong)_width * _height * _views * 8u,
            _backend.GetBufferMemoryRequirements(_device, bytes).Size + _backend.GetTextureMemoryRequirements(_device, description).Size);
        _attachments.Add(attachment);
        return attachment;
    }

    internal GraphPassId Import(ref RenderGraphEdit edit, ClusterStencilAttachment attachment, GraphPassId hardwareComplete)
    {
        GraphPassId copy = AddCopy(ref edit, attachment.Texture, attachment.Bytes, export: false);
        edit.OrderAfter(copy, hardwareComplete);
        GraphPassId unpack = AddCompute(ref edit, _unpackPipeline, "UnpackStencil", attachment.Bytes);
        edit.OrderAfter(unpack, copy);
        attachment.ImportCopy = copy;
        attachment.CompletePass = unpack;
        _measurements?.IncludeColorPass(ref edit, unpack, attachment.CapacityBytes, attachment.AllocationBytes);
        return unpack;
    }

    internal void SetSourceReady(ref RenderGraphEdit edit, GraphPassId pass, ClusterBlendMeasurements? measurements)
    {
        _sourceReady = pass;
        foreach (ClusterStencilAttachment attachment in _attachments) edit.OrderAfter(attachment.Pack, pass);
        if (ReferenceEquals(_measurements, measurements)) return;
        _measurements = measurements;
        if (measurements is not null)
            foreach (ClusterStencilAttachment attachment in _attachments)
                if (attachment.ImportCopy != default)
                    measurements.IncludeColorPass(ref edit, attachment.CompletePass, attachment.CapacityBytes, attachment.AllocationBytes);
    }

    internal void Remove(ref RenderGraphEdit edit)
    {
        foreach (ClusterStencilAttachment attachment in _attachments)
        {
            edit.Remove(attachment.Pack); edit.Remove(attachment.ExportCopy);
            if (attachment.ImportCopy != default) { edit.Remove(attachment.ImportCopy); edit.Remove(attachment.CompletePass); }
            edit.Remove(attachment.Bytes); edit.Remove(attachment.Texture);
        }
        edit.Remove(ClearPass); edit.Remove(Values); edit.Remove(_uniforms);
        _graph.RetireAfterSubmittedFrames(_clearPipeline); _graph.RetireAfterSubmittedFrames(_packPipeline);
        _graph.RetireAfterSubmittedFrames(_unpackPipeline); _graph.RetireAfterSubmittedFrames(_uniformBuffer);
    }

    private TextureDesc CreateAttachmentDescription() => new(TextureDimension.Texture2D, _width, _height, 1, 1, _views, 1,
        Format.D32FloatS8UInt, TextureUsages.DepthStencilAttachment | TextureUsages.CopySource | TextureUsages.CopyDestination,
        label: "Cluster hardware stencil attachment");

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
        var resources = ClusterLinkedStageLayout.Create(pipeline.ParameterLayout,
            (category, space, register) => _shader.IsSlangParameterLocationUsed(target, entry, category, space, register));
        foreach (ClusterLinkedStageResource resource in resources.Resources)
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
        return edit.AddCopyFramePass(export ? "Export Cluster stencil" : "Import hardware stencil",
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

    private static Shader CreateShader(SlangCompileTarget compileTarget)
    {
        IGlobalSession global = Shader.CreateSlangGlobalSession();
        TargetDesc target = new() { Format = compileTarget,
            Profile = global.FindProfile(compileTarget == SlangCompileTarget.Dxil ? "sm_6_6" : "spirv_1_5") };
        global.CreateSession(new SessionDesc { Targets = [target], CompilerOptionEntries =
            [new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(0)),
             new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0))] }, out ISession session);
        IModule unit = session.LoadModuleFromSource("cluster_stencil_transfer", "cluster_stencil_transfer.slang",
            Slang.CreateBlob(TransferSource), out ISlangBlob? diagnostics) ?? throw new InvalidDataException(diagnostics?.AsString);
        var entries = new IEntryPoint[3];
        for (int index = 0; index < entries.Length; index++) unit.GetDefinedEntryPoint(index, out entries[index]);
        session.CreateCompositeComponentType([unit, .. entries], out IComponentType combined, out diagnostics);
        if (!combined.Link(out IComponentType linked, out diagnostics).Succeeded) throw new InvalidDataException(diagnostics?.AsString);
        return Shader.CreateFromSlang("Cluster stencil transfer", global, session, [unit], entries, linked, [target], generateLibraryCode: false);
    }

    private const string TransferSource = """
        struct StencilTransferParameters { uint Width, Height, Views, RowPitch; uint ViewBytes, Padding0, Padding1, Padding2; };
        ConstantBuffer<StencilTransferParameters> StencilTransferUniforms;
        Texture2DArray<uint> StencilRead;
        RWTexture2DArray<uint> StencilWrite;
        ByteAddressBuffer StencilBytesRead;
        RWByteAddressBuffer StencilBytesWrite;
        [shader("compute")] [numthreads(64,1,1)]
        void ClearStencil(uint3 pixel : SV_DispatchThreadID)
        {
            if (pixel.x < StencilTransferUniforms.Width) StencilWrite[pixel] = 0u;
        }
        [shader("compute")] [numthreads(64,1,1)]
        void PackStencil(uint3 pixel : SV_DispatchThreadID)
        {
            uint address = pixel.x * 4u;
            if (address >= StencilTransferUniforms.ViewBytes * StencilTransferUniforms.Views) return;
            uint view = address / StencilTransferUniforms.ViewBytes;
            uint offset = address % StencilTransferUniforms.ViewBytes;
            uint first = offset % StencilTransferUniforms.RowPitch;
            uint row = offset / StencilTransferUniforms.RowPitch;
            uint packed = 0u;
            for (uint component = 0u; component < 4u; component++)
                if (row < StencilTransferUniforms.Height && first + component < StencilTransferUniforms.Width)
                    packed |= (StencilRead.Load(int4(first + component, row, view, 0)) & 255u) << (component * 8u);
            StencilBytesWrite.Store(address, packed);
        }
        [shader("compute")] [numthreads(64,1,1)]
        void UnpackStencil(uint3 pixel : SV_DispatchThreadID)
        {
            if (pixel.x >= StencilTransferUniforms.Width) return;
            uint address = pixel.z * StencilTransferUniforms.ViewBytes + pixel.y * StencilTransferUniforms.RowPitch + pixel.x;
            StencilWrite[pixel] = (StencilBytesRead.Load(address & ~3u) >> ((address & 3u) * 8u)) & 255u;
        }
        """;
}

/// <summary>A hardware attachment paired with explicit stencil export and import stages.</summary>
public sealed class ClusterStencilAttachment
{
    private readonly ClusterStencil _stencil;
    internal ClusterStencilAttachment(ClusterStencil stencil, GraphTextureId texture, GraphBufferId bytes,
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
    internal ulong CapacityBytes { get; }
    internal ulong AllocationBytes { get; }
    /// <summary>Copies the attachment stencil plane and unpacks it into the same integer image.</summary>
    public GraphPassId Import(ref RenderGraphEdit edit, GraphPassId hardwareComplete) => _stencil.Import(ref edit, this, hardwareComplete);
}
