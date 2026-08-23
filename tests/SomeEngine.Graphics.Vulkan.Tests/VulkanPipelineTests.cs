namespace SomeEngine.Graphics.Vulkan.Tests;

using SlangShaderSharp;
using Xunit;

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
}
