using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Xunit.Collection("Direct3D12 shader compilation")]
public sealed class PublishedDescriptorCopyTests(D3D12TestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(1)]
    [InlineData(65)]
    [InlineData(129)]
    public void Published_indices_keep_samples_and_typed_nulls_after_heap_growth_and_disposal(int count)
    {
        D3D12TestShader shader = shaderCompiler.Compile("published_descriptor_samples", """
            Texture2D<float> textures[];
            SamplerState samplers[];
            RWStructuredBuffer<uint> outputValues;
            uint textureBase;
            uint samplerBase;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x * 2] = uint(textures[textureBase + id.x].SampleLevel(
                    samplers[samplerBase + id.x], float2(0.5), 0) * 100.0);
                outputValues[id.x * 2 + 1] = uint(textures[textureBase + id.x].SampleLevel(
                    samplers[samplerBase + id.x], float2(1.5), 0) * 100.0);
            }
            """, [new("computeMain", SlangStage.Compute)], "sm_6_6");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline pipeline = backend.CreateComputePipeline(device,
            new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(0), shader.GetEntryCode(0)));
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
            Enumerable.Repeat(new DescriptorSlotDesc(ResourceBindingType.TextureSrv,
                Format.R32Float, TextureDimension: TextureViewDimension.Texture2D), count).ToArray());
        using DescriptorTable samplers = backend.CreateDescriptorTable(device,
            Enumerable.Repeat(new DescriptorSlotDesc(ResourceBindingType.Sampler), count).ToArray());
        uint[] expected = new uint[count * 2];
        for (uint index = 0; index < count; index++)
        {
            ResourceBinding texture = index % 7 == 3
                ? ResourceBinding.Null(ResourceBindingType.TextureSrv)
                : ResourceBinding.SampledTexture(index % 2 == 0 ? firstView : secondView);
            backend.WriteDescriptor(textures, index, texture);
            backend.WriteDescriptor(samplers, index, ResourceBinding.SampledWith(index % 3 == 1 ? border : edge));
            expected[index * 2] = index % 7 == 3 ? 0u : index % 2 == 0 ? 25u : 75u;
            expected[index * 2 + 1] = index % 3 == 1 ? 0u : expected[index * 2];
        }
        backend.PublishDescriptors(device);
        byte[] ordinary = new byte[8];
        MemoryMarshal.Write(ordinary, backend.GetDescriptorIndex(textures, 0).Value);
        MemoryMarshal.Write(ordinary.AsSpan(4), backend.GetDescriptorIndex(samplers, 0).Value);
        ulong byteCount = checked((ulong)expected.Length * sizeof(uint));
        using Buffer output = backend.CreateBuffer(device,
            new(byteCount, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(device,
            new(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(device,
            new(byteCount, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));

        for (int recording = 0; recording < 2; recording++)
        {
            backend.Begin(context, recording == 0
                ? new CommandRecordingDesc(1, 1, 8)
                : new CommandRecordingDesc(512, 256, 8));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
                ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
            backend.SetPipeline(context, pipeline);
            backend.SetTransientParameterBindings(context,
                new(layout, [ResourceBinding.WritableBuffer(outputView)], ordinary));
            backend.Dispatch(context, new(checked((uint)count), 1, 1));
            backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
            backend.CopyBuffer(context, new(output, 0, readback, 0, byteCount));
            using RecordedCommands commands = backend.End(context);
            if (recording == 1)
            {
                textures.Dispose();
                samplers.Dispose();
                firstView.Dispose();
                secondView.Dispose();
                first.Dispose();
                second.Dispose();
                edge.Dispose();
                border.Dispose();
                backend.PublishDescriptors(device);
            }
            QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute),
                new([], [], [commands], [], []));
            Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            using MappedBuffer mapped = backend.Map(readback, MapType.Read, new(0, byteCount));
            mapped.Invalidate(new(0, byteCount));
            Assert.Equal(expected, MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray());
            backend.CollectCompleted(device);
        }
    }
}
