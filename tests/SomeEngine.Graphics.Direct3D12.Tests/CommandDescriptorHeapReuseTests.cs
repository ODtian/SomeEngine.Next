using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Collection("Direct3D12 shader compilation")]
public sealed class CommandDescriptorHeapReuseTests(D3D12TestCompiler shaderCompiler)
{
    [Fact]
    public void Bounded_and_indexed_bindings_preserve_samples_across_recordings_and_replay()
    {
        D3D12TestShader boundedShader = shaderCompiler.Compile("bounded_heap_reuse", """
            Texture2D<float> image;
            SamplerState imageSampler;
            RWStructuredBuffer<uint> outputValues;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain()
            {
                outputValues[0] = uint(image.SampleLevel(imageSampler, float2(0.5), 0) * 100.0);
            }
            """, [new("computeMain", SlangStage.Compute)], "sm_6_6");
        D3D12TestShader indexedShader = shaderCompiler.Compile("indexed_heap_reuse", """
            Texture2D<float> textures[];
            SamplerState samplers[];
            RWStructuredBuffer<uint> outputValues;
            uint textureIndex;
            uint samplerIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain()
            {
                outputValues[0] = uint(textures[textureIndex].SampleLevel(
                    samplers[samplerIndex], float2(0.5), 0) * 100.0);
                outputValues[1] = uint(textures[textureIndex].SampleLevel(
                    samplers[samplerIndex], float2(1.5), 0) * 100.0);
            }
            """, [new("computeMain", SlangStage.Compute)], "sm_6_6");
        VariableLayoutReflection boundedLayout = boundedShader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        VariableLayoutReflection indexedLayout = indexedShader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline boundedPipeline = backend.CreateComputePipeline(device,
            new(boundedShader.SlangCode, boundedShader.Reflection,
                boundedShader.GetEntryPoint(0), boundedShader.GetEntryCode(0)));
        using Pipeline indexedPipeline = backend.CreateComputePipeline(device,
            new(indexedShader.SlangCode, indexedShader.Reflection,
                indexedShader.GetEntryPoint(0), indexedShader.GetEntryCode(0)));
        TextureDesc textureDescription = new(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            Format.R32Float, TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture first = backend.CreateTexture(device, textureDescription);
        using Texture second = backend.CreateTexture(device, textureDescription);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, first, 0.25f);
        D3D12TestSupport.UploadSinglePixelR32Float(backend, device, second, 0.75f);
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstView = backend.CreateTextureSrv(device,
            new(first, range, Format.R32Float, TextureViewDimension.Texture2D));
        using TextureSrv secondView = backend.CreateTextureSrv(device,
            new(second, range, Format.R32Float, TextureViewDimension.Texture2D));
        using Sampler edge = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge));
        using Sampler border = backend.CreateSampler(device,
            new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToBorder, AddressType.ClampToBorder, AddressType.ClampToBorder,
                BorderColor: System.Numerics.Vector4.Zero));
        using DescriptorTable textures = backend.CreateDescriptorTable(device,
            [new(ResourceBindingType.TextureSrv, Format.R32Float,
                TextureDimension: TextureViewDimension.Texture2D)]);
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            [new(ResourceBindingType.Sampler)]);
        byte[] ordinary = new byte[8];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        using Buffer output = backend.CreateBuffer(device,
            new(12, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav boundedOutput = backend.CreateBufferUav(device,
            new(output, new(0, 4), StructureStride: sizeof(uint)));
        using BufferUav indexedOutput = backend.CreateBufferUav(device,
            new(output, new(4, 8), StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new(12, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        Queue queue = backend.GetQueue(device, QueueType.Compute);

        for (int recording = 0; recording < 4; recording++)
        {
            if (recording is 0 or 2)
            {
                backend.WriteDescriptor(textures, 0,
                    ResourceBinding.SampledTexture(recording < 2 ? firstView : secondView));
                backend.WriteDescriptor(samplers, 0,
                    ResourceBinding.SampledWith(recording < 2 ? edge : border));
                backend.PublishDescriptors(device);
            }
            backend.Begin(context, new CommandRecordingDesc(1, 1, 8));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
                ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, boundedPipeline);
            backend.SetTransientParameterBindings(context, new(boundedLayout,
                [ResourceBinding.SampledTexture(firstView), ResourceBinding.SampledWith(edge),
                    ResourceBinding.WritableBuffer(boundedOutput)], []));
            backend.Dispatch(context, new(1, 1, 1));
            backend.SetPipeline(context, indexedPipeline);
            backend.SetTransientParameterBindings(context,
                new(indexedLayout, [ResourceBinding.WritableBuffer(indexedOutput)], ordinary));
            backend.Dispatch(context, new(1, 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(output, 0, readback, 0, 12));
            using RecordedCommands commands = backend.End(context);
            QueueCompletion completion = backend.Submit(queue, new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            backend.CollectCompleted(device);
            uint[] expected = recording < 2 ? [25, 25, 25] : [25, 75, 0];
            using (MappedBuffer mapped = backend.Map(readback, MapType.Read, new(0, 12)))
            {
                mapped.Invalidate(mapped.Range);
                Assert.Equal(expected, MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
            }

            Assert.True(backend.TryRepeatCommands(context, out RecordedCommands repeated));
            using (repeated)
            {
                QueueCompletion repeatCompletion = backend.Submit(queue, new([], [], [repeated], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(repeatCompletion, TimeSpan.FromSeconds(10)));
            }
            backend.CollectCompleted(device);
            using MappedBuffer replayed = backend.Map(readback, MapType.Read, new(0, 12));
            replayed.Invalidate(replayed.Range);
            Assert.Equal(expected, MemoryMarshal.Cast<byte, uint>(replayed.Bytes).ToArray());
        }
    }
}
