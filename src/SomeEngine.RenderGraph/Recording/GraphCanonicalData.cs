using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.RenderGraph;

internal sealed class GraphCanonicalData : IEquatable<GraphCanonicalData>
{
    internal GraphCanonicalData(byte[] bytes, GraphSignature signature)
    {
        Bytes = bytes;
        Signature = signature;
    }

    public byte[] Bytes { get; }
    public GraphSignature Signature { get; }

    public static GraphCanonicalData Create(
        DeviceCompilationSnapshot device,
        FrozenResource[] resources,
        FrozenBufferView[] bufferViews,
        FrozenTextureView[] textureViews,
        FrozenPass[] passes)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(10u); // canonical schema
            writer.Write(device.SemanticGeneration);
            writer.Write((byte)device.ResourceHeapTier);
            writer.Write(device.SupportsEnhancedBarriers);
            writer.Write(device.SupportsAsyncCompute);
            writer.Write(device.SupportsCopyQueue);
            writer.Write(device.Queues.Count);
            foreach (QueueType queue in device.Queues) writer.Write((byte)queue);

            writer.Write(resources.Length);
            foreach (FrozenResource resource in resources) WriteResource(writer, resource);
            writer.Write(bufferViews.Length);
            foreach (FrozenBufferView view in bufferViews) WriteBufferView(writer, view);
            writer.Write(textureViews.Length);
            foreach (FrozenTextureView view in textureViews) WriteTextureView(writer, view);
            writer.Write(passes.Length);
            foreach (FrozenPass pass in passes) WritePass(writer, pass);
        }

        byte[] bytes = stream.ToArray();
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return new GraphCanonicalData(bytes, GraphSignature.FromBytes(hash));
    }

    private static void WriteBufferView(BinaryWriter writer, in FrozenBufferView view)
    {
        writer.Write(view.Resource);
        writer.Write(view.Range.Offset);
        writer.Write(view.Range.Size);
        writer.Write((byte)view.Kind);
        writer.Write((ushort)view.Format);
        writer.Write(view.Stride);
    }

    private static void WriteTextureView(BinaryWriter writer, in FrozenTextureView view)
    {
        writer.Write(view.Resource);
        writer.Write(view.Range.FirstMip);
        writer.Write(view.Range.MipCount);
        writer.Write(view.Range.FirstLayer);
        writer.Write(view.Range.LayerCount);
        writer.Write((byte)view.Range.Aspect);
        writer.Write((byte)view.Usage);
        writer.Write((ushort)view.Format);
        writer.Write((byte)view.Dimension);
    }

    public bool Equals(GraphCanonicalData? other) => other is not null && Bytes.AsSpan().SequenceEqual(other.Bytes);
    public override bool Equals(object? obj) => obj is GraphCanonicalData other && Equals(other);
    public override int GetHashCode() => Signature.GetHashCode();

    private static void WriteResource(BinaryWriter writer, in FrozenResource resource)
    {
        writer.Write((byte)resource.Kind);
        writer.Write(resource.IsImported);
        if (resource.Kind == ResourceNodeKind.Buffer)
        {
            writer.Write(resource.BufferDesc.Size);
            writer.Write((uint)resource.BufferDesc.Usage);
            if (resource.IsImported)
            {
                writer.Write((byte)resource.ImportedBuffer.InitialUse);
                writer.Write((byte)resource.ImportedBuffer.FinalUse);
                writer.Write(resource.ImportedBuffer.ContentsAvailable);
                writer.Write((byte)resource.ImportedBuffer.Metadata.MemoryType);
                WriteReadinessShape(writer, resource.ImportedBuffer.Readiness);
            }
        }
        else
        {
            writer.Write(resource.TextureDesc.Width);
            writer.Write(resource.TextureDesc.Height);
            writer.Write(resource.TextureDesc.Depth);
            writer.Write(resource.TextureDesc.MipLevels);
            writer.Write(resource.TextureDesc.ArrayLayers);
            writer.Write(resource.TextureDesc.SampleCount);
            writer.Write((ushort)resource.TextureDesc.Format);
            writer.Write((uint)resource.TextureDesc.Usage);
            writer.Write((byte)resource.TextureDesc.Dimension);
            writer.Write(resource.TextureDesc.CubeCompatible);
            writer.Write(resource.TextureDesc.AllowedViewFormats.Length);
            foreach (Format format in resource.TextureDesc.AllowedViewFormats) writer.Write((ushort)format);
            if (resource.IsImported)
            {
                writer.Write((byte)resource.ImportedTexture.InitialUse);
                writer.Write((byte)resource.ImportedTexture.FinalUse);
                writer.Write(resource.ImportedTexture.ContentsAvailable);
                writer.Write((byte)resource.ImportedTexture.Metadata.MemoryType);
                WriteReadinessShape(writer, resource.ImportedTexture.Readiness);
            }
        }

        if (!resource.IsImported)
        {
            writer.Write(resource.Requirements.Size);
            writer.Write(resource.Requirements.Alignment);
            writer.Write((byte)resource.Requirements.MemoryType);
            writer.Write((byte)resource.Requirements.ResourceClass);
            writer.Write(resource.Requirements.CompatibilityClass);
        }
    }

    private static void WriteReadinessShape(BinaryWriter writer, GpuCompletion[]? readiness)
    {
        writer.Write(readiness?.Length ?? 0);
        if (readiness is null) return;
        foreach (GpuCompletion completion in readiness) writer.Write((byte)completion.Queue);
    }

    private static void WritePass(BinaryWriter writer, FrozenPass pass)
    {
        QueueType[] queues = pass.Queues.ToArray();
        writer.Write(queues.Length);
        foreach (QueueType queue in queues) writer.Write((byte)queue);
        writer.Write((byte)pass.RecordingLane);
        writer.Write(pass.Identity.Module.ToByteArray());
        writer.Write(pass.Identity.MetadataToken);
        writer.Write(pass.Identity.DeclaringType);
        writer.Write(pass.Identity.Method);
        writer.Write(pass.Shaders.Length);
        foreach (FrozenShaderContract shader in pass.Shaders)
        {
            writer.Write(shader.Key.Word0);
            writer.Write(shader.Key.Word1);
            writer.Write(shader.Key.Word2);
            writer.Write(shader.Key.Word3);
            writer.Write((byte)shader.Stage);
            writer.Write(shader.LayoutHash);
            writer.Write(shader.Bindings.Length);
            foreach (ShaderBinding binding in shader.Bindings)
            {
                writer.Write(binding.Group);
                writer.Write(binding.Binding);
                writer.Write((byte)binding.Kind);
                writer.Write(binding.Count);
                writer.Write((byte)binding.Visibility);
                writer.Write((byte)binding.ReflectedAccess);
                writer.Write((byte)binding.DeclaredEffect);
                writer.Write((byte)binding.DeclaredOperations);
                writer.Write((byte)binding.ReflectedOperations);
                writer.Write((byte)binding.TextureDimension);
                writer.Write((byte)binding.TextureSampleType);
                writer.Write((ushort)binding.StorageFormat);
            }
            writer.Write(shader.PushConstants.Length);
            foreach (PushConstantRange range in shader.PushConstants)
            {
                writer.Write(range.Offset);
                writer.Write(range.Size);
                writer.Write((byte)range.Visibility);
                writer.Write(range.Register);
                writer.Write(range.Space);
            }
            writer.Write(shader.Accesses.Length);
            foreach (FrozenShaderBindingAccess access in shader.Accesses)
            {
                writer.Write(access.Group);
                writer.Write(access.Binding);
                writer.Write(access.Element);
                writer.Write((byte)access.Kind);
                writer.Write(access.Access);
                writer.Write(access.View);
            }
        }
        writer.Write(pass.Accesses.Length);
        foreach (FrozenAccess access in pass.Accesses) WriteAccess(writer, access);
        writer.Write(pass.ColorAttachments.Length);
        foreach (FrozenColorAttachment attachment in pass.ColorAttachments)
        {
            writer.Write(attachment.Slot);
            writer.Write(attachment.View);
            writer.Write(attachment.Access);
            writer.Write((byte)attachment.Load);
        }
        writer.Write(pass.DepthStencilAttachment.HasValue);
        if (pass.DepthStencilAttachment is FrozenDepthStencilAttachment depthStencil)
        {
            writer.Write(depthStencil.View);
            writer.Write(depthStencil.DepthAccess);
            writer.Write(depthStencil.StencilAccess);
            writer.Write(depthStencil.Depth.HasValue);
            if (depthStencil.Depth is DepthAttachmentOps depth)
            {
                writer.Write((byte)depth.Load);
                writer.Write(depth.ReadOnly);
            }
            writer.Write(depthStencil.Stencil.HasValue);
            if (depthStencil.Stencil is StencilAttachmentOps stencil)
            {
                writer.Write((byte)stencil.Load);
                writer.Write(stencil.ReadOnly);
            }
        }
    }

    private static void WriteAccess(BinaryWriter writer, in FrozenAccess access)
    {
        writer.Write((byte)access.Kind);
        writer.Write(access.Resource);
        writer.Write(access.View);
        writer.Write((byte)access.Effect);
        writer.Write((byte)access.PriorContents);
        writer.Write((byte)access.Coverage);
        if (access.Kind == ResourceNodeKind.Buffer)
        {
            writer.Write((byte)access.BufferUse);
            writer.Write(access.BufferRange.Offset);
            writer.Write(access.BufferRange.Size);
        }
        else
        {
            writer.Write((byte)access.TextureUse);
            writer.Write(access.TextureRange.FirstMip);
            writer.Write(access.TextureRange.MipCount);
            writer.Write(access.TextureRange.FirstLayer);
            writer.Write(access.TextureRange.LayerCount);
            writer.Write((byte)access.TextureRange.Aspect);
        }
    }
}

internal readonly record struct GraphSignature(ulong Word0, ulong Word1, ulong Word2, ulong Word3) : IComparable<GraphSignature>
{
    public static GraphSignature FromBytes(ReadOnlySpan<byte> bytes) => new(
        BitConverter.ToUInt64(bytes[..8]),
        BitConverter.ToUInt64(bytes.Slice(8, 8)),
        BitConverter.ToUInt64(bytes.Slice(16, 8)),
        BitConverter.ToUInt64(bytes.Slice(24, 8)));

    public int CompareTo(GraphSignature other)
    {
        int result = Word0.CompareTo(other.Word0);
        if (result != 0) return result;
        result = Word1.CompareTo(other.Word1);
        if (result != 0) return result;
        result = Word2.CompareTo(other.Word2);
        return result != 0 ? result : Word3.CompareTo(other.Word3);
    }
}
