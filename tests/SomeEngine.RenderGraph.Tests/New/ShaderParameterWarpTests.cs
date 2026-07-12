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

public sealed class ShaderParameterWarpTests
{
    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void Generated_binding_writes_imported_device_local_storage_buffer_on_warp()
    {
        string directory = Path.Combine(
            FindProjectRoot(),
            ".artifacts",
            "shader-parameter-warp-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "generated_storage_write.slang");
        File.WriteAllText(sourcePath, ShaderSource);
        try
        {
            ShaderAsset cooked = SlangShaderImporter.ImportTransient(
                sourcePath,
                SlangShaderCookProfiles.D3D12ShaderModel62);
            ShaderDesc shaderDescription = ShaderAssetProjection.Dxil(
                cooked,
                "Main",
                AssetShaderStage.Compute);
            ShaderBinding reflected = Assert.Single(shaderDescription.Interface.Bindings.ToArray());
            Assert.Equal(BindingKind.StorageBuffer, reflected.Kind);
            Assert.Equal(DeclaredEffect.Write, reflected.DeclaredEffect);

            using D3DDevice device = new(new D3DOptions
            {
                UseWarpAdapter = true,
                EnableDebugLayer = true,
            });
            BindGroupLayoutHandle groupLayout = default;
            PipelineLayoutHandle pipelineLayout = default;
            ShaderHandle shader = default;
            PipelineHandle pipeline = default;
            BufferHandle storage = default;
            BufferHandle readback = default;
            try
            {
                groupLayout = device.CreateBindGroupLayout(
                    [new BindingDesc(
                        reflected.Binding,
                        BindingKind.StorageBuffer,
                        1,
                        SomeEngine.Graphics.ShaderStage.Compute)]);
                pipelineLayout = device.CreatePipelineLayout(new PipelineLayoutDesc(
                    new[] { groupLayout },
                    Array.Empty<PushConstantRange>(),
                    "generated-storage-write-layout"));
                shader = device.CreateShader(shaderDescription);
                pipeline = device.CreateComputePipeline(new ComputePipelineDesc(
                    pipelineLayout,
                    shader,
                    "generated-storage-write-pipeline"));
                storage = device.CreateBuffer(new BufferDesc(
                    4 * sizeof(uint),
                    BufferUsage.CopySource | BufferUsage.CopyDestination | BufferUsage.ShaderWrite,
                    "generated-storage-output"));
                readback = device.CreateBuffer(
                    new BufferDesc(4 * sizeof(uint), BufferUsage.CopyDestination, "generated-storage-readback"),
                    MemoryType.Readback);
                TransitionToCopyDestination(device, storage);

                using RenderGraph graph = new(device, new RenderGraphOptions
                {
                    CompileOptimizedPlansAsynchronously = false,
                });
                GraphBuilder builder = graph.Begin();
                BufferId output = builder.ImportBuffer(
                    storage,
                    BufferUse.CopyDestination,
                    BufferUse.CopySource,
                    contentsAvailable: false);
                BufferId destination = builder.ImportBuffer(
                    readback,
                    BufferUse.CopyDestination,
                    BufferUse.CopyDestination,
                    contentsAvailable: false);

                PassBuilder compute = builder.AddPass("generated-storage-write", QueueSelection.Compute);
                WarpStorageShaderParameters parameters = new()
                {
                    Output = new BufferParameter(
                        output,
                        new BufferRange(0, 4 * sizeof(uint)),
                        BindingKind.StorageBuffer,
                        BufferUse.ShaderWrite,
                        ResourceEffect.Write,
                        Stride: sizeof(uint),
                        PriorContents: PriorContents.Discard,
                        Coverage: WriteCoverage.Full,
                        Name: "generated-storage-output-uav"),
                };
                ShaderParameterBinding pairing = new(
                    shaderDescription,
                    pipelineLayout,
                    new[] { groupLayout });
                GeneratedParameterSet generated = parameters.Pair(ref builder, ref compute, pairing);
                compute.UsesPipeline(pipeline);
                WarpStorageShaderParameters frozen = parameters;
                compute.Execute((ICommandContext commands, in PassResources resources) =>
                {
                    commands.SetPipeline(pipeline);
                    frozen.Bind(generated, commands, resources);
                    commands.Dispatch(4, 1, 1);
                });

                PassBuilder copy = builder.AddPass("generated-storage-readback", QueueSelection.Copy);
                BufferAccess source = copy.Read(output, BufferUse.CopySource);
                BufferAccess target = copy.Write(destination, BufferUse.CopyDestination);
                copy.Execute((ICommandContext commands, in PassResources resources) =>
                    commands.CopyBuffer(
                        resources.Get(source),
                        0,
                        resources.Get(target),
                        0,
                        4 * sizeof(uint)));

                GraphExecution execution = graph.Execute(ref builder);
                Assert.True(execution.Wait(TimeSpan.FromSeconds(10)));
                byte[] bytes = new byte[4 * sizeof(uint)];
                device.ReadBuffer(readback, 0, bytes);
                Assert.Equal(0xA500_0000u, BitConverter.ToUInt32(bytes, 0));
                Assert.Equal(0xA500_0001u, BitConverter.ToUInt32(bytes, 4));
                Assert.Equal(0xA500_0002u, BitConverter.ToUInt32(bytes, 8));
                Assert.Equal(0xA500_0003u, BitConverter.ToUInt32(bytes, 12));
                Assert.DoesNotContain(device.DrainDiagnostics(), static diagnostic =>
                    diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
            }
            finally
            {
                if (readback.IsValid) device.DestroyBuffer(readback);
                if (storage.IsValid) device.DestroyBuffer(storage);
                if (pipeline.IsValid) device.DestroyPipeline(pipeline);
                if (shader.IsValid) device.DestroyShader(shader);
                if (pipelineLayout.IsValid) device.DestroyPipelineLayout(pipelineLayout);
                if (groupLayout.IsValid) device.DestroyBindGroupLayout(groupLayout);
                device.CollectGarbage();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void TransitionToCopyDestination(D3DDevice device, BufferHandle buffer)
    {
        CommandListHandle commandList;
        using (ICommandContext commands = device.AcquireCommandContext(
            QueueType.Copy,
            "generated-storage-initial-state"))
        {
            commands.Barriers([
                ResourceBarrier.Transition(
                    buffer.Resource,
                    ResourceState.Common,
                    ResourceState.CopyDestination),
            ]);
            commandList = commands.Finish();
        }
        GpuCompletion completion = device.Submit(QueueType.Copy, [commandList]);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(10)));
    }

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

    private const string ShaderSource = """
        import resource_effects;

        [ResourceEffect(ResourceEffects.Write, ResourceOperations.None)]
        RWStructuredBuffer<uint> Output : register(u0, space0);

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void Main(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            Output[dispatchThreadId.x] = 0xA5000000u | dispatchThreadId.x;
        }
        """;
}

[ShaderParameters]
internal partial struct WarpStorageShaderParameters
{
    public BufferParameter Output;
}
