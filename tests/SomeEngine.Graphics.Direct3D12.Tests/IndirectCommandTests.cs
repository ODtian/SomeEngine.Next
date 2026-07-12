using System.Buffers.Binary;
using System.Text.Json;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class IndirectCommandTests
{
    private const string ComputeSource = """
        RWStructuredBuffer<uint> Output : register(u0, space0);

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            InterlockedAdd(Output[0], 1);
        }
        """;

    private const string ProducerSource = """
        RWStructuredBuffer<uint> Arguments : register(u0, space0);
        RWStructuredBuffer<uint> Count : register(u1, space0);

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void GenerateDraw(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Arguments[2] = 0; Arguments[3] = 1; Arguments[4] = 0; Arguments[5] = 0;
            Arguments[6] = 3; Arguments[7] = 1; Arguments[8] = 0; Arguments[9] = 0;
            Count[0] = 1;
        }

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void GenerateDrawIndexed(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Arguments[2] = 0; Arguments[3] = 1; Arguments[4] = 0; Arguments[5] = 0; Arguments[6] = 0;
            Arguments[7] = 3; Arguments[8] = 1; Arguments[9] = 0; Arguments[10] = 0; Arguments[11] = 0;
            Count[0] = 1;
        }

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void GenerateDispatch(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Arguments[2] = 0; Arguments[3] = 1; Arguments[4] = 1;
            Arguments[6] = 1; Arguments[7] = 1; Arguments[8] = 1;
            Count[0] = 1;
        }
        """;

    private static readonly Lazy<byte[]> ComputeBytecode = new(CompileSm62Compute);
    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> ProducerBytecode = new(CompileIndirectProducers);

    [Fact]
    public void Warp_draw_indirect_cpu_and_gpu_counts_produce_expected_pixels() =>
        AssertRasterIndirect(indexed: false);

    [Fact]
    public void Warp_draw_indexed_indirect_cpu_and_gpu_counts_produce_expected_pixels() =>
        AssertRasterIndirect(indexed: true);

    [Fact]
    public void Warp_dispatch_indirect_cpu_and_gpu_counts_produce_expected_uav_values()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP indirect-dispatch lane must execute on Windows.");
        using Device device = CreateDevice();
        BufferHandle zeroUpload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle output = device.CreateBuffer(new BufferDesc(
            4,
            BufferUsage.ShaderWrite | BufferUsage.CopyDestination | BufferUsage.CopySource));
        BufferHandle readback = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        BufferHandle arguments = device.CreateBuffer(new BufferDesc(64, BufferUsage.Indirect), MemoryType.Upload);
        BufferHandle count = device.CreateBuffer(new BufferDesc(4, BufferUsage.Indirect), MemoryType.Upload);
        BufferHandle gpuArguments = device.CreateBuffer(new BufferDesc(
            64,
            BufferUsage.ShaderWrite | BufferUsage.Indirect));
        BufferHandle gpuCount = device.CreateBuffer(new BufferDesc(
            4,
            BufferUsage.ShaderWrite | BufferUsage.Indirect));
        device.WriteBuffer(zeroUpload, 0, new byte[4]);

        byte[] argumentBytes = new byte[64];
        WriteDispatch(argumentBytes.AsSpan(8, 12), 1, 1, 1);
        WriteDispatch(argumentBytes.AsSpan(24, 12), 1, 1, 1);
        device.WriteBuffer(arguments, 0, argumentBytes);
        Span<byte> countBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(countBytes, 1);
        device.WriteBuffer(count, 0, countBytes);

        BufferViewHandle outputView = device.CreateBufferView(new BufferViewDesc(
            output,
            BufferRange.Whole,
            BindingKind.StorageBuffer,
            Stride: 4));
        BindGroupLayoutHandle groupLayout = device.CreateBindGroupLayout([
            new BindingDesc(0, BindingKind.StorageBuffer, 1, ShaderStage.Compute),
        ]);
        BindGroupHandle group = device.CreateBindGroup(groupLayout, [BindingWrite.Buffer(0, outputView)]);
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            new[] { groupLayout },
            Array.Empty<PushConstantRange>()));
        ShaderHandle shader = device.CreateShader(new ShaderDesc(
            new ShaderArtifactKey(0x1D01, 0x1D02, 0x1D03, 0x1D04),
            ShaderBinaryFormat.Dxil,
            ShaderStage.Compute,
            "CSMain",
            ComputeBytecode.Value,
            new ShaderInterface(new[] {
                new ShaderBinding(0, 0, BindingKind.StorageBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadWrite, DeclaredEffect.Write),
            }, Array.Empty<PushConstantRange>(), 0x1D01_1D02_1D03_1D04),
            "test:dispatch-indirect"));
        PipelineHandle pipeline = device.CreateComputePipeline(new ComputePipelineDesc(layout, shader));
        IndirectProducer producer = CreateProducer(device, gpuArguments, gpuCount, "GenerateDispatch", 0x1D20);

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Compute, "indirect-dispatch-output");
        commands.Barriers([
            ResourceBarrier.Transition(output.Resource, ResourceState.Common, ResourceState.CopyDestination),
            ResourceBarrier.Transition(gpuArguments.Resource, ResourceState.Common, ResourceState.UnorderedAccess),
            ResourceBarrier.Transition(gpuCount.Resource, ResourceState.Common, ResourceState.UnorderedAccess),
        ]);
        commands.CopyBuffer(zeroUpload, 0, output, 0, 4);
        commands.Barriers([ResourceBarrier.Transition(output.Resource, ResourceState.CopyDestination, ResourceState.UnorderedAccess)]);
        commands.SetPipeline(producer.Pipeline);
        commands.SetBindGroup(0, producer.Group);
        commands.Dispatch(1, 1, 1);
        commands.Barriers([
            ResourceBarrier.UnorderedAccess(gpuArguments.Resource),
            ResourceBarrier.UnorderedAccess(gpuCount.Resource),
            ResourceBarrier.Transition(gpuArguments.Resource, ResourceState.UnorderedAccess, ResourceState.IndirectArgument),
            ResourceBarrier.Transition(gpuCount.Resource, ResourceState.UnorderedAccess, ResourceState.IndirectArgument),
        ]);
        commands.SetPipeline(pipeline);
        commands.SetBindGroup(0, group);
        commands.DispatchIndirect(arguments, 8, 2, 16);
        commands.Barriers([ResourceBarrier.UnorderedAccess(output.Resource)]);
        commands.DispatchIndirect(arguments, 8, 2, 16, count, 0);
        commands.Barriers([ResourceBarrier.UnorderedAccess(output.Resource)]);
        commands.DispatchIndirect(gpuArguments, 8, 2, 16);
        commands.Barriers([ResourceBarrier.UnorderedAccess(output.Resource)]);
        commands.DispatchIndirect(gpuArguments, 8, 2, 16, gpuCount, 0);
        commands.Barriers([
            ResourceBarrier.UnorderedAccess(output.Resource),
            ResourceBarrier.Transition(output.Resource, ResourceState.UnorderedAccess, ResourceState.CopySource),
        ]);
        commands.CopyBuffer(output, 0, readback, 0, 4);
        GpuCompletion completion = device.Submit(QueueType.Compute, [commands.Finish()]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        Span<byte> actual = stackalloc byte[4];
        device.ReadBuffer(readback, 0, actual);
        // CPU arguments contribute 2 + 1. The GPU-written arguments contribute only the
        // second record without a count buffer; the GPU-written count of one exposes only the
        // zero-dispatch first record. A stale/ignored GPU count would produce five instead.
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(actual));
        AssertNoNativeErrors(device);
    }

    private static void AssertRasterIndirect(bool indexed)
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP indirect-draw lane must execute on Windows.");
        using Device device = CreateDevice();
        ShaderHandle vertex = device.CreateShader(RasterShader(
            new ShaderArtifactKey(0x1A01, 0x1A02, 0x1A03, indexed ? 0x1A14u : 0x1A04u),
            ShaderStage.Vertex,
            "VSMain",
            "triangle.vs.dxil"));
        ShaderHandle pixel = device.CreateShader(RasterShader(
            new ShaderArtifactKey(0x1B01, 0x1B02, 0x1B03, indexed ? 0x1B14u : 0x1B04u),
            ShaderStage.Pixel,
            "PSMain",
            "triangle.ps.dxil"));
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            Array.Empty<BindGroupLayoutHandle>(),
            Array.Empty<PushConstantRange>()));
        PipelineHandle pipeline = device.CreateRasterPipeline(new RasterPipelineDesc(
            layout,
            vertex,
            pixel,
            new[] { Format.R8G8B8A8UNorm },
            Rasterizer: new RasterizerDesc(Cull: CullMode.None),
            BlendAttachments: new[]
            {
                new BlendAttachmentDesc(
                    Enabled: false,
                    SourceColor: BlendFactor.One,
                    DestinationColor: BlendFactor.Zero,
                    ColorOperation: BlendOperation.Add,
                    SourceAlpha: BlendFactor.One,
                    DestinationAlpha: BlendFactor.Zero,
                    AlphaOperation: BlendOperation.Add,
                    WriteMask: ColorWriteMask.All),
            }));

        TextureDesc textureDescription = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment | TextureUsage.CopySource);
        TextureHandle cpuTexture = device.CreateTexture(textureDescription);
        TextureHandle gpuZeroTexture = device.CreateTexture(textureDescription);
        TextureHandle gpuArgumentsTexture = device.CreateTexture(textureDescription);
        TextureHandle gpuCountTexture = device.CreateTexture(textureDescription);
        TextureViewHandle cpuView = device.CreateTextureView(new TextureViewDesc(
            cpuTexture,
            TextureSubresourceRange.WholeColor,
            TextureViewUsage.ColorAttachment));
        TextureViewHandle gpuZeroView = device.CreateTextureView(new TextureViewDesc(
            gpuZeroTexture,
            TextureSubresourceRange.WholeColor,
            TextureViewUsage.ColorAttachment));
        TextureViewHandle gpuArgumentsView = device.CreateTextureView(new TextureViewDesc(
            gpuArgumentsTexture,
            TextureSubresourceRange.WholeColor,
            TextureViewUsage.ColorAttachment));
        TextureViewHandle gpuCountView = device.CreateTextureView(new TextureViewDesc(
            gpuCountTexture,
            TextureSubresourceRange.WholeColor,
            TextureViewUsage.ColorAttachment));
        TextureCopyRegion region = new(0, 0, TextureAspect.Color, 8, 8);
        TextureCopyFootprint footprint = device.GetTextureCopyFootprint(textureDescription, region);
        BufferHandle cpuReadback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);
        BufferHandle zeroReadback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);
        BufferHandle gpuArgumentsReadback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);
        BufferHandle gpuCountReadback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);

        BufferHandle args = device.CreateBuffer(new BufferDesc(64, BufferUsage.Indirect), MemoryType.Upload);
        byte[] argumentData = new byte[64];
        if (indexed) WriteDrawIndexed(argumentData.AsSpan(8, 20), 3, 1, 0, 0, 0);
        else WriteDraw(argumentData.AsSpan(8, 16), 3, 1, 0, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(argumentData.AsSpan(40, 4), 0);
        device.WriteBuffer(args, 0, argumentData);

        BufferHandle gpuArguments = device.CreateBuffer(new BufferDesc(
            64,
            BufferUsage.ShaderWrite | BufferUsage.Indirect));
        BufferHandle gpuCount = device.CreateBuffer(new BufferDesc(
            4,
            BufferUsage.ShaderWrite | BufferUsage.Indirect));
        IndirectProducer producer = CreateProducer(
            device,
            gpuArguments,
            gpuCount,
            indexed ? "GenerateDrawIndexed" : "GenerateDraw",
            indexed ? 0x1D40u : 0x1D30u);

        BufferHandle indexBuffer = default;
        if (indexed)
        {
            indexBuffer = device.CreateBuffer(new BufferDesc(6, BufferUsage.Index), MemoryType.Upload);
            byte[] indices = new byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(0, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(2, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(indices.AsSpan(4, 2), 2);
            device.WriteBuffer(indexBuffer, 0, indices);
        }

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics, indexed ? "draw-indexed-indirect" : "draw-indirect");
        commands.Barriers([
            ResourceBarrier.Transition(cpuTexture.Resource, ResourceState.Common, ResourceState.RenderTarget),
            ResourceBarrier.Transition(gpuZeroTexture.Resource, ResourceState.Common, ResourceState.RenderTarget),
            ResourceBarrier.Transition(gpuArgumentsTexture.Resource, ResourceState.Common, ResourceState.RenderTarget),
            ResourceBarrier.Transition(gpuCountTexture.Resource, ResourceState.Common, ResourceState.RenderTarget),
            ResourceBarrier.Transition(gpuArguments.Resource, ResourceState.Common, ResourceState.UnorderedAccess),
            ResourceBarrier.Transition(gpuCount.Resource, ResourceState.Common, ResourceState.UnorderedAccess),
        ]);
        commands.SetPipeline(producer.Pipeline);
        commands.SetBindGroup(0, producer.Group);
        commands.Dispatch(1, 1, 1);
        commands.Barriers([
            ResourceBarrier.UnorderedAccess(gpuArguments.Resource),
            ResourceBarrier.UnorderedAccess(gpuCount.Resource),
            ResourceBarrier.Transition(gpuArguments.Resource, ResourceState.UnorderedAccess, ResourceState.IndirectArgument),
            ResourceBarrier.Transition(gpuCount.Resource, ResourceState.UnorderedAccess, ResourceState.IndirectArgument),
        ]);
        commands.SetPipeline(pipeline);
        commands.SetViewport(new Viewport(0, 0, 8, 8));
        commands.SetScissor(new Rect(0, 0, 8, 8));
        if (indexed) commands.SetIndexBuffer(indexBuffer, 0, IndexFormat.UInt16);

        commands.BeginRendering(new RenderingInfo(
            new[] { new ColorAttachment(cpuView, LoadAction.Clear, StoreAction.Store) },
            null,
            8,
            8));
        if (indexed) commands.DrawIndexedIndirect(args, 8, 1, 20);
        else commands.DrawIndirect(args, 8, 1, 16);
        commands.EndRendering();

        commands.BeginRendering(new RenderingInfo(
            new[] { new ColorAttachment(gpuZeroView, LoadAction.Clear, StoreAction.Store) },
            null,
            8,
            8));
        if (indexed) commands.DrawIndexedIndirect(args, 8, 1, 20, args, 40);
        else commands.DrawIndirect(args, 8, 1, 16, args, 40);
        commands.EndRendering();

        commands.BeginRendering(new RenderingInfo(
            new[] { new ColorAttachment(gpuArgumentsView, LoadAction.Clear, StoreAction.Store) },
            null,
            8,
            8));
        if (indexed) commands.DrawIndexedIndirect(gpuArguments, 8, 2, 20);
        else commands.DrawIndirect(gpuArguments, 8, 2, 16);
        commands.EndRendering();

        commands.BeginRendering(new RenderingInfo(
            new[] { new ColorAttachment(gpuCountView, LoadAction.Clear, StoreAction.Store) },
            null,
            8,
            8));
        if (indexed) commands.DrawIndexedIndirect(gpuArguments, 8, 2, 20, gpuCount, 0);
        else commands.DrawIndirect(gpuArguments, 8, 2, 16, gpuCount, 0);
        commands.EndRendering();

        commands.Barriers([
            ResourceBarrier.Transition(cpuTexture.Resource, ResourceState.RenderTarget, ResourceState.CopySource),
            ResourceBarrier.Transition(gpuZeroTexture.Resource, ResourceState.RenderTarget, ResourceState.CopySource),
            ResourceBarrier.Transition(gpuArgumentsTexture.Resource, ResourceState.RenderTarget, ResourceState.CopySource),
            ResourceBarrier.Transition(gpuCountTexture.Resource, ResourceState.RenderTarget, ResourceState.CopySource),
        ]);
        commands.CopyTextureToBuffer(new TextureBufferCopy(cpuTexture, region, cpuReadback, footprint.Layout));
        commands.CopyTextureToBuffer(new TextureBufferCopy(gpuZeroTexture, region, zeroReadback, footprint.Layout));
        commands.CopyTextureToBuffer(new TextureBufferCopy(gpuArgumentsTexture, region, gpuArgumentsReadback, footprint.Layout));
        commands.CopyTextureToBuffer(new TextureBufferCopy(gpuCountTexture, region, gpuCountReadback, footprint.Layout));
        GpuCompletion completion = device.Submit(QueueType.Graphics, [commands.Finish()]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        byte[] cpuPixels = ReadTightRows(device, cpuReadback, footprint, 8 * 4, 8);
        byte[] zeroPixels = ReadTightRows(device, zeroReadback, footprint, 8 * 4, 8);
        byte[] gpuArgumentsPixels = ReadTightRows(device, gpuArgumentsReadback, footprint, 8 * 4, 8);
        byte[] gpuCountPixels = ReadTightRows(device, gpuCountReadback, footprint, 8 * 4, 8);
        GraphicsDiagnostic[] diagnostics = device.DrainDiagnostics();
        Assert.DoesNotContain(
            diagnostics,
            static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
        Assert.Contains(cpuPixels, static value => value != 0);
        Assert.All(zeroPixels, static value => Assert.Equal(0, value));
        Assert.Contains(gpuArgumentsPixels, static value => value != 0);
        Assert.All(gpuCountPixels, static value => Assert.Equal(0, value));
    }

    private static Device CreateDevice() => new(new Options
    {
        UseWarpAdapter = true,
        EnableDebugLayer = true,
        EnableGpuValidation = false,
    });

    private static ShaderDesc RasterShader(
        ShaderArtifactKey key,
        ShaderStage stage,
        string entryPoint,
        string fixture) => new(
            key,
            ShaderBinaryFormat.Dxil,
            stage,
            entryPoint,
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)),
            new ShaderInterface(
                Array.Empty<ShaderBinding>(),
                Array.Empty<PushConstantRange>(),
            1),
            $"test:{entryPoint}");

    private static IndirectProducer CreateProducer(
        Device device,
        BufferHandle arguments,
        BufferHandle count,
        string entryPoint,
        uint keySeed)
    {
        BufferViewHandle argumentsView = device.CreateBufferView(new BufferViewDesc(
            arguments,
            BufferRange.Whole,
            BindingKind.StorageBuffer,
            Stride: 4));
        BufferViewHandle countView = device.CreateBufferView(new BufferViewDesc(
            count,
            BufferRange.Whole,
            BindingKind.StorageBuffer,
            Stride: 4));
        BindGroupLayoutHandle groupLayout = device.CreateBindGroupLayout([
            new BindingDesc(0, BindingKind.StorageBuffer, 1, ShaderStage.Compute),
            new BindingDesc(1, BindingKind.StorageBuffer, 1, ShaderStage.Compute),
        ]);
        BindGroupHandle group = device.CreateBindGroup(groupLayout, [
            BindingWrite.Buffer(0, argumentsView),
            BindingWrite.Buffer(1, countView),
        ]);
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            new[] { groupLayout },
            Array.Empty<PushConstantRange>()));
        ShaderHandle shader = device.CreateShader(new ShaderDesc(
            new ShaderArtifactKey(keySeed, keySeed + 1, keySeed + 2, keySeed + 3),
            ShaderBinaryFormat.Dxil,
            ShaderStage.Compute,
            entryPoint,
            ProducerBytecode.Value[entryPoint],
            new ShaderInterface(
                new[]
                {
                    new ShaderBinding(0, 0, BindingKind.StorageBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadWrite, DeclaredEffect.Write),
                    new ShaderBinding(0, 1, BindingKind.StorageBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadWrite, DeclaredEffect.Write),
                },
                Array.Empty<PushConstantRange>(),
                ((ulong)keySeed << 32) | keySeed),
            $"test:{entryPoint}"));
        PipelineHandle pipeline = device.CreateComputePipeline(new ComputePipelineDesc(layout, shader));
        return new IndirectProducer(pipeline, group);
    }

    private static void WriteDraw(Span<byte> destination, uint vertices, uint instances, uint firstVertex, uint firstInstance)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, vertices);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], instances);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], firstVertex);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], firstInstance);
    }

    private static void WriteDrawIndexed(
        Span<byte> destination,
        uint indices,
        uint instances,
        uint firstIndex,
        int vertexOffset,
        uint firstInstance)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, indices);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], instances);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], firstIndex);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], vertexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], firstInstance);
    }

    private static void WriteDispatch(Span<byte> destination, uint x, uint y, uint z)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, x);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], y);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], z);
    }

    private static byte[] ReadTightRows(
        Device device,
        BufferHandle buffer,
        in TextureCopyFootprint footprint,
        int rowBytes,
        int rows)
    {
        byte[] result = new byte[rowBytes * rows];
        for (int row = 0; row < rows; row++)
        {
            device.ReadBuffer(
                buffer,
                footprint.Layout.Offset + (ulong)row * footprint.Layout.BytesPerRow,
                result.AsSpan(row * rowBytes, rowBytes));
        }
        return result;
    }

    private static void AssertNoNativeErrors(Device device) => Assert.DoesNotContain(
        device.DrainDiagnostics(),
        static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

    private static byte[] CompileSm62Compute()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), $"someengine-indirect-{Guid.NewGuid():N}");
        string shaderDirectory = Path.Combine(projectRoot, "assets", "Shaders");
        Directory.CreateDirectory(shaderDirectory);
        File.WriteAllText(Path.Combine(projectRoot, "Directory.Build.props"), "<Project />");
        string sourcePath = Path.Combine(shaderDirectory, "dispatch_indirect.slang");
        File.WriteAllText(sourcePath, ComputeSource);
        SourceMetaFiles.Save(
            sourcePath,
            new SourceMeta
            {
                SourceGuid = SourceGuid.New(),
                Importer = nameof(SlangShaderImporter),
                ImporterSettings = JsonSerializer.SerializeToElement(
                    new SlangShaderImporterSettings { CookProfile = SlangShaderCookProfiles.D3D12ShaderModel62Name },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }),
            });
        try
        {
            ShaderAsset asset = Assert.IsType<ShaderAsset>(
                Assert.Single(new SlangSourceImporter().Import(projectRoot, sourcePath)).Asset);
            return Assert.Single(
                asset.Variants!,
                static value => value.Backend == "dxil" && value.EntryPoint == "CSMain").Data!.Value.ToArray();
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static IReadOnlyDictionary<string, byte[]> CompileIndirectProducers()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), $"someengine-indirect-producer-{Guid.NewGuid():N}");
        string shaderDirectory = Path.Combine(projectRoot, "assets", "Shaders");
        Directory.CreateDirectory(shaderDirectory);
        File.WriteAllText(Path.Combine(projectRoot, "Directory.Build.props"), "<Project />");
        string sourcePath = Path.Combine(shaderDirectory, "indirect_producer.slang");
        File.WriteAllText(sourcePath, ProducerSource);
        SourceMetaFiles.Save(
            sourcePath,
            new SourceMeta
            {
                SourceGuid = SourceGuid.New(),
                Importer = nameof(SlangShaderImporter),
                ImporterSettings = JsonSerializer.SerializeToElement(
                    new SlangShaderImporterSettings { CookProfile = SlangShaderCookProfiles.D3D12ShaderModel62Name },
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }),
            });
        try
        {
            ShaderAsset asset = Assert.IsType<ShaderAsset>(
                Assert.Single(new SlangSourceImporter().Import(projectRoot, sourcePath)).Asset);
            IReadOnlyDictionary<string, byte[]> result = asset.Variants!
                .Where(static value => value.Backend == "dxil" && value.EntryPoint is not null)
                .ToDictionary(
                    static value => value.EntryPoint!,
                    static value => value.Data!.Value.ToArray(),
                    StringComparer.Ordinal);
            Assert.Equal(
                new[] { "GenerateDispatch", "GenerateDraw", "GenerateDrawIndexed" },
                result.Keys.Order(StringComparer.Ordinal));
            return result;
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
        }
    }

    private readonly record struct IndirectProducer(PipelineHandle Pipeline, BindGroupHandle Group);
}
