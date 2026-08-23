namespace SomeEngine.Graphics.Vulkan.Tests;

using SlangShaderSharp;
using Xunit;
using Xunit.Sdk;

public sealed class VulkanPipelineTests
{
    private const string Source = """
        float4 Tint;

        [shader("vertex")]
        float4 vertexMain(uint id : SV_VertexID) : SV_Position
        {
            const float2 positions[3] =
            {
                float2(0.0, 0.75),
                float2(0.75, -0.75),
                float2(-0.75, -0.75),
            };
            return float4(positions[id], 0, 1);
        }

        [shader("fragment")]
        float4 pixelMain() : SV_Target0
        {
            return Tint;
        }

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void computeMain(uint3 id : SV_DispatchThreadID)
        {
        }
        """;

    [Fact]
    public void Slang_spirv_creates_dynamic_rendering_graphics_and_compute_pipelines()
    {
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            Source,
            ("vertexMain", SlangStage.Vertex),
            ("pixelMain", SlangStage.Fragment),
            ("computeMain", SlangStage.Compute));
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Format[] colors = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blend = [new()];

        using Pipeline graphics = backend.CreateGraphicsPipeline(
            device,
            new GraphicsPipelineDesc(
                shader.Program,
                shader.Entries[0],
                shader.Entries[1],
                [],
                [],
                PrimitiveTopology.TriangleList,
                StripCut.Disabled,
                new RasterizerState(Cull: CullType.None),
                new MultisampleState(),
                new DepthStencilState(),
                new BlendState(blend),
                new AttachmentFormatSignature(colors, null),
                DynamicStates.Viewport | DynamicStates.Scissor));
        using Pipeline compute = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[2]));

        Assert.Equal(PipelineType.Graphics, graphics.Type);
        Assert.Equal(PipelineType.Compute, compute.Type);
    }

    [Fact]
    public void Multiple_compute_entries_execute_the_requested_spirv_entry_point()
    {
        const string source = """
            RWStructuredBuffer<uint> Output;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void writeEleven(uint3 id : SV_DispatchThreadID) { Output[0] = 11; }
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void writeTwentyTwo(uint3 id : SV_DispatchThreadID) { Output[0] = 22; }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("writeEleven", SlangStage.Compute),
            ("writeTwentyTwo", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using Pipeline first = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[0]));
        using Pipeline second = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[1]));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(8, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));

        backend.Begin(context);
        Record(first, 0);
        Record(second, sizeof(uint));
        using RecordedCommands commands = backend.End(context);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(11u, BitConverter.ToUInt32(mapped.Bytes[..4]));
        Assert.Equal(22u, BitConverter.ToUInt32(mapped.Bytes[4..8]));

        void Record(Pipeline pipeline, ulong readbackOffset)
        {
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(
                context,
                new ParameterBlockBindings(
                    globals,
                    [ResourceBinding.WritableBuffer(outputUav)],
                    []));
            backend.Barrier(context, new BufferBarrier(
                output,
                readbackOffset == 0 ? PipelineSync.None : PipelineSync.Copy,
                PipelineSync.ComputeShading,
                readbackOffset == 0 ? ResourceAccess.NoAccess : ResourceAccess.CopySource,
                ResourceAccess.UnorderedAccess));
            backend.Dispatch(context, new DispatchArguments(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(
                output,
                PipelineSync.ComputeShading,
                PipelineSync.Copy,
                ResourceAccess.UnorderedAccess,
                ResourceAccess.CopySource));
            backend.CopyBuffer(context, new BufferCopy(
                output,
                0,
                readback,
                readbackOffset,
                sizeof(uint)));
        }
    }

    [Fact]
    public void Compute_pipeline_materializes_a_scalar_static_sampler()
    {
        const string source = """
            Texture2D<float4> Input;
            SamplerState LinearSampler;
            RWStructuredBuffer<float4> Output;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                Output[0] = Input.SampleLevel(LinearSampler, float2(0.5, 0.5), 0);
            }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        VariableLayoutReflection global = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection data = global.TypeLayout.UnwrapArray();
        if (data.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            data = data.ElementTypeLayout.UnwrapArray();
        nint samplerRange = Enumerable.Range(0, checked((int)data.BindingRangeCount))
            .Select(static index => (nint)index)
            .Single(index =>
                (data.GetBindingRangeType(index) & SlangBindingType.BaseMask) ==
                SlangBindingType.Sampler);
        VariableReflection sampler = data.GetBindingRangeLeafVariable(samplerRange);
        SamplerDesc samplerDesc = new(
            FilterType.Linear,
            FilterType.Linear,
            FilterType.Linear,
            AddressType.ClampToEdge,
            AddressType.ClampToEdge,
            AddressType.ClampToEdge);
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(
                shader.Program,
                shader.Entries[0],
                StaticSamplers: new[] { new StaticSamplerBinding(sampler, samplerDesc) }));

        Assert.Equal(PipelineType.Compute, pipeline.Type);
    }

    [Fact]
    public void Global_unbounded_resource_and_sampler_tables_execute_through_published_indices()
    {
        const string source = """
            Texture2D<float4> bindlessTextures[];
            SamplerState bindlessSamplers[];
            RWStructuredBuffer<uint> outputValues;
            uint textureIndex;
            uint samplerIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                float sampled = bindlessTextures[textureIndex].SampleLevel(
                    bindlessSamplers[samplerIndex], float2(0.5), 0).x;
                outputValues[0] = asuint(sampled) + 0xBEEFu;
            }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using DescriptorTable textures = backend.CreateDescriptorTable(
            device,
            [new DescriptorSlotDesc(
                ResourceBindingType.TextureSrv,
                Format.R32Float,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(
            device,
            [new DescriptorSlotDesc(ResourceBindingType.Sampler)]);
        using Sampler sampler = backend.CreateSampler(
            device,
            new SamplerDesc(
                FilterType.Nearest,
                FilterType.Nearest,
                FilterType.Nearest,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge));
        using Texture texture = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                1,
                1,
                1,
                1,
                1,
                1,
                Format.R32Float,
                TextureUsages.Sampled | TextureUsages.CopyDestination));
        TextureSubresourceRange textureRange = new(
            0,
            1,
            0,
            1,
            TextureAspects.Color);
        using TextureSrv textureSrv = backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                texture,
                textureRange,
                Format.R32Float,
                TextureViewDimension.Texture2D));
        backend.WriteDescriptor(
            textures,
            0,
            ResourceBinding.SampledTexture(textureSrv));
        backend.WriteDescriptor(
            samplers,
            0,
            ResourceBinding.SampledWith(sampler));
        uint textureIndex = backend.GetDescriptorIndex(textures, 0).Value;
        uint samplerIndex = backend.GetDescriptorIndex(samplers, 0).Value;
        backend.PublishDescriptors(device);

        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[0]));
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(256, BufferUsages.CopySource),
            MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapped.Bytes.Clear();
            BitConverter.GetBytes(1.0f).CopyTo(mapped.Bytes);
            mapped.Flush(new BufferRange(0, sizeof(float)));
        }
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputUav = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                output,
                BufferRange.Whole,
                StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(4, BufferUsages.CopyDestination),
            MemoryType.Readback);
        byte[] ordinary = new byte[16];
        BitConverter.GetBytes(textureIndex).CopyTo(ordinary, 0);
        BitConverter.GetBytes(samplerIndex).CopyTo(ordinary, 4);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new TextureBarrier(
            texture,
            textureRange,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopyDestination,
            TextureLayout.Undefined,
            TextureLayout.CopyDestination));
        backend.CopyBufferToTexture(context, new BufferTextureCopy(
            upload,
            0,
            256,
            1,
            texture,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            1,
            1,
            1));
        backend.Barrier(context, new TextureBarrier(
            texture,
            textureRange,
            PipelineSync.Copy,
            PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination,
            ResourceAccess.ShaderResource,
            TextureLayout.CopyDestination,
            TextureLayout.ShaderResource));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.None,
            PipelineSync.ComputeShading,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(
                globals,
                [ResourceBinding.WritableBuffer(outputUav)],
                ordinary));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.ComputeShading,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 4));
        using RecordedCommands commands = backend.End(context);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        Assert.Equal(0x3F80_BEEFu, BitConverter.ToUInt32(result.Bytes));
    }

    [Fact]
    public void Bindless_publish_is_atomic_and_recorded_commands_retain_their_generation()
    {
        const string source = """
            StructuredBuffer<uint> bindlessValues[];
            RWStructuredBuffer<uint> outputValues;
            uint descriptorIndex;
            uint outputIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[outputIndex] = bindlessValues[descriptorIndex][0];
            }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using Buffer firstSource = CreateSource(11);
        using Buffer secondSource = CreateSource(22);
        using BufferSrv firstView = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(
                firstSource,
                BufferRange.Whole,
                StructureStride: sizeof(uint)));
        using BufferSrv secondView = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(
                secondSource,
                BufferRange.Whole,
                StructureStride: sizeof(uint)));
        using DescriptorTable table = backend.CreateDescriptorTable(
            device,
            [new DescriptorSlotDesc(
                ResourceBindingType.BufferSrv,
                StructureStride: sizeof(uint))]);
        uint descriptorIndex = backend.GetDescriptorIndex(table, 0).Value;
        backend.WriteDescriptor(
            table,
            0,
            ResourceBinding.ReadOnlyBuffer(firstView));
        backend.PublishDescriptors(device);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.Program, shader.Entries[0]));
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(12, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(
            device,
            new BufferUavDesc(
                output,
                BufferRange.Whole,
                StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(12, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 3));

        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.None,
            PipelineSync.ComputeShading,
            ResourceAccess.NoAccess,
            ResourceAccess.UnorderedAccess));
        RecordDispatch(0);
        using RecordedCommands firstCommands = backend.End(context);

        backend.WriteDescriptor(
            table,
            0,
            ResourceBinding.ReadOnlyBuffer(secondView));
        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.ComputeShading,
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.UnorderedAccess));
        RecordDispatch(1);
        using RecordedCommands unpublishedCommands = backend.End(context);

        backend.PublishDescriptors(device);
        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.ComputeShading,
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.UnorderedAccess));
        RecordDispatch(2);
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.ComputeShading,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 12));
        using RecordedCommands secondCommands = backend.End(context);

        table.Dispose();
        firstView.Dispose();
        secondView.Dispose();
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc(
                [],
                [],
                [firstCommands, unpublishedCommands, secondCommands],
                [],
                []));
        Assert.Equal(
            WaitStatus.Completed,
            backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        Assert.Equal(11u, BitConverter.ToUInt32(mapped.Bytes));
        Assert.Equal(11u, BitConverter.ToUInt32(mapped.Bytes[4..]));
        Assert.Equal(22u, BitConverter.ToUInt32(mapped.Bytes[8..]));

        Buffer CreateSource(uint value)
        {
            Buffer buffer = backend.CreateBuffer(
                device,
                new BufferDesc(sizeof(uint), BufferUsages.ShaderRead),
                MemoryType.Upload);
            using MappedBuffer mapped = backend.Map(buffer, MapType.Write, BufferRange.Whole);
            BitConverter.TryWriteBytes(mapped.Bytes, value);
            mapped.Flush(mapped.Range);
            return buffer;
        }

        void RecordDispatch(uint destinationIndex)
        {
            byte[] ordinary = new byte[16];
            BitConverter.TryWriteBytes(ordinary.AsSpan(0, 4), descriptorIndex);
            BitConverter.TryWriteBytes(ordinary.AsSpan(4, 4), destinationIndex);
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(
                context,
                new ParameterBlockBindings(
                    globals,
                    [ResourceBinding.WritableBuffer(outputView)],
                    ordinary));
            backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        }
    }

    [Fact]
    public void Conservative_rasterization_extension_creates_a_graphics_pipeline()
    {
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            Source,
            ("vertexMain", SlangStage.Vertex),
            ("pixelMain", SlangStage.Fragment));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Format[] colors = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blend = [new()];
        try
        {
            using Pipeline pipeline = backend.CreateGraphicsPipeline(
                device,
                new GraphicsPipelineDesc(
                    shader.Program,
                    shader.Entries[0],
                    shader.Entries[1],
                    [],
                    [],
                    PrimitiveTopology.TriangleList,
                    StripCut.Disabled,
                    new RasterizerState(
                        Cull: CullType.None,
                        ConservativeRasterization: true),
                    new MultisampleState(),
                    new DepthStencilState(),
                    new BlendState(blend),
                    new AttachmentFormatSignature(colors, null)));
            Assert.Equal(PipelineType.Graphics, pipeline.Type);
        }
        catch (NotSupportedException)
        {
            throw SkipException.ForSkip(
                "The Vulkan adapter does not expose VK_EXT_conservative_rasterization.");
        }
    }

    [Fact]
    public void Vertex_attribute_divisor_extension_accepts_non_unit_instance_rates()
    {
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            Source,
            ("vertexMain", SlangStage.Vertex),
            ("pixelMain", SlangStage.Fragment));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        VertexBufferLayout[] vertexBuffers =
        [
            new(
                BufferIndex: 0,
                Stride: 16,
                PerInstance: true,
                InstanceStepRate: 2),
        ];
        Format[] colors = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blend = [new()];
        try
        {
            using Pipeline pipeline = backend.CreateGraphicsPipeline(
                device,
                new GraphicsPipelineDesc(
                    shader.Program,
                    shader.Entries[0],
                    shader.Entries[1],
                    vertexBuffers,
                    [],
                    PrimitiveTopology.TriangleList,
                    StripCut.Disabled,
                    new RasterizerState(Cull: CullType.None),
                    new MultisampleState(),
                    new DepthStencilState(),
                    new BlendState(blend),
                    new AttachmentFormatSignature(colors, null)));
            Assert.Equal(PipelineType.Graphics, pipeline.Type);
        }
        catch (NotSupportedException)
        {
            throw SkipException.ForSkip(
                "The Vulkan adapter does not expose VK_EXT_vertex_attribute_divisor.");
        }
    }

    [Fact]
    public void Transform_feedback_pipeline_captures_partial_and_full_vertex_outputs()
    {
        const string source = """
            struct VertexOutput
            {
                float4 position : SV_Position;
                float4 value : TEXCOORD0;
            };
            [shader("vertex")]
            VertexOutput vertexMain(uint id : SV_VertexID)
            {
                const float2 positions[3] =
                {
                    float2(0.0, 0.75),
                    float2(0.75, -0.75),
                    float2(-0.75, -0.75),
                };
                VertexOutput result;
                result.position = float4(positions[id], 0, 1);
                result.value = float4(1, 2, 3, 4);
                return result;
            }
            [shader("fragment")]
            float4 pixelMain(float4 value : TEXCOORD0) : SV_Target0
            {
                return value;
            }
            """;
        using VulkanTestShaderProgram shader = VulkanTestShaderProgram.Compile(
            source,
            ("vertexMain", SlangStage.Vertex),
            ("pixelMain", SlangStage.Fragment));
        VariableLayoutReflection output = shader.Entries[0].ResultVarLayout;
        VariableLayoutReflection value = output.TypeLayout.GetFieldByIndex(1);
        StreamOutputElement[] elements =
        [
            StreamOutputElement.Output(value, 0, 1, 2, 0),
            StreamOutputElement.Gap(0, 1, 0),
            StreamOutputElement.Output(value, 0, 0, 4, 1),
        ];
        uint[] strides = [12, 16];
        StreamOutputState streamOutput = new(elements, strides, 0);
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        Format[] colorFormats = [Format.R8G8B8A8UNorm];
        BlendAttachmentState[] blend = [new()];
        Pipeline? pipeline = null;
        try
        {
            pipeline = backend.CreateGraphicsPipeline(
                device,
                new GraphicsPipelineDesc(
                    shader.Program,
                    shader.Entries[0],
                    shader.Entries[1],
                    [],
                    [],
                    PrimitiveTopology.TriangleList,
                    StripCut.Disabled,
                    new RasterizerState(Cull: CullType.None),
                    new MultisampleState(),
                    new DepthStencilState(),
                    new BlendState(blend),
                    new AttachmentFormatSignature(colorFormats, null),
                    streamOutput));
        }
        catch (NotSupportedException)
        {
            throw SkipException.ForSkip(
                "The Vulkan adapter does not expose VK_EXT_transform_feedback.");
        }
        using (pipeline)
        using (Texture color = backend.CreateTexture(
                   device,
                   new TextureDesc(
                       TextureDimension.Texture2D,
                       4,
                       4,
                       1,
                       1,
                       1,
                       1,
                       Format.R8G8B8A8UNorm,
                       TextureUsages.ColorAttachment)))
        using (ColorAttachmentView attachment = backend.CreateColorAttachmentView(
                   device,
                   new ColorAttachmentViewDesc(
                       color,
                       new TextureSubresourceRange(
                           0,
                           1,
                           0,
                           1,
                           TextureAspects.Color),
                       Format.R8G8B8A8UNorm,
                       TextureViewDimension.Texture2D)))
        using (Buffer partial = backend.CreateBuffer(
                   device,
                   new BufferDesc(
                       36,
                       BufferUsages.StreamOutput | BufferUsages.CopySource)))
        using (Buffer full = backend.CreateBuffer(
                   device,
                   new BufferDesc(
                       48,
                       BufferUsages.StreamOutput | BufferUsages.CopySource)))
        using (Buffer readback = backend.CreateBuffer(
                   device,
                   new BufferDesc(84, BufferUsages.CopyDestination),
                   MemoryType.Readback))
        using (QueryPool query = backend.CreateQueryPool(
                   device,
                   new QueryPoolDesc(
                       QueryType.StreamOutputStatistics,
                       QueueType.Graphics,
                       1)))
        using (Buffer queryReadback = backend.CreateBuffer(
                   device,
                   new BufferDesc(16, BufferUsages.QueryResolve),
                   MemoryType.Readback))
        using (CommandContext context = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Graphics, 0, 1)))
        {
            TextureSubresourceRange colorRange = new(
                0,
                1,
                0,
                1,
                TextureAspects.Color);
            backend.Begin(context);
            backend.Barrier(context, new TextureBarrier(
                color,
                colorRange,
                PipelineSync.None,
                PipelineSync.RenderTarget,
                ResourceAccess.NoAccess,
                ResourceAccess.RenderTarget,
                TextureLayout.Undefined,
                TextureLayout.RenderTarget));
            backend.Barrier(context, new BufferBarrier(
                partial,
                PipelineSync.None,
                PipelineSync.Draw,
                ResourceAccess.NoAccess,
                ResourceAccess.StreamOutput));
            backend.Barrier(context, new BufferBarrier(
                full,
                PipelineSync.None,
                PipelineSync.Draw,
                ResourceAccess.NoAccess,
                ResourceAccess.StreamOutput));
            ColorAttachmentDesc[] colors =
            [
                new(
                    attachment,
                    LoadType.Clear,
                    StoreType.Discard,
                    System.Numerics.Vector4.Zero),
            ];
            backend.BeginRendering(context, new RenderingDesc(colors, null, 4, 4));
            backend.BeginQuery(context, query, 0);
            backend.SetPipeline(context, pipeline);
            backend.SetViewports(context, [new Viewport(0, 0, 4, 4)]);
            backend.SetScissors(context, [new ScissorRect(0, 0, 4, 4)]);
            backend.SetStreamOutputBuffers(
                context,
                0,
                [
                    new StreamOutputBufferBinding(partial, 0, 36),
                    new StreamOutputBufferBinding(full, 0, 48),
                ]);
            backend.Draw(context, new DrawArguments(3, 1, 0, 0));
            backend.SetStreamOutputBuffers(context, 0, []);
            backend.EndQuery(context, query, 0);
            backend.EndRendering(context);
            backend.ResolveQueries(
                context,
                query,
                0,
                1,
                queryReadback,
                BufferRange.Whole);
            backend.Barrier(context, new BufferBarrier(
                partial,
                PipelineSync.Draw,
                PipelineSync.Copy,
                ResourceAccess.StreamOutput,
                ResourceAccess.CopySource));
            backend.Barrier(context, new BufferBarrier(
                full,
                PipelineSync.Draw,
                PipelineSync.Copy,
                ResourceAccess.StreamOutput,
                ResourceAccess.CopySource));
            backend.CopyBuffer(context, new BufferCopy(partial, 0, readback, 0, 36));
            backend.CopyBuffer(context, new BufferCopy(full, 0, readback, 36, 48));
            using RecordedCommands commands = backend.End(context);
            Queue queue = backend.GetQueue(device, QueueType.Graphics);
            QueueCompletion completion = backend.Submit(
                queue,
                new QueueSubmitDesc([], [], [commands], [], []));
            Assert.Equal(
                WaitStatus.Completed,
                backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
            using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
            mapped.Invalidate(mapped.Range);
            for (int vertex = 0; vertex < 3; vertex++)
            {
                int partialOffset = vertex * 12;
                Assert.Equal(2.0f, BitConverter.ToSingle(mapped.Bytes[partialOffset..]));
                Assert.Equal(3.0f, BitConverter.ToSingle(mapped.Bytes[(partialOffset + 4)..]));
                int fullOffset = 36 + vertex * 16;
                Assert.Equal(1.0f, BitConverter.ToSingle(mapped.Bytes[fullOffset..]));
                Assert.Equal(2.0f, BitConverter.ToSingle(mapped.Bytes[(fullOffset + 4)..]));
                Assert.Equal(3.0f, BitConverter.ToSingle(mapped.Bytes[(fullOffset + 8)..]));
                Assert.Equal(4.0f, BitConverter.ToSingle(mapped.Bytes[(fullOffset + 12)..]));
            }
            using MappedBuffer queryResult = backend.Map(
                queryReadback,
                MapType.Read,
                BufferRange.Whole);
            queryResult.Invalidate(queryResult.Range);
            Assert.Equal(1UL, BitConverter.ToUInt64(queryResult.Bytes));
            Assert.Equal(1UL, BitConverter.ToUInt64(queryResult.Bytes[8..]));
        }
    }
}
