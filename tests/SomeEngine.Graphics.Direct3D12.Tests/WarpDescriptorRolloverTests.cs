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

public sealed class DescriptorRolloverTests
{
    private const string ComputeSource = """
        RWStructuredBuffer<uint> Output : register(u0, space0);
        StructuredBuffer<uint> Input : register(t1, space0);

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Output[0] = Input[0] + 1;
        }
        """;

    private static readonly Lazy<byte[]> ComputeBytecode = new(CompileSm62Compute);

    [Fact]
    public void Warp_switches_heaps_replays_active_bindings_and_retires_old_heaps()
    {
        AssertRolloverPreservesOutput(churnSamplers: false, materializations: 4_097);
    }

    [Fact]
    public void Concurrent_contexts_roll_over_independently_beyond_4096_resources_and_256_samplers()
    {
        AssertConcurrentRolloverPreservesOutputs();
    }

    private static void AssertRolloverPreservesOutput(bool churnSamplers, int materializations)
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "The required Direct3D12/WARP descriptor rollover lane must run; it may not silently skip.");

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
            ResourceDescriptorsPerCommandList = 4_096,
            SamplerDescriptorsPerCommandList = 256,
        });

        BufferHandle upload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle input = device.CreateBuffer(new BufferDesc(4, BufferUsage.ShaderRead | BufferUsage.CopyDestination));
        BufferHandle output = device.CreateBuffer(new BufferDesc(4, BufferUsage.ShaderWrite | BufferUsage.CopySource));
        BufferHandle readback = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        Span<byte> inputBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(inputBytes, 41);
        device.WriteBuffer(upload, 0, inputBytes);

        BufferViewHandle inputView = device.CreateBufferView(new BufferViewDesc(
            input,
            BufferRange.Whole,
            BindingKind.ReadOnlyBuffer,
            Stride: 4));
        BufferViewHandle outputView = device.CreateBufferView(new BufferViewDesc(
            output,
            BufferRange.Whole,
            BindingKind.StorageBuffer,
            Stride: 4));
        SamplerHandle sampler = device.CreateSampler(new SamplerDesc(
            FilterMode.Nearest,
            FilterMode.Nearest,
            FilterMode.Nearest,
            AddressMode.Clamp,
            AddressMode.Clamp,
            AddressMode.Clamp));

        BindGroupLayoutHandle stableLayout = device.CreateBindGroupLayout([
            new BindingDesc(0, BindingKind.StorageBuffer, 1, ShaderStage.Compute),
            new BindingDesc(1, BindingKind.ReadOnlyBuffer, 1, ShaderStage.Compute),
        ]);
        BindGroupLayoutHandle churnLayout = device.CreateBindGroupLayout(churnSamplers
            ? [new BindingDesc(0, BindingKind.Sampler, 1, ShaderStage.Compute)]
            : [new BindingDesc(0, BindingKind.ReadOnlyBuffer, 1, ShaderStage.Compute)]);
        BindGroupHandle stable = device.CreateBindGroup(stableLayout, [
            BindingWrite.Buffer(0, outputView),
            BindingWrite.Buffer(1, inputView),
        ]);
        BindGroupHandle churn = device.CreateBindGroup(churnLayout, churnSamplers
            ? [BindingWrite.SamplerValue(0, sampler)]
            : [BindingWrite.Buffer(0, inputView)]);
        PipelineLayoutHandle pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            new[] { stableLayout, churnLayout },
            Array.Empty<PushConstantRange>()));
        ShaderHandle shader = device.CreateShader(new ShaderDesc(
            new ShaderArtifactKey(0xDD01, 0xDD02, 0xDD03, 0xDD04),
            ShaderBinaryFormat.Dxil,
            ShaderStage.Compute,
            "CSMain",
            ComputeBytecode.Value,
            new ShaderInterface(
                new[]
                {
                    new ShaderBinding(0, 0, BindingKind.StorageBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadWrite, DeclaredEffect.Write),
                    new ShaderBinding(0, 1, BindingKind.ReadOnlyBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadOnly, DeclaredEffect.Read),
                },
                Array.Empty<PushConstantRange>(),
                0xDD01_DD02_DD03_DD04),
            "test:descriptor-rollover"));
        PipelineHandle pipeline = device.CreateComputePipeline(new ComputePipelineDesc(
            pipelineLayout,
            shader,
            "descriptor-rollover"));

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Compute, "descriptor-rollover");
        commands.Barriers([
            ResourceBarrier.Transition(input.Resource, ResourceState.Common, ResourceState.CopyDestination),
        ]);
        commands.CopyBuffer(upload, 0, input, 0, 4);
        commands.Barriers([
            ResourceBarrier.Transition(input.Resource, ResourceState.CopyDestination, ResourceState.ShaderResource),
            ResourceBarrier.Transition(output.Resource, ResourceState.Common, ResourceState.UnorderedAccess),
        ]);
        commands.SetPipeline(pipeline);
        commands.SetBindGroup(0, stable);
        for (int index = 0; index < materializations; index++) commands.SetBindGroup(1, churn);
        commands.Dispatch(1, 1, 1);
        commands.Barriers([
            ResourceBarrier.UnorderedAccess(output.Resource),
            ResourceBarrier.Transition(output.Resource, ResourceState.UnorderedAccess, ResourceState.CopySource),
        ]);
        commands.CopyBuffer(output, 0, readback, 0, 4);
        Assert.True(((CommandContext)commands).DescriptorPageCount > 1);
        GpuCompletion completion = device.Submit(QueueType.Compute, [commands.Finish()]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        Span<byte> result = stackalloc byte[4];
        device.ReadBuffer(readback, 0, result);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(result));
        Assert.True(device.CollectGarbage() >= 1);
        using (ICommandContext recycled = device.AcquireCommandContext(QueueType.Compute, "descriptor-rollover.recycled"))
            Assert.Equal(1, ((CommandContext)recycled).DescriptorPageCount);
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }

    private static void AssertConcurrentRolloverPreservesOutputs()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "The required Direct3D12/WARP descriptor rollover lane must run; it may not silently skip.");

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
            ResourceDescriptorsPerCommandList = 4_096,
            SamplerDescriptorsPerCommandList = 256,
        });

        BufferHandle upload = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle input = device.CreateBuffer(new BufferDesc(4, BufferUsage.ShaderRead | BufferUsage.CopyDestination));
        BufferHandle firstOutput = device.CreateBuffer(new BufferDesc(4, BufferUsage.ShaderWrite | BufferUsage.CopySource));
        BufferHandle secondOutput = device.CreateBuffer(new BufferDesc(4, BufferUsage.ShaderWrite | BufferUsage.CopySource));
        BufferHandle firstReadback = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        BufferHandle secondReadback = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        Span<byte> inputBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(inputBytes, 41);
        device.WriteBuffer(upload, 0, inputBytes);

        using (ICommandContext prepare = device.AcquireCommandContext(QueueType.Compute, "descriptor-rollover.prepare"))
        {
            prepare.Barriers([ResourceBarrier.Transition(input.Resource, ResourceState.Common, ResourceState.CopyDestination)]);
            prepare.CopyBuffer(upload, 0, input, 0, 4);
            prepare.Barriers([ResourceBarrier.Transition(input.Resource, ResourceState.CopyDestination, ResourceState.ShaderResource)]);
            GpuCompletion prepared = device.Submit(QueueType.Compute, [prepare.Finish()]);
            Assert.True(device.Wait(prepared, TimeSpan.FromSeconds(10)));
        }
        Assert.True(device.CollectGarbage() >= 1);

        BufferViewHandle inputView = device.CreateBufferView(new BufferViewDesc(
            input,
            BufferRange.Whole,
            BindingKind.ReadOnlyBuffer,
            Stride: 4));
        BufferViewHandle firstOutputView = device.CreateBufferView(new BufferViewDesc(
            firstOutput,
            BufferRange.Whole,
            BindingKind.StorageBuffer,
            Stride: 4));
        BufferViewHandle secondOutputView = device.CreateBufferView(new BufferViewDesc(
            secondOutput,
            BufferRange.Whole,
            BindingKind.StorageBuffer,
            Stride: 4));
        SamplerHandle sampler = device.CreateSampler(new SamplerDesc(
            FilterMode.Nearest,
            FilterMode.Nearest,
            FilterMode.Nearest,
            AddressMode.Clamp,
            AddressMode.Clamp,
            AddressMode.Clamp));

        BindGroupLayoutHandle stableLayout = device.CreateBindGroupLayout([
            new BindingDesc(0, BindingKind.StorageBuffer, 1, ShaderStage.Compute),
            new BindingDesc(1, BindingKind.ReadOnlyBuffer, 1, ShaderStage.Compute),
        ]);
        BindGroupLayoutHandle churnLayout = device.CreateBindGroupLayout([
            new BindingDesc(0, BindingKind.ReadOnlyBuffer, 1, ShaderStage.Compute),
            new BindingDesc(1, BindingKind.Sampler, 1, ShaderStage.Compute),
        ]);
        BindGroupHandle firstStable = device.CreateBindGroup(stableLayout, [
            BindingWrite.Buffer(0, firstOutputView),
            BindingWrite.Buffer(1, inputView),
        ]);
        BindGroupHandle secondStable = device.CreateBindGroup(stableLayout, [
            BindingWrite.Buffer(0, secondOutputView),
            BindingWrite.Buffer(1, inputView),
        ]);
        BindGroupHandle churn = device.CreateBindGroup(churnLayout, [
            BindingWrite.Buffer(0, inputView),
            BindingWrite.SamplerValue(1, sampler),
        ]);
        PipelineLayoutHandle pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            new[] { stableLayout, churnLayout },
            Array.Empty<PushConstantRange>()));
        ShaderHandle shader = device.CreateShader(new ShaderDesc(
            new ShaderArtifactKey(0xDD11, 0xDD12, 0xDD13, 0xDD14),
            ShaderBinaryFormat.Dxil,
            ShaderStage.Compute,
            "CSMain",
            ComputeBytecode.Value,
            new ShaderInterface(
                new[]
                {
                    new ShaderBinding(0, 0, BindingKind.StorageBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadWrite, DeclaredEffect.Write),
                    new ShaderBinding(0, 1, BindingKind.ReadOnlyBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadOnly, DeclaredEffect.Read),
                },
                Array.Empty<PushConstantRange>(),
                0xDD11_DD12_DD13_DD14),
            "test:descriptor-rollover.concurrent"));
        PipelineHandle pipeline = device.CreateComputePipeline(new ComputePipelineDesc(
            pipelineLayout,
            shader,
            "descriptor-rollover.concurrent"));

        ICommandContext firstContext = device.AcquireCommandContext(QueueType.Compute, "descriptor-rollover.worker-0");
        ICommandContext secondContext = device.AcquireCommandContext(QueueType.Compute, "descriptor-rollover.worker-1");
        Task<(CommandListHandle Handle, int Pages)> firstTask = Task.Run(() => Record(
            firstContext,
            firstOutput,
            firstReadback,
            pipeline,
            firstStable,
            churn));
        Task<(CommandListHandle Handle, int Pages)> secondTask = Task.Run(() => Record(
            secondContext,
            secondOutput,
            secondReadback,
            pipeline,
            secondStable,
            churn));
        Task.WaitAll(firstTask, secondTask);

        Assert.True(firstTask.Result.Pages > 1);
        Assert.True(secondTask.Result.Pages > 1);
        GpuCompletion completion = device.Submit(
            QueueType.Compute,
            [firstTask.Result.Handle, secondTask.Result.Handle]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(20)));

        Span<byte> firstResult = stackalloc byte[4];
        Span<byte> secondResult = stackalloc byte[4];
        device.ReadBuffer(firstReadback, 0, firstResult);
        device.ReadBuffer(secondReadback, 0, secondResult);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(firstResult));
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(secondResult));

        Assert.True(device.CollectGarbage() >= 2);
        using ICommandContext firstRecycled = device.AcquireCommandContext(QueueType.Compute, "descriptor-rollover.recycled-0");
        using ICommandContext secondRecycled = device.AcquireCommandContext(QueueType.Compute, "descriptor-rollover.recycled-1");
        Assert.Equal(1, ((CommandContext)firstRecycled).DescriptorPageCount);
        Assert.Equal(1, ((CommandContext)secondRecycled).DescriptorPageCount);
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static diagnostic => diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

        static (CommandListHandle Handle, int Pages) Record(
            ICommandContext commands,
            BufferHandle output,
            BufferHandle readback,
            PipelineHandle pipeline,
            BindGroupHandle stable,
            BindGroupHandle churn)
        {
            using (commands)
            {
                commands.Barriers([ResourceBarrier.Transition(output.Resource, ResourceState.Common, ResourceState.UnorderedAccess)]);
                commands.SetPipeline(pipeline);
                commands.SetBindGroup(0, stable);
                for (int index = 0; index < 4_097; index++) commands.SetBindGroup(1, churn);
                commands.Dispatch(1, 1, 1);
                commands.Barriers([
                    ResourceBarrier.UnorderedAccess(output.Resource),
                    ResourceBarrier.Transition(output.Resource, ResourceState.UnorderedAccess, ResourceState.CopySource),
                ]);
                commands.CopyBuffer(output, 0, readback, 0, 4);
                int pages = ((CommandContext)commands).DescriptorPageCount;
                return (commands.Finish(), pages);
            }
        }
    }

    private static byte[] CompileSm62Compute()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), $"someengine-d3d12-rollover-{Guid.NewGuid():N}");
        string shaderDirectory = Path.Combine(projectRoot, "assets", "Shaders");
        Directory.CreateDirectory(shaderDirectory);
        File.WriteAllText(Path.Combine(projectRoot, "Directory.Build.props"), "<Project />");
        string sourcePath = Path.Combine(shaderDirectory, "descriptor_rollover.slang");
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
            var variant = Assert.Single(
                asset.Variants!,
                static value => string.Equals(value.Backend, "dxil", StringComparison.Ordinal) &&
                                string.Equals(value.EntryPoint, "CSMain", StringComparison.Ordinal));
            return variant.Data!.Value.ToArray();
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
        }
    }
}
