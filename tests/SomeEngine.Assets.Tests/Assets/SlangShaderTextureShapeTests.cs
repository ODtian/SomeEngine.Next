using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangShaderTextureShapeTests
{
    [Fact]
    public async Task Cooked_entry_reflection_preserves_slang_texture_shape_scalar_and_image_facts()
    {
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(Path.GetTempPath(), $"someengine-shape-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "texture_shape.slang");
        File.WriteAllText(path, Source);
        try
        {
            Shader imported = SlangShaderImporter.ImportTransient(path);
            string cookedPath = Path.Combine(directory, "texture_shape.shader.asset");
            AssetWriter.Write(imported, cookedPath);
            Shader asset = await Shader.ReadAsync(cookedPath);
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
                DescriptorType.SampledTexture,
                TextureViewDimension.Texture2DArray,
                TextureSampleType.Float,
                SlangResourceShape.Texture2DArray,
                SlangScalarType.Float32,
                AccessEffect.Read);
            AssertTexture(
                resources,
                "multisampledUInt",
                DescriptorType.SampledTexture,
                TextureViewDimension.Texture2DMS,
                TextureSampleType.UInt,
                SlangResourceShape.Texture2DMultisample,
                SlangScalarType.UInt32,
                AccessEffect.Read);
            AssertTexture(
                resources,
                "environmentMap",
                DescriptorType.SampledTexture,
                TextureViewDimension.Cube,
                TextureSampleType.Float,
                SlangResourceShape.TextureCube,
                SlangScalarType.Float32,
                AccessEffect.Read);
            AssertTexture(
                resources,
                "storageVolume",
                DescriptorType.StorageTexture,
                TextureViewDimension.Texture3D,
                TextureSampleType.Float,
                SlangResourceShape.Texture3D,
                SlangScalarType.Float32,
                AccessEffect.Write);
            AssertTexture(
                resources,
                "textureTable",
                DescriptorType.SampledTexture,
                TextureViewDimension.Texture2D,
                TextureSampleType.Float,
                SlangResourceShape.Texture2D,
                SlangScalarType.Float32,
                AccessEffect.Read);
            Assert.Equal(3U, resources.Single(value => value.Name == "textureTable").DescriptorCount);

            ShaderResourceReflection storage = resources.Single(value => value.Name == "storageVolume");
            Assert.Null(storage.StorageFormat);
            Assert.Equal((uint)SlangImageFormat.Unknown, storage.SlangImageFormat);
            Assert.Equal(AccessEffect.Write, storage.Effect);
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
                Assert.Equal(StorageFormat.R8G8B8A8UNorm, formatted.StorageFormat);
                Assert.Equal((uint)SlangImageFormat.RGBA8, formatted.SlangImageFormat);
                Assert.Equal(AccessEffect.Write, formatted.Effect);
                Assert.Equal(ShaderQualifiers.None, formatted.Qualifiers);
            }

            ShaderResourceReflection atomic = resources.Single(value => value.Name == "atomicValues");
            Assert.Equal(DescriptorType.StorageBuffer, atomic.Kind);
            Assert.Equal(AccessEffect.ReadWrite, atomic.Effect);
            Assert.Equal(ShaderQualifiers.Atomic, atomic.Qualifiers);

            ShaderResourceReflection append = resources.Single(value => value.Name == "appendValues");
            Assert.Equal(AccessEffect.ReadWrite, append.Effect);
            Assert.Equal(
                ShaderQualifiers.Atomic | ShaderQualifiers.Append,
                append.Qualifiers);
            ShaderResourceReflection consume = resources.Single(value => value.Name == "consumeValues");
            Assert.Equal(AccessEffect.ReadWrite, consume.Effect);
            Assert.Equal(
                ShaderQualifiers.Atomic | ShaderQualifiers.Consume,
                consume.Qualifiers);

            ShaderResourceReflection sampler = resources.Single(value => value.Name == "linearSampler");
            Assert.Null(sampler.TextureDimension);
            Assert.Null(sampler.TextureSampleType);
            Assert.Null(sampler.StorageFormat);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertTexture(
        IList<ShaderResourceReflection> resources,
        string name,
        DescriptorType kind,
        TextureViewDimension dimension,
        TextureSampleType sampleType,
        SlangResourceShape slangShape,
        SlangScalarType slangScalar,
        AccessEffect effect)
    {
        ShaderResourceReflection resource = resources.Single(value => value.Name == name);
        Assert.Equal(kind, resource.Kind);
        Assert.Equal(dimension, resource.TextureDimension);
        Assert.Equal(sampleType, resource.TextureSampleType);
        Assert.Equal((uint)slangShape, resource.SlangResourceShape);
        Assert.Equal((uint)slangScalar, resource.SlangScalarType);
        Assert.Equal(effect, resource.Effect);
        Assert.Equal(ShaderQualifiers.None, resource.Qualifiers);
    }

    private const string Source = """
        enum ResourceEffects : uint
        {
            Read = 1,
            Write = 2,
            ReadWrite = 3,
        };

        enum ResourceQualifiers : uint
        {
            None = 0,
            Atomic = 1,
        };

        [__AttributeUsage(_AttributeTargets.Var)]
        struct ResourceEffectAttribute
        {
            ResourceEffects effects;
            ResourceQualifiers qualifiers;
        };

        [[vk::binding(0, 0)]] [ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)] Texture2DArray<float4> sampledArray;
        [[vk::binding(1, 0)]] [ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)] Texture2DMS<uint4> multisampledUInt;
        [[vk::binding(2, 0)]] [ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)] TextureCube<float4> environmentMap;
        [[vk::binding(3, 0)]] [ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)] RWTexture3D<float4> storageVolume;
        [[vk::binding(4, 0)]] SamplerState linearSampler;
        [[vk::binding(5, 0)]] [ResourceEffect(ResourceEffects.Read, ResourceQualifiers.None)] Texture2D<float4> textureTable[3];
        [[vk::binding(6, 0)]] [format("rgba8")] [ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)] RWTexture2D<float4> formattedStorage;
        [[vk::binding(7, 0)]] [ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.Atomic)] RWStructuredBuffer<uint> atomicValues;
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
