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

internal sealed unsafe partial class D3D12Backend
{
    private static readonly byte[] AttributeSemantic = "ATTRIBUTE\0"u8.ToArray();

    public Pipeline CreateGraphicsPipeline(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        ValidateGraphicsDescription(desc);
        ValidateDynamicStates(nativeDevice, desc.DynamicStates);
        ValidateDepthBounds(nativeDevice, desc.DepthStencil, desc.DynamicStates);
        ValidateAttachmentFormats(nativeDevice, desc.Attachments, desc.Blend);

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
        D3D12RootSignatureState root = D3D12RootSignatureBuilder.Compile(
            this,
            nativeDevice,
            reflection,
            entries,
            desc.StaticSamplers,
            PipelineType.Graphics,
            allowInputAssembler: true,
            allowStreamOutput: desc.HasStreamOutput);

        ID3D12PipelineState* pipelineState = null;
        NativeLease? nativeState = null;
        D3D12RootSignatureState? rootToRelease = root;
        RetainedSlangProgram? retainedProgram = null;
        D3D12ClassicPipeline? result = null;
        nint[] allocatedSemantics = [];
        try
        {
            byte[] key = CreateNativeGraphicsPipeline(
                nativeDevice,
                nativeCache,
                root,
                vertex,
                pixel,
                desc,
                ref allocatedSemantics,
                out pipelineState);

            SetNativeName(pipelineState, desc.Label ?? "Graphics Pipeline State");
            nativeState = new NativeLease(
                (IUnknown*)pipelineState,
                ownsReference: true,
                root.NativeLifetime);
            pipelineState = null;
            retainedProgram = RetainProgram(desc.Program);
            result = new D3D12ClassicPipeline(
                nativeDevice,
                nativeState,
                root,
                retainedProgram,
                PipelineType.Graphics,
                desc.Topology,
                desc.StripCut,
                desc.DynamicStates,
                desc.Label);
            nativeState = null;
            rootToRelease = null;
            retainedProgram = null;
            nativeDevice.RegisterChild(result);
            StoreCachedData(nativeDevice, nativeCache, 1, key, (ID3D12PipelineState*)result.NativeObject);
            return result;
        }
        catch
        {
            if (result is not null)
                result.Dispose();
            else
            {
                nativeState?.Release();
                if (pipelineState is not null)
                    _ = pipelineState->Release();
                retainedProgram?.Dispose();
            }
            throw;
        }
        finally
        {
            rootToRelease?.Release();
            foreach (nint semantic in allocatedSemantics)
            {
                if (semantic != 0)
                    Marshal.FreeCoTaskMem(semantic);
            }
        }
    }

    private byte[] CreateNativeGraphicsPipeline(
        D3D12Device nativeDevice,
        D3D12PipelineCache? nativeCache,
        D3D12RootSignatureState root,
        in CompiledShader vertex,
        in CompiledShader pixel,
        in GraphicsPipelineDesc desc,
        ref nint[] allocatedSemantics,
        out ID3D12PipelineState* pipelineState)
    {
        byte[] key = CreateGraphicsPipelineKey(nativeDevice, root, vertex, pixel, desc);
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
        BlendDesc blend = CreateBlend(
            desc.Blend,
            desc.Attachments.ColorFormats.Length,
            desc.Multisample.AlphaToCoverage);
        RasterizerDesc rasterizer = CreateRasterizer(desc.Rasterizer, desc.Multisample);
        RTFormatArray renderTargets = CreateRenderTargetFormats(desc.Attachments.ColorFormats);
        D3D12PipelineCache.CacheCandidate? cacheCandidate = TryGetCachedData(nativeCache, 1, key);
        byte[]? cachedData = cacheCandidate?.Payload;

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
            StreamOutputDesc streamOutput = desc.HasStreamOutput
                ? new StreamOutputDesc(
                    outputPointer,
                    checked((uint)outputElements.Length),
                    stridePointer,
                    checked((uint)outputStrides.Length),
                    desc.StreamOutput.RasterizedStreamIndex ?? uint.MaxValue)
                : default;
            ShaderBytecode vertexBytecode = new(vertexCode, (nuint)vertex.Code.Length);
            ShaderBytecode pixelBytecode = new(pixelCode, (nuint)pixel.Code.Length);
            InputLayoutDesc inputLayout = new(
                inputPointer,
                checked((uint)inputElements.Length));
            NativeFormat depthStencilFormat = GetDepthStencilFormat(desc.Attachments);
            NativeSampleDesc sampleDescription = new(desc.Multisample.SampleCount, 0);
            PipelineStateFlags pipelineFlags = ToPipelineStateFlags(desc.DynamicStates);
            CachedPipelineState cached = cachedData is null
                ? default
                : new CachedPipelineState(cachedPointer, (nuint)cachedData.Length);
            pipelineState = desc.DepthStencil.DepthBoundsTest
                ? CreateGraphicsPipelineStateWithCache(
                    nativeDevice,
                    cacheCandidate,
                    root.Native,
                    vertexBytecode,
                    pixelBytecode,
                    streamOutput,
                    blend,
                    desc.Multisample.SampleMask,
                    rasterizer,
                    CreateDepthStencil1(desc.DepthStencil),
                    PipelineStateSubobjectType.DepthStencil1,
                    inputLayout,
                    ToNativeStripCut(desc.StripCut),
                    ToNativeTopologyType(desc.Topology),
                    renderTargets,
                    depthStencilFormat,
                    sampleDescription,
                    cached,
                    pipelineFlags,
                    "ID3D12Device2::CreatePipelineState(graphics)")
                : CreateGraphicsPipelineStateWithCache(
                    nativeDevice,
                    cacheCandidate,
                    root.Native,
                    vertexBytecode,
                    pixelBytecode,
                    streamOutput,
                    blend,
                    desc.Multisample.SampleMask,
                    rasterizer,
                    CreateDepthStencil(desc.DepthStencil),
                    PipelineStateSubobjectType.DepthStencil,
                    inputLayout,
                    ToNativeStripCut(desc.StripCut),
                    ToNativeTopologyType(desc.Topology),
                    renderTargets,
                    depthStencilFormat,
                    sampleDescription,
                    cached,
                    pipelineFlags,
                    "ID3D12Device2::CreatePipelineState(graphics)");
        }
        return key;
    }

    private static ID3D12PipelineState* CreateGraphicsPipelineStateWithCache<TDepthStencil>(
        D3D12Device nativeDevice,
        D3D12PipelineCache.CacheCandidate? cacheCandidate,
        ID3D12RootSignature* rootSignature,
        ShaderBytecode vertex,
        ShaderBytecode pixel,
        StreamOutputDesc streamOutput,
        BlendDesc blend,
        uint sampleMask,
        RasterizerDesc rasterizer,
        TDepthStencil depthStencil,
        PipelineStateSubobjectType depthStencilType,
        InputLayoutDesc inputLayout,
        IndexBufferStripCutValue stripCut,
        PrimitiveTopologyType topology,
        in RTFormatArray renderTargets,
        NativeFormat depthStencilFormat,
        NativeSampleDesc sampleDescription,
        CachedPipelineState cached,
        PipelineStateFlags flags,
        string operation)
        where TDepthStencil : unmanaged
    {
        ID3D12PipelineState* pipelineState = null;
        int createResult = CreateGraphicsPipelineState(
            nativeDevice,
            rootSignature,
            vertex,
            pixel,
            streamOutput,
            blend,
            sampleMask,
            rasterizer,
            depthStencil,
            depthStencilType,
            inputLayout,
            stripCut,
            topology,
            renderTargets,
            depthStencilFormat,
            sampleDescription,
            cached,
            flags,
            &pipelineState);
        if (cacheCandidate is not null && RetryRejectedCachedPipeline(
                nativeDevice,
                cacheCandidate.Value,
                createResult,
                ref pipelineState,
                operation))
        {
            createResult = CreateGraphicsPipelineState(
                nativeDevice,
                rootSignature,
                vertex,
                pixel,
                streamOutput,
                blend,
                sampleMask,
                rasterizer,
                depthStencil,
                depthStencilType,
                inputLayout,
                stripCut,
                topology,
                renderTargets,
                depthStencilFormat,
                sampleDescription,
                default,
                flags,
                &pipelineState);
        }
        ThrowIfFailed(
            nativeDevice,
            createResult,
            NativeOperationType.PipelineCreation,
            operation);
        return pipelineState;
    }

    private static RTFormatArray CreateRenderTargetFormats(ReadOnlySpan<Format> formats)
    {
        NativeFormat[] nativeFormats = CreateColorFormats(formats);
        RTFormatArray result = default;
        result.NumRenderTargets = checked((uint)nativeFormats.Length);
        for (int index = 0; index < nativeFormats.Length; index++)
            result.RTFormats[index] = nativeFormats[index];
        return result;
    }

    private static NativeFormat GetDepthStencilFormat(in AttachmentFormatSignature attachments) =>
        attachments.DepthStencilFormat is Format format
            ? FormatMappings.ToDxgi(format)
            : NativeFormat.FormatUnknown;

    public Pipeline CreateComputePipeline(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
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
        D3D12RootSignatureState root = D3D12RootSignatureBuilder.Compile(
            this,
            nativeDevice,
            reflection,
            entries,
            desc.StaticSamplers.Span,
            PipelineType.Compute,
            allowInputAssembler: false,
            allowStreamOutput: false);
        ID3D12PipelineState* pipelineState = null;
        NativeLease? nativeState = null;
        D3D12RootSignatureState? rootToRelease = root;
        RetainedSlangProgram? retainedProgram = null;
        D3D12ClassicPipeline? result = null;
        try
        {
            byte[] key = CreateComputePipelineKey(nativeDevice, root, compute);
            D3D12PipelineCache.CacheCandidate? cacheCandidate =
                TryGetCachedData(nativeCache, 2, key);
            byte[]? cachedData = cacheCandidate?.Payload;
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
                int createResult = nativeDevice.Native->CreateComputePipelineState(
                    &native,
                    &iid,
                    (void**)&pipelineState);
                if (cacheCandidate is not null && RetryRejectedCachedPipeline(
                    nativeDevice,
                    cacheCandidate.Value,
                    createResult,
                    ref pipelineState,
                    "ID3D12Device::CreateComputePipelineState"))
                {
                    native.CachedPSO = default;
                    createResult = nativeDevice.Native->CreateComputePipelineState(
                        &native,
                        &iid,
                        (void**)&pipelineState);
                }
                ThrowIfFailed(
                    nativeDevice,
                    createResult,
                    NativeOperationType.PipelineCreation,
                    "ID3D12Device::CreateComputePipelineState");
            }

            SetNativeName(pipelineState, desc.Label ?? "Compute Pipeline State");
            nativeState = new NativeLease(
                (IUnknown*)pipelineState,
                ownsReference: true,
                root.NativeLifetime);
            pipelineState = null;
            retainedProgram = RetainProgram(desc.Program);
            result = new D3D12ClassicPipeline(
                nativeDevice,
                nativeState,
                root,
                retainedProgram,
                PipelineType.Compute,
                default,
                default,
                DynamicStates.None,
                desc.Label);
            nativeState = null;
            rootToRelease = null;
            retainedProgram = null;
            nativeDevice.RegisterChild(result);
            StoreCachedData(nativeDevice, nativeCache, 2, key, (ID3D12PipelineState*)result.NativeObject);
            return result;
        }
        catch
        {
            if (result is not null)
                result.Dispose();
            else
            {
                nativeState?.Release();
                if (pipelineState is not null)
                    _ = pipelineState->Release();
                retainedProgram?.Dispose();
            }
            throw;
        }
        finally
        {
            rootToRelease?.Release();
        }
    }

    public Pipeline CreateMeshPipeline(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
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
        ValidateDynamicStates(nativeDevice, desc.DynamicStates);
        ValidateDepthBounds(nativeDevice, desc.DepthStencil, desc.DynamicStates);
        ValidateAttachmentFormats(nativeDevice, desc.Attachments, desc.Blend);

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
        D3D12RootSignatureState root = D3D12RootSignatureBuilder.Compile(
            this,
            nativeDevice,
            reflection,
            CollectionsMarshal.AsSpan(entryList),
            desc.StaticSamplers,
            PipelineType.Mesh,
            allowInputAssembler: false,
            allowStreamOutput: false);
        ID3D12PipelineState* pipelineState = null;
        NativeLease? nativeState = null;
        D3D12RootSignatureState? rootToRelease = root;
        RetainedSlangProgram? retainedProgram = null;
        D3D12ClassicPipeline? result = null;
        try
        {
            byte[] key = CreateNativeMeshPipeline(
                nativeDevice,
                nativeCache,
                root,
                mesh,
                amplification,
                pixel,
                desc,
                out pipelineState);

            SetNativeName(pipelineState, desc.Label ?? "Mesh Pipeline State");
            nativeState = new NativeLease(
                (IUnknown*)pipelineState,
                ownsReference: true,
                root.NativeLifetime);
            pipelineState = null;
            retainedProgram = RetainProgram(desc.Program);
            result = new D3D12ClassicPipeline(
                nativeDevice,
                nativeState,
                root,
                retainedProgram,
                PipelineType.Mesh,
                PrimitiveTopology.TriangleList,
                StripCut.Disabled,
                desc.DynamicStates,
                desc.Label);
            nativeState = null;
            rootToRelease = null;
            retainedProgram = null;
            nativeDevice.RegisterChild(result);
            StoreCachedData(nativeDevice, nativeCache, 3, key, (ID3D12PipelineState*)result.NativeObject);
            return result;
        }
        catch
        {
            if (result is not null)
                result.Dispose();
            else
            {
                nativeState?.Release();
                if (pipelineState is not null)
                    _ = pipelineState->Release();
                retainedProgram?.Dispose();
            }
            throw;
        }
        finally
        {
            rootToRelease?.Release();
        }
    }

    private byte[] CreateNativeMeshPipeline(
        D3D12Device nativeDevice,
        D3D12PipelineCache? nativeCache,
        D3D12RootSignatureState root,
        in CompiledShader mesh,
        CompiledShader? amplification,
        CompiledShader? pixel,
        in MeshPipelineDesc desc,
        out ID3D12PipelineState* pipelineState)
    {
        byte[] key = CreateMeshPipelineKey(
            nativeDevice,
            root,
            mesh,
            amplification,
            pixel,
            desc);
        BlendDesc blend = CreateBlend(
            desc.Blend,
            desc.Attachments.ColorFormats.Length,
            desc.Multisample.AlphaToCoverage);
        RasterizerDesc rasterizer = CreateRasterizer(desc.Rasterizer, desc.Multisample);
        RTFormatArray renderTargets = CreateRenderTargetFormats(desc.Attachments.ColorFormats);
        D3D12PipelineCache.CacheCandidate? cacheCandidate = TryGetCachedData(nativeCache, 3, key);
        byte[]? cachedData = cacheCandidate?.Payload;
        fixed (byte* meshCode = mesh.Code)
        fixed (byte* amplificationCode = amplification?.Code)
        fixed (byte* pixelCode = pixel?.Code)
        fixed (byte* cachedPointer = cachedData)
        {
            ShaderBytecode amplificationBytecode = amplification is null
                ? default
                : new ShaderBytecode(amplificationCode, (nuint)amplification.Code.Length);
            ShaderBytecode meshBytecode = new(meshCode, (nuint)mesh.Code.Length);
            ShaderBytecode pixelBytecode = pixel is null
                ? default
                : new ShaderBytecode(pixelCode, (nuint)pixel.Code.Length);
            NativeFormat depthStencilFormat = GetDepthStencilFormat(desc.Attachments);
            NativeSampleDesc sampleDescription = new(desc.Multisample.SampleCount, 0);
            PipelineStateFlags pipelineFlags = ToPipelineStateFlags(desc.DynamicStates);
            CachedPipelineState cached = cachedData is null
                ? default
                : new CachedPipelineState(cachedPointer, (nuint)cachedData.Length);
            pipelineState = desc.DepthStencil.DepthBoundsTest
                ? CreateMeshPipelineStateWithCache(
                    nativeDevice,
                    cacheCandidate,
                    root.Native,
                    amplificationBytecode,
                    meshBytecode,
                    pixelBytecode,
                    blend,
                    desc.Multisample.SampleMask,
                    rasterizer,
                    CreateDepthStencil1(desc.DepthStencil),
                    PipelineStateSubobjectType.DepthStencil1,
                    renderTargets,
                    depthStencilFormat,
                    sampleDescription,
                    cached,
                    pipelineFlags,
                    "ID3D12Device2::CreatePipelineState(mesh)")
                : CreateMeshPipelineStateWithCache(
                    nativeDevice,
                    cacheCandidate,
                    root.Native,
                    amplificationBytecode,
                    meshBytecode,
                    pixelBytecode,
                    blend,
                    desc.Multisample.SampleMask,
                    rasterizer,
                    CreateDepthStencil(desc.DepthStencil),
                    PipelineStateSubobjectType.DepthStencil,
                    renderTargets,
                    depthStencilFormat,
                    sampleDescription,
                    cached,
                    pipelineFlags,
                    "ID3D12Device2::CreatePipelineState(mesh)");
        }
        return key;
    }

    private static ID3D12PipelineState* CreateMeshPipelineStateWithCache<TDepthStencil>(
        D3D12Device nativeDevice,
        D3D12PipelineCache.CacheCandidate? cacheCandidate,
        ID3D12RootSignature* rootSignature,
        ShaderBytecode amplification,
        ShaderBytecode mesh,
        ShaderBytecode pixel,
        BlendDesc blend,
        uint sampleMask,
        RasterizerDesc rasterizer,
        TDepthStencil depthStencil,
        PipelineStateSubobjectType depthStencilType,
        in RTFormatArray renderTargets,
        NativeFormat depthStencilFormat,
        NativeSampleDesc sampleDescription,
        CachedPipelineState cached,
        PipelineStateFlags flags,
        string operation)
        where TDepthStencil : unmanaged
    {
        ID3D12PipelineState* pipelineState = null;
        int createResult = CreateMeshPipelineState(
            nativeDevice,
            rootSignature,
            amplification,
            mesh,
            pixel,
            blend,
            sampleMask,
            rasterizer,
            depthStencil,
            depthStencilType,
            PrimitiveTopologyType.Triangle,
            renderTargets,
            depthStencilFormat,
            sampleDescription,
            cached,
            flags,
            &pipelineState);
        if (cacheCandidate is not null && RetryRejectedCachedPipeline(
                nativeDevice,
                cacheCandidate.Value,
                createResult,
                ref pipelineState,
                operation))
        {
            createResult = CreateMeshPipelineState(
                nativeDevice,
                rootSignature,
                amplification,
                mesh,
                pixel,
                blend,
                sampleMask,
                rasterizer,
                depthStencil,
                depthStencilType,
                PrimitiveTopologyType.Triangle,
                renderTargets,
                depthStencilFormat,
                sampleDescription,
                default,
                flags,
                &pipelineState);
        }
        ThrowIfFailed(
            nativeDevice,
            createResult,
            NativeOperationType.PipelineCreation,
            operation);
        return pipelineState;
    }

    private static D3D12PipelineCache? GetPipelineCache(
        D3D12Device device,
        PipelineCache? cache)
    {
        if (cache is null)
            return null;
        D3D12PipelineCache result = RequireD3D12.PipelineCache(cache);
        if (!ReferenceEquals(result.Device, device))
            throw new ArgumentException("The PipelineCache belongs to another Device.", nameof(cache));
        if (result.IsDisposed && !D3D12PipelineCompiler.AllowsDisposedCache(result))
            throw new ObjectDisposedException(nameof(cache));
        return result;
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
                    GraphicsError.ShaderCompilation,
                    FormatSlangFailure("Slang program layout materialization failed", diagnostics));
            }
            if (program.GetSpecializationParamCount() != 0)
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
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
                    GraphicsError.ShaderCompilation,
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
        const DynamicStates knownDynamicStates =
            DynamicStates.Viewport |
            DynamicStates.Scissor |
            DynamicStates.BlendConstants |
            DynamicStates.StencilReference |
            DynamicStates.DepthBounds |
            DynamicStates.DepthBias;
        if ((desc.DynamicStates & ~knownDynamicStates) != 0)
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
        if (blend.LogicOperation is LogicOperation logicOperation &&
            !Enum.IsDefined(logicOperation))
            throw new ArgumentOutOfRangeException(nameof(blend));
        if (blend.LogicOperation.HasValue)
        {
            foreach (BlendAttachmentState attachment in blend.Attachments)
            {
                if (attachment.Enabled)
                {
                    throw new ArgumentException(
                        "Logic operations and color blending cannot be enabled together.",
                        nameof(blend));
                }
            }
        }
    }

    private static void ValidateDepthBounds(
        D3D12Device device,
        in DepthStencilState state,
        DynamicStates dynamicStates)
    {
        if ((dynamicStates & DynamicStates.DepthBounds) != 0 && !state.DepthBoundsTest)
        {
            throw new ArgumentException(
                "Dynamic depth bounds require DepthBoundsTest to be enabled in the Pipeline.",
                nameof(dynamicStates));
        }
        if (state.DepthBoundsTest && !device.Capabilities.SupportsDepthBounds)
            throw new NotSupportedException("Depth-bounds testing is unavailable on this Device.");
    }

    private static void ValidateDynamicStates(
        D3D12Device device,
        DynamicStates requested)
    {
        DynamicStates unsupported =
            requested & ~device.Capabilities.SupportedDynamicStates;
        if (unsupported != DynamicStates.None)
        {
            throw new NotSupportedException(
                $"Dynamic Pipeline state {unsupported} is unavailable on this Device.");
        }
    }

    private static void ValidateAttachmentFormats(
        D3D12Device device,
        in AttachmentFormatSignature attachments,
        in BlendState blend)
    {
        for (int index = 0; index < attachments.ColorFormats.Length; index++)
        {
            Format format = attachments.ColorFormats[index];
            FormatSupport support = device.Capabilities.GetFormatSupport(format);
            FormatFeatures required = FormatFeatures.ColorAttachment;
            if (attachments.SampleCount > 1)
                required |= FormatFeatures.MultisampleColorAttachment;
            BlendAttachmentState state = blend.Attachments.IsEmpty
                ? new BlendAttachmentState()
                : blend.Attachments[index];
            if (state.Enabled)
                required |= FormatFeatures.ColorAttachmentBlend;
            if (blend.LogicOperation.HasValue)
                required |= FormatFeatures.LogicOperation;
            if ((support.Features & required) != required ||
                !support.SupportsSampleCount(attachments.SampleCount))
            {
                throw new NotSupportedException(
                    $"Format {format} does not support the requested Pipeline attachment state.");
            }
        }

        if (attachments.DepthStencilFormat is Format depthFormat)
        {
            FormatSupport support = device.Capabilities.GetFormatSupport(depthFormat);
            if ((support.Features & FormatFeatures.DepthStencilAttachment) == 0 ||
                !support.SupportsSampleCount(attachments.SampleCount))
            {
                throw new NotSupportedException(
                    $"Format {depthFormat} does not support the requested depth/stencil state.");
            }
        }
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
        semantics.EnsureCapacity(state.Elements.Length);
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

    private static BlendDesc CreateBlend(
        in BlendState state,
        int colorCount,
        bool alphaToCoverage)
    {
        BlendDesc result = default;
        result.AlphaToCoverageEnable = alphaToCoverage;
        result.IndependentBlendEnable = state.IndependentBlend;
        BlendAttachmentState defaultState = new();
        bool logicOperationEnabled = state.LogicOperation.HasValue;
        LogicOp logicOperation = state.LogicOperation is LogicOperation operation
            ? ToNativeLogicOperation(operation)
            : LogicOp.Copy;
        for (int index = 0; index < 8; index++)
        {
            BlendAttachmentState attachment = index < colorCount && !state.Attachments.IsEmpty
                ? state.Attachments[state.IndependentBlend ? index : 0]
                : defaultState;
            result.RenderTarget[index] = new RenderTargetBlendDesc(
                attachment.Enabled,
                logicOperationEnabled,
                ToNativeBlend(attachment.SourceColor),
                ToNativeBlend(attachment.DestinationColor),
                ToNativeBlendOperation(attachment.ColorOperation),
                ToNativeBlend(attachment.SourceAlpha),
                ToNativeBlend(attachment.DestinationAlpha),
                ToNativeBlendOperation(attachment.AlphaOperation),
                logicOperation,
                (byte)attachment.WriteMask);
        }
        return result;
    }

    private static PipelineStateFlags ToPipelineStateFlags(DynamicStates states)
    {
        PipelineStateFlags result = PipelineStateFlags.None;
        if ((states & DynamicStates.DepthBias) != 0)
            result |= PipelineStateFlags.DynamicDepthBias;
        if ((states & DynamicStates.StripCut) != 0)
            result |= PipelineStateFlags.DynamicIndexBufferStripCut;
        return result;
    }

    private static int CreateGraphicsPipelineState<TDepthStencil>(
        D3D12Device device,
        ID3D12RootSignature* rootSignature,
        ShaderBytecode vertex,
        ShaderBytecode pixel,
        StreamOutputDesc streamOutput,
        BlendDesc blend,
        uint sampleMask,
        RasterizerDesc rasterizer,
        TDepthStencil depthStencil,
        PipelineStateSubobjectType depthStencilType,
        InputLayoutDesc inputLayout,
        IndexBufferStripCutValue stripCut,
        PrimitiveTopologyType topology,
        in RTFormatArray renderTargets,
        NativeFormat depthStencilFormat,
        NativeSampleDesc sampleDescription,
        CachedPipelineState cached,
        PipelineStateFlags flags,
        ID3D12PipelineState** pipelineState)
        where TDepthStencil : unmanaged
    {
        GraphicsPipelineStream<TDepthStencil> stream = new(
            rootSignature,
            vertex,
            pixel,
            streamOutput,
            blend,
            sampleMask,
            rasterizer,
            depthStencil,
            depthStencilType,
            inputLayout,
            stripCut,
            topology,
            renderTargets,
            depthStencilFormat,
            sampleDescription,
            device.EnabledNodeMask,
            cached,
            flags);
        PipelineStateStreamDesc description = new(
            (nuint)sizeof(GraphicsPipelineStream<TDepthStencil>),
            &stream);
        Guid iid = ID3D12PipelineState.Guid;
        return device.Native->CreatePipelineState(
            &description,
            &iid,
            (void**)pipelineState);
    }

    private static int CreateMeshPipelineState<TDepthStencil>(
        D3D12Device device,
        ID3D12RootSignature* rootSignature,
        ShaderBytecode amplification,
        ShaderBytecode mesh,
        ShaderBytecode pixel,
        BlendDesc blend,
        uint sampleMask,
        RasterizerDesc rasterizer,
        TDepthStencil depthStencil,
        PipelineStateSubobjectType depthStencilType,
        PrimitiveTopologyType topology,
        in RTFormatArray renderTargets,
        NativeFormat depthStencilFormat,
        NativeSampleDesc sampleDescription,
        CachedPipelineState cached,
        PipelineStateFlags flags,
        ID3D12PipelineState** pipelineState)
        where TDepthStencil : unmanaged
    {
        MeshPipelineStream<TDepthStencil> stream = new(
            rootSignature,
            amplification,
            mesh,
            pixel,
            blend,
            sampleMask,
            rasterizer,
            depthStencil,
            depthStencilType,
            topology,
            renderTargets,
            depthStencilFormat,
            sampleDescription,
            device.EnabledNodeMask,
            cached,
            flags);
        PipelineStateStreamDesc description = new(
            (nuint)sizeof(MeshPipelineStream<TDepthStencil>),
            &stream);
        Guid iid = ID3D12PipelineState.Guid;
        return device.Native->CreatePipelineState(
            &description,
            &iid,
            (void**)pipelineState);
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

    private static DepthStencilDesc1 CreateDepthStencil1(in DepthStencilState state)
    {
        DepthStencilDesc1 result = default;
        result.DepthEnable = state.DepthTest;
        result.DepthWriteMask = state.DepthWrite ? DepthWriteMask.All : DepthWriteMask.Zero;
        result.DepthFunc = ToNativeComparison(state.DepthComparison);
        result.StencilEnable = state.StencilTest;
        result.StencilReadMask = state.StencilReadMask;
        result.StencilWriteMask = state.StencilWriteMask;
        result.FrontFace = ToNativeStencilFace(state.Front);
        result.BackFace = ToNativeStencilFace(state.Back);
        result.DepthBoundsTestEnable = state.DepthBoundsTest;
        return result;
    }

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

    private static LogicOp ToNativeLogicOperation(LogicOperation value) => value switch
    {
        LogicOperation.Clear => LogicOp.Clear,
        LogicOperation.Set => LogicOp.Set,
        LogicOperation.Copy => LogicOp.Copy,
        LogicOperation.CopyInverted => LogicOp.CopyInverted,
        LogicOperation.NoOperation => LogicOp.Noop,
        LogicOperation.Invert => LogicOp.Invert,
        LogicOperation.And => LogicOp.And,
        LogicOperation.Nand => LogicOp.Nand,
        LogicOperation.Or => LogicOp.Or,
        LogicOperation.Nor => LogicOp.Nor,
        LogicOperation.Xor => LogicOp.Xor,
        LogicOperation.Equivalence => LogicOp.Equiv,
        LogicOperation.AndReverse => LogicOp.AndReverse,
        LogicOperation.AndInverted => LogicOp.AndInverted,
        LogicOperation.OrReverse => LogicOp.OrReverse,
        LogicOperation.OrInverted => LogicOp.OrInverted,
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
        D3D12RootSignatureState root,
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
            writer => WriteClassicRootSignatures(writer, root),
            static _ => { });

    private static byte[] CreateGraphicsPipelineKey(
        D3D12Device device,
        D3D12RootSignatureState root,
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
            writer => WriteClassicRootSignatures(writer, root),
            data.Write);
    }

    private static byte[] CreateMeshPipelineKey(
        D3D12Device device,
        D3D12RootSignatureState root,
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
            writer => WriteClassicRootSignatures(writer, root),
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

    private static void WriteClassicRootSignatures(
        BinaryWriter writer,
        D3D12RootSignatureState root)
    {
        writer.Write(1u);
        WriteCanonicalBytes(writer, root.Serialized);
        writer.Write(0u);
    }

    private static D3D12PipelineCache.CacheCandidate? TryGetCachedData(
        D3D12PipelineCache? cache,
        byte family,
        ReadOnlySpan<byte> key) =>
        cache is not null && cache.TryGet(family, key, out D3D12PipelineCache.CacheCandidate? candidate)
            ? candidate
            : null;

    private static bool RetryRejectedCachedPipeline(
        D3D12Device device,
        D3D12PipelineCache.CacheCandidate candidate,
        int result,
        ref ID3D12PipelineState* pipeline,
        string operation)
    {
        if (result >= 0)
            return false;
        if (pipeline is not null)
        {
            _ = pipeline->Release();
            pipeline = null;
        }
        if (IsOutOfMemoryCode(result) || IsDirectDeviceRemovalCode(result))
        {
            ThrowIfFailed(
                device,
                result,
                NativeOperationType.PipelineCreation,
                operation);
        }
        _ = candidate.Owner.Reject(candidate);
        return true;
    }

    private static void StoreCachedData(
        D3D12Device device,
        D3D12PipelineCache? cache,
        byte family,
        ReadOnlySpan<byte> key,
        ID3D12PipelineState* pipeline)
    {
        if (cache is null)
            return;
        ID3D10Blob* blob = null;
        int result = pipeline->GetCachedBlob(&blob);
        if (result < 0)
        {
            if (blob is not null)
                _ = blob->Release();
            if (IsDirectDeviceRemovalCode(result))
            {
                ThrowIfFailed(
                    device,
                    result,
                    NativeOperationType.Ordinary,
                    "ID3D12PipelineState::GetCachedBlob");
            }
            return;
        }
        if (blob is null)
            return;
        try
        {
            D3D12PipelineCache.CacheAdmission? admission;
            try
            {
                nuint nativeSize = blob->GetBufferSize();
                void* nativePointer = blob->GetBufferPointer();
                if (nativeSize > int.MaxValue || (nativeSize != 0 && nativePointer == null))
                    return;
                ReadOnlySpan<byte> bytes = new(nativePointer, (int)nativeSize);
                admission = cache.PrepareAdmission(
                    family,
                    key,
                    bytes,
                    CancellationToken.None);
            }
            catch (Exception exception) when (exception is
                OutOfMemoryException or
                OverflowException)
            {
                return;
            }
            _ = cache.CommitAdmission(admission);
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

    private static T RetainComReference<T>(T value)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            void* pointer = ComInterfaceMarshaller<T>.ConvertToUnmanaged(value);
            try
            {
                return UniqueComInterfaceMarshaller<T>.ConvertToManaged(pointer)
                    ?? throw new InvalidOperationException(
                        $"The {typeof(T).Name} COM reference could not be materialized.");
            }
            finally
            {
                ComInterfaceMarshaller<T>.Free(pointer);
            }
        }
        catch (InvalidCastException)
        {
            // Managed test and tool implementations have no native COM identity. Holding their
            // managed object is sufficient for those implementations.
            return value;
        }
    }

    private static void ReleaseComReference<T>(T? value)
        where T : class
    {
        if (value is null)
            return;
        object instance = value;
        if (instance is System.Runtime.InteropServices.Marshalling.ComObject wrapper)
            wrapper.FinalRelease();
    }

    private static RetainedSlangProgram RetainProgram(IComponentType program) =>
        RetainedSlangProgram.Capture(program);

    private sealed class RetainedSlangProgram : IDisposable
    {
        private readonly object _gate = new();
        private IComponentType? _program;
        private ISession? _session;
        private IGlobalSession? _globalSession;

        private RetainedSlangProgram(
            IComponentType program,
            ISession session,
            IGlobalSession globalSession)
        {
            _program = program;
            _session = session;
            _globalSession = globalSession;
        }

        internal IComponentType Program
        {
            get
            {
                lock (_gate)
                {
                    return _program
                        ?? throw new ObjectDisposedException(nameof(RetainedSlangProgram));
                }
            }
        }

        internal static RetainedSlangProgram Capture(IComponentType program)
        {
            ArgumentNullException.ThrowIfNull(program);
            IGlobalSession? globalReference = null;
            ISession? sessionReference = null;
            IComponentType? programReference = null;
            try
            {
                ISession session = program.GetSession();
                IGlobalSession globalSession = session.GetGlobalSession();
                globalReference = RetainComReference(globalSession);
                sessionReference = RetainComReference(session);
                programReference = RetainComReference(program);
                RetainedSlangProgram result = new(
                    programReference,
                    sessionReference,
                    globalReference);
                globalReference = null;
                sessionReference = null;
                programReference = null;
                return result;
            }
            finally
            {
                ReleaseComReference(programReference);
                ReleaseComReference(sessionReference);
                ReleaseComReference(globalReference);
            }
        }

        internal RetainedSlangProgram Retain()
        {
            lock (_gate)
            {
                return Capture(
                    _program
                    ?? throw new ObjectDisposedException(nameof(RetainedSlangProgram)));
            }
        }

        public void Dispose()
        {
            IComponentType? program;
            ISession? session;
            IGlobalSession? globalSession;
            lock (_gate)
            {
                program = _program;
                session = _session;
                globalSession = _globalSession;
                _program = null;
                _session = null;
                _globalSession = null;
            }
            ReleaseComReference(program);
            ReleaseComReference(session);
            ReleaseComReference(globalSession);
        }
    }

    private abstract class D3D12Pipeline : Pipeline
    {
        private readonly D3D12Device _device;
        private readonly D3D12RootSignatureState _root;
        private readonly NativeLease _native;
        private readonly D3D12RootSignatureState[] _additionalRoots;
        private readonly NativeLease[] _additionalLeases;
        private readonly object _programGate = new();
        private RetainedSlangProgram? _program;

        protected D3D12Pipeline(
            D3D12Device device,
            NativeLease native,
            D3D12RootSignatureState root,
            D3D12RootSignatureState[] additionalRoots,
            NativeLease[] additionalLeases,
            RetainedSlangProgram program,
            PipelineType type,
            string? label)
            : base(device, type, label)
        {
            _device = device;
            _root = root;
            _additionalRoots = additionalRoots;
            _additionalLeases = additionalLeases;
            _native = native;
            _program = program;
        }

        internal IUnknown* NativeObject => (IUnknown*)_native.Pointer;
        internal NativeLease NativeLifetime => _native;
        internal D3D12RootSignatureState RootSignature => _root;
        internal RetainedSlangProgram RetainProgramReference()
        {
            lock (_programGate)
            {
                RetainedSlangProgram program = _program
                    ?? throw new ObjectDisposedException(nameof(Pipeline));
                return program.Retain();
            }
        }
        internal NativeLease RetainNativeState()
        {
            _native.Retain();
            return _native;
        }
        internal override void Release(bool fromParent)
        {
            _native.Release();
            for (int index = _additionalLeases.Length - 1; index >= 0; index--)
                _additionalLeases[index].Release();
            for (int index = _additionalRoots.Length - 1; index >= 0; index--)
                _additionalRoots[index].Release();
            _root.Release();
            RetainedSlangProgram? program;
            lock (_programGate)
            {
                program = _program;
                _program = null;
            }
            program?.Dispose();
            _device.UnregisterChild(this);
        }
    }

    private sealed class D3D12ClassicPipeline : D3D12Pipeline
    {
        internal D3D12ClassicPipeline(
            D3D12Device device,
            NativeLease native,
            D3D12RootSignatureState root,
            RetainedSlangProgram program,
            PipelineType type,
            PrimitiveTopology topology,
            StripCut stripCut,
            DynamicStates dynamicStates,
            string? label)
            : base(
                device,
                native,
                root,
                [],
                [],
                program,
                type,
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
    private readonly struct GraphicsPipelineStream<TDepthStencil>
        where TDepthStencil : unmanaged
    {
        internal GraphicsPipelineStream(
            ID3D12RootSignature* rootSignature,
            ShaderBytecode vertex,
            ShaderBytecode pixel,
            StreamOutputDesc streamOutput,
            BlendDesc blend,
            uint sampleMask,
            RasterizerDesc rasterizer,
            TDepthStencil depthStencil,
            PipelineStateSubobjectType depthStencilType,
            InputLayoutDesc inputLayout,
            IndexBufferStripCutValue stripCut,
            PrimitiveTopologyType topology,
            RTFormatArray renderTargets,
            NativeFormat depthStencilFormat,
            NativeSampleDesc sampleDescription,
            uint nodeMask,
            CachedPipelineState cached,
            PipelineStateFlags flags)
        {
            RootSignature = new(PipelineStateSubobjectType.RootSignature, (nint)rootSignature);
            VS = new(PipelineStateSubobjectType.VS, vertex);
            PS = new(PipelineStateSubobjectType.PS, pixel);
            StreamOutput = new(PipelineStateSubobjectType.StreamOutput, streamOutput);
            Blend = new(PipelineStateSubobjectType.Blend, blend);
            SampleMask = new(PipelineStateSubobjectType.SampleMask, sampleMask);
            Rasterizer = new(PipelineStateSubobjectType.Rasterizer, rasterizer);
            DepthStencil = new(depthStencilType, depthStencil);
            InputLayout = new(PipelineStateSubobjectType.InputLayout, inputLayout);
            StripCut = new(PipelineStateSubobjectType.IBStripCutValue, stripCut);
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
        private readonly PipelineSubobject<ShaderBytecode> VS;
        private readonly PipelineSubobject<ShaderBytecode> PS;
        private readonly PipelineSubobject<StreamOutputDesc> StreamOutput;
        private readonly BlendPipelineSubobject Blend;
        private readonly PipelineSubobject<uint> SampleMask;
        private readonly PipelineSubobject<RasterizerDesc> Rasterizer;
        private readonly PipelineSubobject<TDepthStencil> DepthStencil;
        private readonly PipelineSubobject<InputLayoutDesc> InputLayout;
        private readonly PipelineSubobject<IndexBufferStripCutValue> StripCut;
        private readonly PipelineSubobject<PrimitiveTopologyType> Topology;
        private readonly PipelineSubobject<RTFormatArray> RenderTargets;
        private readonly PipelineSubobject<NativeFormat> DepthStencilFormat;
        private readonly SampleDescPipelineSubobject SampleDescription;
        private readonly PipelineSubobject<uint> NodeMask;
        private readonly PipelineSubobject<CachedPipelineState> Cached;
        private readonly PipelineSubobject<PipelineStateFlags> Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MeshPipelineStream<TDepthStencil>
        where TDepthStencil : unmanaged
    {
        internal MeshPipelineStream(
            ID3D12RootSignature* rootSignature,
            ShaderBytecode amplification,
            ShaderBytecode mesh,
            ShaderBytecode pixel,
            BlendDesc blend,
            uint sampleMask,
            RasterizerDesc rasterizer,
            TDepthStencil depthStencil,
            PipelineStateSubobjectType depthStencilType,
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
            DepthStencil = new(depthStencilType, depthStencil);
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
        private readonly PipelineSubobject<TDepthStencil> DepthStencil;
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
        private LogicOperation? LogicOperation { get; init; }
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
                LogicOperation = desc.Blend.LogicOperation,
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
            writer.Write(LogicOperation.HasValue);
            if (LogicOperation.HasValue)
                writer.Write((byte)LogicOperation.Value);
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
        private LogicOperation? LogicOperation { get; init; }
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
            LogicOperation = desc.Blend.LogicOperation,
            DepthFormat = desc.Attachments.DepthStencilFormat,
            DynamicStates = desc.DynamicStates,
        };

        internal void Write(BinaryWriter writer)
        {
            WriteRasterizer(writer, Rasterizer);
            WriteMultisample(writer, Multisample);
            WriteDepthStencil(writer, DepthStencil);
            writer.Write(IndependentBlend);
            writer.Write(LogicOperation.HasValue);
            if (LogicOperation.HasValue)
                writer.Write((byte)LogicOperation.Value);
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

    private static partial class RequireD3D12
    {
        internal static D3D12Pipeline Pipeline(Pipeline value) =>
            value as D3D12Pipeline ??
            throw new ArgumentException(
                "The Pipeline was not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
