using System.Buffers;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using Xunit;

namespace SomeEngine.Graphics.Vulkan.Tests;

[Collection("Vulkan shader compilation")]
public sealed class VulkanBindlessResourceTests(VulkanTestCompiler shaderCompiler)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Published_buffer_and_image_indices_survive_shared_bindless_declarations(
        bool stripResourceNames)
    {
        const string source = """
            StructuredBuffer<uint> sourceValues[];
            Texture2D<uint> sourceImages[];
            Texture2D<uint> otherImages[];
            RWStructuredBuffer<uint> outputValues;
            uint bufferIndex;
            uint imageIndex;
            uint otherImageIndex;
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                uint bufferValue = sourceValues[bufferIndex][0];
                uint imageValue = sourceImages[imageIndex].Load(int3(0, 0, 0));
                uint otherImageValue = otherImages[otherImageIndex].Load(int3(0, 0, 0));
                outputValues[0] = bufferValue;
                outputValues[1] = imageValue;
                outputValues[2] = otherImageValue;
                outputValues[3] = bufferValue + imageValue + otherImageValue;
            }
            """;
        VulkanTestShader shader = shaderCompiler.Compile(
            source,
            ("computeMain", SlangStage.Compute));
        VariableLayoutReflection globals = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        ReadOnlySequence<byte> code = shader.GetEntryCode(0);
        if (stripResourceNames)
            code = new ReadOnlySequence<byte>(RemoveResourceNames(code.ToArray()));
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));
        using Buffer sourceBuffer = backend.CreateBuffer(
            device,
            new BufferDesc(sizeof(uint), BufferUsages.ShaderRead),
            MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(sourceBuffer, MapType.Write, BufferRange.Whole))
        {
            BitConverter.GetBytes(11u).CopyTo(mapped.Bytes);
            mapped.Flush(mapped.Range);
        }
        using BufferSrv sourceView = backend.CreateBufferSrv(
            device,
            new BufferSrvDesc(sourceBuffer, BufferRange.Whole, StructureStride: sizeof(uint)));
        TextureDesc imageDescription = new(
            TextureDimension.Texture2D,
            1, 1, 1, 1, 1, 1,
            Format.R32UInt,
            TextureUsages.Sampled | TextureUsages.CopyDestination);
        using Texture firstImage = backend.CreateTexture(device, imageDescription);
        using Texture secondImage = backend.CreateTexture(device, imageDescription);
        TextureSubresourceRange imageRange = new(0, 1, 0, 1, TextureAspects.Color);
        using TextureSrv firstImageView = backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                firstImage, imageRange, Format.R32UInt, TextureViewDimension.Texture2D));
        using TextureSrv secondImageView = backend.CreateTextureSrv(
            device,
            new TextureSrvDesc(
                secondImage, imageRange, Format.R32UInt, TextureViewDimension.Texture2D));
        using DescriptorTable resources = backend.CreateDescriptorTable(
            device,
            [
                new DescriptorSlotDesc(ResourceBindingType.BufferSrv, StructureStride: sizeof(uint)),
                new DescriptorSlotDesc(
                    ResourceBindingType.TextureSrv,
                    Format.R32UInt,
                    TextureDimension: TextureViewDimension.Texture2D),
                new DescriptorSlotDesc(
                    ResourceBindingType.TextureSrv,
                    Format.R32UInt,
                    TextureDimension: TextureViewDimension.Texture2D),
            ]);
        backend.WriteDescriptor(resources, 0, ResourceBinding.ReadOnlyBuffer(sourceView));
        backend.WriteDescriptor(resources, 1, ResourceBinding.SampledTexture(firstImageView));
        backend.WriteDescriptor(resources, 2, ResourceBinding.SampledTexture(secondImageView));
        byte[] ordinary = new byte[16];
        BitConverter.GetBytes(backend.GetDescriptorIndex(resources, 0).Value).CopyTo(ordinary, 0);
        BitConverter.GetBytes(backend.GetDescriptorIndex(resources, 1).Value).CopyTo(ordinary, 4);
        BitConverter.GetBytes(backend.GetDescriptorIndex(resources, 2).Value).CopyTo(ordinary, 8);
        backend.PublishDescriptors(device);
        using Pipeline pipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(shader.SlangCode, shader.Reflection, shader.Entries[0], code));
        using Buffer upload = backend.CreateBuffer(
            device,
            new BufferDesc(768, BufferUsages.CopySource),
            MemoryType.Upload);
        using (MappedBuffer mapped = backend.Map(upload, MapType.Write, BufferRange.Whole))
        {
            mapped.Bytes.Clear();
            BitConverter.GetBytes(31u).CopyTo(mapped.Bytes);
            BitConverter.GetBytes(7u).CopyTo(mapped.Bytes[512..]);
            mapped.Flush(mapped.Range);
        }
        using Buffer output = backend.CreateBuffer(
            device,
            new BufferDesc(16, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav outputView = backend.CreateBufferUav(
            device,
            new BufferUavDesc(output, BufferRange.Whole, StructureStride: sizeof(uint)));
        using Buffer readback = backend.CreateBuffer(
            device,
            new BufferDesc(16, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 1));
        backend.Begin(context);
        UploadImage(firstImage, 0);
        UploadImage(secondImage, 512);
        backend.Barrier(context, new BufferBarrier(
            sourceBuffer,
            PipelineSync.None,
            PipelineSync.ComputeShading,
            ResourceAccess.NoAccess,
            ResourceAccess.ShaderResource));
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
                [ResourceBinding.WritableBuffer(outputView)],
                ordinary));
        backend.Dispatch(context, new DispatchArguments(1, 1, 1));
        backend.Barrier(context, new BufferBarrier(
            output,
            PipelineSync.ComputeShading,
            PipelineSync.Copy,
            ResourceAccess.UnorderedAccess,
            ResourceAccess.CopySource));
        backend.CopyBuffer(context, new BufferCopy(output, 0, readback, 0, 16));
        using RecordedCommands commands = backend.End(context);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(5)));
        using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        Assert.Equal(new uint[] { 11, 31, 7, 49 }, MemoryMarshal.Cast<byte, uint>(result.Bytes).ToArray());

        void UploadImage(Texture image, ulong offset)
        {
            backend.Barrier(context, new TextureBarrier(
                image,
                imageRange,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopyDestination,
                TextureLayout.Undefined,
                TextureLayout.CopyDestination));
            backend.CopyBufferToTexture(context, new BufferTextureCopy(
                upload, offset, 256, 1,
                image, 0, 0, TextureAspects.Color,
                0, 0, 0, 1, 1, 1));
            backend.Barrier(context, new TextureBarrier(
                image,
                imageRange,
                PipelineSync.Copy,
                PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination,
                ResourceAccess.ShaderResource,
                TextureLayout.CopyDestination,
                TextureLayout.ShaderResource));
        }
    }

    private static byte[] RemoveResourceNames(byte[] code)
    {
        const uint nameInstruction = 5; // SPIR-V OpName is optional debug information.
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(code);
        var result = new List<uint>(words.Length);
        result.AddRange(words[..5].ToArray());
        for (int index = 5; index < words.Length;)
        {
            int count = checked((int)(words[index] >> 16));
            if ((words[index] & 0xffff) != nameInstruction)
                result.AddRange(words.Slice(index, count).ToArray());
            index += count;
        }
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(result)).ToArray();
    }
}
