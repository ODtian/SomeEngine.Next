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

public sealed class DescriptorPipelineTests
{
    private const string ComputeSource = """
        Texture2D<float4> UnusedTexture : register(t0, space0);
        SamplerState UnusedSampler : register(s1, space0);
        RWStructuredBuffer<uint> Output : register(u2, space0);
        StructuredBuffer<uint> Inputs[2] : register(t3, space1);
        cbuffer Constants : register(b5, space2)
        {
            uint Addend;
        };

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Output[0] = Inputs[0][0] + Inputs[1][0] + Addend;
        }
        """;

    [Fact]
    public void Warp_executes_multi_group_descriptor_arrays_push_constants_and_compute()
    {
        if (!OperatingSystem.IsWindows()) return;

        byte[] dxil = CompileSm62Compute();
        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            ResourceDescriptorsPerCommandList = 32,
            SamplerDescriptorsPerCommandList = 8,
        });

        BufferHandle upload = device.CreateBuffer(new BufferDesc(8, BufferUsage.CopySource), MemoryType.Upload);
        BufferHandle inputs = device.CreateBuffer(new BufferDesc(8, BufferUsage.ShaderRead | BufferUsage.CopyDestination));
        BufferHandle output = device.CreateBuffer(new BufferDesc(4, BufferUsage.ShaderWrite | BufferUsage.CopySource));
        BufferHandle readback = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopyDestination), MemoryType.Readback);
        TextureHandle texture = device.CreateTexture(new TextureDesc(1, 1, Format.R8G8B8A8UNorm, TextureUsage.Sampled));

        Span<byte> inputBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(inputBytes, 11);
        BinaryPrimitives.WriteUInt32LittleEndian(inputBytes[4..], 31);
        device.WriteBuffer(upload, 0, inputBytes);

        BufferViewHandle input0 = device.CreateBufferView(new BufferViewDesc(
            inputs, new BufferRange(0, 4), BindingKind.ReadOnlyBuffer, Stride: 4));
        BufferViewHandle input1 = device.CreateBufferView(new BufferViewDesc(
            inputs, new BufferRange(4, 4), BindingKind.ReadOnlyBuffer, Stride: 4));
        BufferViewHandle outputView = device.CreateBufferView(new BufferViewDesc(
            output, new BufferRange(0, 4), BindingKind.StorageBuffer, Stride: 4));
        TextureViewHandle textureView = device.CreateTextureView(new TextureViewDesc(
            texture,
            TextureSubresourceRange.WholeColor,
            TextureViewUsage.ShaderResource));
        SamplerHandle sampler = device.CreateSampler(new SamplerDesc(
            FilterMode.Nearest,
            FilterMode.Linear,
            FilterMode.Nearest,
            AddressMode.Repeat,
            AddressMode.Mirror,
            AddressMode.Border));

        BindGroupLayoutHandle group0Layout = device.CreateBindGroupLayout([
            new BindingDesc(0, BindingKind.SampledTexture, 1, ShaderStage.Compute),
            new BindingDesc(1, BindingKind.Sampler, 1, ShaderStage.Compute),
            new BindingDesc(2, BindingKind.StorageBuffer, 1, ShaderStage.Compute),
        ]);
        BindGroupLayoutHandle group1Layout = device.CreateBindGroupLayout([
            new BindingDesc(3, BindingKind.ReadOnlyBuffer, 2, ShaderStage.Compute),
        ]);
        BindGroupHandle group0 = device.CreateBindGroup(group0Layout, [
            BindingWrite.Texture(0, textureView),
            BindingWrite.SamplerValue(1, sampler),
            BindingWrite.Buffer(2, outputView),
        ]);
        PipelineLayoutHandle pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            new[] { group0Layout, group1Layout },
            new[] { new PushConstantRange(0, 4, ShaderStage.Compute, Register: 5, Space: 2) }));
        ShaderHandle shader = device.CreateShader(ComputeShader(dxil));
        PipelineHandle pipeline = device.CreateComputePipeline(new ComputePipelineDesc(
            pipelineLayout,
            shader,
            "descriptor-array-compute"));

        ICommandContext commands = device.AcquireCommandContext(QueueType.Compute, "descriptor-array-compute");
        commands.Barriers([
            ResourceBarrier.Transition(inputs.Resource, ResourceState.Common, ResourceState.CopyDestination),
        ]);
        commands.CopyBuffer(upload, 0, inputs, 0, 8);
        commands.Barriers([
            ResourceBarrier.Transition(inputs.Resource, ResourceState.CopyDestination, ResourceState.ShaderResource),
            ResourceBarrier.Transition(output.Resource, ResourceState.Common, ResourceState.UnorderedAccess),
            ResourceBarrier.Transition(texture.Resource, ResourceState.Common, ResourceState.ShaderResource),
        ]);
        commands.SetPipeline(pipeline);
        commands.SetBindGroup(0, group0);
        commands.SetBindings(1, group1Layout, [
            BindingWrite.Buffer(3, input0, 0),
            BindingWrite.Buffer(3, input1, 1),
        ]);
        Span<byte> push = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(push, 7);
        commands.SetPushConstants(pipelineLayout, ShaderStage.Compute, 0, push);
        commands.Dispatch(1, 1, 1);
        commands.Barriers([
            ResourceBarrier.UnorderedAccess(output.Resource),
            ResourceBarrier.Transition(output.Resource, ResourceState.UnorderedAccess, ResourceState.CopySource),
        ]);
        commands.CopyBuffer(output, 0, readback, 0, 4);
        CommandListHandle commandList;
        try
        {
            commandList = commands.Finish();
        }
        catch (Exception exception)
        {
            string diagnostics = string.Join(" | ", device.DrainDiagnostics().Select(static value => value.Message));
            try { commands.Dispose(); } catch { }
            throw new InvalidOperationException($"D3D12 command-list close failed. {diagnostics}", exception);
        }
        commands.Dispose();

        Assert.Throws<InvalidOperationException>(() => device.DestroyBufferView(input0));
        GpuCompletion completion = device.Submit(QueueType.Compute, [commandList]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));

        Span<byte> result = stackalloc byte[4];
        device.ReadBuffer(readback, 0, result);
        Assert.Equal(49u, BinaryPrimitives.ReadUInt32LittleEndian(result));
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);

        device.DestroyBindGroup(group0);
        device.DestroyPipeline(pipeline);
        device.DestroyPipelineLayout(pipelineLayout);
        device.DestroyShader(shader);
        device.DestroyBindGroupLayout(group1Layout);
        device.DestroyBindGroupLayout(group0Layout);
        device.DestroySampler(sampler);
        device.DestroyTextureView(textureView);
        device.DestroyBufferView(outputView);
        device.DestroyBufferView(input1);
        device.DestroyBufferView(input0);
        device.DestroyTexture(texture);
        device.DestroyBuffer(readback);
        device.DestroyBuffer(output);
        device.DestroyBuffer(inputs);
        device.DestroyBuffer(upload);
        device.CollectGarbage();
    }

    [Fact]
    public void Descriptor_rebinding_burns_through_the_command_allocation_capacity()
    {
        if (!OperatingSystem.IsWindows()) return;

        byte[] dxil = CompileSm62Compute();
        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            ResourceDescriptorsPerCommandList = 3,
            SamplerDescriptorsPerCommandList = 1,
        });
        BufferHandle inputs = device.CreateBuffer(new BufferDesc(8, BufferUsage.ShaderRead));
        BufferViewHandle input0 = device.CreateBufferView(new BufferViewDesc(
            inputs, new BufferRange(0, 4), BindingKind.ReadOnlyBuffer, Stride: 4));
        BufferViewHandle input1 = device.CreateBufferView(new BufferViewDesc(
            inputs, new BufferRange(4, 4), BindingKind.ReadOnlyBuffer, Stride: 4));
        BindGroupLayoutHandle group0 = device.CreateBindGroupLayout([
            new BindingDesc(0, BindingKind.SampledTexture, 1, ShaderStage.Compute),
            new BindingDesc(1, BindingKind.Sampler, 1, ShaderStage.Compute),
            new BindingDesc(2, BindingKind.StorageBuffer, 1, ShaderStage.Compute),
        ]);
        BindGroupLayoutHandle array = device.CreateBindGroupLayout([
            new BindingDesc(3, BindingKind.ReadOnlyBuffer, 2, ShaderStage.Compute),
        ]);
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            new[] { group0, array },
            new[] { new PushConstantRange(0, 4, ShaderStage.Compute, 5, 2) }));
        ShaderHandle shader = device.CreateShader(ComputeShader(dxil));
        PipelineHandle pipeline = device.CreateComputePipeline(new ComputePipelineDesc(layout, shader));

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Compute))
        {
            commands.SetPipeline(pipeline);
            BindingWrite[] writes = [
                BindingWrite.Buffer(3, input0, 0),
                BindingWrite.Buffer(3, input1, 1),
            ];
            commands.SetBindings(1, array, writes);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                commands.SetBindings(1, array, writes));
            Assert.Contains("descriptor heap is exhausted", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        device.DestroyPipeline(pipeline);
        device.DestroyShader(shader);
        device.DestroyPipelineLayout(layout);
        device.DestroyBindGroupLayout(array);
        device.DestroyBindGroupLayout(group0);
        device.DestroyBufferView(input1);
        device.DestroyBufferView(input0);
        device.DestroyBuffer(inputs);
        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            static item => item.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }

    private static ShaderDesc ComputeShader(byte[] dxil) => new(
        new ShaderArtifactKey(0xD3D1, 0x2C01, 0xB17D, 0xA22A),
        ShaderBinaryFormat.Dxil,
        ShaderStage.Compute,
        "CSMain",
        dxil,
        new ShaderInterface(
            new[]
            {
                new ShaderBinding(0, 0, BindingKind.SampledTexture, 1, ShaderStage.Compute, ReflectedAccess.ReadOnly, DeclaredEffect.Read),
                new ShaderBinding(0, 1, BindingKind.Sampler, 1, ShaderStage.Compute, ReflectedAccess.ReadOnly, DeclaredEffect.Read),
                new ShaderBinding(0, 2, BindingKind.StorageBuffer, 1, ShaderStage.Compute, ReflectedAccess.ReadWrite, DeclaredEffect.Write),
                new ShaderBinding(1, 3, BindingKind.ReadOnlyBuffer, 2, ShaderStage.Compute, ReflectedAccess.ReadOnly, DeclaredEffect.Read),
            },
            new[] { new PushConstantRange(0, 4, ShaderStage.Compute, 5, 2) },
            0xD3D1_2C01_B17D_A22A),
        "test:descriptor-array-compute");

    private static byte[] CompileSm62Compute()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), $"someengine-d3d12-bindings-{Guid.NewGuid():N}");
        string shaderDirectory = Path.Combine(projectRoot, "assets", "Shaders");
        Directory.CreateDirectory(shaderDirectory);
        File.WriteAllText(Path.Combine(projectRoot, "Directory.Build.props"), "<Project />");
        string sourcePath = Path.Combine(shaderDirectory, "descriptor_compute.slang");
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
