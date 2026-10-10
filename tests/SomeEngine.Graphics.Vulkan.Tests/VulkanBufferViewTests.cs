using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanBufferViewTests(VulkanTestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void PolicySeedReadsSelectedCountFromAlignedMinimumView(uint countIndex)
    {
        string source = File.ReadAllText(FindShader("cluster_traversal_seed_input.slang")) + """

            StructuredBuffer<uint> Counts;
            RWStructuredBuffer<uint> Results;
            ClusterTraversalSeedUniforms Seed;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() { Results[0] = ResolveClusterTraversalViewCount(Seed, Counts); }
            """;
        var shader = shaderCompiler.Compile(source, ("computeMain", SlangStage.Compute));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], shader.GetEntryCode(0)));
        uint alignment = Math.Max(4u, device.Capabilities.Limits.ShaderBufferAlignment);
        uint viewBytes = (countIndex + 1u) * sizeof(uint);
        using Buffer counts = backend.CreateBuffer(device,
            new BufferDesc(alignment + viewBytes, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(counts, MapType.Write, BufferRange.Whole))
        {
            mapped.Bytes.Fill(0xff);
            BitConverter.TryWriteBytes(mapped.Bytes.Slice(checked((int)(alignment + countIndex * sizeof(uint)))), 7u);
            mapped.Flush(mapped.Range);
        }
        using BufferSrv countView = backend.CreateBufferSrv(device,
            new BufferSrvDesc(counts, new BufferRange(alignment, viewBytes), StructureStride: sizeof(uint)));
        using Buffer output = backend.CreateBuffer(device,
            new BufferDesc(sizeof(uint), BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new BufferDesc(sizeof(uint), BufferUsages.CopyDestination), MemoryType.Readback);
        uint[] seed = [10u, 1u, 64u, 0u, 0u, countIndex, 0u, 0u];
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        ResourceBinding[] resources = [ResourceBinding.ReadOnlyBuffer(countView), ResourceBinding.WritableBuffer(outputView)];
        using CommandContext commands = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(commands);
        backend.Barrier(commands, new BufferBarrier(counts, counts.InitialSync, PipelineSync.ComputeShading,
            counts.InitialAccess, ResourceAccess.ShaderResource));
        backend.Barrier(commands, new BufferBarrier(output, output.InitialSync, PipelineSync.ComputeShading,
            output.InitialAccess, ResourceAccess.UnorderedAccess));
        backend.SetPipeline(commands, pipeline);
        backend.SetTransientParameterBindings(commands,
            new ParameterBlockBindings(globals, resources, MemoryMarshal.AsBytes(seed.AsSpan())));
        backend.Dispatch(commands, new DispatchArguments(1, 1, 1));
        backend.Barrier(commands, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(commands, new BufferCopy(output, 0, readback, 0, sizeof(uint)));
        using RecordedCommands recorded = backend.End(commands);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Graphics),
            new QueueSubmitDesc([], [], [recorded], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        Assert.Equal(7u, BitConverter.ToUInt32(result.Bytes));
    }

    [Fact]
    public void ShaderBufferViewsRejectOffsetsOutsideTheReportedAlignment()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        using Device device = backend.CreateDevice(new DeviceDesc(default, [new DeviceQueueDesc(QueueType.Graphics)]));
        uint alignment = Math.Max(1u, device.Capabilities.Limits.ShaderBufferAlignment);
        using Buffer buffer = backend.CreateBuffer(device,
            new BufferDesc(alignment * 2u + sizeof(uint), BufferUsages.ShaderRead | BufferUsages.ShaderWrite));
        using BufferSrv validRead = backend.CreateBufferSrv(device,
            new BufferSrvDesc(buffer, new BufferRange(alignment, sizeof(uint))));
        using BufferUav validWrite = backend.CreateBufferUav(device,
            new BufferUavDesc(buffer, new BufferRange(alignment, sizeof(uint))));
        if (alignment > 1u)
        {
            BufferRange invalid = new(alignment - 1u, sizeof(uint));
            Assert.Throws<ArgumentException>(() => backend.CreateBufferSrv(device, new BufferSrvDesc(buffer, invalid)));
            Assert.Throws<ArgumentException>(() => backend.CreateBufferUav(device, new BufferUavDesc(buffer, invalid)));
        }
    }

    private static string FindShader(string name)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "assets", "Shaders", name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Engine shader was not found.", name);
    }
}
