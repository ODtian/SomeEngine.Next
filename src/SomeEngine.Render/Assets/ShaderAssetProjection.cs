using System.Security.Cryptography;
using System.Text;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;
using AssetTextureDimension = SomeEngine.Assets.Schema.ShaderTextureDimension;
using GraphicsShaderStage = SomeEngine.Graphics.ShaderStage;
using GraphicsTextureDimension = SomeEngine.Graphics.ShaderTextureDimension;

namespace SomeEngine.Render.Assets;

/// <summary>
/// Projects a versioned cooked shader asset into the backend-neutral Graphics shader contract.
/// Source compilation and graph-scheduling policy remain outside this composition boundary.
/// </summary>
public static class ShaderAssetProjection
{
    private const uint ProjectionSchema = 3;
    private const uint SlangResourceUnknownShape = 0x08;

    public static ShaderDesc Dxil(ShaderAsset asset, string entryPoint, AssetShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ValidateVersion(asset);

        ShaderBytecode variant = (asset.Variants ?? throw Missing("variants"))
            .Single(item =>
                string.Equals(item.Backend, "dxil", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.EntryPoint, entryPoint, StringComparison.Ordinal) &&
                item.Stage == stage);
        ShaderEntryPointReflection reflection = (asset.EntryPointReflections ?? throw Missing("entry-point reflections"))
            .Single(item =>
                string.Equals(item.Backend, "dxil", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.EntryPoint, entryPoint, StringComparison.Ordinal) &&
                item.Stage == stage);

        byte[] bytecode = variant.Data?.ToArray() ?? throw Missing($"DXIL bytes for {entryPoint}");
        if (bytecode.Length == 0) throw Missing($"DXIL bytes for {entryPoint}");
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(bytecode));
        if (!string.Equals(actualHash, variant.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Cooked shader entry '{entryPoint}' has a stale bytecode content hash.");

        IList<ShaderResourceReflection> resources = reflection.Reflection?.Resources ?? [];
        GraphicsShaderStage graphicsStage = stage switch
        {
            AssetShaderStage.Vertex => GraphicsShaderStage.Vertex,
            AssetShaderStage.Pixel => GraphicsShaderStage.Pixel,
            AssetShaderStage.Compute => GraphicsShaderStage.Compute,
            _ => throw new NotSupportedException($"Graphics does not expose cooked shader stage {stage}."),
        };
        ShaderBinding[] bindings = resources
            .Select(resource => ProjectBinding(resource, graphicsStage))
            .OrderBy(static binding => binding.Group)
            .ThenBy(static binding => binding.Binding)
            .ToArray();
        if (bindings.Select(static binding => (binding.Group, binding.Binding)).Distinct().Count() != bindings.Length)
            throw new InvalidDataException($"Cooked shader entry '{entryPoint}' repeats a descriptor slot.");

        ulong layoutHash = ComputeLayoutHash(bindings);
        ShaderInterface shaderInterface = new(bindings, Array.Empty<PushConstantRange>(), layoutHash);
        ShaderArtifactKey key = ComputeKey(graphicsStage, entryPoint, bytecode, shaderInterface);
        return new ShaderDesc(
            key,
            ShaderBinaryFormat.Dxil,
            graphicsStage,
            entryPoint,
            bytecode,
            shaderInterface,
            $"{asset.Name ?? "shader"}:{entryPoint}");
    }

    private static void ValidateVersion(ShaderAsset asset)
    {
        if (asset.SchemaVersion != SlangShaderImporter.ShaderAssetSchemaVersion)
            throw new InvalidDataException(
                $"Cooked shader schema version {asset.SchemaVersion} is not supported; expected {SlangShaderImporter.ShaderAssetSchemaVersion}.");
        if (asset.ImportTrace?.ImporterVersion != SlangShaderImporter.ImporterVersion)
            throw new InvalidDataException(
                $"Cooked shader importer version {asset.ImportTrace?.ImporterVersion ?? 0} is not supported; expected {SlangShaderImporter.ImporterVersion}.");
    }

    private static ShaderArtifactKey ComputeKey(
        GraphicsShaderStage stage,
        string entryPoint,
        ReadOnlySpan<byte> bytecode,
        in ShaderInterface shaderInterface)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ProjectionSchema);
            writer.Write((byte)ShaderBinaryFormat.Dxil);
            writer.Write((byte)stage);
            writer.Write(entryPoint);
            writer.Write(shaderInterface.LayoutHash);
            WriteBindings(writer, shaderInterface.Bindings.Span);
            writer.Write(shaderInterface.PushConstants.Length);
            writer.Write(bytecode.Length);
            writer.Write(bytecode);
        }

        byte[] hash = SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        ShaderArtifactKey key = new(
            BitConverter.ToUInt64(hash.AsSpan(0, 8)),
            BitConverter.ToUInt64(hash.AsSpan(8, 8)),
            BitConverter.ToUInt64(hash.AsSpan(16, 8)),
            BitConverter.ToUInt64(hash.AsSpan(24, 8)));
        return key.IsValid ? key : throw new CryptographicException("SHA-256 produced an invalid shader artifact key.");
    }

    private static ulong ComputeLayoutHash(ReadOnlySpan<ShaderBinding> bindings)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ProjectionSchema);
            WriteBindings(writer, bindings);
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)), hash);
        return BitConverter.ToUInt64(hash);
    }

    private static void WriteBindings(BinaryWriter writer, ReadOnlySpan<ShaderBinding> bindings)
    {
        writer.Write(bindings.Length);
        foreach (ref readonly ShaderBinding binding in bindings)
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
    }

    private static ShaderBinding ProjectBinding(
        ShaderResourceReflection resource,
        GraphicsShaderStage entryStage)
    {
        if (resource.DescriptorCount == 0)
            throw new NotSupportedException($"Shader resource '{resource.Name}' has an unknown or unbounded descriptor count.");
        BindingKind kind = resource.BindingType switch
        {
            ShaderBindingType.ConstantBuffer => BindingKind.ConstantBuffer,
            ShaderBindingType.StorageBufferRead or ShaderBindingType.RawBufferRead => BindingKind.ReadOnlyBuffer,
            ShaderBindingType.StorageBufferReadWrite or ShaderBindingType.RawBufferReadWrite => BindingKind.StorageBuffer,
            ShaderBindingType.TextureRead => BindingKind.SampledTexture,
            ShaderBindingType.TextureReadWrite => BindingKind.StorageTexture,
            ShaderBindingType.Sampler => BindingKind.Sampler,
            _ => throw new NotSupportedException($"Shader resource '{resource.Name}' has unsupported binding type {resource.BindingType}."),
        };
        ValidateTextureFacts(resource, kind);

        GraphicsShaderStage visibility = MapVisibility(resource.Stages);
        if ((visibility & entryStage) == 0)
            throw new InvalidDataException($"Shader resource '{resource.Name}' does not include entry-point stage {entryStage}.");
        return new ShaderBinding(
            resource.Space,
            resource.Binding,
            kind,
            resource.DescriptorCount,
            visibility,
            ProjectReflectedAccess(resource),
            ProjectDeclaredEffect(resource),
            ProjectTextureDimension(resource),
            ProjectTextureSampleType(resource),
            ProjectStorageFormat(resource.StorageFormat),
            ProjectDeclaredOperations(resource),
            ProjectReflectedOperations(resource));
    }

    private static void ValidateTextureFacts(ShaderResourceReflection resource, BindingKind kind)
    {
        if (kind is not (BindingKind.SampledTexture or BindingKind.StorageTexture)) return;

        if (resource.TextureDimension == AssetTextureDimension.Unknown &&
            resource.SlangResourceShape is not (0 or SlangResourceUnknownShape))
        {
            throw new NotSupportedException(
                $"Shader resource '{resource.Name}' has Slang shape 0x{resource.SlangResourceShape:X} that the Graphics texture-dimension contract cannot express.");
        }
        if (resource.TextureSampleType == ShaderTextureSampleType.Unknown && resource.SlangScalarType != 0)
        {
            throw new NotSupportedException(
                $"Shader resource '{resource.Name}' has Slang scalar type {resource.SlangScalarType} that the Graphics texture sample-type contract cannot express.");
        }
        if (kind == BindingKind.StorageTexture &&
            resource.StorageFormat == ShaderStorageFormat.Unknown &&
            resource.SlangImageFormat != 0)
        {
            throw new NotSupportedException(
                $"Shader resource '{resource.Name}' has Slang image format {resource.SlangImageFormat} that the Graphics storage-format contract cannot express.");
        }
    }

    private static ReflectedAccess ProjectReflectedAccess(ShaderResourceReflection resource) =>
        resource.ReflectedAccess switch
        {
            ShaderReflectedAccess.Unknown => ReflectedAccess.Unknown,
            ShaderReflectedAccess.ReadOnly => ReflectedAccess.ReadOnly,
            ShaderReflectedAccess.WriteOnly => ReflectedAccess.WriteOnly,
            ShaderReflectedAccess.ReadWrite => ReflectedAccess.ReadWrite,
            _ => throw new InvalidDataException($"Shader resource '{resource.Name}' has invalid reflected access."),
        };

    private static DeclaredEffect ProjectDeclaredEffect(ShaderResourceReflection resource) =>
        resource.DeclaredEffect switch
        {
            ShaderDeclaredEffect.Unspecified => DeclaredEffect.Unspecified,
            ShaderDeclaredEffect.Read => DeclaredEffect.Read,
            ShaderDeclaredEffect.Write => DeclaredEffect.Write,
            ShaderDeclaredEffect.ReadWrite => DeclaredEffect.ReadWrite,
            _ => throw new InvalidDataException($"Shader resource '{resource.Name}' has invalid declared effect."),
        };

    private static DeclaredOperations ProjectDeclaredOperations(ShaderResourceReflection resource)
    {
        const ShaderDeclaredOperations all =
            ShaderDeclaredOperations.Atomic |
            ShaderDeclaredOperations.Append |
            ShaderDeclaredOperations.Consume |
            ShaderDeclaredOperations.RasterOrdered |
            ShaderDeclaredOperations.Feedback;
        if ((resource.DeclaredOperations & ~all) != 0)
            throw new InvalidDataException(
                $"Shader resource '{resource.Name}' has invalid declared operations 0x{(uint)resource.DeclaredOperations:X}.");

        DeclaredOperations result = DeclaredOperations.None;
        if ((resource.DeclaredOperations & ShaderDeclaredOperations.Atomic) != 0)
            result |= DeclaredOperations.Atomic;
        if ((resource.DeclaredOperations & ShaderDeclaredOperations.Append) != 0)
            result |= DeclaredOperations.Append;
        if ((resource.DeclaredOperations & ShaderDeclaredOperations.Consume) != 0)
            result |= DeclaredOperations.Consume;
        if ((resource.DeclaredOperations & ShaderDeclaredOperations.RasterOrdered) != 0)
            result |= DeclaredOperations.RasterOrdered;
        if ((resource.DeclaredOperations & ShaderDeclaredOperations.Feedback) != 0)
            result |= DeclaredOperations.Feedback;
        return result;
    }

    private static ReflectedOperations ProjectReflectedOperations(ShaderResourceReflection resource)
    {
        const ShaderReflectedOperations all =
            ShaderReflectedOperations.Atomic |
            ShaderReflectedOperations.Append |
            ShaderReflectedOperations.Consume |
            ShaderReflectedOperations.RasterOrdered |
            ShaderReflectedOperations.Feedback;
        if ((resource.ReflectedOperations & ~all) != 0)
            throw new InvalidDataException(
                $"Shader resource '{resource.Name}' has invalid reflected operations 0x{(uint)resource.ReflectedOperations:X}.");

        ReflectedOperations result = ReflectedOperations.None;
        if ((resource.ReflectedOperations & ShaderReflectedOperations.Atomic) != 0)
            result |= ReflectedOperations.Atomic;
        if ((resource.ReflectedOperations & ShaderReflectedOperations.Append) != 0)
            result |= ReflectedOperations.Append;
        if ((resource.ReflectedOperations & ShaderReflectedOperations.Consume) != 0)
            result |= ReflectedOperations.Consume;
        if ((resource.ReflectedOperations & ShaderReflectedOperations.RasterOrdered) != 0)
            result |= ReflectedOperations.RasterOrdered;
        if ((resource.ReflectedOperations & ShaderReflectedOperations.Feedback) != 0)
            result |= ReflectedOperations.Feedback;
        return result;
    }

    private static GraphicsTextureDimension ProjectTextureDimension(ShaderResourceReflection resource) =>
        resource.TextureDimension switch
        {
            AssetTextureDimension.Unknown => GraphicsTextureDimension.Unknown,
            AssetTextureDimension.Texture1D => GraphicsTextureDimension.Texture1D,
            AssetTextureDimension.Texture1DArray => GraphicsTextureDimension.Texture1DArray,
            AssetTextureDimension.Texture2D => GraphicsTextureDimension.Texture2D,
            AssetTextureDimension.Texture2DArray => GraphicsTextureDimension.Texture2DArray,
            AssetTextureDimension.Texture2DMS => GraphicsTextureDimension.Texture2DMS,
            AssetTextureDimension.Texture2DMSArray => GraphicsTextureDimension.Texture2DMSArray,
            AssetTextureDimension.Cube => GraphicsTextureDimension.Cube,
            AssetTextureDimension.CubeArray => GraphicsTextureDimension.CubeArray,
            AssetTextureDimension.Texture3D => GraphicsTextureDimension.Texture3D,
            _ => throw new InvalidDataException($"Shader resource '{resource.Name}' has invalid texture dimension."),
        };

    private static TextureSampleType ProjectTextureSampleType(ShaderResourceReflection resource) =>
        resource.TextureSampleType switch
        {
            ShaderTextureSampleType.Unknown => TextureSampleType.Unknown,
            ShaderTextureSampleType.Float => TextureSampleType.Float,
            ShaderTextureSampleType.UInt => TextureSampleType.UInt,
            ShaderTextureSampleType.SInt => TextureSampleType.SInt,
            ShaderTextureSampleType.Depth => TextureSampleType.Depth,
            _ => throw new InvalidDataException($"Shader resource '{resource.Name}' has invalid texture sample type."),
        };

    private static GraphicsShaderStage MapVisibility(uint stages)
    {
        const uint supportedStageBits = 0x01 | 0x02 | 0x20;
        if ((stages & ~supportedStageBits) != 0)
            throw new NotSupportedException($"Graphics does not expose cooked shader visibility bits 0x{stages & ~supportedStageBits:X}.");

        GraphicsShaderStage result = 0;
        if ((stages & 0x01) != 0) result |= GraphicsShaderStage.Vertex;
        if ((stages & 0x02) != 0) result |= GraphicsShaderStage.Pixel;
        if ((stages & 0x20) != 0) result |= GraphicsShaderStage.Compute;
        return result;
    }

    private static Format ProjectStorageFormat(ShaderStorageFormat format) => format switch
    {
        ShaderStorageFormat.Unknown => Format.Unknown,
        ShaderStorageFormat.R8UNorm => Format.R8UNorm,
        ShaderStorageFormat.R8G8UNorm => Format.R8G8UNorm,
        ShaderStorageFormat.R8G8B8A8UNorm => Format.R8G8B8A8UNorm,
        ShaderStorageFormat.R8G8B8A8UNormSrgb => Format.R8G8B8A8UNormSrgb,
        ShaderStorageFormat.B8G8R8A8UNorm => Format.B8G8R8A8UNorm,
        ShaderStorageFormat.R16UInt => Format.R16UInt,
        ShaderStorageFormat.R16Float => Format.R16Float,
        ShaderStorageFormat.R16G16Float => Format.R16G16Float,
        ShaderStorageFormat.R16G16B16A16Float => Format.R16G16B16A16Float,
        ShaderStorageFormat.R32UInt => Format.R32UInt,
        ShaderStorageFormat.R32Float => Format.R32Float,
        ShaderStorageFormat.R32G32Float => Format.R32G32Float,
        ShaderStorageFormat.R32G32B32Float => Format.R32G32B32Float,
        ShaderStorageFormat.R32G32B32A32Float => Format.R32G32B32A32Float,
        ShaderStorageFormat.D24UNormS8UInt => Format.D24UNormS8UInt,
        ShaderStorageFormat.D32Float => Format.D32Float,
        _ => throw new InvalidDataException($"Cooked shader has invalid storage format {format}."),
    };

    private static InvalidDataException Missing(string field) =>
        new($"Cooked shader asset is missing {field}.");
}
