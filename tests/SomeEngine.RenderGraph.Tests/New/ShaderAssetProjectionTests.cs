using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Importers;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using System.Security.Cryptography;
using Xunit;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ShaderAssetProjectionTests
{
    [Fact]
    public void Cooked_asset_provider_projects_exact_zero_binding_dxil_entries()
    {
        using CookedShaderAssetFixture assets = new();
        ShaderAsset asset = assets.LoadHelloTriangle();

        ShaderDesc vertex = ShaderAssetProjection.Dxil(asset, "VSMain", AssetShaderStage.Vertex);
        ShaderDesc pixel = ShaderAssetProjection.Dxil(asset, "PSMain", AssetShaderStage.Pixel);

        Assert.Equal(ShaderBinaryFormat.Dxil, vertex.Format);
        Assert.Equal(SomeEngine.Graphics.ShaderStage.Vertex, vertex.Stage);
        Assert.Equal(SomeEngine.Graphics.ShaderStage.Pixel, pixel.Stage);
        Assert.NotEmpty(vertex.Bytecode.ToArray());
        Assert.NotEmpty(pixel.Bytecode.ToArray());
        Assert.True(vertex.Key.IsValid);
        Assert.True(pixel.Key.IsValid);
        Assert.NotEqual(vertex.Key, pixel.Key);
        Assert.Empty(vertex.Interface.Bindings.ToArray());
        Assert.Empty(pixel.Interface.Bindings.ToArray());
    }

    [Fact]
    public void Cooked_resource_shape_projects_losslessly_into_graphics_shader_binding()
    {
        byte[] bytecode = [1, 2, 3, 4, 5, 6];
        ShaderResourceReflection resource = new()
        {
            Name = "environmentMaps",
            Stages = 0x02,
            Binding = 7,
            Space = 3,
            BindingType = ShaderBindingType.TextureRead,
            DescriptorCount = 2,
            ReflectedAccess = ShaderReflectedAccess.ReadOnly,
            DeclaredEffect = ShaderDeclaredEffect.Unspecified,
            TextureDimension = SomeEngine.Assets.Schema.ShaderTextureDimension.CubeArray,
            TextureSampleType = ShaderTextureSampleType.Float,
            StorageFormat = ShaderStorageFormat.Unknown,
            SlangResourceShape = 0x44,
            SlangResourceAccess = 1,
            SlangScalarType = 8,
            SlangImageFormat = 0,
        };
        ShaderAsset asset = Asset(bytecode, resource);

        ShaderDesc shader = ShaderAssetProjection.Dxil(asset, "PSMain", AssetShaderStage.Pixel);
        ShaderBinding binding = Assert.Single(shader.Interface.Bindings.ToArray());

        Assert.Equal(3U, binding.Group);
        Assert.Equal(7U, binding.Binding);
        Assert.Equal(2U, binding.Count);
        Assert.Equal(BindingKind.SampledTexture, binding.Kind);
        Assert.Equal(ReflectedAccess.ReadOnly, binding.ReflectedAccess);
        Assert.Equal(DeclaredEffect.Unspecified, binding.DeclaredEffect);
        Assert.Equal(SomeEngine.Graphics.ShaderTextureDimension.CubeArray, binding.TextureDimension);
        Assert.Equal(TextureSampleType.Float, binding.TextureSampleType);
        Assert.Equal(Format.Unknown, binding.StorageFormat);
        Assert.True(shader.Key.IsValid);
    }

    [Fact]
    public void Projection_rejects_stale_schema_and_importer_versions()
    {
        byte[] bytecode = [1, 2, 3, 4];
        ShaderResourceReflection resource = new()
        {
            Name = "texture",
            Stages = 0x02,
            BindingType = ShaderBindingType.TextureRead,
            DescriptorCount = 1,
        };
        ShaderAsset asset = Asset(bytecode, resource);

        asset.SchemaVersion = 0;
        InvalidDataException staleSchema = Assert.Throws<InvalidDataException>(() =>
            ShaderAssetProjection.Dxil(asset, "PSMain", AssetShaderStage.Pixel));
        Assert.Contains("schema version", staleSchema.Message, StringComparison.OrdinalIgnoreCase);

        asset.SchemaVersion = SlangShaderImporter.ShaderAssetSchemaVersion;
        asset.ImportTrace!.ImporterVersion = SlangShaderImporter.ImporterVersion - 1;
        InvalidDataException staleImporter = Assert.Throws<InvalidDataException>(() =>
            ShaderAssetProjection.Dxil(asset, "PSMain", AssetShaderStage.Pixel));
        Assert.Contains("importer version", staleImporter.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Current_slang_projection_rejects_known_raw_texture_facts_that_cannot_be_normalized()
    {
        byte[] bytecode = [1, 2, 3, 4];
        ShaderResourceReflection resource = new()
        {
            Name = "unsupportedTexture",
            Stages = 0x02,
            BindingType = ShaderBindingType.TextureRead,
            DescriptorCount = 1,
            ReflectedAccess = ShaderReflectedAccess.ReadOnly,
            SlangResourceShape = 0x02,
            SlangScalarType = 8,
        };
        ShaderAsset asset = Asset(bytecode, resource);

        NotSupportedException shape = Assert.Throws<NotSupportedException>(() =>
            ShaderAssetProjection.Dxil(asset, "PSMain", AssetShaderStage.Pixel));
        Assert.Contains("dimension", shape.Message, StringComparison.OrdinalIgnoreCase);

        resource.TextureDimension = SomeEngine.Assets.Schema.ShaderTextureDimension.Texture2D;
        NotSupportedException sample = Assert.Throws<NotSupportedException>(() =>
            ShaderAssetProjection.Dxil(asset, "PSMain", AssetShaderStage.Pixel));
        Assert.Contains("sample", sample.Message, StringComparison.OrdinalIgnoreCase);

        resource.BindingType = ShaderBindingType.TextureReadWrite;
        resource.ReflectedAccess = ShaderReflectedAccess.ReadWrite;
        resource.TextureSampleType = ShaderTextureSampleType.Float;
        resource.SlangImageFormat = 15;
        NotSupportedException storage = Assert.Throws<NotSupportedException>(() =>
            ShaderAssetProjection.Dxil(asset, "PSMain", AssetShaderStage.Pixel));
        Assert.Contains("storage", storage.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Storage_projection_keeps_a_genuinely_unknown_slang_image_format_unconstrained()
    {
        byte[] bytecode = [1, 2, 3, 4];
        ShaderResourceReflection resource = new()
        {
            Name = "storageTexture",
            Stages = 0x02,
            BindingType = ShaderBindingType.TextureReadWrite,
            DescriptorCount = 1,
            ReflectedAccess = ShaderReflectedAccess.ReadWrite,
            DeclaredEffect = ShaderDeclaredEffect.ReadWrite,
            DeclaredOperations = ShaderDeclaredOperations.Atomic,
            TextureDimension = SomeEngine.Assets.Schema.ShaderTextureDimension.Texture2D,
            TextureSampleType = ShaderTextureSampleType.Float,
            StorageFormat = ShaderStorageFormat.Unknown,
            SlangResourceShape = 0x02,
            SlangScalarType = 8,
            SlangImageFormat = 0,
        };

        ShaderBinding binding = Assert.Single(
            ShaderAssetProjection.Dxil(Asset(bytecode, resource), "PSMain", AssetShaderStage.Pixel)
                .Interface.Bindings.ToArray());
        Assert.Equal(Format.Unknown, binding.StorageFormat);
        Assert.Equal(DeclaredEffect.ReadWrite, binding.DeclaredEffect);
        Assert.Equal(DeclaredOperations.Atomic, binding.DeclaredOperations);
    }

    private static ShaderAsset Asset(byte[] bytecode, ShaderResourceReflection resource) => new()
    {
        SchemaVersion = SlangShaderImporter.ShaderAssetSchemaVersion,
        Name = "projection-shape",
        ImportTrace = new ImportTrace { ImporterVersion = SlangShaderImporter.ImporterVersion },
        Variants =
        [
            new ShaderBytecode
            {
                Backend = "dxil",
                EntryPoint = "PSMain",
                Stage = AssetShaderStage.Pixel,
                Data = bytecode,
                ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytecode)),
            },
        ],
        EntryPointReflections =
        [
            new ShaderEntryPointReflection
            {
                Backend = "dxil",
                EntryPoint = "PSMain",
                Stage = AssetShaderStage.Pixel,
                Reflection = new ShaderReflectionData { Resources = [resource] },
            },
        ],
    };
}
