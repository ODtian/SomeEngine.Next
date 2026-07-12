using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using Xunit;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;
using D3DDevice = SomeEngine.Graphics.Direct3D12.Device;
using D3DOptions = SomeEngine.Graphics.Direct3D12.Options;

namespace SomeEngine.RenderGraph.Tests;

public sealed class IndirectWarpEndToEndTests
{
    private const int Width = 32;
    private const int Height = 32;
    private const ulong DrawArgumentBytes = 2 * DrawIndirectArguments.ByteSize;
    private const ulong DrawIndexedArgumentBytes = 2 * DrawIndexedIndirectArguments.ByteSize;
    private const ulong DispatchArgumentBytes = 2 * DispatchIndirectArguments.ByteSize;
    private const ulong CountBytes = sizeof(uint);
    private const ulong DispatchOutputBytes = 2 * sizeof(uint);

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Gpu_producer_and_count_drive_all_graph_indirect_commands_on_warp()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP indirect lane must execute on Windows.");
        string directory = Path.Combine(
            FindProjectRoot(),
            ".artifacts",
            "indirect-warp-end-to-end-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ShaderAsset producerAsset = Import(directory, "indirect_producer.slang", ProducerSource);
            ShaderDesc drawProducer = Project(producerAsset, "GenerateDraw", AssetShaderStage.Compute);
            ShaderDesc indexedProducer = Project(producerAsset, "GenerateDrawIndexed", AssetShaderStage.Compute);
            ShaderDesc dispatchProducer = Project(producerAsset, "GenerateDispatch", AssetShaderStage.Compute);

            ShaderAsset rasterAsset = Import(directory, "indirect_raster.slang", RasterSource);
            ShaderDesc vertex = Project(rasterAsset, "VSMain", AssetShaderStage.Vertex);
            ShaderDesc pixel = Project(rasterAsset, "PSMain", AssetShaderStage.Pixel);

            ShaderAsset dispatchAsset = Import(directory, "indirect_dispatch.slang", DispatchSource);
            ShaderDesc dispatch = Project(dispatchAsset, "CSMain", AssetShaderStage.Compute);

            AssertProducerInterface(drawProducer);
            AssertProducerInterface(indexedProducer);
            AssertProducerInterface(dispatchProducer);
            ShaderBinding dispatchOutput = Assert.Single(dispatch.Interface.Bindings.ToArray());
            Assert.Equal(BindingKind.StorageBuffer, dispatchOutput.Kind);
            Assert.Equal(DeclaredEffect.Write, dispatchOutput.DeclaredEffect);

            using D3DDevice device = new(new D3DOptions
            {
                UseWarpAdapter = true,
                EnableDebugLayer = true,
                EnableGpuValidation = false,
            });
            using PipelineResources pipelines = new(
                device,
                drawProducer,
                indexedProducer,
                dispatchProducer,
                vertex,
                pixel,
                dispatch);

            ExecuteRasterIndirect(device, pipelines, drawProducer, pipelines.DrawProducerPipeline, indexed: false);
            ExecuteRasterIndirect(device, pipelines, indexedProducer, pipelines.IndexedProducerPipeline, indexed: true);
            ExecuteDispatchIndirect(device, pipelines, dispatchProducer);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void ExecuteRasterIndirect(
        D3DDevice device,
        PipelineResources pipelines,
        in ShaderDesc producerDescription,
        PipelineHandle producerPipeline,
        bool indexed)
    {
        ulong argumentBytes = indexed ? DrawIndexedArgumentBytes : DrawArgumentBytes;
        uint commandStride = indexed ? DrawIndexedIndirectArguments.ByteSize : DrawIndirectArguments.ByteSize;
        BufferHandle readback = default;
        BufferHandle indexBuffer = default;
        try
        {
            TextureDesc colorDescription = new(
                Width,
                Height,
                Format.R8G8B8A8UNorm,
                TextureUsage.ColorAttachment | TextureUsage.CopySource,
                Name: indexed ? "rg-indexed-indirect-color" : "rg-draw-indirect-color");
            TextureCopyRegion copyRegion = new(0, 0, TextureAspect.Color, Width, Height);
            TextureCopyFootprint footprint = device.GetTextureCopyFootprint(colorDescription, copyRegion);
            readback = device.CreateBuffer(
                new BufferDesc(
                    footprint.RequiredBufferSize,
                    BufferUsage.CopyDestination,
                    indexed ? "rg-indexed-indirect-readback" : "rg-draw-indirect-readback"),
                MemoryType.Readback);

            if (indexed)
            {
                indexBuffer = device.CreateBuffer(
                    new BufferDesc(3 * sizeof(ushort), BufferUsage.Index, "rg-indirect-index-buffer"),
                    MemoryType.Upload);
                device.WriteBuffer(indexBuffer, 0, new byte[] { 0, 0, 1, 0, 2, 0 });
            }

            using (RenderGraph graph = new(device, new RenderGraphOptions
            {
                CompileOptimizedPlansAsynchronously = false,
            }))
            {
                GraphBuilder builder = graph.Begin();
                BufferId arguments = builder.CreateBuffer(new BufferDesc(
                    argumentBytes,
                    BufferUsage.ShaderWrite | BufferUsage.Indirect,
                    indexed ? "rg-indexed-indirect-arguments" : "rg-draw-indirect-arguments"));
                BufferId count = builder.CreateBuffer(new BufferDesc(
                    CountBytes,
                    BufferUsage.ShaderWrite | BufferUsage.Indirect,
                    indexed ? "rg-indexed-indirect-count" : "rg-draw-indirect-count"));
                BufferId producerScratch = builder.CreateBuffer(new BufferDesc(
                    DispatchOutputBytes,
                    BufferUsage.ShaderWrite,
                    "rg-indirect-producer-scratch"));
                TextureId color = builder.CreateTexture(colorDescription);
                TextureViewId colorView = builder.CreateTextureView(
                    color,
                    TextureSubresourceRange.WholeColor,
                    TextureViewUsage.ColorAttachment,
                    name: indexed ? "rg-indexed-indirect-rtv" : "rg-draw-indirect-rtv");
                BufferId destination = builder.ImportBuffer(
                    readback,
                    BufferUse.CopyDestination,
                    BufferUse.CopyDestination,
                    contentsAvailable: false);
                BufferId indices = default;
                if (indexed)
                {
                    indices = builder.ImportBuffer(
                        indexBuffer,
                        BufferUse.Index,
                        BufferUse.Index,
                        contentsAvailable: true);
                }

                PassBuilder producer = builder.AddPass(
                    indexed ? "generate-indexed-indirect" : "generate-draw-indirect",
                    QueueSelection.Compute);
                IndirectProducerShaderParameters producerParameters = new()
                {
                    Arguments = WriteBuffer(arguments, argumentBytes, "indirect-arguments-uav"),
                    Count = WriteBuffer(count, CountBytes, "indirect-count-uav"),
                    Output = WriteBuffer(producerScratch, DispatchOutputBytes, "indirect-scratch-uav"),
                };
                ShaderParameterBinding producerPairing = new(
                    producerDescription,
                    pipelines.ProducerPipelineLayout,
                    new[] { pipelines.ProducerGroupLayout });
                GeneratedParameterSet producerBindings = producerParameters.Pair(
                    ref builder,
                    ref producer,
                    producerPairing);
                producer.UsesPipeline(producerPipeline);
                IndirectProducerShaderParameters frozenProducerParameters = producerParameters;
                producer.Execute((ICommandContext commands, in PassResources resources) =>
                {
                    commands.SetPipeline(producerPipeline);
                    frozenProducerParameters.Bind(producerBindings, commands, resources);
                    commands.Dispatch(1, 1, 1);
                });

                PassBuilder raster = builder.AddPass(
                    indexed ? "consume-indexed-indirect" : "consume-draw-indirect",
                    QueueSelection.Graphics);
                _ = raster.ColorAttachment(0, colorView, LoadAction.Clear, new System.Numerics.Vector4(0, 0, 0, 1));
                BufferAccess argumentAccess = raster.Read(
                    arguments,
                    BufferUse.Indirect,
                    new BufferRange(0, argumentBytes));
                BufferAccess countAccess = raster.Read(
                    count,
                    BufferUse.Indirect,
                    new BufferRange(0, CountBytes));
                BufferAccess indexAccess = default;
                if (indexed)
                {
                    indexAccess = raster.Read(
                        indices,
                        BufferUse.Index,
                        new BufferRange(0, 3 * sizeof(ushort)));
                }
                raster.UsesShader(pipelines.VertexDescription);
                raster.UsesShader(pipelines.PixelDescription);
                raster.UsesPipeline(pipelines.RasterPipeline);
                raster.Execute((ICommandContext commands, in PassResources resources) =>
                {
                    commands.SetViewport(new Viewport(0, 0, Width, Height));
                    commands.SetScissor(new Rect(0, 0, Width, Height));
                    commands.SetPipeline(pipelines.RasterPipeline);
                    BufferHandle argumentHandle = resources.Get(argumentAccess);
                    BufferHandle countHandle = resources.Get(countAccess);
                    if (indexed)
                    {
                        commands.SetIndexBuffer(resources.Get(indexAccess), 0, IndexFormat.UInt16);
                        commands.DrawIndexedIndirect(
                            argumentHandle,
                            0,
                            maxCommandCount: 2,
                            commandStride,
                            countHandle,
                            countBufferOffset: 0);
                    }
                    else
                    {
                        commands.DrawIndirect(
                            argumentHandle,
                            0,
                            maxCommandCount: 2,
                            commandStride,
                            countHandle,
                            countBufferOffset: 0);
                    }
                });

                PassBuilder copy = builder.AddPass(
                    indexed ? "indexed-indirect-readback" : "draw-indirect-readback",
                    QueueSelection.Graphics);
                TextureAccess source = copy.Read(
                    color,
                    TextureUse.CopySource,
                    TextureSubresourceRange.WholeColor);
                BufferAccess target = copy.Write(destination, BufferUse.CopyDestination);
                copy.Execute((ICommandContext commands, in PassResources resources) =>
                    commands.CopyTextureToBuffer(new TextureBufferCopy(
                        resources.Get(source),
                        copyRegion,
                        resources.Get(target),
                        footprint.Layout)));

                GraphExecution execution = graph.Execute(ref builder);
                Assert.True(execution.Wait(TimeSpan.FromSeconds(10)));
            }

            byte[] pixels = ReadTightRows(device, readback, footprint, Width * 4, Height);
            Assert.True(
                ColorEnergy(pixels, LeftTriangleX, Height / 2) > 100,
                $"The GPU-counted {(indexed ? "DrawIndexedIndirect" : "DrawIndirect")} command produced no left triangle.");
            Assert.InRange(
                ColorEnergy(pixels, RightTriangleX, Height / 2),
                0,
                3);
            AssertNoNativeErrors(device);
        }
        finally
        {
            if (indexBuffer.IsValid) device.DestroyBuffer(indexBuffer);
            if (readback.IsValid) device.DestroyBuffer(readback);
            device.CollectGarbage();
        }
    }

    private static void ExecuteDispatchIndirect(
        D3DDevice device,
        PipelineResources pipelines,
        in ShaderDesc producerDescription)
    {
        BufferHandle readback = default;
        try
        {
            readback = device.CreateBuffer(
                new BufferDesc(DispatchOutputBytes, BufferUsage.CopyDestination, "rg-dispatch-indirect-readback"),
                MemoryType.Readback);
            using (RenderGraph graph = new(device, new RenderGraphOptions
            {
                CompileOptimizedPlansAsynchronously = false,
            }))
            {
                GraphBuilder builder = graph.Begin();
                BufferId arguments = builder.CreateBuffer(new BufferDesc(
                    DispatchArgumentBytes,
                    BufferUsage.ShaderWrite | BufferUsage.Indirect,
                    "rg-dispatch-indirect-arguments"));
                BufferId count = builder.CreateBuffer(new BufferDesc(
                    CountBytes,
                    BufferUsage.ShaderWrite | BufferUsage.Indirect,
                    "rg-dispatch-indirect-count"));
                BufferId output = builder.CreateBuffer(new BufferDesc(
                    DispatchOutputBytes,
                    BufferUsage.ShaderWrite | BufferUsage.CopySource,
                    "rg-dispatch-indirect-output"));
                BufferId destination = builder.ImportBuffer(
                    readback,
                    BufferUse.CopyDestination,
                    BufferUse.CopyDestination,
                    contentsAvailable: false);

                PassBuilder producer = builder.AddPass("generate-dispatch-indirect", QueueSelection.Compute);
                IndirectProducerShaderParameters producerParameters = new()
                {
                    Arguments = WriteBuffer(arguments, DispatchArgumentBytes, "dispatch-arguments-uav"),
                    Count = WriteBuffer(count, CountBytes, "dispatch-count-uav"),
                    Output = WriteBuffer(output, DispatchOutputBytes, "dispatch-output-initializer-uav"),
                };
                ShaderParameterBinding producerPairing = new(
                    producerDescription,
                    pipelines.ProducerPipelineLayout,
                    new[] { pipelines.ProducerGroupLayout });
                GeneratedParameterSet producerBindings = producerParameters.Pair(
                    ref builder,
                    ref producer,
                    producerPairing);
                producer.UsesPipeline(pipelines.DispatchProducerPipeline);
                IndirectProducerShaderParameters frozenProducerParameters = producerParameters;
                producer.Execute((ICommandContext commands, in PassResources resources) =>
                {
                    commands.SetPipeline(pipelines.DispatchProducerPipeline);
                    frozenProducerParameters.Bind(producerBindings, commands, resources);
                    commands.Dispatch(1, 1, 1);
                });

                PassBuilder consumer = builder.AddPass("consume-dispatch-indirect", QueueSelection.Compute);
                BufferAccess argumentAccess = consumer.Read(
                    arguments,
                    BufferUse.Indirect,
                    new BufferRange(0, DispatchArgumentBytes));
                BufferAccess countAccess = consumer.Read(
                    count,
                    BufferUse.Indirect,
                    new BufferRange(0, CountBytes));
                IndirectDispatchShaderParameters consumerParameters = new()
                {
                    Output = new BufferParameter(
                        output,
                        new BufferRange(0, DispatchOutputBytes),
                        BindingKind.StorageBuffer,
                        BufferUse.ShaderWrite,
                        ResourceEffect.Write,
                        Stride: sizeof(uint),
                        PriorContents: PriorContents.Required,
                        Coverage: WriteCoverage.Partial,
                        Name: "dispatch-indirect-output-uav"),
                };
                ShaderParameterBinding consumerPairing = new(
                    pipelines.DispatchDescription,
                    pipelines.DispatchPipelineLayout,
                    new[] { pipelines.DispatchGroupLayout });
                GeneratedParameterSet consumerBindings = consumerParameters.Pair(
                    ref builder,
                    ref consumer,
                    consumerPairing);
                consumer.UsesPipeline(pipelines.DispatchPipeline);
                IndirectDispatchShaderParameters frozenConsumerParameters = consumerParameters;
                consumer.Execute((ICommandContext commands, in PassResources resources) =>
                {
                    commands.SetPipeline(pipelines.DispatchPipeline);
                    frozenConsumerParameters.Bind(consumerBindings, commands, resources);
                    commands.DispatchIndirect(
                        resources.Get(argumentAccess),
                        0,
                        2,
                        DispatchIndirectArguments.ByteSize,
                        resources.Get(countAccess),
                        0);
                });

                PassBuilder copy = builder.AddPass("dispatch-indirect-readback", QueueSelection.Compute);
                BufferAccess source = copy.Read(
                    output,
                    BufferUse.CopySource,
                    new BufferRange(0, DispatchOutputBytes));
                BufferAccess target = copy.Write(
                    destination,
                    BufferUse.CopyDestination,
                    new BufferRange(0, DispatchOutputBytes));
                copy.Execute((ICommandContext commands, in PassResources resources) =>
                    commands.CopyBuffer(
                        resources.Get(source),
                        0,
                        resources.Get(target),
                        0,
                        DispatchOutputBytes));

                GraphExecution execution = graph.Execute(ref builder);
                Assert.True(execution.Wait(TimeSpan.FromSeconds(10)));
            }

            byte[] actual = new byte[DispatchOutputBytes];
            device.ReadBuffer(readback, 0, actual);
            Assert.Equal(1u, BitConverter.ToUInt32(actual, 0));
            Assert.Equal(0u, BitConverter.ToUInt32(actual, sizeof(uint)));
            AssertNoNativeErrors(device);
        }
        finally
        {
            if (readback.IsValid) device.DestroyBuffer(readback);
            device.CollectGarbage();
        }
    }

    private static int LeftTriangleX => (int)MathF.Round(Width * 0.275f);
    private static int RightTriangleX => (int)MathF.Round(Width * 0.725f);

    private static BufferParameter WriteBuffer(BufferId resource, ulong size, string name) => new(
        resource,
        new BufferRange(0, size),
        BindingKind.StorageBuffer,
        BufferUse.ShaderWrite,
        ResourceEffect.Write,
        Stride: sizeof(uint),
        PriorContents: PriorContents.Discard,
        Coverage: WriteCoverage.Full,
        Name: name);

    private static byte[] ReadTightRows(
        D3DDevice device,
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

    private static int ColorEnergy(byte[] pixels, int x, int y)
    {
        int offset = checked((y * Width + x) * 4);
        return pixels[offset] + pixels[offset + 1] + pixels[offset + 2];
    }

    private static ShaderAsset Import(string directory, string fileName, string source)
    {
        string sourcePath = Path.Combine(directory, fileName);
        File.WriteAllText(sourcePath, source);
        return SlangShaderImporter.ImportTransient(sourcePath, SlangShaderCookProfiles.D3D12ShaderModel62);
    }

    private static ShaderDesc Project(ShaderAsset asset, string entryPoint, AssetShaderStage stage) =>
        ShaderAssetProjection.Dxil(asset, entryPoint, stage);

    private static void AssertProducerInterface(in ShaderDesc description)
    {
        ShaderBinding[] bindings = description.Interface.Bindings.ToArray();
        Assert.Equal(3, bindings.Length);
        Assert.All(bindings, static binding =>
        {
            Assert.Equal(BindingKind.StorageBuffer, binding.Kind);
            Assert.Equal(DeclaredEffect.Write, binding.DeclaredEffect);
        });
    }

    private static void AssertNoNativeErrors(D3DDevice device) => Assert.DoesNotContain(
        device.DrainDiagnostics(),
        static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

    private static string FindProjectRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "SomeEngine.slnx"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }
        throw new DirectoryNotFoundException("Could not locate SomeEngine.slnx for shader imports.");
    }

    private sealed class PipelineResources : IDisposable
    {
        private readonly D3DDevice _device;
        private ShaderHandle _drawProducerShader;
        private ShaderHandle _indexedProducerShader;
        private ShaderHandle _dispatchProducerShader;
        private ShaderHandle _vertexShader;
        private ShaderHandle _pixelShader;
        private ShaderHandle _dispatchShader;

        public PipelineResources(
            D3DDevice device,
            in ShaderDesc drawProducer,
            in ShaderDesc indexedProducer,
            in ShaderDesc dispatchProducer,
            in ShaderDesc vertex,
            in ShaderDesc pixel,
            in ShaderDesc dispatch)
        {
            _device = device;
            VertexDescription = vertex;
            PixelDescription = pixel;
            DispatchDescription = dispatch;

            ProducerGroupLayout = CreateGroupLayout(device, drawProducer);
            ProducerPipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                new[] { ProducerGroupLayout },
                Array.Empty<PushConstantRange>(),
                "rg-indirect-producer-layout"));
            _drawProducerShader = device.CreateShader(drawProducer);
            _indexedProducerShader = device.CreateShader(indexedProducer);
            _dispatchProducerShader = device.CreateShader(dispatchProducer);
            DrawProducerPipeline = device.CreateComputePipeline(new ComputePipelineDesc(
                ProducerPipelineLayout,
                _drawProducerShader,
                "rg-draw-indirect-producer"));
            IndexedProducerPipeline = device.CreateComputePipeline(new ComputePipelineDesc(
                ProducerPipelineLayout,
                _indexedProducerShader,
                "rg-indexed-indirect-producer"));
            DispatchProducerPipeline = device.CreateComputePipeline(new ComputePipelineDesc(
                ProducerPipelineLayout,
                _dispatchProducerShader,
                "rg-dispatch-indirect-producer"));

            RasterPipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                Array.Empty<BindGroupLayoutHandle>(),
                Array.Empty<PushConstantRange>(),
                "rg-indirect-raster-layout"));
            _vertexShader = device.CreateShader(vertex);
            _pixelShader = device.CreateShader(pixel);
            RasterPipeline = device.CreateRasterPipeline(new RasterPipelineDesc(
                RasterPipelineLayout,
                _vertexShader,
                _pixelShader,
                new[] { Format.R8G8B8A8UNorm },
                Topology: PrimitiveTopology.TriangleList,
                Rasterizer: new RasterizerDesc(
                    FillMode.Solid,
                    CullMode.None,
                    FrontFace.CounterClockwise,
                    DepthClip: true),
                DepthStencil: new DepthStencilDesc(false, false, CompareOp.Always),
                BlendAttachments: new[]
                {
                    new BlendAttachmentDesc(
                        false,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOperation.Add,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOperation.Add,
                        ColorWriteMask.All),
                },
                Name: "rg-indirect-raster-pipeline"));

            DispatchGroupLayout = CreateGroupLayout(device, dispatch);
            DispatchPipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                new[] { DispatchGroupLayout },
                Array.Empty<PushConstantRange>(),
                "rg-indirect-dispatch-layout"));
            _dispatchShader = device.CreateShader(dispatch);
            DispatchPipeline = device.CreateComputePipeline(new ComputePipelineDesc(
                DispatchPipelineLayout,
                _dispatchShader,
                "rg-indirect-dispatch-pipeline"));
        }

        public ShaderDesc VertexDescription { get; }
        public ShaderDesc PixelDescription { get; }
        public ShaderDesc DispatchDescription { get; }
        public BindGroupLayoutHandle ProducerGroupLayout { get; }
        public PipelineLayoutHandle ProducerPipelineLayout { get; }
        public PipelineHandle DrawProducerPipeline { get; }
        public PipelineHandle IndexedProducerPipeline { get; }
        public PipelineHandle DispatchProducerPipeline { get; }
        public PipelineLayoutHandle RasterPipelineLayout { get; }
        public PipelineHandle RasterPipeline { get; }
        public BindGroupLayoutHandle DispatchGroupLayout { get; }
        public PipelineLayoutHandle DispatchPipelineLayout { get; }
        public PipelineHandle DispatchPipeline { get; }

        public void Dispose()
        {
            if (DispatchPipeline.IsValid) _device.DestroyPipeline(DispatchPipeline);
            if (_dispatchShader.IsValid) _device.DestroyShader(_dispatchShader);
            if (DispatchPipelineLayout.IsValid) _device.DestroyPipelineLayout(DispatchPipelineLayout);
            if (DispatchGroupLayout.IsValid) _device.DestroyBindGroupLayout(DispatchGroupLayout);
            if (RasterPipeline.IsValid) _device.DestroyPipeline(RasterPipeline);
            if (_pixelShader.IsValid) _device.DestroyShader(_pixelShader);
            if (_vertexShader.IsValid) _device.DestroyShader(_vertexShader);
            if (RasterPipelineLayout.IsValid) _device.DestroyPipelineLayout(RasterPipelineLayout);
            if (DispatchProducerPipeline.IsValid) _device.DestroyPipeline(DispatchProducerPipeline);
            if (IndexedProducerPipeline.IsValid) _device.DestroyPipeline(IndexedProducerPipeline);
            if (DrawProducerPipeline.IsValid) _device.DestroyPipeline(DrawProducerPipeline);
            if (_dispatchProducerShader.IsValid) _device.DestroyShader(_dispatchProducerShader);
            if (_indexedProducerShader.IsValid) _device.DestroyShader(_indexedProducerShader);
            if (_drawProducerShader.IsValid) _device.DestroyShader(_drawProducerShader);
            if (ProducerPipelineLayout.IsValid) _device.DestroyPipelineLayout(ProducerPipelineLayout);
            if (ProducerGroupLayout.IsValid) _device.DestroyBindGroupLayout(ProducerGroupLayout);
            _device.CollectGarbage();
        }

        private static BindGroupLayoutHandle CreateGroupLayout(D3DDevice device, in ShaderDesc description)
        {
            BindingDesc[] bindings = description.Interface.Bindings
                .ToArray()
                .OrderBy(static binding => binding.Binding)
                .Select(static binding => new BindingDesc(
                    binding.Binding,
                    binding.Kind,
                    binding.Count,
                    binding.Visibility))
                .ToArray();
            return device.CreateBindGroupLayout(bindings);
        }
    }

    private const string ProducerSource = """
        import resource_effects;

        [ResourceEffect(ResourceEffects.Write, ResourceOperations.None)]
        RWStructuredBuffer<uint> Arguments : register(u0, space0);

        [ResourceEffect(ResourceEffects.Write, ResourceOperations.None)]
        RWStructuredBuffer<uint> Count : register(u1, space0);

        [ResourceEffect(ResourceEffects.Write, ResourceOperations.None)]
        RWStructuredBuffer<uint> Output : register(u2, space0);

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void GenerateDraw(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Arguments[0] = 3; Arguments[1] = 1; Arguments[2] = 0; Arguments[3] = 0;
            Arguments[4] = 3; Arguments[5] = 1; Arguments[6] = 0; Arguments[7] = 1;
            Count[0] = 1;
            Output[0] = 0; Output[1] = 0;
        }

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void GenerateDrawIndexed(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Arguments[0] = 3; Arguments[1] = 1; Arguments[2] = 0; Arguments[3] = 0; Arguments[4] = 0;
            Arguments[5] = 3; Arguments[6] = 1; Arguments[7] = 0; Arguments[8] = 0; Arguments[9] = 1;
            Count[0] = 1;
            Output[0] = 0; Output[1] = 0;
        }

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void GenerateDispatch(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Arguments[0] = 1; Arguments[1] = 1; Arguments[2] = 1;
            Arguments[3] = 2; Arguments[4] = 1; Arguments[5] = 1;
            Count[0] = 1;
            Output[0] = 0; Output[1] = 0;
        }
        """;

    private const string RasterSource = """
        struct VertexOutput
        {
            float4 Position : SV_Position;
        };

        [shader("vertex")]
        VertexOutput VSMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
        {
            float2 localPosition = vertexId == 0
                ? float2(-0.25, -0.35)
                : (vertexId == 1 ? float2(0.0, 0.35) : float2(0.25, -0.35));
            float offset = instanceId == 0 ? -0.45 : 0.45;
            VertexOutput output;
            output.Position = float4(localPosition.x + offset, localPosition.y, 0.0, 1.0);
            return output;
        }

        [shader("pixel")]
        float4 PSMain() : SV_Target0
        {
            return float4(1.0, 0.25, 0.0, 1.0);
        }
        """;

    private const string DispatchSource = """
        import resource_effects;

        [ResourceEffect(ResourceEffects.Write, ResourceOperations.None)]
        RWStructuredBuffer<uint> Output : register(u0, space0);

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Output[dispatchThreadId.x] = dispatchThreadId.x + 1;
        }
        """;
}

[ShaderParameters]
internal partial struct IndirectProducerShaderParameters
{
    public BufferParameter Arguments;
    public BufferParameter Count;
    public BufferParameter Output;
}

[ShaderParameters]
internal partial struct IndirectDispatchShaderParameters
{
    public BufferParameter Output;
}
