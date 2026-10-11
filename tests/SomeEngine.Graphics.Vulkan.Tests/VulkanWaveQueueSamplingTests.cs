using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanWaveQueueSamplingTests(VulkanTestCompiler shaderCompiler)
{
    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public void SampledAlphaPreservesWaveDistributedScanlines(byte alpha)
    {
        VulkanTestShader shader = shaderCompiler.Compile(
            ReadWaveQueue() + SamplingSource.Replace("READ_ALPHA",
                "AlbedoMap.SampleLevel(MaterialSampler, float2(.5, .5), 0).a"),
            ("computeMain", SlangStage.Compute));

        uint[] actual = DispatchAndRead(shader, alpha);

        Assert.Equal(ExpectedRows(alpha), actual);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public void LoadedAlphaPreservesWaveDistributedScanlines(byte alpha)
    {
        VulkanTestShader shader = shaderCompiler.Compile(
            ReadWaveQueue() + SamplingSource.Replace("READ_ALPHA", "AlbedoMap.Load(int3(0, 0, 0)).a"),
            ("computeMain", SlangStage.Compute));

        uint[] actual = DispatchAndRead(shader, alpha);

        Assert.Equal(ExpectedRows(alpha), actual);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public void SampledAlphaPreservesSparseWaveConsumers(byte alpha)
    {
        string source = SamplingSource.Replace("READ_ALPHA",
            "AlbedoMap.SampleLevel(MaterialSampler, float2(.5, .5), 0).a")
            .Replace("WaveQueue::DistributeWithUniform<VisibilitySampler, SamplingWork>(work, visibility);",
                "if ((lane & 3u) != 1u) WaveQueue::DistributeWithUniform<VisibilitySampler, SamplingWork>(work, visibility);");
        VulkanTestShader shader = shaderCompiler.Compile(
            ReadWaveQueue() + source, ("computeMain", SlangStage.Compute));

        uint[] actual = DispatchAndRead(shader, alpha);

        uint[] expected = ExpectedRows(alpha);
        for (int producer = 1; producer < 32; producer += 4)
            expected.AsSpan(producer * 8 * 4, 8 * 4).Fill(uint.MaxValue);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(7u)]
    [InlineData(16u)]
    [InlineData(30u)]
    [InlineData(31u)]
    public void OneActiveProducerWritesAllOfItsSubtasks(uint producer)
    {
        string source = SamplingSource.Replace("READ_ALPHA",
            "AlbedoMap.SampleLevel(MaterialSampler, float2(.5, .5), 0).a")
            .Replace("WaveQueue::DistributeWithUniform<VisibilitySampler, SamplingWork>(work, visibility);",
                 $"if (lane == {producer}u) WaveQueue::DistributeWithUniform<VisibilitySampler, SamplingWork>(work, visibility);")
            .Replace("work.Count = mode == 0u ? 0u : mode == 1u ? 1u : mode == 2u ? 3u : 7u;",
                "work.Count = 7u;");
        VulkanTestShader shader = shaderCompiler.Compile(
            ReadWaveQueue() + source, ("computeMain", SlangStage.Compute));

        uint[] actual = DispatchAndRead(shader, 255);

        uint[] expected = ExpectedRows(255, onlyProducer: producer);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EmptyProducerWaveLeavesOutputUntouched()
    {
        string source = SamplingSource.Replace("READ_ALPHA",
            "AlbedoMap.SampleLevel(MaterialSampler, float2(.5, .5), 0).a")
            .Replace("work.Count = mode == 0u ? 0u : mode == 1u ? 1u : mode == 2u ? 3u : 7u;",
                "work.Count = 0u;");
        VulkanTestShader shader = shaderCompiler.Compile(
            ReadWaveQueue() + source, ("computeMain", SlangStage.Compute));

        uint[] actual = DispatchAndRead(shader, 255);

        Assert.All(actual, value => Assert.Equal(uint.MaxValue, value));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)255)]
    public void NaturalWaveSizePreservesEveryProducerAcrossMultipleSubgroups(byte alpha)
    {
        const int producers = 128;
        string source = SamplingSource.Replace("READ_ALPHA",
            "AlbedoMap.SampleLevel(MaterialSampler, float2(.5, .5), 0).a")
            .Replace("[numthreads(32, 1, 1)]", "[numthreads(128, 1, 1)]")
            .Replace("[WaveSize(32)]", "")
            .Replace("SamplingWork work;",
                "Results[lane * 8u + 7u] = uint4(WaveGetLaneCount(), WaveGetLaneIndex(), 0u, 0u); SamplingWork work;");
        VulkanTestShader shader = shaderCompiler.Compile(
            ReadWaveQueue() + source, ("computeMain", SlangStage.Compute));

        uint[] actual = DispatchAndRead(shader, alpha, producers);
        var observedWidths = new HashSet<uint>();
        for (int producer = 0; producer < producers; producer++)
        {
            Span<uint> description = actual.AsSpan((producer * 8 + 7) * 4, 4);
            uint width = description[0];
            Assert.InRange(width, 4u, 128u);
            Assert.Equal(0u, width & (width - 1u));
            Assert.InRange(description[1], 0u, width - 1u);
            Assert.Equal(0u, description[2]);
            Assert.Equal(0u, description[3]);
            observedWidths.Add(width);
            description.Fill(uint.MaxValue);
        }
        Console.WriteLine($"Executed natural subgroup widths: {string.Join(", ", observedWidths.Order())}");
        Assert.Equal(ExpectedRows(alpha, producers), actual);
    }

    private static uint[] DispatchAndRead(VulkanTestShader shader, byte alpha, int producers = 32)
    {
        bool nativeValidation = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_LAYER_PATH"));
        using IGraphicsBackend backend = new ValidationLayer(
            VulkanGraphicsBackend.Create(new VulkanBackendOptions(EnableValidation: nativeValidation)),
            new ValidationOptions(NativeValidationDisabled: !nativeValidation));
        using Device device = backend.CreateDevice(
            new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));

        using Texture texture = backend.CreateTexture(device, new TextureDesc(
            TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination));
        TextureSubresourceRange textureRange = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv textureView = backend.CreateTextureSrv(device, new TextureSrvDesc(
            texture, textureRange, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D));
        using Sampler sampler = backend.CreateSampler(device, new SamplerDesc(
            FilterType.Linear, FilterType.Linear, FilterType.Linear,
            AddressType.Repeat, AddressType.Repeat, AddressType.Repeat));

        ulong resultBytes = checked((ulong)producers * 8 * 4 * sizeof(uint));
        using Buffer upload = backend.CreateBuffer(device,
            new BufferDesc(4 + resultBytes, BufferUsages.CopySource), MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapped.Bytes.Fill(0xff);
            mapped.Bytes[0] = 255;
            mapped.Bytes[1] = mapped.Bytes[2] = 0;
            mapped.Bytes[3] = alpha;
            mapped.Flush(mapped.Range);
        }
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(resultBytes,
                BufferUsages.CopyDestination | BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: 4 * sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(resultBytes, BufferUsages.CopyDestination), MemoryType.Readback);
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        ResourceBinding[] globalResources = [ResourceBinding.WritableBuffer(outputView)];
        ResourceBinding[] entryResources =
            [ResourceBinding.SampledTexture(textureView), ResourceBinding.SampledWith(sampler)];
        using PersistentParameterBindings globalBindings = backend.CreatePersistentParameterBindings(device, pipeline,
            new ParameterBlockBindings(globals, globalResources, []));
        using PersistentParameterBindings entryBindings = backend.CreatePersistentParameterBindings(device, pipeline,
            new ParameterBlockBindings(shader.Entries[0].VarLayout, entryResources, []));

        using CommandContext commands = backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(commands);
        backend.Barrier(commands, new BufferBarrier(upload, upload.InitialSync, PipelineSync.Copy,
            upload.InitialAccess, ResourceAccess.CopySource));
        backend.Barrier(commands, new BufferBarrier(output, output.InitialSync, PipelineSync.Copy,
            output.InitialAccess, ResourceAccess.CopyDestination));
        backend.CopyBuffer(commands, new BufferCopy(upload, 4, output, 0, resultBytes));
        backend.Barrier(commands, new TextureBarrier(texture, textureRange,
            PipelineSync.None, PipelineSync.Copy, ResourceAccess.NoAccess, ResourceAccess.CopyDestination,
            TextureLayout.Undefined, TextureLayout.CopyDestination));
        backend.CopyBufferToTexture(commands, new BufferTextureCopy(
            upload, 0, 0, 0, texture, 0, 0, TextureAspects.Color, 0, 0, 0, 1, 1, 1));
        backend.Barrier(commands, new TextureBarrier(texture, textureRange,
            PipelineSync.Copy, PipelineSync.ComputeShading, ResourceAccess.CopyDestination, ResourceAccess.ShaderResource,
            TextureLayout.CopyDestination, TextureLayout.ShaderResource));
        backend.Barrier(commands, new BufferBarrier(output, PipelineSync.Copy, PipelineSync.ComputeShading,
            ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
        backend.SetPipeline(commands, pipeline);
        backend.SetPersistentParameterBindings(commands, globalBindings);
        backend.SetPersistentParameterBindings(commands, entryBindings);
        backend.Dispatch(commands, new DispatchArguments(1, 1, 1));
        backend.Barrier(commands, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(commands, new BufferCopy(output, 0, readback, 0, resultBytes));
        using RecordedCommands recorded = backend.End(commands);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        Assert.Equal(DeviceStatus.Active, device.Status);
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        return MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray();
    }

    private static uint[] ExpectedRows(byte alpha, int producers = 32, uint? onlyProducer = null)
    {
        uint[] expected = new uint[producers * 8 * 4];
        Array.Fill(expected, uint.MaxValue);
        for (uint producer = 0; producer < producers; producer++)
        {
            uint count = onlyProducer is uint selected ? (producer == selected ? 7u : 0u) :
                (producer & 3u) switch { 0 => 0, 1 => 1, 2 => 3, _ => 7 };
            for (uint local = 0; local < count; local++)
            {
                uint width = 1 + (producer + local) % 9;
                uint accepted = 0;
                for (uint x = 0; x < width; x++)
                    if ((x + producer + local) % 3 != 0 && alpha >= 128)
                        accepted++;
                int first = checked((int)((producer * 8 + local) * 4));
                expected[first] = producer;
                expected[first + 1] = local;
                expected[first + 2] = width;
                expected[first + 3] = accepted;
            }
        }
        return expected;
    }

    private static string ReadWaveQueue()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "assets", "Shaders", "wave_queue.slang");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException("Engine wave shader was not found.", "wave_queue.slang");
    }

    private const string SamplingSource = """

        struct VisibilitySampler
        {
            Texture2D<float4> AlbedoMap;
            SamplerState MaterialSampler;
            float evaluate() { return READ_ALPHA; }
        };
        RWStructuredBuffer<uint4> Results;
        struct SamplingWork : IWaveUniformTask<VisibilitySampler>
        {
            uint Count;
            uint Producer;
            [mutating] uint GetTaskCount() { return Count; }
            [mutating] void ExecuteTask(uniform VisibilitySampler visibility,
                uint sourceLane, uint localIndex, bool active)
            {
                // Fetch the producer while every queue consumer still participates.
                uint producer = WaveReadLaneAt(Producer, sourceLane);
                if (active)
                {
                    uint width = 1u + ((producer + localIndex) % 9u);
                    uint accepted = 0u;
                    [loop]
                    for (uint x = 0u; x < width; x++)
                    {
                        if ((x + producer + localIndex) % 3u == 0u)
                            continue;
                        accepted += visibility.evaluate() >= .5 ? 1u : 0u;
                    }
                    Results[producer * 8u + localIndex] = uint4(producer, localIndex, width, accepted);
                }
            }
        };
        [shader("compute")]
        [numthreads(32, 1, 1)]
        [WaveSize(32)]
        void computeMain(uniform VisibilitySampler visibility, uint lane : SV_GroupIndex)
        {
            SamplingWork work;
            uint mode = lane & 3u;
            work.Count = mode == 0u ? 0u : mode == 1u ? 1u : mode == 2u ? 3u : 7u;
            work.Producer = lane;
            WaveQueue::DistributeWithUniform<VisibilitySampler, SamplingWork>(work, visibility);
        }
        """;
}
