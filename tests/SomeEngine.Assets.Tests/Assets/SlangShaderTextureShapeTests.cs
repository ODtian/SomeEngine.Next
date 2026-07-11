using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangShaderTextureShapeTests
{
    [Fact]
    public void Cooked_entry_reflection_preserves_slang_texture_shape_scalar_and_image_facts()
    {
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(Path.GetTempPath(), $"someengine-shape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "texture_shape.slang");
        File.WriteAllText(path, Source);
        try
        {
            ShaderAsset imported = SlangShaderImporter.ImportTransient(path);
            string cookedPath = Path.Combine(directory, "texture_shape.shader.asset");
            ShaderAssetCodec.Save(imported, cookedPath);
            ShaderAsset asset = ShaderAssetCodec.Load(cookedPath);
            Assert.Equal(SlangShaderImporter.ShaderAssetSchemaVersion, asset.SchemaVersion);
            Assert.Equal(SlangShaderImporter.ImporterVersion, asset.ImportTrace!.ImporterVersion);
            ShaderEntryPointReflection reflection = asset.EntryPointReflections!
                .Single(value =>
                    value.Backend == "dxil" &&
                    value.EntryPoint == "Main" &&
                    value.Stage == SomeEngine.Assets.Schema.ShaderStage.Compute);
            IList<ShaderResourceReflection> resources = reflection.Reflection!.Resources!;

            AssertTexture(
                resources,
                "sampledArray",
                ShaderBindingType.TextureRead,
                ShaderTextureDimension.Texture2DArray,
                ShaderTextureSampleType.Float,
                SlangResourceShape.Texture2DArray,
                SlangScalarType.Float32,
                ShaderDeclaredEffect.Read);
            AssertTexture(
                resources,
                "multisampledUInt",
                ShaderBindingType.TextureRead,
                ShaderTextureDimension.Texture2DMS,
                ShaderTextureSampleType.UInt,
                SlangResourceShape.Texture2DMultisample,
                SlangScalarType.UInt32,
                ShaderDeclaredEffect.Read);
            AssertTexture(
                resources,
                "environmentMap",
                ShaderBindingType.TextureRead,
                ShaderTextureDimension.Cube,
                ShaderTextureSampleType.Float,
                SlangResourceShape.TextureCube,
                SlangScalarType.Float32,
                ShaderDeclaredEffect.Read);
            AssertTexture(
                resources,
                "storageVolume",
                ShaderBindingType.TextureReadWrite,
                ShaderTextureDimension.Texture3D,
                ShaderTextureSampleType.Float,
                SlangResourceShape.Texture3D,
                SlangScalarType.Float32,
                ShaderDeclaredEffect.Write);
            AssertTexture(
                resources,
                "textureTable",
                ShaderBindingType.TextureRead,
                ShaderTextureDimension.Texture2D,
                ShaderTextureSampleType.Float,
                SlangResourceShape.Texture2D,
                SlangScalarType.Float32,
                ShaderDeclaredEffect.Read);
            Assert.Equal(3U, resources.Single(value => value.Name == "textureTable").DescriptorCount);

            ShaderResourceReflection storage = resources.Single(value => value.Name == "storageVolume");
            Assert.Equal(ShaderStorageFormat.Unknown, storage.StorageFormat);
            Assert.Equal((uint)SlangImageFormat.Unknown, storage.SlangImageFormat);
            Assert.Equal(ShaderDeclaredEffect.Write, storage.DeclaredEffect);
            Assert.Equal(1U, storage.DescriptorCount);

            foreach (string backend in new[] { "dxil", "spirv" })
            {
                ShaderResourceReflection formatted = asset.EntryPointReflections!
                    .Single(value =>
                        value.Backend == backend &&
                        value.EntryPoint == "Main" &&
                        value.Stage == SomeEngine.Assets.Schema.ShaderStage.Compute)
                    .Reflection!.Resources!
                    .Single(value => value.Name == "formattedStorage");
                Assert.Equal(ShaderStorageFormat.R8G8B8A8UNorm, formatted.StorageFormat);
                Assert.Equal((uint)SlangImageFormat.RGBA8, formatted.SlangImageFormat);
                Assert.Equal(ShaderDeclaredEffect.Write, formatted.DeclaredEffect);
                Assert.Equal(ShaderDeclaredOperations.None, formatted.DeclaredOperations);
            }

            ShaderResourceReflection atomic = resources.Single(value => value.Name == "atomicValues");
            Assert.Equal(ShaderBindingType.StorageBufferReadWrite, atomic.BindingType);
            Assert.Equal(ShaderDeclaredEffect.ReadWrite, atomic.DeclaredEffect);
            Assert.Equal(ShaderDeclaredOperations.Atomic, atomic.DeclaredOperations);

            ShaderResourceReflection append = resources.Single(value => value.Name == "appendValues");
            Assert.Equal(ShaderDeclaredEffect.Unspecified, append.DeclaredEffect);
            Assert.Equal(ShaderDeclaredOperations.None, append.DeclaredOperations);
            Assert.Equal(
                ShaderReflectedOperations.Atomic | ShaderReflectedOperations.Append,
                append.ReflectedOperations);
            ShaderResourceReflection consume = resources.Single(value => value.Name == "consumeValues");
            Assert.Equal(ShaderDeclaredEffect.Unspecified, consume.DeclaredEffect);
            Assert.Equal(ShaderDeclaredOperations.None, consume.DeclaredOperations);
            Assert.Equal(
                ShaderReflectedOperations.Atomic | ShaderReflectedOperations.Consume,
                consume.ReflectedOperations);

            ShaderResourceReflection sampler = resources.Single(value => value.Name == "linearSampler");
            Assert.Equal(ShaderTextureDimension.Unknown, sampler.TextureDimension);
            Assert.Equal(ShaderTextureSampleType.Unknown, sampler.TextureSampleType);
            Assert.Equal(ShaderStorageFormat.Unknown, sampler.StorageFormat);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertTexture(
        IList<ShaderResourceReflection> resources,
        string name,
        ShaderBindingType bindingType,
        ShaderTextureDimension dimension,
        ShaderTextureSampleType sampleType,
        SlangResourceShape slangShape,
        SlangScalarType slangScalar,
        ShaderDeclaredEffect declaredEffect)
    {
        ShaderResourceReflection resource = resources.Single(value => value.Name == name);
        Assert.Equal(bindingType, resource.BindingType);
        Assert.Equal(dimension, resource.TextureDimension);
        Assert.Equal(sampleType, resource.TextureSampleType);
        Assert.Equal((uint)slangShape, resource.SlangResourceShape);
        Assert.Equal((uint)slangScalar, resource.SlangScalarType);
        Assert.Equal(declaredEffect, resource.DeclaredEffect);
        Assert.Equal(ShaderDeclaredOperations.None, resource.DeclaredOperations);
        Assert.Equal(ShaderReflectedOperations.None, resource.ReflectedOperations);
        Assert.NotEqual(ShaderReflectedAccess.Unknown, resource.ReflectedAccess);
    }

    private const string Source = """
        enum ResourceEffects : uint
        {
            Read = 1,
            Write = 2,
            ReadWrite = 3,
        };

        enum ResourceOperations : uint
        {
            None = 0,
            Atomic = 1,
        };

        [__AttributeUsage(_AttributeTargets.Var)]
        struct ResourceEffectAttribute
        {
            ResourceEffects effects;
            ResourceOperations operations;
        };

        [[vk::binding(0, 0)]] [ResourceEffect(ResourceEffects.Read, ResourceOperations.None)] Texture2DArray<float4> sampledArray;
        [[vk::binding(1, 0)]] [ResourceEffect(ResourceEffects.Read, ResourceOperations.None)] Texture2DMS<uint4> multisampledUInt;
        [[vk::binding(2, 0)]] [ResourceEffect(ResourceEffects.Read, ResourceOperations.None)] TextureCube<float4> environmentMap;
        [[vk::binding(3, 0)]] [ResourceEffect(ResourceEffects.Write, ResourceOperations.None)] RWTexture3D<float4> storageVolume;
        [[vk::binding(4, 0)]] SamplerState linearSampler;
        [[vk::binding(5, 0)]] [ResourceEffect(ResourceEffects.Read, ResourceOperations.None)] Texture2D<float4> textureTable[3];
        [[vk::binding(6, 0)]] [format("rgba8")] [ResourceEffect(ResourceEffects.Write, ResourceOperations.None)] RWTexture2D<float4> formattedStorage;
        [[vk::binding(7, 0)]] [ResourceEffect(ResourceEffects.ReadWrite, ResourceOperations.Atomic)] RWStructuredBuffer<uint> atomicValues;
        [[vk::binding(8, 0)]] AppendStructuredBuffer<uint> appendValues;
        [[vk::binding(9, 0)]] ConsumeStructuredBuffer<uint> consumeValues;

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void Main(uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            float4 sampled = sampledArray.Load(int4(0, 0, 0, 0));
            uint4 multisampled = multisampledUInt.Load(int2(0, 0), 0);
            float4 environment = environmentMap.SampleLevel(linearSampler, float3(1, 0, 0), 0);
            float4 tableValue = textureTable[0].Load(int3(0, 0, 0));
            storageVolume[uint3(0, 0, 0)] = sampled + float4(multisampled) + environment + tableValue;
            formattedStorage[uint2(0, 0)] = sampled;
            uint original;
            InterlockedAdd(atomicValues[0], 1, original);
            appendValues.Append(original);
            uint consumed = consumeValues.Consume();
            atomicValues[1] = consumed;
        }
        """;
}
