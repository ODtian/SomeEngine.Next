using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using SomeEngine.Render.Assets;
using Xunit;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;
using NullDevice = SomeEngine.Graphics.Null.Device;
using NullOptions = SomeEngine.Graphics.Null.Options;

namespace SomeEngine.RenderGraph.Tests;

public sealed class ShaderArtifactEndToEndTests
{
    [Fact]
    public void Slang_cook_codec_projection_and_rg_validate_a_nonempty_texture_contract()
    {
        if (!OperatingSystem.IsWindows()) return;

        string directory = Path.Combine(
            FindProjectRoot(),
            ".artifacts",
            "shader-contract-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sourcePath = Path.Combine(directory, "shader_contract.slang");
        string cookedPath = Path.Combine(directory, "shader_contract.shader.asset");
        File.WriteAllText(sourcePath, Source);
        try
        {
            ShaderAsset imported = SlangShaderImporter.ImportTransient(
                sourcePath,
                SlangShaderCookProfiles.D3D12ShaderModel62);
            ShaderAssetCodec.Save(imported, cookedPath);
            ShaderAsset cooked = ShaderAssetCodec.Load(cookedPath);
            Assert.True(
                cooked.Variants?.Any(value =>
                    value.Backend == "dxil" &&
                    value.EntryPoint == "Main" &&
                    value.Stage == AssetShaderStage.Compute) == true,
                $"Cooked variants: {string.Join(", ", cooked.Variants?.Select(value => $"{value.Backend}/{value.EntryPoint}/{value.Stage}") ?? [])}");
            ShaderDesc shader = ShaderAssetProjection.Dxil(cooked, "Main", AssetShaderStage.Compute);
            ShaderBinding[] bindings = shader.Interface.Bindings.ToArray();
            ShaderBinding sampledBinding = Assert.Single(
                bindings,
                static value => value.Kind == BindingKind.SampledTexture);
            ShaderBinding storageBinding = Assert.Single(
                bindings,
                static value => value.Kind == BindingKind.StorageTexture);

            Assert.Equal(DeclaredEffect.Read, sampledBinding.DeclaredEffect);
            Assert.Equal(SomeEngine.Graphics.ShaderTextureDimension.Texture2DArray, sampledBinding.TextureDimension);
            Assert.Equal(TextureSampleType.Float, sampledBinding.TextureSampleType);
            Assert.Equal(DeclaredEffect.Write, storageBinding.DeclaredEffect);
            Assert.Equal(Format.Unknown, storageBinding.StorageFormat);

            using NullDevice device = new(new NullOptions());
            GraphRecording recording = new();
            TextureId texture = recording.AddTexture(
                new TextureDesc(
                    4,
                    4,
                    Format.R8G8B8A8UNorm,
                    TextureUsage.Sampled,
                    ArrayLayers: 2),
                default);
            TextureViewId view = recording.AddTextureView(
                texture,
                new TextureSubresourceRange(0, 1, 0, 2, TextureAspect.Color),
                TextureViewUsage.ShaderResource,
                Format.Unknown,
                null,
                TextureViewDimension.Texture2DArray);
            TextureId outputTexture = recording.AddTexture(
                new TextureDesc(4, 4, Format.R8G8B8A8UNorm, TextureUsage.Storage),
                default);
            TextureViewId outputView = recording.AddTextureView(
                outputTexture,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                TextureViewUsage.Storage,
                Format.Unknown,
                null,
                TextureViewDimension.Texture2D);
            int pass = recording.AddPass("asset-contract", QueueSelection.Compute);
            TextureViewAccess access = recording.AddTextureViewAccess(
                pass,
                view,
                ResourceEffect.Read,
                PriorContents.Required,
                WriteCoverage.Partial);
            TextureViewAccess outputAccess = recording.AddTextureViewAccess(
                pass,
                outputView,
                ResourceEffect.Write,
                PriorContents.Discard,
                WriteCoverage.Full);
            ShaderBindingAccess sampledMapping = recording.AddShaderBindingAccess(
                pass,
                sampledBinding.Group,
                sampledBinding.Binding,
                0,
                access);
            ShaderBindingAccess storageMapping = recording.AddShaderBindingAccess(
                pass,
                storageBinding.Group,
                storageBinding.Binding,
                0,
                outputAccess);
            recording.AddShader(pass, shader, [sampledMapping, storageMapping]);
            recording.SetExecution(pass, static (ICommandContext _, in PassResources _) => { });

            FrozenGraph frozen = recording.Freeze(device);
            Assert.Single(frozen.Passes[0].Shaders);
            Assert.Equal(2, frozen.Passes[0].Shaders[0].Bindings.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindProjectRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "SomeEngine.slnx"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }
        throw new DirectoryNotFoundException("Could not locate SomeEngine.slnx for shader-library imports.");
    }

    private const string Source = """
        import resource_effects;

        [ResourceEffect(ResourceEffects.Write, ResourceOperations.None)]
        RWTexture2D<float4> outputTexture : register(u1, space0);

        [shader("compute")]
        [numthreads(1, 1, 1)]
        void Main(
            [ResourceEffect(ResourceEffects.Read, ResourceOperations.None)]
            uniform Texture2DArray<float4> sourceTexture,
            uint3 dispatchThreadId : SV_DispatchThreadID)
        {
            outputTexture[dispatchThreadId.xy] = sourceTexture.Load(int4(dispatchThreadId.xy, 0, 0));
        }
        """;
}
