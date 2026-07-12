using System.Buffers.Binary;
using SomeEngine.Graphics.Null;

namespace SomeEngine.Graphics.Tests;

internal static class PortableRhiTestSupport
{
    private static long s_key;

    public static (PipelineHandle Pipeline, PipelineLayoutHandle Layout, ShaderHandle Shader) CreateComputePipeline(
        Device device,
        PipelineCacheKey cacheKey = default)
    {
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            Array.Empty<BindGroupLayoutHandle>(),
            Array.Empty<PushConstantRange>()));
        ShaderHandle shader = device.CreateShader(Shader(ShaderStage.Compute));
        PipelineHandle pipeline = device.CreateComputePipeline(new ComputePipelineDesc(layout, shader, CacheKey: cacheKey));
        return (pipeline, layout, shader);
    }

    public static (PipelineHandle Pipeline, PipelineLayoutHandle Layout, ShaderHandle Vertex, ShaderHandle Pixel) CreateRasterPipeline(
        Device device,
        PipelineCacheKey cacheKey = default)
    {
        PipelineLayoutHandle layout = device.CreatePipelineLayout(new PipelineLayoutDesc(
            Array.Empty<BindGroupLayoutHandle>(),
            Array.Empty<PushConstantRange>()));
        ShaderHandle vertex = device.CreateShader(Shader(ShaderStage.Vertex));
        ShaderHandle pixel = device.CreateShader(Shader(ShaderStage.Pixel));
        PipelineHandle pipeline = device.CreateRasterPipeline(new RasterPipelineDesc(
            layout,
            vertex,
            pixel,
            new[] { Format.R8G8B8A8UNorm },
            CacheKey: cacheKey));
        return (pipeline, layout, vertex, pixel);
    }

    public static (TextureHandle Texture, TextureViewHandle View) CreateRenderTarget(Device device)
    {
        TextureHandle texture = device.CreateTexture(new TextureDesc(
            4,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment | TextureUsage.CopySource));
        TextureViewHandle view = device.CreateTextureView(new TextureViewDesc(
            texture,
            default,
            TextureViewUsage.ColorAttachment));
        return (texture, view);
    }

    public static RenderingInfo Rendering(TextureViewHandle view) => new(
        new[] { new ColorAttachment(view, LoadAction.Clear, StoreAction.Store) },
        null,
        4,
        4);

    public static (BufferHandle Upload, BufferHandle DeviceLocal) StageBuffer(
        Device device,
        ICommandContext context,
        byte[] bytes,
        BufferUsage finalUsage,
        ResourceState finalState)
    {
        BufferHandle upload = device.CreateBuffer(
            new BufferDesc(checked((ulong)bytes.Length), BufferUsage.CopySource),
            MemoryType.Upload);
        device.WriteBuffer(upload, 0, bytes);
        BufferHandle destination = device.CreateBuffer(new BufferDesc(
            checked((ulong)bytes.Length),
            BufferUsage.CopyDestination | finalUsage));
        context.Barriers([ResourceBarrier.Transition(destination.Resource, ResourceState.Common, ResourceState.CopyDestination)]);
        context.CopyBuffer(upload, 0, destination, 0, checked((ulong)bytes.Length));
        context.Barriers([ResourceBarrier.Transition(destination.Resource, ResourceState.CopyDestination, finalState)]);
        return (upload, destination);
    }

    public static BufferHandle CreateReadback(Device device, ulong size) => device.CreateBuffer(
        new BufferDesc(size, BufferUsage.CopyDestination),
        MemoryType.Readback);

    public static byte[] UInt32Words(params uint[] values)
    {
        byte[] bytes = new byte[checked(values.Length * sizeof(uint))];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint), sizeof(uint)), values[index]);
        return bytes;
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset = 0) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

    public static ulong ReadUInt64(ReadOnlySpan<byte> bytes, int offset = 0) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, sizeof(ulong)));

    private static ShaderDesc Shader(ShaderStage stage)
    {
        ulong key = checked((ulong)Interlocked.Increment(ref s_key));
        return new ShaderDesc(
            new ShaderArtifactKey(key, key + 1, key + 2, key + 3),
            ShaderBinaryFormat.Dxil,
            stage,
            "main",
            ReadOnlyMemory<byte>.Empty,
            new ShaderInterface(
                Array.Empty<ShaderBinding>(),
                Array.Empty<PushConstantRange>(),
                key));
    }
}
