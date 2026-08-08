using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using NativeBlend = Silk.NET.Direct3D12.Blend;
using NativeBlendOp = Silk.NET.Direct3D12.BlendOp;
using NativeFormat = Silk.NET.DXGI.Format;
using NativeSampleDesc = Silk.NET.DXGI.SampleDesc;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private static readonly byte[] AttributeSemantic = "ATTRIBUTE\0"u8.ToArray();

    public Pipeline CreateGraphicsPipeline(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        ValidateGraphicsDescription(desc);

        ShaderReflection reflection = GetProgramReflection(desc.Program);
        CompiledShader vertex = CompileShader(
            desc.Program,
            reflection,
            desc.Vertex,
            SlangStage.Vertex,
            "vertex");
        CompiledShader pixel = CompileShader(
            desc.Program,
            reflection,
            desc.Pixel,
            SlangStage.Fragment,
            "pixel");
        ValidateVertexLocations(desc.Vertex, desc.VertexAttributes);

        EntryPointReflection[] entries = [desc.Vertex, desc.Pixel];
        D3D12RootLayout root = D3D12RootLayoutBuilder.Compile(
            this,
            nativeDevice,
            desc.Program,
            reflection,
            entries,
            PipelineType.Graphics,
            allowInputAssembler: true,
            allowStreamOutput: desc.HasStreamOutput);

        byte[] key = CreateGraphicsPipelineKey(nativeDevice, root, vertex, pixel, desc);
        ID3D12PipelineState* pipelineState = null;
        D3D12ClassicPipeline? result = null;
        nint[] allocatedSemantics = [];
        try
        {
            InputElementDesc[] inputElements = CreateInputElements(
                desc.VertexBuffers,
                desc.VertexAttributes);
            SODeclarationEntry[] outputElements = [];
            uint[] outputStrides = [];
            if (desc.HasStreamOutput)
            {
                CreateStreamOutput(
                    desc.StreamOutput,
                    out outputElements,
                    out outputStrides,
                    out allocatedSemantics);
            }

            BlendDesc blend = CreateBlend(desc.Blend, desc.Attachments.ColorFormats.Length);
            RasterizerDesc rasterizer = CreateRasterizer(desc.Rasterizer, desc.Multisample);
            DepthStencilDesc depthStencil = CreateDepthStencil(desc.DepthStencil);
            NativeFormat[] colorFormats = CreateColorFormats(desc.Attachments.ColorFormats);
            byte[]? cachedData = TryGetCachedData(nativeCache, 1, key);

            fixed (byte* vertexCode = vertex.Code)
            fixed (byte* pixelCode = pixel.Code)
            fixed (byte* semantic = AttributeSemantic)
            fixed (InputElementDesc* inputPointer = inputElements)
            fixed (SODeclarationEntry* outputPointer = outputElements)
            fixed (uint* stridePointer = outputStrides)
            fixed (byte* cachedPointer = cachedData)
            {
                for (int index = 0; index < inputElements.Length; index++)
                    inputElements[index].SemanticName = semantic;

                GraphicsPipelineStateDesc native = new()
                {
                    PRootSignature = root.Native,
                    VS = new ShaderBytecode(vertexCode, (nuint)vertex.Code.Length),
                    PS = new ShaderBytecode(pixelCode, (nuint)pixel.Code.Length),
                    BlendState = blend,
                    SampleMask = desc.Multisample.SampleMask,
                    RasterizerState = rasterizer,
                    DepthStencilState = depthStencil,
                    InputLayout = new InputLayoutDesc(
                        inputPointer,
                        checked((uint)inputElements.Length)),
                    IBStripCutValue = ToNativeStripCut(desc.StripCut),
                    PrimitiveTopologyType = ToNativeTopologyType(desc.Topology),
                    NumRenderTargets = checked((uint)colorFormats.Length),
                    DSVFormat = desc.Attachments.DepthStencilFormat is Format depthFormat
                        ? FormatMappings.ToDxgi(depthFormat)
                        : NativeFormat.FormatUnknown,
                    SampleDesc = new NativeSampleDesc(desc.Multisample.SampleCount, 0),
                    NodeMask = nativeDevice.EnabledNodeMask,
                    CachedPSO = cachedData is null
                        ? default
                        : new CachedPipelineState(cachedPointer, (nuint)cachedData.Length),
                    Flags = PipelineStateFlags.None,
                };
                for (int index = 0; index < colorFormats.Length; index++)
                    native.RTVFormats[index] = colorFormats[index];
                if (desc.HasStreamOutput)
                {
                    native.StreamOutput = new StreamOutputDesc(
                        outputPointer,
                        checked((uint)outputElements.Length),
                        stridePointer,
                        checked((uint)outputStrides.Length),
                        desc.StreamOutput.RasterizedStreamIndex ?? uint.MaxValue);
                }

                Guid iid = ID3D12PipelineState.Guid;
                NativeCall.ThrowIfFailed(
                    nativeDevice.Native->CreateGraphicsPipelineState(
                        &native,
                        &iid,
                        (void**)&pipelineState),
                    "ID3D12Device::CreateGraphicsPipelineState");
            }

            StoreCachedData(nativeCache, 1, key, pipelineState);
            result = new D3D12ClassicPipeline(
                nativeDevice,
                pipelineState,
                root,
                PipelineType.Graphics,
                ToPipelineSignature(key),
                desc.Topology,
                desc.StripCut,
                desc.DynamicStates,
                desc.Label);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
            {
                if (pipelineState is not null)
                    _ = pipelineState->Release();
                root.Release();
            }
            else
            {
                result.Dispose();
            }
            throw;
        }
        finally
        {
            foreach (nint semantic in allocatedSemantics)
            {
                if (semantic != 0)
                    Marshal.FreeCoTaskMem(semantic);
            }
        }
    }

    public Pipeline CreateComputePipeline(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        ArgumentNullException.ThrowIfNull(desc.Program);

        ShaderReflection reflection = GetProgramReflection(desc.Program);
        CompiledShader compute = CompileShader(
            desc.Program,
            reflection,
            desc.Compute,
            SlangStage.Compute,
            "compute");
        EntryPointReflection[] entries = [desc.Compute];
        D3D12RootLayout root = D3D12RootLayoutBuilder.Compile(
            this,
            nativeDevice,
            desc.Program,
            reflection,
            entries,
            PipelineType.Compute,
            allowInputAssembler: false,
            allowStreamOutput: false);
        byte[] key = CreateComputePipelineKey(nativeDevice, root, compute);
        ID3D12PipelineState* pipelineState = null;
        D3D12ClassicPipeline? result = null;
        try
        {
            byte[]? cachedData = TryGetCachedData(nativeCache, 2, key);
            fixed (byte* shaderCode = compute.Code)
            fixed (byte* cachedPointer = cachedData)
            {
                ComputePipelineStateDesc native = new(
                    root.Native,
                    new ShaderBytecode(shaderCode, (nuint)compute.Code.Length),
                    nativeDevice.EnabledNodeMask,
                    cachedData is null
                        ? default
                        : new CachedPipelineState(cachedPointer, (nuint)cachedData.Length),
                    PipelineStateFlags.None);
                Guid iid = ID3D12PipelineState.Guid;
                NativeCall.ThrowIfFailed(
                    nativeDevice.Native->CreateComputePipelineState(
                        &native,
                        &iid,
                        (void**)&pipelineState),
                    "ID3D12Device::CreateComputePipelineState");
            }

            StoreCachedData(nativeCache, 2, key, pipelineState);
            result = new D3D12ClassicPipeline(
                nativeDevice,
                pipelineState,
                root,
                PipelineType.Compute,
                ToPipelineSignature(key),
                default,
                default,
                DynamicStates.None,
                desc.Label);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
            {
                if (pipelineState is not null)
                    _ = pipelineState->Release();
                root.Release();
            }
            else
            {
                result.Dispose();
            }
            throw;
        }
    }

    public Pipeline CreateMeshPipeline(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        MeshShaders meshCapability =
            nativeDevice.RequireCapability<MeshShaders>(nameof(CreateMeshPipeline));
        if (desc.Amplification != EntryPointReflection.Null &&
            !meshCapability.AmplificationShaders)
        {
            throw new NotSupportedException(
                "The Device does not support amplification shaders.");
        }
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        ValidateMeshDescription(desc);

        ShaderReflection reflection = GetProgramReflection(desc.Program);
        CompiledShader mesh = CompileShader(
            desc.Program,
            reflection,
            desc.Mesh,
            SlangStage.Mesh,
            "mesh");
        CompiledShader? amplification = desc.Amplification == EntryPointReflection.Null
            ? null
            : CompileShader(
                desc.Program,
                reflection,
                desc.Amplification,
                SlangStage.Amplification,
                "amplification");
        CompiledShader? pixel = desc.Pixel == EntryPointReflection.Null
            ? null
            : CompileShader(
                desc.Program,
                reflection,
                desc.Pixel,
                SlangStage.Fragment,
                "pixel");

        List<EntryPointReflection> entryList = [desc.Mesh];
        if (desc.Amplification != EntryPointReflection.Null)
            entryList.Add(desc.Amplification);
        if (desc.Pixel != EntryPointReflection.Null)
            entryList.Add(desc.Pixel);
        D3D12RootLayout root = D3D12RootLayoutBuilder.Compile(
            this,
            nativeDevice,
            desc.Program,
            reflection,
            CollectionsMarshal.AsSpan(entryList),
            PipelineType.Mesh,
            allowInputAssembler: false,
            allowStreamOutput: false);
        byte[] key = CreateMeshPipelineKey(
            nativeDevice,
            root,
            mesh,
            amplification,
            pixel,
            desc);
        ID3D12PipelineState* pipelineState = null;
        D3D12ClassicPipeline? result = null;
        try
        {
            BlendDesc blend = CreateBlend(desc.Blend, desc.Attachments.ColorFormats.Length);
            RasterizerDesc rasterizer = CreateRasterizer(desc.Rasterizer, desc.Multisample);
            DepthStencilDesc depthStencil = CreateDepthStencil(desc.DepthStencil);
            NativeFormat[] colorFormats = CreateColorFormats(desc.Attachments.ColorFormats);
            RTFormatArray renderTargets = default;
            renderTargets.NumRenderTargets = checked((uint)colorFormats.Length);
            for (int index = 0; index < colorFormats.Length; index++)
                renderTargets.RTFormats[index] = colorFormats[index];
            byte[]? cachedData = TryGetCachedData(nativeCache, 3, key);

            fixed (byte* meshCode = mesh.Code)
            fixed (byte* amplificationCode = amplification?.Code)
            fixed (byte* pixelCode = pixel?.Code)
            fixed (byte* cachedPointer = cachedData)
            {
                MeshPipelineStream stream = new(
                    root.Native,
                    amplification is null
                        ? default
                        : new ShaderBytecode(
                            amplificationCode,
                            (nuint)amplification.Code.Length),
                    new ShaderBytecode(meshCode, (nuint)mesh.Code.Length),
                    pixel is null
                        ? default
                        : new ShaderBytecode(pixelCode, (nuint)pixel.Code.Length),
                    blend,
                    desc.Multisample.SampleMask,
                    rasterizer,
                    depthStencil,
                    PrimitiveTopologyType.Triangle,
                    renderTargets,
                    desc.Attachments.DepthStencilFormat is Format depthFormat
                        ? FormatMappings.ToDxgi(depthFormat)
                        : NativeFormat.FormatUnknown,
                    new NativeSampleDesc(desc.Multisample.SampleCount, 0),
                    nativeDevice.EnabledNodeMask,
                    cachedData is null
                        ? default
                        : new CachedPipelineState(cachedPointer, (nuint)cachedData.Length),
                    PipelineStateFlags.None);
                PipelineStateStreamDesc native = new((nuint)sizeof(MeshPipelineStream), &stream);
                Guid iid = ID3D12PipelineState.Guid;
                int createResult = nativeDevice.Native->CreatePipelineState(
                    &native,
                    &iid,
                    (void**)&pipelineState);
                ThrowIfDeviceFailed(
                    nativeDevice,
                    createResult,
                    "ID3D12Device2::CreatePipelineState(mesh)");
            }

            StoreCachedData(nativeCache, 3, key, pipelineState);
            result = new D3D12ClassicPipeline(
                nativeDevice,
                pipelineState,
                root,
                PipelineType.Mesh,
                ToPipelineSignature(key),
                PrimitiveTopology.TriangleList,
                StripCut.Disabled,
                desc.DynamicStates,
                desc.Label);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
            {
                if (pipelineState is not null)
                    _ = pipelineState->Release();
                root.Release();
            }
            else
            {
                result.Dispose();
            }
            throw;
        }
    }

    private static D3D12PipelineCache? GetPipelineCache(
        D3D12Device device,
        PipelineCache? cache)
    {
        if (cache is null)
            return null;
        return NativeCast.PipelineCache(cache);
    }

    private static ShaderReflection GetProgramReflection(IComponentType program)
    {
        ArgumentNullException.ThrowIfNull(program);
        ISlangBlob? diagnostics = null;
        try
        {
            ShaderReflection reflection = program.GetLayout(0, out diagnostics);
            if (reflection == ShaderReflection.Null)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    FormatSlangFailure("Slang program layout materialization failed", diagnostics));
            }
            if (program.GetSpecializationParamCount() != 0)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "Pipeline creation requires a fully specialized Slang program.");
            }
            return reflection;
        }
        finally
        {
            ReleaseSlang(diagnostics);
        }
    }

    private static CompiledShader CompileShader(
        IComponentType program,
        ShaderReflection reflection,
        EntryPointReflection entryPoint,
        SlangStage expectedStage,
        string role)
    {
        if (entryPoint == EntryPointReflection.Null)
            throw new ArgumentException($"The {role} entry point is null.", nameof(entryPoint));
        if (entryPoint.Stage != expectedStage)
        {
            throw new ArgumentException(
                $"The selected {role} entry point has Slang stage {entryPoint.Stage}.",
                nameof(entryPoint));
        }

        int selectedIndex = -1;
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            if (reflection.GetEntryPointByIndex(index) == entryPoint)
            {
                selectedIndex = checked((int)index);
                break;
            }
        }
        if (selectedIndex < 0)
        {
            throw new ArgumentException(
                $"The {role} entry-point reflection does not belong to the supplied linked program.",
                nameof(entryPoint));
        }

        ISlangBlob? code = null;
        ISlangBlob? diagnostics = null;
        try
        {
            SlangResult result = program.GetEntryPointCode(
                selectedIndex,
                0,
                out code!,
                out diagnostics);
            if (result.Failed || code is null || code.GetBufferPointer() is null ||
                code.GetBufferSize() == 0)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    FormatSlangFailure($"Slang {role} DXIL generation failed", diagnostics),
                    result);
            }
            byte[] bytes = new ReadOnlySpan<byte>(
                (void*)code.GetBufferPointer(),
                checked((int)code.GetBufferSize())).ToArray();
            return new CompiledShader(
                entryPoint,
                bytes,
                SHA256.HashData(bytes),
                GetSlangEntryPointIdentity(program, selectedIndex));
        }
        finally
        {
            ReleaseSlang(code);
            ReleaseSlang(diagnostics);
        }
    }

    private static string FormatSlangFailure(string prefix, ISlangBlob? diagnostics)
    {
        if (diagnostics is null || diagnostics.GetBufferPointer() is null ||
            diagnostics.GetBufferSize() == 0)
            return prefix + ".";
        ReadOnlySpan<byte> bytes = new(
            (void*)diagnostics.GetBufferPointer(),
            checked((int)diagnostics.GetBufferSize()));
        int zero = bytes.IndexOf((byte)0);
        if (zero >= 0)
            bytes = bytes[..zero];
        string detail = System.Text.Encoding.UTF8.GetString(bytes).Trim();
        return string.IsNullOrEmpty(detail) ? prefix + "." : $"{prefix}: {detail}";
    }

    private static void ReleaseSlang(object? value)
    {
        if (value is System.Runtime.InteropServices.Marshalling.ComObject wrapper)
            wrapper.FinalRelease();
    }

    private static void ValidateGraphicsDescription(in GraphicsPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc.Program);
        ValidateOutputState(
            desc.Attachments,
            desc.Multisample,
            desc.DepthStencil,
            desc.Blend);
        const DynamicStates knownDynamicStates =
            DynamicStates.Viewport |
            DynamicStates.Scissor |
            DynamicStates.BlendConstants |
            DynamicStates.StencilReference |
            DynamicStates.DepthBounds |
            DynamicStates.DepthBias |
            DynamicStates.PrimitiveTopology |
            DynamicStates.StripCut;
        if (!Enum.IsDefined(desc.Topology) || !Enum.IsDefined(desc.StripCut) ||
            (desc.DynamicStates & ~knownDynamicStates) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc));

        HashSet<uint> buffers = [];
        foreach (ref readonly VertexBufferLayout buffer in desc.VertexBuffers)
        {
            if (buffer.Stride == 0 || !buffers.Add(buffer.BufferIndex) ||
                (buffer.PerInstance && buffer.InstanceStepRate == 0))
                throw new ArgumentException("The vertex-buffer layout is invalid.", nameof(desc));
        }
        HashSet<uint> locations = [];
        foreach (ref readonly VertexAttribute attribute in desc.VertexAttributes)
        {
            if (!locations.Add(attribute.Location) || !buffers.Contains(attribute.BufferIndex))
                throw new ArgumentException("The vertex-attribute layout is invalid.", nameof(desc));
            VertexBufferLayout buffer = FindVertexBuffer(desc.VertexBuffers, attribute.BufferIndex);
            uint size = FormatMappings.BytesPerElement(attribute.Format);
            if (attribute.Offset > buffer.Stride || size > buffer.Stride - attribute.Offset)
                throw new ArgumentException("A vertex attribute escapes its Buffer stride.", nameof(desc));
        }
    }

    private static void ValidateMeshDescription(in MeshPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc.Program);
        ValidateOutputState(
            desc.Attachments,
            desc.Multisample,
            desc.DepthStencil,
            desc.Blend);
        if (!Enum.IsDefined(desc.DynamicStates))
            throw new ArgumentOutOfRangeException(nameof(desc));
    }

    private static void ValidateOutputState(
        in AttachmentFormatSignature attachments,
        in MultisampleState multisample,
        in DepthStencilState depthStencil,
        in BlendState blend)
    {
        if (attachments.ColorFormats.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(attachments));
        if (multisample.SampleCount == 0 || attachments.SampleCount != multisample.SampleCount)
            throw new ArgumentException("Pipeline sample counts must be non-zero and equal.", nameof(multisample));
        foreach (Format format in attachments.ColorFormats)
        {
            if (FormatMappings.IsDepthStencil(format))
                throw new ArgumentException("A color attachment uses a depth/stencil format.", nameof(attachments));
        }
        if (attachments.DepthStencilFormat is Format depth &&
            !FormatMappings.IsDepthStencil(depth))
            throw new ArgumentException("The depth/stencil attachment format is not depth/stencil.", nameof(attachments));
        if (attachments.DepthStencilFormat is null &&
            (depthStencil.DepthTest || depthStencil.DepthWrite || depthStencil.StencilTest))
            throw new ArgumentException("Depth/stencil state requires a depth/stencil attachment.", nameof(depthStencil));
        if (!blend.Attachments.IsEmpty &&
            blend.Attachments.Length != attachments.ColorFormats.Length)
            throw new ArgumentException("Blend attachment count must equal the color attachment count.", nameof(blend));
    }

    private static VertexBufferLayout FindVertexBuffer(
        ReadOnlySpan<VertexBufferLayout> buffers,
        uint index)
    {
        foreach (ref readonly VertexBufferLayout buffer in buffers)
        {
            if (buffer.BufferIndex == index)
                return buffer;
        }
        throw new ArgumentException("A vertex attribute references an absent Buffer layout.");
    }

    private static void ValidateVertexLocations(
        EntryPointReflection vertex,
        ReadOnlySpan<VertexAttribute> attributes)
    {
        foreach (ref readonly VertexAttribute attribute in attributes)
        {
            if (!ContainsSemantic(vertex, "ATTRIBUTE", attribute.Location))
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    $"Slang vertex reflection has no ATTRIBUTE{attribute.Location} input.");
            }
        }
    }

    private static bool ContainsSemantic(
        EntryPointReflection entry,
        string semantic,
        uint semanticIndex)
    {
        for (uint index = 0; index < entry.ParameterCount; index++)
        {
            if (ContainsSemantic(entry.GetParameterByIndex(index), semantic, semanticIndex))
                return true;
        }
        return false;
    }

    private static bool ContainsSemantic(
        VariableLayoutReflection layout,
        string semantic,
        uint semanticIndex)
    {
        if (layout == VariableLayoutReflection.Null)
            return false;
        if (string.Equals(layout.SemanticName, semantic, StringComparison.OrdinalIgnoreCase) &&
            layout.SemanticIndex == semanticIndex)
            return true;
        TypeLayoutReflection type = layout.TypeLayout.UnwrapArray();
        for (uint index = 0; index < type.FieldCount; index++)
        {
            if (ContainsSemantic(type.GetFieldByIndex(index), semantic, semanticIndex))
                return true;
        }
        return false;
    }

    private static InputElementDesc[] CreateInputElements(
        ReadOnlySpan<VertexBufferLayout> buffers,
        ReadOnlySpan<VertexAttribute> attributes)
    {
        InputElementDesc[] result = new InputElementDesc[attributes.Length];
        for (int index = 0; index < result.Length; index++)
        {
            ref readonly VertexAttribute attribute = ref attributes[index];
            VertexBufferLayout buffer = FindVertexBuffer(buffers, attribute.BufferIndex);
            result[index] = new InputElementDesc(
                null,
                attribute.Location,
                FormatMappings.ToDxgi(attribute.Format),
                attribute.BufferIndex,
                attribute.Offset,
                buffer.PerInstance
                    ? InputClassification.PerInstanceData
                    : InputClassification.PerVertexData,
                buffer.PerInstance ? buffer.InstanceStepRate : 0);
        }
        return result;
    }

    private static void CreateStreamOutput(
        in StreamOutputState state,
        out SODeclarationEntry[] declarations,
        out uint[] strides,
        out nint[] allocatedSemantics)
    {
        if (state.BufferStrides.Length > 4)
            throw new ArgumentOutOfRangeException(nameof(state));
        if (state.RasterizedStreamIndex is uint rasterized && rasterized > 3)
            throw new ArgumentOutOfRangeException(nameof(state));

        declarations = new SODeclarationEntry[state.Elements.Length];
        strides = state.BufferStrides.ToArray();
        List<nint> semantics = [];
        try
        {
            for (int index = 0; index < declarations.Length; index++)
            {
                StreamOutputElement element = state.Elements[index];
                if (element.Stream > 3 || element.ComponentCount == 0 ||
                    element.ComponentCount > 4 ||
                    element.StartComponent + element.ComponentCount > 4 ||
                    element.OutputSlot >= strides.Length)
                    throw new ArgumentException("A stream-output element is invalid.", nameof(state));

                byte* semantic = null;
                uint semanticIndex = 0;
                if (!element.IsGap)
                {
                    if (element.Variable == VariableLayoutReflection.Null ||
                        string.IsNullOrEmpty(element.Variable.SemanticName))
                        throw new ArgumentException("A stream-output variable has no Slang semantic.", nameof(state));
                    nint allocation = Marshal.StringToCoTaskMemUTF8(element.Variable.SemanticName);
                    semantics.Add(allocation);
                    semantic = (byte*)allocation;
                    if (element.Variable.SemanticIndex > uint.MaxValue)
                        throw new ArgumentOutOfRangeException(nameof(state));
                    semanticIndex = checked((uint)element.Variable.SemanticIndex);
                }
                declarations[index] = new SODeclarationEntry(
                    element.Stream,
                    semantic,
                    semanticIndex,
                    element.StartComponent,
                    element.ComponentCount,
                    element.OutputSlot);
            }
            allocatedSemantics = [.. semantics];
        }
        catch
        {
            foreach (nint semantic in semantics)
                Marshal.FreeCoTaskMem(semantic);
            throw;
        }
    }

    private static BlendDesc CreateBlend(in BlendState state, int colorCount)
    {
        BlendDesc result = default;
        result.AlphaToCoverageEnable = state.Attachments.IsEmpty
            ? false
            : false;
        result.IndependentBlendEnable = state.IndependentBlend;
        BlendAttachmentState defaultState = default;
        for (int index = 0; index < 8; index++)
        {
            BlendAttachmentState attachment = index < colorCount && !state.Attachments.IsEmpty
                ? state.Attachments[state.IndependentBlend ? index : 0]
                : defaultState;
            result.RenderTarget[index] = new RenderTargetBlendDesc(
                attachment.Enabled,
                state.LogicOperationEnabled,
                ToNativeBlend(attachment.SourceColor),
                ToNativeBlend(attachment.DestinationColor),
                ToNativeBlendOperation(attachment.ColorOperation),
                ToNativeBlend(attachment.SourceAlpha),
                ToNativeBlend(attachment.DestinationAlpha),
                ToNativeBlendOperation(attachment.AlphaOperation),
                LogicOp.Copy,
                (byte)attachment.WriteMask);
        }
        return result;
    }

    private static RasterizerDesc CreateRasterizer(
        in RasterizerState state,
        in MultisampleState multisample) => new(
            state.Fill == FillType.Solid ? FillMode.Solid : FillMode.Wireframe,
            state.Cull switch
            {
                CullType.None => CullMode.None,
                CullType.Front => CullMode.Front,
                CullType.Back => CullMode.Back,
                _ => throw new ArgumentOutOfRangeException(nameof(state)),
            },
            state.FrontFace == FrontFace.CounterClockwise,
            state.DepthBias,
            state.DepthBiasClamp,
            state.SlopeScaledDepthBias,
            state.DepthClip,
            multisample.SampleCount > 1,
            false,
            0,
            state.ConservativeRasterization
                ? ConservativeRasterizationMode.On
                : ConservativeRasterizationMode.Off);

    private static DepthStencilDesc CreateDepthStencil(in DepthStencilState state) => new(
        state.DepthTest,
        state.DepthWrite ? DepthWriteMask.All : DepthWriteMask.Zero,
        ToNativeComparison(state.DepthComparison),
        state.StencilTest,
        state.StencilReadMask,
        state.StencilWriteMask,
        ToNativeStencilFace(state.Front),
        ToNativeStencilFace(state.Back));

    private static DepthStencilopDesc ToNativeStencilFace(in StencilFaceState state) => new(
        ToNativeStencilOperation(state.Fail),
        ToNativeStencilOperation(state.DepthFail),
        ToNativeStencilOperation(state.Pass),
        ToNativeComparison(state.Comparison));

    private static NativeBlend ToNativeBlend(BlendFactor value) => value switch
    {
        BlendFactor.Zero => NativeBlend.Zero,
        BlendFactor.One => NativeBlend.One,
        BlendFactor.SourceColor => NativeBlend.SrcColor,
        BlendFactor.OneMinusSourceColor => NativeBlend.InvSrcColor,
        BlendFactor.SourceAlpha => NativeBlend.SrcAlpha,
        BlendFactor.OneMinusSourceAlpha => NativeBlend.InvSrcAlpha,
        BlendFactor.DestinationAlpha => NativeBlend.DestAlpha,
        BlendFactor.OneMinusDestinationAlpha => NativeBlend.InvDestAlpha,
        BlendFactor.DestinationColor => NativeBlend.DestColor,
        BlendFactor.OneMinusDestinationColor => NativeBlend.InvDestColor,
        BlendFactor.SourceAlphaSaturate => NativeBlend.SrcAlphaSat,
        BlendFactor.BlendConstant => NativeBlend.BlendFactor,
        BlendFactor.OneMinusBlendConstant => NativeBlend.InvBlendFactor,
        BlendFactor.Source1Color => NativeBlend.Src1Color,
        BlendFactor.OneMinusSource1Color => NativeBlend.InvSrc1Color,
        BlendFactor.Source1Alpha => NativeBlend.Src1Alpha,
        BlendFactor.OneMinusSource1Alpha => NativeBlend.InvSrc1Alpha,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static NativeBlendOp ToNativeBlendOperation(BlendOperation value) => value switch
    {
        BlendOperation.Add => NativeBlendOp.Add,
        BlendOperation.Subtract => NativeBlendOp.Subtract,
        BlendOperation.ReverseSubtract => NativeBlendOp.RevSubtract,
        BlendOperation.Minimum => NativeBlendOp.Min,
        BlendOperation.Maximum => NativeBlendOp.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static StencilOp ToNativeStencilOperation(StencilOperation value) => value switch
    {
        StencilOperation.Keep => StencilOp.Keep,
        StencilOperation.Zero => StencilOp.Zero,
        StencilOperation.Replace => StencilOp.Replace,
        StencilOperation.IncrementClamp => StencilOp.IncrSat,
        StencilOperation.DecrementClamp => StencilOp.DecrSat,
        StencilOperation.Invert => StencilOp.Invert,
        StencilOperation.IncrementWrap => StencilOp.Incr,
        StencilOperation.DecrementWrap => StencilOp.Decr,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static ComparisonFunc ToNativeComparison(CompareOperation value) => value switch
    {
        CompareOperation.Never => ComparisonFunc.Never,
        CompareOperation.Less => ComparisonFunc.Less,
        CompareOperation.Equal => ComparisonFunc.Equal,
        CompareOperation.LessOrEqual => ComparisonFunc.LessEqual,
        CompareOperation.Greater => ComparisonFunc.Greater,
        CompareOperation.NotEqual => ComparisonFunc.NotEqual,
        CompareOperation.GreaterOrEqual => ComparisonFunc.GreaterEqual,
        CompareOperation.Always => ComparisonFunc.Always,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static PrimitiveTopologyType ToNativeTopologyType(PrimitiveTopology value) => value switch
    {
        PrimitiveTopology.PointList => PrimitiveTopologyType.Point,
        PrimitiveTopology.LineList or PrimitiveTopology.LineStrip => PrimitiveTopologyType.Line,
        PrimitiveTopology.TriangleList or PrimitiveTopology.TriangleStrip => PrimitiveTopologyType.Triangle,
        PrimitiveTopology.PatchList => PrimitiveTopologyType.Patch,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static IndexBufferStripCutValue ToNativeStripCut(StripCut value) => value switch
    {
        StripCut.Disabled => IndexBufferStripCutValue.ValueDisabled,
        StripCut.UInt16 => IndexBufferStripCutValue.Value0xFfff,
        StripCut.UInt32 => IndexBufferStripCutValue.Value0xFfffffff,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static NativeFormat[] CreateColorFormats(ReadOnlySpan<Format> formats)
    {
        NativeFormat[] result = new NativeFormat[formats.Length];
        for (int index = 0; index < result.Length; index++)
            result[index] = FormatMappings.ToDxgi(formats[index]);
        return result;
    }

    private static byte[] CreateComputePipelineKey(
        D3D12Device device,
        D3D12RootLayout root,
        CompiledShader compute) =>
        CreateCanonicalPipelineKey(
            device,
            2,
            writer =>
            {
                writer.Write(1u);
                writer.Write(true);
                WriteCompiledShaderIdentity(writer, compute);
            },
            writer => WriteClassicRootLayouts(writer, root),
            static _ => { });

    private static byte[] CreateGraphicsPipelineKey(
        D3D12Device device,
        D3D12RootLayout root,
        CompiledShader vertex,
        CompiledShader pixel,
        in GraphicsPipelineDesc desc)
    {
        GraphicsPipelineKeyData data = GraphicsPipelineKeyData.Capture(desc);
        return CreateCanonicalPipelineKey(
            device,
            1,
            writer =>
            {
                writer.Write(2u);
                writer.Write(true);
                WriteCompiledShaderIdentity(writer, vertex);
                writer.Write(true);
                WriteCompiledShaderIdentity(writer, pixel);
            },
            writer => WriteClassicRootLayouts(writer, root),
            data.Write);
    }

    private static byte[] CreateMeshPipelineKey(
        D3D12Device device,
        D3D12RootLayout root,
        CompiledShader mesh,
        CompiledShader? amplification,
        CompiledShader? pixel,
        in MeshPipelineDesc desc)
    {
        MeshPipelineKeyData data = MeshPipelineKeyData.Capture(desc);
        return CreateCanonicalPipelineKey(
            device,
            3,
            writer =>
            {
                writer.Write(3u);
                writer.Write(true);
                WriteCompiledShaderIdentity(writer, mesh);
                writer.Write(amplification is not null);
                if (amplification is not null)
                    WriteCompiledShaderIdentity(writer, amplification);
                writer.Write(pixel is not null);
                if (pixel is not null)
                    WriteCompiledShaderIdentity(writer, pixel);
            },
            writer => WriteClassicRootLayouts(writer, root),
            data.Write);
    }

    private static void WriteCompiledShaderIdentity(
        BinaryWriter writer,
        CompiledShader shader)
    {
        writer.Write((int)shader.EntryPoint.Stage);
        WriteCanonicalString(writer, GetStableEntryPointName(shader.EntryPoint));
        WriteCanonicalBytes(writer, shader.ProgramIdentity);
        WriteCanonicalBytes(writer, shader.CodeHash);
    }

    private static void WriteClassicRootLayouts(
        BinaryWriter writer,
        D3D12RootLayout root)
    {
        writer.Write(1u);
        WriteCanonicalBytes(writer, root.Serialized);
        writer.Write(0u);
    }

    private static byte[]? TryGetCachedData(
        D3D12PipelineCache? cache,
        byte family,
        ReadOnlySpan<byte> key) =>
        cache is not null && cache.TryGet(family, key, out byte[] data) ? data : null;

    private static void StoreCachedData(
        D3D12PipelineCache? cache,
        byte family,
        ReadOnlySpan<byte> key,
        ID3D12PipelineState* pipeline)
    {
        if (cache is null)
            return;
        ID3D10Blob* blob = null;
        NativeCall.ThrowIfFailed(
            pipeline->GetCachedBlob(&blob),
            "ID3D12PipelineState::GetCachedBlob");
        try
        {
            ReadOnlySpan<byte> bytes = new(
                blob->GetBufferPointer(),
                checked((int)blob->GetBufferSize()));
            cache.Store(family, key, bytes);
        }
        finally
        {
            _ = blob->Release();
        }
    }

    private sealed class CompiledShader
    {
        internal CompiledShader(
            EntryPointReflection entryPoint,
            byte[] code,
            byte[] codeHash,
            byte[] programIdentity)
        {
            EntryPoint = entryPoint;
            Code = code;
            CodeHash = codeHash;
            ProgramIdentity = programIdentity;
        }

        internal EntryPointReflection EntryPoint { get; }
        internal byte[] Code { get; }
        internal byte[] CodeHash { get; }
        internal byte[] ProgramIdentity { get; }
    }

    private abstract class D3D12Pipeline : Pipeline, ID3D12PipelineArtifact
    {
        private readonly D3D12Device _device;
        private readonly D3D12RootLayout _root;
        private readonly NativeLease _native;
        private readonly D3D12RootLayout[] _additionalRoots;
        private int _released;

        protected D3D12Pipeline(
            D3D12Device device,
            IUnknown* native,
            D3D12RootLayout root,
            ReadOnlySpan<D3D12RootLayout> additionalRoots,
            PipelineType type,
            in PipelineSignature signature,
            string? label)
            : base(device, type, signature, label)
        {
            _device = device;
            _root = root;
            _additionalRoots = additionalRoots.ToArray();
            _native = new NativeLease(
                native,
                ownsReference: true,
                root.NativeLifetime);
        }

        internal IUnknown* NativeObject => (IUnknown*)_native.Pointer;
        internal D3D12RootLayout RootLayout => _root;
        ID3D12RootSignature* ID3D12PipelineArtifact.RootSignature => _root.Native;
        NativeLease ID3D12PipelineArtifact.NativeLifetime => _native;

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            ReleaseAdditional();
            _native.Release();
            foreach (D3D12RootLayout root in _additionalRoots)
                root.Release();
            _root.Release();
            _device.UnregisterChild(this);
        }

        protected virtual void ReleaseAdditional()
        {
        }
    }

    private sealed class D3D12ClassicPipeline : D3D12Pipeline
    {
        internal D3D12ClassicPipeline(
            D3D12Device device,
            ID3D12PipelineState* native,
            D3D12RootLayout root,
            PipelineType type,
            in PipelineSignature signature,
            PrimitiveTopology topology,
            StripCut stripCut,
            DynamicStates dynamicStates,
            string? label)
            : base(
                device,
                (IUnknown*)native,
                root,
                ReadOnlySpan<D3D12RootLayout>.Empty,
                type,
                signature,
                label)
        {
            Topology = topology;
            StripCut = stripCut;
            DynamicStates = dynamicStates;
        }

        internal ID3D12PipelineState* Native =>
            (ID3D12PipelineState*)NativeObject;
        internal PrimitiveTopology Topology { get; }
        internal StripCut StripCut { get; }
        internal DynamicStates DynamicStates { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PipelineSubobject<T>
        where T : unmanaged
    {
        internal PipelineSubobject(PipelineStateSubobjectType type, T value)
        {
            Type = type;
            Value = value;
        }

        private readonly PipelineStateSubobjectType Type;
        private readonly T Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MeshPipelineStream
    {
        internal MeshPipelineStream(
            ID3D12RootSignature* rootSignature,
            ShaderBytecode amplification,
            ShaderBytecode mesh,
            ShaderBytecode pixel,
            BlendDesc blend,
            uint sampleMask,
            RasterizerDesc rasterizer,
            DepthStencilDesc depthStencil,
            PrimitiveTopologyType topology,
            RTFormatArray renderTargets,
            NativeFormat depthStencilFormat,
            NativeSampleDesc sampleDescription,
            uint nodeMask,
            CachedPipelineState cached,
            PipelineStateFlags flags)
        {
            RootSignature = new(PipelineStateSubobjectType.RootSignature, (nint)rootSignature);
            AS = new(PipelineStateSubobjectType.As, amplification);
            MS = new(PipelineStateSubobjectType.MS, mesh);
            PS = new(PipelineStateSubobjectType.PS, pixel);
            Blend = new(PipelineStateSubobjectType.Blend, blend);
            SampleMask = new(PipelineStateSubobjectType.SampleMask, sampleMask);
            Rasterizer = new(PipelineStateSubobjectType.Rasterizer, rasterizer);
            DepthStencil = new(PipelineStateSubobjectType.DepthStencil, depthStencil);
            Topology = new(PipelineStateSubobjectType.PrimitiveTopology, topology);
            RenderTargets = new(PipelineStateSubobjectType.RenderTargetFormats, renderTargets);
            DepthStencilFormat = new(
                PipelineStateSubobjectType.DepthStencilFormat,
                depthStencilFormat);
            SampleDescription = new(PipelineStateSubobjectType.SampleDesc, sampleDescription);
            NodeMask = new(PipelineStateSubobjectType.NodeMask, nodeMask);
            Cached = new(PipelineStateSubobjectType.CachedPso, cached);
            Flags = new(PipelineStateSubobjectType.Flags, flags);
        }

        private readonly PipelineSubobject<nint> RootSignature;
        private readonly PipelineSubobject<ShaderBytecode> AS;
        private readonly PipelineSubobject<ShaderBytecode> MS;
        private readonly PipelineSubobject<ShaderBytecode> PS;
        private readonly BlendPipelineSubobject Blend;
        private readonly PipelineSubobject<uint> SampleMask;
        private readonly PipelineSubobject<RasterizerDesc> Rasterizer;
        private readonly PipelineSubobject<DepthStencilDesc> DepthStencil;
        private readonly PipelineSubobject<PrimitiveTopologyType> Topology;
        private readonly PipelineSubobject<RTFormatArray> RenderTargets;
        private readonly PipelineSubobject<NativeFormat> DepthStencilFormat;
        private readonly SampleDescPipelineSubobject SampleDescription;
        private readonly PipelineSubobject<uint> NodeMask;
        private readonly PipelineSubobject<CachedPipelineState> Cached;
        private readonly PipelineSubobject<PipelineStateFlags> Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 336)]
    private readonly struct BlendPipelineSubobject
    {
        internal BlendPipelineSubobject(PipelineStateSubobjectType type, BlendDesc value)
        {
            Type = type;
            Value = value;
        }

        private readonly PipelineStateSubobjectType Type;
        private readonly BlendDesc Value;
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private readonly struct SampleDescPipelineSubobject
    {
        internal SampleDescPipelineSubobject(
            PipelineStateSubobjectType type,
            NativeSampleDesc value)
        {
            Type = type;
            Value = value;
        }

        private readonly PipelineStateSubobjectType Type;
        private readonly NativeSampleDesc Value;
    }

    private sealed class GraphicsPipelineKeyData
    {
        private VertexBufferLayout[] VertexBuffers { get; init; } = [];
        private VertexAttribute[] VertexAttributes { get; init; } = [];
        private BlendAttachmentState[] BlendAttachments { get; init; } = [];
        private Format[] ColorFormats { get; init; } = [];
        private StreamOutputKeyElement[] StreamOutputElements { get; init; } = [];
        private uint[] StreamOutputStrides { get; init; } = [];
        private PrimitiveTopology Topology { get; init; }
        private StripCut StripCut { get; init; }
        private RasterizerState Rasterizer { get; init; }
        private MultisampleState Multisample { get; init; }
        private DepthStencilState DepthStencil { get; init; }
        private bool IndependentBlend { get; init; }
        private bool LogicOperation { get; init; }
        private Format? DepthFormat { get; init; }
        private DynamicStates DynamicStates { get; init; }
        private bool HasStreamOutput { get; init; }
        private uint? RasterizedStream { get; init; }

        internal static GraphicsPipelineKeyData Capture(in GraphicsPipelineDesc desc)
        {
            StreamOutputKeyElement[] elements = desc.HasStreamOutput
                ? desc.StreamOutput.Elements.ToArray()
                    .Select(static value => StreamOutputKeyElement.Capture(value))
                    .ToArray()
                : [];
            return new GraphicsPipelineKeyData
            {
                VertexBuffers = desc.VertexBuffers.ToArray(),
                VertexAttributes = desc.VertexAttributes.ToArray(),
                BlendAttachments = desc.Blend.Attachments.ToArray(),
                ColorFormats = desc.Attachments.ColorFormats.ToArray(),
                StreamOutputElements = elements,
                StreamOutputStrides = desc.HasStreamOutput
                    ? desc.StreamOutput.BufferStrides.ToArray()
                    : [],
                Topology = desc.Topology,
                StripCut = desc.StripCut,
                Rasterizer = desc.Rasterizer,
                Multisample = desc.Multisample,
                DepthStencil = desc.DepthStencil,
                IndependentBlend = desc.Blend.IndependentBlend,
                LogicOperation = desc.Blend.LogicOperationEnabled,
                DepthFormat = desc.Attachments.DepthStencilFormat,
                DynamicStates = desc.DynamicStates,
                HasStreamOutput = desc.HasStreamOutput,
                RasterizedStream = desc.HasStreamOutput
                    ? desc.StreamOutput.RasterizedStreamIndex
                    : null,
            };
        }

        internal void Write(BinaryWriter writer)
        {
            writer.Write((byte)Topology);
            writer.Write((byte)StripCut);
            WriteRasterizer(writer, Rasterizer);
            WriteMultisample(writer, Multisample);
            WriteDepthStencil(writer, DepthStencil);
            writer.Write(IndependentBlend);
            writer.Write(LogicOperation);
            writer.Write(BlendAttachments.Length);
            foreach (BlendAttachmentState value in BlendAttachments)
                WriteBlendAttachment(writer, value);
            writer.Write(ColorFormats.Length);
            foreach (Format value in ColorFormats)
                writer.Write((ushort)value);
            writer.Write(DepthFormat.HasValue);
            if (DepthFormat.HasValue)
                writer.Write((ushort)DepthFormat.Value);
            writer.Write((ushort)DynamicStates);
            writer.Write(VertexBuffers.Length);
            foreach (VertexBufferLayout value in VertexBuffers)
            {
                writer.Write(value.BufferIndex);
                writer.Write(value.Stride);
                writer.Write(value.PerInstance);
                writer.Write(value.InstanceStepRate);
            }
            writer.Write(VertexAttributes.Length);
            foreach (VertexAttribute value in VertexAttributes)
            {
                writer.Write(value.Location);
                writer.Write(value.BufferIndex);
                writer.Write((ushort)value.Format);
                writer.Write(value.Offset);
            }
            writer.Write(HasStreamOutput);
            writer.Write(StreamOutputElements.Length);
            foreach (StreamOutputKeyElement value in StreamOutputElements)
                value.Write(writer);
            writer.Write(StreamOutputStrides.Length);
            foreach (uint value in StreamOutputStrides)
                writer.Write(value);
            writer.Write(RasterizedStream.HasValue);
            if (RasterizedStream.HasValue)
                writer.Write(RasterizedStream.Value);
        }
    }

    private sealed class MeshPipelineKeyData
    {
        private BlendAttachmentState[] BlendAttachments { get; init; } = [];
        private Format[] ColorFormats { get; init; } = [];
        private RasterizerState Rasterizer { get; init; }
        private MultisampleState Multisample { get; init; }
        private DepthStencilState DepthStencil { get; init; }
        private bool IndependentBlend { get; init; }
        private bool LogicOperation { get; init; }
        private Format? DepthFormat { get; init; }
        private DynamicStates DynamicStates { get; init; }

        internal static MeshPipelineKeyData Capture(in MeshPipelineDesc desc) => new()
        {
            BlendAttachments = desc.Blend.Attachments.ToArray(),
            ColorFormats = desc.Attachments.ColorFormats.ToArray(),
            Rasterizer = desc.Rasterizer,
            Multisample = desc.Multisample,
            DepthStencil = desc.DepthStencil,
            IndependentBlend = desc.Blend.IndependentBlend,
            LogicOperation = desc.Blend.LogicOperationEnabled,
            DepthFormat = desc.Attachments.DepthStencilFormat,
            DynamicStates = desc.DynamicStates,
        };

        internal void Write(BinaryWriter writer)
        {
            WriteRasterizer(writer, Rasterizer);
            WriteMultisample(writer, Multisample);
            WriteDepthStencil(writer, DepthStencil);
            writer.Write(IndependentBlend);
            writer.Write(LogicOperation);
            writer.Write(BlendAttachments.Length);
            foreach (BlendAttachmentState value in BlendAttachments)
                WriteBlendAttachment(writer, value);
            writer.Write(ColorFormats.Length);
            foreach (Format value in ColorFormats)
                writer.Write((ushort)value);
            writer.Write(DepthFormat.HasValue);
            if (DepthFormat.HasValue)
                writer.Write((ushort)DepthFormat.Value);
            writer.Write((ushort)DynamicStates);
        }
    }

    private readonly record struct StreamOutputKeyElement(
        bool Gap,
        string Semantic,
        uint SemanticIndex,
        uint Stream,
        byte StartComponent,
        byte ComponentCount,
        byte OutputSlot)
    {
        internal static StreamOutputKeyElement Capture(in StreamOutputElement value) => new(
            value.IsGap,
            value.IsGap ? string.Empty : value.Variable.SemanticName,
            value.IsGap ? 0 : checked((uint)value.Variable.SemanticIndex),
            value.Stream,
            value.StartComponent,
            value.ComponentCount,
            value.OutputSlot);

        internal void Write(BinaryWriter writer)
        {
            writer.Write(Gap);
            WriteCanonicalString(writer, Semantic);
            writer.Write(SemanticIndex);
            writer.Write(Stream);
            writer.Write(StartComponent);
            writer.Write(ComponentCount);
            writer.Write(OutputSlot);
        }
    }

    private static void WriteRasterizer(BinaryWriter writer, in RasterizerState value)
    {
        writer.Write((byte)value.Fill);
        writer.Write((byte)value.Cull);
        writer.Write((byte)value.FrontFace);
        writer.Write(value.DepthBias);
        WriteCanonicalSingle(writer, value.DepthBiasClamp);
        WriteCanonicalSingle(writer, value.SlopeScaledDepthBias);
        writer.Write(value.DepthClip);
        writer.Write(value.ConservativeRasterization);
    }

    private static void WriteMultisample(BinaryWriter writer, in MultisampleState value)
    {
        writer.Write(value.SampleCount);
        writer.Write(value.SampleMask);
        writer.Write(value.AlphaToCoverage);
    }

    private static void WriteDepthStencil(BinaryWriter writer, in DepthStencilState value)
    {
        writer.Write(value.DepthTest);
        writer.Write(value.DepthWrite);
        writer.Write((byte)value.DepthComparison);
        writer.Write(value.DepthBoundsTest);
        writer.Write(value.StencilTest);
        writer.Write(value.StencilReadMask);
        writer.Write(value.StencilWriteMask);
        WriteStencilFace(writer, value.Front);
        WriteStencilFace(writer, value.Back);
    }

    private static void WriteStencilFace(BinaryWriter writer, in StencilFaceState value)
    {
        writer.Write((byte)value.Fail);
        writer.Write((byte)value.DepthFail);
        writer.Write((byte)value.Pass);
        writer.Write((byte)value.Comparison);
    }

    private static void WriteBlendAttachment(BinaryWriter writer, in BlendAttachmentState value)
    {
        writer.Write(value.Enabled);
        writer.Write((byte)value.SourceColor);
        writer.Write((byte)value.DestinationColor);
        writer.Write((byte)value.ColorOperation);
        writer.Write((byte)value.SourceAlpha);
        writer.Write((byte)value.DestinationAlpha);
        writer.Write((byte)value.AlphaOperation);
        writer.Write((byte)value.WriteMask);
    }

    private static partial class NativeCast
    {
        internal static D3D12Pipeline Pipeline(Pipeline value)
        {
#if DEBUG
            return (D3D12Pipeline)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<Pipeline, D3D12Pipeline>(ref value);
#endif
        }
    }
}
