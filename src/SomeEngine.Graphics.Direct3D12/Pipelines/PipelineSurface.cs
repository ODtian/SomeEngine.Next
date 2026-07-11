using System.Buffers.Binary;
using Vortice.Direct3D12;
using Vortice.DXGI;
using GraphicsFormat = SomeEngine.Graphics.Format;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    private readonly HandleTable<NativeTextureView> _textureViews;
    private readonly HandleTable<NativeBufferView> _bufferViews;
    private readonly HandleTable<NativeSampler> _samplers;
    private readonly HandleTable<NativeBindGroupLayout> _bindGroupLayouts;
    private readonly HandleTable<NativeBindGroup> _bindGroups;
    private readonly HandleTable<NativeShader> _shaders;
    private readonly HandleTable<NativePipelineLayout> _pipelineLayouts;
    private readonly HandleTable<NativePipeline> _pipelines;

    public TextureViewHandle CreateTextureView(in TextureViewDesc desc)
        => CreateTextureViewCore(desc);

    public BufferViewHandle CreateBufferView(in BufferViewDesc desc) => CreateBufferViewCore(desc);
    public SamplerHandle CreateSampler(in SamplerDesc desc) => CreateSamplerCore(desc);

    public void DestroyTextureView(TextureViewHandle view)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeTextureView native = _textureViews.Get(view.Domain, view.Slot, view.Generation, "texture view");
        if (native.BindingCount != 0) throw new InvalidOperationException("A texture view cannot be destroyed while bind groups reference it.");
        RetirementPoint point = BeginRetirement(native);
        _ = _textureViews.Remove(view.Domain, view.Slot, view.Generation, "texture view");
        native.ReleaseTexture();
        ScheduleRetirement(native, point);
    }

    public void DestroyBufferView(BufferViewHandle view) => DestroyBufferViewCore(view);
    public void DestroySampler(SamplerHandle sampler) => DestroySamplerCore(sampler);
    public BindGroupLayoutHandle CreateBindGroupLayout(ReadOnlySpan<BindingDesc> bindings) => CreateBindGroupLayoutCore(bindings);
    public BindGroupHandle CreateBindGroup(BindGroupLayoutHandle layout, ReadOnlySpan<BindingWrite> writes, string? name = null) =>
        CreateBindGroupCore(layout, writes, name);
    public void DestroyBindGroupLayout(BindGroupLayoutHandle layout) => DestroyBindGroupLayoutCore(layout);
    public void DestroyBindGroup(BindGroupHandle group) => DestroyBindGroupCore(group);

    public ShaderHandle CreateShader(in ShaderDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        if (!desc.Key.IsValid) throw new ArgumentException("A shader requires a non-zero artifact key.", nameof(desc));
        if (desc.Format != ShaderBinaryFormat.Dxil) throw new NotSupportedException("D3D12 accepts DXIL shader artifacts only.");
        if (desc.Stage is not (ShaderStage.Vertex or ShaderStage.Pixel or ShaderStage.Compute))
        {
            throw new ArgumentException("A shader artifact must describe exactly one supported stage.", nameof(desc));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(desc.EntryPoint);

        byte[] bytecode = desc.Bytecode.ToArray();
        DxilProgramInfo program = ValidateDxilContainer(bytecode);
        if (program.Stage != desc.Stage)
        {
            throw new ArgumentException(
                $"Shader descriptor stage {desc.Stage} does not match the DXIL program stage {program.Stage}.",
                nameof(desc));
        }
        if (!program.IsSupportedBy(_native.HighestShaderModel))
        {
            throw new NotSupportedException(
                $"Shader '{desc.Name ?? desc.EntryPoint}' requires Shader Model {program.Major}.{program.Minor}, " +
                $"but adapter '{Info.Name}' supports up to {DxilProgramInfo.Format(_native.HighestShaderModel)}. " +
                "Provide a compatible cooked DXIL artifact; runtime compilation and bytecode rewriting are not supported.");
        }
        ShaderInterface shaderInterface = new(
            desc.Interface.Bindings.ToArray(),
            desc.Interface.PushConstants.ToArray(),
            desc.Interface.LayoutHash);
        NativeShader native = new(desc.Key, desc.Stage, desc.EntryPoint, bytecode, shaderInterface, program);
        HandleKey key = _shaders.Add(native);
        return new ShaderHandle(_domain, key.Slot, key.Generation);
    }

    public PipelineLayoutHandle CreatePipelineLayout(in PipelineLayoutDesc desc)
        => CreatePipelineLayoutCore(desc);

    public PipelineHandle CreateRasterPipeline(in RasterPipelineDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativePipelineLayout layout = _pipelineLayouts.Get(desc.Layout.Domain, desc.Layout.Slot, desc.Layout.Generation, "pipeline layout");
        NativeShader vertexShader = _shaders.Get(desc.VertexShader.Domain, desc.VertexShader.Slot, desc.VertexShader.Generation, "vertex shader");
        NativeShader pixelShader = _shaders.Get(desc.PixelShader.Domain, desc.PixelShader.Slot, desc.PixelShader.Generation, "pixel shader");
        if (vertexShader.Stage != ShaderStage.Vertex) throw new ArgumentException("VertexShader does not reference a vertex-stage artifact.", nameof(desc));
        if (pixelShader.Stage != ShaderStage.Pixel) throw new ArgumentException("PixelShader does not reference a pixel-stage artifact.", nameof(desc));
        ValidateShaderInterface(layout, vertexShader, nameof(desc.VertexShader));
        ValidateShaderInterface(layout, pixelShader, nameof(desc.PixelShader));

        GraphicsFormat[] colorFormats = desc.ColorFormats.ToArray();
        if (colorFormats.Length > 8) throw new ArgumentOutOfRangeException(nameof(desc), "D3D12 supports at most eight simultaneous color attachments.");
        for (int index = 0; index < colorFormats.Length; index++)
        {
            if (colorFormats[index] == GraphicsFormat.Unknown || IsDepthFormat(colorFormats[index]))
            {
                throw new ArgumentException($"Color format {index} is not a color-renderable format.", nameof(desc));
            }
        }
        if (desc.DepthStencilFormat != GraphicsFormat.Unknown && !IsDepthFormat(desc.DepthStencilFormat))
        {
            throw new ArgumentException("DepthStencilFormat is not a depth format.", nameof(desc));
        }
        if (desc.DepthStencilFormat == GraphicsFormat.Unknown && (desc.DepthStencil.DepthEnabled || desc.DepthStencil.DepthWrite))
        {
            throw new ArgumentException("Depth testing or writing requires a depth-stencil format.", nameof(desc));
        }
        if (colorFormats.Length == 0 && desc.DepthStencilFormat == GraphicsFormat.Unknown)
        {
            throw new ArgumentException("A raster pipeline requires at least one color or depth-stencil attachment format.", nameof(desc));
        }
        if (desc.SampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(desc.SampleCount));

        InputLayoutDescription inputLayout = CreateInputLayout(desc.VertexAttributes.Span, desc.VertexBuffers.Span);
        BlendDescription blend = CreateBlendState(desc.BlendAttachments.Span, colorFormats.Length);
        RasterizerDescription rasterizer = new(
            Mappings.CullMode(desc.Rasterizer.Cull),
            Mappings.FillMode(desc.Rasterizer.Fill),
            desc.Rasterizer.FrontFace == FrontFace.CounterClockwise,
            0,
            0f,
            0f,
            desc.Rasterizer.DepthClip,
            desc.SampleCount > 1,
            false,
            0,
            ConservativeRasterizationMode.Off);
        DepthStencilDescription depthStencil = new(
            desc.DepthStencil.DepthEnabled,
            desc.DepthStencil.DepthWrite ? DepthWriteMask.All : DepthWriteMask.Zero,
            Mappings.Comparison(desc.DepthStencil.DepthCompare));

        GraphicsPipelineStateDescription nativeDesc = new()
        {
            RootSignature = layout.RootSignature,
            VertexShader = vertexShader.Bytecode,
            PixelShader = pixelShader.Bytecode,
            BlendState = blend,
            SampleMask = uint.MaxValue,
            RasterizerState = rasterizer,
            DepthStencilState = depthStencil,
            InputLayout = inputLayout,
            PrimitiveTopologyType = Mappings.TopologyType(desc.Topology),
            RenderTargetFormats = colorFormats.Select(Mappings.Format).ToArray(),
            DepthStencilFormat = Mappings.Format(desc.DepthStencilFormat),
            SampleDescription = new SampleDescription(checked((uint)desc.SampleCount), 0),
            Flags = PipelineStateFlags.None,
        };

        ID3D12PipelineState pipelineState;
        try
        {
            pipelineState = _native.Device.CreateGraphicsPipelineState(nativeDesc);
        }
        catch (Exception exception)
        {
            GraphicsDiagnostic[] nativeDiagnostics = _native.DrainDiagnostics();
            foreach (GraphicsDiagnostic diagnostic in nativeDiagnostics) _diagnostics.Enqueue(diagnostic);
            string detail = nativeDiagnostics.Length == 0
                ? "The D3D12 information queue did not report a validation message."
                : string.Join(" | ", nativeDiagnostics.Select(static diagnostic => diagnostic.Message));
            throw new InvalidOperationException($"D3D12 raster pipeline creation failed. {detail}", exception);
        }
        layout.AddPipeline();
        vertexShader.AddPipeline();
        pixelShader.AddPipeline();
        NativeRasterPipeline native = new(
            pipelineState,
            layout,
            vertexShader,
            pixelShader,
            desc.Topology,
            colorFormats,
            desc.DepthStencilFormat,
            desc.DepthStencil,
            desc.SampleCount);
        HandleKey key = _pipelines.Add(native);
        return new PipelineHandle(_domain, key.Slot, key.Generation);
    }

    public PipelineHandle CreateComputePipeline(in ComputePipelineDesc desc) => CreateComputePipelineCore(desc);

    public PipelineMetadata GetPipelineMetadata(PipelineHandle pipeline)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativePipeline native = _pipelines.Get(
            pipeline.Domain,
            pipeline.Slot,
            pipeline.Generation,
            "pipeline");
        return native switch
        {
            NativeRasterPipeline raster => new PipelineMetadata(
                PipelineType.Raster,
                [
                    new PipelineShaderIdentity(raster.VertexShader.Key, raster.VertexShader.Stage),
                    new PipelineShaderIdentity(raster.PixelShader.Key, raster.PixelShader.Stage),
                ]),
            NativeComputePipeline compute => new PipelineMetadata(
                PipelineType.Compute,
                [new PipelineShaderIdentity(compute.Shader.Key, compute.Shader.Stage)]),
            _ => throw new InvalidOperationException("The native pipeline kind is unknown."),
        };
    }

    public void DestroyShader(ShaderHandle shader)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeShader native = _shaders.Get(shader.Domain, shader.Slot, shader.Generation, "shader");
        if (native.PipelineCount != 0) throw new InvalidOperationException("A shader cannot be destroyed while raster pipelines reference it.");
        RetirementPoint point = BeginRetirement(native);
        _ = _shaders.Remove(shader.Domain, shader.Slot, shader.Generation, "shader");
        ScheduleRetirement(native, point);
    }

    public void DestroyPipelineLayout(PipelineLayoutHandle layout)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativePipelineLayout native = _pipelineLayouts.Get(layout.Domain, layout.Slot, layout.Generation, "pipeline layout");
        if (native.PipelineCount != 0) throw new InvalidOperationException("A pipeline layout cannot be destroyed while raster pipelines reference it.");
        RetirementPoint point = BeginRetirement(native);
        _ = _pipelineLayouts.Remove(layout.Domain, layout.Slot, layout.Generation, "pipeline layout");
        native.ReleaseDependencies();
        ScheduleRetirement(native, point);
    }

    public void DestroyPipeline(PipelineHandle pipeline)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativePipeline native = _pipelines.Get(pipeline.Domain, pipeline.Slot, pipeline.Generation, "pipeline");
        RetirementPoint point = BeginRetirement(native);
        _ = _pipelines.Remove(pipeline.Domain, pipeline.Slot, pipeline.Generation, "pipeline");
        native.ReleaseDependencies();
        ScheduleRetirement(native, point);
    }

    internal NativeTextureView GetTextureView(TextureViewHandle handle) =>
        _textureViews.Get(handle.Domain, handle.Slot, handle.Generation, "texture view");

    internal NativeRasterPipeline GetRasterPipeline(PipelineHandle handle) =>
        _pipelines.Get(handle.Domain, handle.Slot, handle.Generation, "pipeline") as NativeRasterPipeline ??
        throw new ArgumentException("The pipeline is not a raster pipeline.", nameof(handle));

    internal NativePipeline GetPipeline(PipelineHandle handle) =>
        _pipelines.Get(handle.Domain, handle.Slot, handle.Generation, "pipeline");

    partial void DisposePipelineState()
    {
        foreach (NativePipeline pipeline in _pipelines.Drain())
        {
            pipeline.ReleaseDependencies();
            pipeline.Dispose();
        }
        foreach (NativeTextureView view in _textureViews.Drain())
        {
            view.ReleaseTexture();
            view.Dispose();
        }
        foreach (NativeBufferView view in _bufferViews.Drain())
        {
            view.ReleaseBuffer();
            view.Dispose();
        }
        foreach (NativeBindGroup group in _bindGroups.Drain())
        {
            group.ReleaseDependencies();
            group.Dispose();
        }
        foreach (NativeSampler sampler in _samplers.Drain()) sampler.Dispose();
        foreach (NativePipelineLayout layout in _pipelineLayouts.Drain())
        {
            layout.ReleaseDependencies();
            layout.Dispose();
        }
        foreach (NativeBindGroupLayout layout in _bindGroupLayouts.Drain()) layout.Dispose();
        foreach (NativeShader shader in _shaders.Drain()) shader.Dispose();
    }

    private static RenderTargetViewDescription CreateRenderTargetViewDescription(
        in TextureDesc texture,
        GraphicsFormat format,
        in ValidatedTextureViewRange range,
        TextureViewDimension dimension)
    {
        RenderTargetViewDescription result = new() { Format = Mappings.Format(format) };
        switch (dimension)
        {
            case TextureViewDimension.Texture1D:
                result.ViewDimension = RenderTargetViewDimension.Texture1D;
                result.Texture1D = new Texture1DRenderTargetView
                {
                    MipSlice = checked((uint)range.Mip),
                };
                break;
            case TextureViewDimension.Texture1DArray:
                result.ViewDimension = RenderTargetViewDimension.Texture1DArray;
                result.Texture1DArray = new Texture1DArrayRenderTargetView
                {
                    MipSlice = checked((uint)range.Mip),
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                };
                break;
            case TextureViewDimension.Texture2D:
                result.ViewDimension = RenderTargetViewDimension.Texture2D;
                result.Texture2D = new Texture2DRenderTargetView
                {
                    MipSlice = checked((uint)range.Mip),
                    PlaneSlice = 0,
                };
                break;
            case TextureViewDimension.Texture2DArray:
                result.ViewDimension = RenderTargetViewDimension.Texture2DArray;
                result.Texture2DArray = new Texture2DArrayRenderTargetView
                {
                    MipSlice = checked((uint)range.Mip),
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                    PlaneSlice = 0,
                };
                break;
            case TextureViewDimension.Texture2DMS:
                result.ViewDimension = RenderTargetViewDimension.Texture2DMultisampled;
                result.Texture2DMS = new Texture2DMultisampledRenderTargetView();
                break;
            case TextureViewDimension.Texture2DMSArray:
                result.ViewDimension = RenderTargetViewDimension.Texture2DMultisampledArray;
                result.Texture2DMSArray = new Texture2DMultisampledArrayRenderTargetView
                {
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                };
                break;
            default:
                throw new ArgumentException($"View dimension {dimension} cannot describe a D3D12 render-target view.", nameof(dimension));
        }
        return result;
    }

    private static DepthStencilViewDescription CreateDepthStencilViewDescription(
        in TextureDesc texture,
        GraphicsFormat format,
        in ValidatedTextureViewRange range,
        TextureViewDimension dimension,
        DepthStencilViewFlags flags)
    {
        DepthStencilViewDescription result = new()
        {
            Format = Mappings.Format(format),
            Flags = flags,
        };
        switch (dimension)
        {
            case TextureViewDimension.Texture1D:
                result.ViewDimension = DepthStencilViewDimension.Texture1D;
                result.Texture1D = new Texture1DDepthStencilView
                {
                    MipSlice = checked((uint)range.Mip),
                };
                break;
            case TextureViewDimension.Texture1DArray:
                result.ViewDimension = DepthStencilViewDimension.Texture1DArray;
                result.Texture1DArray = new Texture1DArrayDepthStencilView
                {
                    MipSlice = checked((uint)range.Mip),
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                };
                break;
            case TextureViewDimension.Texture2D:
                result.ViewDimension = DepthStencilViewDimension.Texture2D;
                result.Texture2D = new Texture2DDepthStencilView
                {
                    MipSlice = checked((uint)range.Mip),
                };
                break;
            case TextureViewDimension.Texture2DArray:
                result.ViewDimension = DepthStencilViewDimension.Texture2DArray;
                result.Texture2DArray = new Texture2DArrayDepthStencilView
                {
                    MipSlice = checked((uint)range.Mip),
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                };
                break;
            case TextureViewDimension.Texture2DMS:
                result.ViewDimension = DepthStencilViewDimension.Texture2DMultisampled;
                result.Texture2DMS = new Texture2DMultisampledDepthStencilView();
                break;
            case TextureViewDimension.Texture2DMSArray:
                result.ViewDimension = DepthStencilViewDimension.Texture2DMultisampledArray;
                result.Texture2DMSArray = new Texture2DMultisampledArrayDepthStencilView
                {
                    FirstArraySlice = checked((uint)range.FirstLayer),
                    ArraySize = checked((uint)range.LayerCount),
                };
                break;
            default:
                throw new ArgumentException($"View dimension {dimension} cannot describe a D3D12 depth-stencil view.", nameof(dimension));
        }
        return result;
    }

    private static uint[] BuildSubresourceList(in TextureDesc texture, in ValidatedTextureViewRange range)
    {
        List<uint> result = [];
        TextureAspect[] orderedAspects = [TextureAspect.Color, TextureAspect.Depth, TextureAspect.Stencil];
        foreach (TextureAspect aspect in orderedAspects)
        {
            if ((range.Aspect & aspect) == 0) continue;
            if (texture.Dimension == TextureDimension.Texture3D)
            {
                result.Add(Device.NativeSubresource(texture, range.Mip, 0, aspect));
                continue;
            }
            for (int index = 0; index < range.LayerCount; index++)
            {
                result.Add(Device.NativeSubresource(texture, range.Mip, range.FirstLayer + index, aspect));
            }
        }
        return result.ToArray();
    }

    private static DxilProgramInfo ValidateDxilContainer(ReadOnlySpan<byte> bytecode)
    {
        if (bytecode.Length < 36 || !bytecode[..4].SequenceEqual("DXBC"u8))
        {
            throw new ArgumentException("DXIL bytecode must be a non-empty DXBC container.", nameof(bytecode));
        }
        uint containerSize = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(24, 4));
        uint chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(28, 4));
        if (containerSize != bytecode.Length || chunkCount == 0 || chunkCount > (bytecode.Length - 32) / 4)
        {
            throw new ArgumentException("The DXIL container header is malformed.", nameof(bytecode));
        }

        DxilProgramInfo? program = null;
        for (uint index = 0; index < chunkCount; index++)
        {
            int directoryOffset = checked(32 + (int)index * 4);
            uint chunkOffsetValue = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(directoryOffset, 4));
            if (chunkOffsetValue > int.MaxValue) throw new ArgumentException("The DXIL chunk directory is malformed.", nameof(bytecode));
            int chunkOffset = (int)chunkOffsetValue;
            if (chunkOffset < 0 || chunkOffset > bytecode.Length - 8) throw new ArgumentException("The DXIL chunk directory is malformed.", nameof(bytecode));
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(chunkOffset + 4, 4));
            if (chunkSize > bytecode.Length - chunkOffset - 8) throw new ArgumentException("A DXIL container chunk is truncated.", nameof(bytecode));
            if (!bytecode.Slice(chunkOffset, 4).SequenceEqual("DXIL"u8)) continue;
            if (program.HasValue) throw new ArgumentException("The shader container contains more than one DXIL program chunk.", nameof(bytecode));
            program = ReadDxilProgram(bytecode.Slice(chunkOffset + 8, checked((int)chunkSize)));
        }
        return program ?? throw new ArgumentException("The shader container does not contain a DXIL program chunk.", nameof(bytecode));
    }

    private static DxilProgramInfo ReadDxilProgram(ReadOnlySpan<byte> program)
    {
        const int headerSize = 24;
        if (program.Length < headerSize) throw new ArgumentException("The DXIL program header is truncated.", nameof(program));

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(program);
        uint sizeInUint32 = BinaryPrimitives.ReadUInt32LittleEndian(program.Slice(4, 4));
        if (sizeInUint32 < headerSize / 4 || sizeInUint32 > (uint)program.Length / 4)
        {
            throw new ArgumentException("The DXIL program size is malformed.", nameof(program));
        }
        ulong declaredSize = checked((ulong)sizeInUint32 * 4);
        if (!program.Slice(8, 4).SequenceEqual("DXIL"u8))
        {
            throw new ArgumentException("The DXIL program bitcode header is malformed.", nameof(program));
        }

        uint bitcodeOffset = BinaryPrimitives.ReadUInt32LittleEndian(program.Slice(16, 4));
        uint bitcodeSize = BinaryPrimitives.ReadUInt32LittleEndian(program.Slice(20, 4));
        ulong bitcodeEnd = checked(8UL + bitcodeOffset + bitcodeSize);
        if (bitcodeOffset < 16 || bitcodeSize == 0 || bitcodeEnd > declaredSize)
        {
            throw new ArgumentException("The DXIL bitcode range is malformed.", nameof(program));
        }

        uint shaderKind = version >> 16;
        uint major = (version & 0xF0) >> 4;
        uint minor = version & 0xF;
        if (major == 0) throw new ArgumentException("The DXIL shader-model version is malformed.", nameof(program));
        ShaderStage stage = shaderKind switch
        {
            0 => ShaderStage.Pixel,
            1 => ShaderStage.Vertex,
            5 => ShaderStage.Compute,
            _ => throw new NotSupportedException($"DXIL shader kind {shaderKind} is not exposed by the current Graphics surface."),
        };
        return new DxilProgramInfo(stage, major, minor);
    }

    private static InputLayoutDescription CreateInputLayout(
        ReadOnlySpan<VertexAttributeDesc> attributes,
        ReadOnlySpan<VertexBufferLayoutDesc> buffers)
    {
        Dictionary<uint, VertexBufferLayoutDesc> layouts = new();
        for (int index = 0; index < buffers.Length; index++)
        {
            VertexBufferLayoutDesc buffer = buffers[index];
            if (buffer.Stride == 0) throw new ArgumentException("Vertex-buffer stride must be non-zero.", nameof(buffers));
            if (buffer.PerInstance && buffer.StepRate == 0) throw new ArgumentException("Per-instance vertex buffers require a non-zero step rate.", nameof(buffers));
            if (!layouts.TryAdd(buffer.Slot, buffer)) throw new ArgumentException($"Vertex-buffer slot {buffer.Slot} is duplicated.", nameof(buffers));
        }

        HashSet<uint> locations = new();
        InputElementDescription[] elements = new InputElementDescription[attributes.Length];
        for (int index = 0; index < attributes.Length; index++)
        {
            VertexAttributeDesc attribute = attributes[index];
            if (!locations.Add(attribute.Location)) throw new ArgumentException($"Vertex location {attribute.Location} is duplicated.", nameof(attributes));
            if (!layouts.TryGetValue(attribute.BufferSlot, out VertexBufferLayoutDesc buffer))
            {
                throw new ArgumentException($"Vertex location {attribute.Location} references undeclared buffer slot {attribute.BufferSlot}.", nameof(attributes));
            }
            uint formatSize = FormatSize(attribute.Format);
            if (attribute.Offset > buffer.Stride || formatSize > buffer.Stride - attribute.Offset)
            {
                throw new ArgumentException($"Vertex location {attribute.Location} exceeds the stride of slot {attribute.BufferSlot}.", nameof(attributes));
            }
            elements[index] = new InputElementDescription(
                "ATTRIBUTE",
                attribute.Location,
                Mappings.Format(attribute.Format),
                attribute.BufferSlot,
                attribute.Offset,
                buffer.PerInstance ? InputClassification.PerInstanceData : InputClassification.PerVertexData,
                buffer.PerInstance ? buffer.StepRate : 0);
        }
        return new InputLayoutDescription(elements);
    }

    private static BlendDescription CreateBlendState(ReadOnlySpan<BlendAttachmentDesc> attachments, int colorCount)
    {
        if (!attachments.IsEmpty && attachments.Length != colorCount)
        {
            throw new ArgumentException("Blend attachment count must match the color-format count.", nameof(attachments));
        }

        BlendDescription result = BlendDescription.Opaque;
        result.IndependentBlendEnable = colorCount > 1;
        for (int index = 0; index < colorCount; index++)
        {
            BlendAttachmentDesc attachment = attachments.IsEmpty
                ? new BlendAttachmentDesc(false, BlendFactor.One, BlendFactor.Zero, BlendOperation.Add, BlendFactor.One, BlendFactor.Zero, BlendOperation.Add, ColorWriteMask.All)
                : attachments[index];
            result.RenderTarget[index] = new RenderTargetBlendDescription(
                attachment.Enabled,
                false,
                Mappings.Blend(attachment.SourceColor),
                Mappings.Blend(attachment.DestinationColor),
                Mappings.BlendOperation(attachment.ColorOperation),
                Mappings.Blend(attachment.SourceAlpha),
                Mappings.Blend(attachment.DestinationAlpha),
                Mappings.BlendOperation(attachment.AlphaOperation),
                LogicOp.Noop,
                (ColorWriteEnable)(byte)attachment.WriteMask);
        }
        return result;
    }

    internal static uint FormatSize(GraphicsFormat format) => format switch
    {
        GraphicsFormat.R8UNorm => 1,
        GraphicsFormat.R8G8UNorm => 2,
        GraphicsFormat.R8G8B8A8UNorm or GraphicsFormat.R8G8B8A8UNormSrgb or GraphicsFormat.B8G8R8A8UNorm => 4,
        GraphicsFormat.R16UInt or GraphicsFormat.R16Float => 2,
        GraphicsFormat.R16G16Float => 4,
        GraphicsFormat.R16G16B16A16Float => 8,
        GraphicsFormat.R32UInt or GraphicsFormat.R32Float => 4,
        GraphicsFormat.R32G32Float => 8,
        GraphicsFormat.R32G32B32Float => 12,
        GraphicsFormat.R32G32B32A32Float => 16,
        GraphicsFormat.D24UNormS8UInt or GraphicsFormat.D32Float => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "The format has no native texel size."),
    };

    private static bool IsDepthFormat(GraphicsFormat format) =>
        format is GraphicsFormat.D24UNormS8UInt or GraphicsFormat.D32Float;
}

internal readonly record struct ValidatedTextureViewRange(
    int FirstMip,
    int MipCount,
    int FirstLayer,
    int LayerCount,
    TextureAspect Aspect)
{
    public ValidatedTextureViewRange(
        int mip,
        int firstLayer,
        int layerCount,
        TextureAspect aspect = TextureAspect.Color)
        : this(mip, 1, firstLayer, layerCount, aspect) { }
    public int Mip => FirstMip;
}

internal sealed class NativeTextureView : NativeDescriptorDependency
{
    private int _textureReleased;

    public NativeTextureView(
        NativeTexture texture,
        GraphicsFormat format,
        ValidatedTextureViewRange range,
        TextureViewDimension dimension,
        TextureViewUsage usage,
        NativeCpuDescriptor? renderTarget,
        NativeCpuDescriptor? shaderResource,
        NativeCpuDescriptor? storage,
        NativeCpuDescriptor?[]? depthStencil,
        uint[] subresources)
    {
        Texture = texture;
        Format = format;
        Range = range;
        Dimension = dimension;
        Usage = usage;
        RenderTarget = renderTarget;
        ShaderResource = shaderResource;
        Storage = storage;
        DepthStencil = depthStencil;
        Subresources = subresources;
    }

    public NativeTexture Texture { get; }
    public GraphicsFormat Format { get; }
    public ValidatedTextureViewRange Range { get; }
    public TextureViewDimension Dimension { get; }
    public TextureViewUsage Usage { get; }
    public NativeCpuDescriptor? RenderTarget { get; }
    public NativeCpuDescriptor? ShaderResource { get; }
    public NativeCpuDescriptor? Storage { get; }
    public NativeCpuDescriptor?[]? DepthStencil { get; }
    public CpuDescriptorHandle Descriptor => RenderTarget?.Handle ??
        throw new InvalidOperationException("The texture view has no render-target descriptor.");
    public uint[] Subresources { get; }
    public int Width => Math.Max(1, Texture.Desc.Width >> Range.Mip);
    public int Height => Math.Max(1, Texture.Desc.Height >> Range.Mip);
    public int SampleCount => Texture.Desc.SampleCount;

    public IEnumerable<uint> EnumerateAttachmentSubresources(TextureAspect aspect)
    {
        if ((Range.Aspect & aspect) == 0 ||
            aspect is not (TextureAspect.Color or TextureAspect.Depth or TextureAspect.Stencil))
        {
            throw new ArgumentException("The requested aspect is not part of this texture view.", nameof(aspect));
        }
        if (Texture.Desc.Dimension == TextureDimension.Texture3D)
        {
            yield return Device.NativeSubresource(Texture.Desc, Range.Mip, 0, aspect);
            yield break;
        }
        for (int layer = 0; layer < Range.LayerCount; layer++)
        {
            yield return Device.NativeSubresource(Texture.Desc, Range.Mip, Range.FirstLayer + layer, aspect);
        }
    }

    public CpuDescriptorHandle GetDepthStencilDescriptor(bool depthReadOnly, bool stencilReadOnly)
    {
        if (DepthStencil is null)
            throw new InvalidOperationException("The texture view has no depth-stencil descriptor.");
        int index = (depthReadOnly ? 1 : 0) | (stencilReadOnly ? 2 : 0);
        NativeCpuDescriptor? descriptor = DepthStencil[index];
        return descriptor?.Handle ??
            throw new ArgumentException("The requested depth-stencil read-only combination is not valid for this format.");
    }

    public void ReleaseTexture()
    {
        if (Interlocked.Exchange(ref _textureReleased, 1) == 0) Texture.RemoveView();
    }

    public NativeCpuDescriptor GetBindingDescriptor(BindingKind kind) => kind switch
    {
        BindingKind.SampledTexture => ShaderResource ??
            throw new ArgumentException("The texture view lacks ShaderResource usage."),
        BindingKind.StorageTexture => Storage ??
            throw new ArgumentException("The texture view lacks Storage usage."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    protected override void DisposeNative()
    {
        if (DepthStencil is not null)
        {
            foreach (NativeCpuDescriptor? descriptor in DepthStencil) descriptor?.Dispose();
        }
        Storage?.Dispose();
        ShaderResource?.Dispose();
        RenderTarget?.Dispose();
    }
}

internal readonly record struct DxilProgramInfo(ShaderStage Stage, uint Major, uint Minor)
{
    public bool IsSupportedBy(ShaderModel highest) => ((Major << 4) | Minor) <= (uint)highest;

    public static string Format(ShaderModel model)
    {
        uint encoded = (uint)model;
        return $"{encoded >> 4}.{encoded & 0xF}";
    }
}

internal sealed class NativeShader : NativeLifetime
{
    private int _pipelines;

    public NativeShader(
        ShaderArtifactKey key,
        ShaderStage stage,
        string entryPoint,
        byte[] bytecode,
        ShaderInterface shaderInterface,
        DxilProgramInfo program)
    {
        Key = key;
        Stage = stage;
        EntryPoint = entryPoint;
        Bytecode = bytecode;
        Interface = shaderInterface;
        Program = program;
    }

    public ShaderArtifactKey Key { get; }
    public ShaderStage Stage { get; }
    public string EntryPoint { get; }
    public byte[] Bytecode { get; }
    public ShaderInterface Interface { get; }
    public DxilProgramInfo Program { get; }
    public int PipelineCount => Volatile.Read(ref _pipelines);

    public void AddPipeline() => Interlocked.Increment(ref _pipelines);
    public void RemovePipeline()
    {
        if (Interlocked.Decrement(ref _pipelines) < 0) throw new InvalidOperationException("Shader pipeline count underflow.");
    }

    protected override void DisposeNative() => Array.Clear(Bytecode);
}

internal sealed class NativePipelineLayout : NativeLifetime
{
    private int _pipelines;
    private int _dependenciesReleased;

    public NativePipelineLayout(
        ID3D12RootSignature rootSignature,
        NativeBindGroupLayout[] groups,
        NativeRootBinding[] bindings,
        NativeRootConstant[] constants)
    {
        RootSignature = rootSignature;
        Groups = groups;
        Bindings = bindings;
        Constants = constants;
    }

    public ID3D12RootSignature RootSignature { get; }
    public NativeBindGroupLayout[] Groups { get; }
    public NativeRootBinding[] Bindings { get; }
    public NativeRootConstant[] Constants { get; }
    public int PipelineCount => Volatile.Read(ref _pipelines);

    public void AddPipeline() => Interlocked.Increment(ref _pipelines);
    public void RemovePipeline()
    {
        if (Interlocked.Decrement(ref _pipelines) < 0) throw new InvalidOperationException("Pipeline-layout child count underflow.");
    }

    public void ReleaseDependencies()
    {
        if (Interlocked.Exchange(ref _dependenciesReleased, 1) != 0) return;
        foreach (NativeBindGroupLayout group in Groups) group.RemoveChild();
    }

    protected override void DisposeNative() => RootSignature.Dispose();
}

internal abstract class NativePipeline : NativeLifetime
{
    protected NativePipeline(ID3D12PipelineState pipelineState, NativePipelineLayout layout)
    {
        PipelineState = pipelineState;
        Layout = layout;
    }

    public ID3D12PipelineState PipelineState { get; }
    public NativePipelineLayout Layout { get; }
    public abstract PipelineType Type { get; }
    public abstract void ReleaseDependencies();
    protected override void DisposeNative() => PipelineState.Dispose();
}

internal sealed class NativeRasterPipeline : NativePipeline
{
    private int _dependenciesReleased;

    public NativeRasterPipeline(
        ID3D12PipelineState pipelineState,
        NativePipelineLayout layout,
        NativeShader vertexShader,
        NativeShader pixelShader,
        PrimitiveTopology topology,
        GraphicsFormat[] colorFormats,
        GraphicsFormat depthStencilFormat,
        DepthStencilDesc depthStencil,
        int sampleCount)
        : base(pipelineState, layout)
    {
        VertexShader = vertexShader;
        PixelShader = pixelShader;
        Topology = topology;
        ColorFormats = colorFormats;
        DepthStencilFormat = depthStencilFormat;
        DepthStencil = depthStencil;
        SampleCount = sampleCount;
    }

    public override PipelineType Type => PipelineType.Raster;
    public NativeShader VertexShader { get; }
    public NativeShader PixelShader { get; }
    public PrimitiveTopology Topology { get; }
    public GraphicsFormat[] ColorFormats { get; }
    public GraphicsFormat DepthStencilFormat { get; }
    public DepthStencilDesc DepthStencil { get; }
    public int SampleCount { get; }

    public override void ReleaseDependencies()
    {
        if (Interlocked.Exchange(ref _dependenciesReleased, 1) != 0) return;
        Layout.RemovePipeline();
        VertexShader.RemovePipeline();
        PixelShader.RemovePipeline();
    }

}

internal sealed class NativeComputePipeline : NativePipeline
{
    private int _dependenciesReleased;

    public NativeComputePipeline(
        ID3D12PipelineState pipelineState,
        NativePipelineLayout layout,
        NativeShader shader)
        : base(pipelineState, layout) => Shader = shader;

    public NativeShader Shader { get; }
    public override PipelineType Type => PipelineType.Compute;

    public override void ReleaseDependencies()
    {
        if (Interlocked.Exchange(ref _dependenciesReleased, 1) != 0) return;
        Layout.RemovePipeline();
        Shader.RemovePipeline();
    }
}

internal readonly record struct NativeRootBinding(
    uint Group,
    uint Binding,
    uint RootParameter,
    DescriptorHeapType HeapType,
    int DescriptorOffset,
    int DescriptorCount);

internal readonly record struct NativeRootConstant(PushConstantRange Range, uint RootParameter);
