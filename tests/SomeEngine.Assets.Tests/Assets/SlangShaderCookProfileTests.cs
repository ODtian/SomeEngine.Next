using System.Text.Json;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangShaderCookProfileTests
{
    [Fact]
    public void SourceProfile_InvalidatesCacheAndPreservesAssetIdentityAndReflection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string projectRoot = Path.Combine(
            Path.GetTempPath(),
            $"someengine-slang-profile-{Guid.NewGuid():N}");
        string shaderDirectory = Path.Combine(projectRoot, "assets", "Shaders");
        Directory.CreateDirectory(shaderDirectory);
        File.WriteAllText(Path.Combine(projectRoot, "Directory.Build.props"), "<Project />");
        string sourcePath = Path.Combine(shaderDirectory, "profile_test.slang");
        File.WriteAllText(
            sourcePath,
            "[shader(\"vertex\")] float4 VSMain(uint id : SV_VertexID) : SV_Position "
                + "{ return float4((id == 2 ? 3.0 : -1.0), (id == 1 ? 3.0 : -1.0), 0.0, 1.0); }\n"
                + "[shader(\"pixel\")] float4 PSMain() : SV_Target "
                + "{ return float4(1.0, 0.0, 1.0, 1.0); }");

        try
        {
            SourceGuid sourceGuid = SourceGuid.New();
            WriteMeta(sourcePath, sourceGuid, SlangShaderCookProfiles.DefaultName);
            var importer = new SlangSourceImporter();

            ShaderAsset defaultAsset = Assert.IsType<ShaderAsset>(
                Assert.Single(importer.Import(projectRoot, sourcePath)).Asset);
            AssetMeta defaultMeta = AssetMetaFiles.TryLoad(
                Path.ChangeExtension(sourcePath, ".shader.asset"))!;

            WriteMeta(sourcePath, sourceGuid, SlangShaderCookProfiles.D3D12ShaderModel62Name);
            ShaderAsset sm62Asset = Assert.IsType<ShaderAsset>(
                Assert.Single(importer.Import(projectRoot, sourcePath)).Asset);
            string outputPath = Path.ChangeExtension(sourcePath, ".shader.asset");
            AssetMeta sm62Meta = AssetMetaFiles.TryLoad(outputPath)!;

            Assert.Equal(defaultAsset.AssetGuid, sm62Asset.AssetGuid);
            Assert.Equal(defaultMeta.AssetGuid, sm62Meta.AssetGuid);
            Assert.Equal(sourceGuid, sm62Meta.SourceGuid);
            Assert.NotEqual(defaultMeta.ContentFingerprint, sm62Meta.ContentFingerprint);
            Assert.Equal(
                sm62Asset.ImportTrace!.ContentFingerprint,
                sm62Meta.ContentFingerprint);
            Assert.Equal(SlangShaderImporter.ImporterVersion, sm62Meta.ImporterVersion);
            Assert.Collection(
                sm62Meta.Dependencies,
                dependency => Assert.Equal(
                    "assets/Shaders/profile_test.slang",
                    dependency.RelativePath));

            Assert.Equal(2, CountBackend(defaultAsset, "dxil"));
            Assert.Equal(2, CountBackend(sm62Asset, "dxil"));
            Assert.Equal(2, CountBackend(defaultAsset, "spirv"));
            Assert.Equal(2, CountBackend(sm62Asset, "spirv"));
            Assert.Equal(
                ReflectionSurface(defaultAsset),
                ReflectionSurface(sm62Asset));
            Assert.Equal(
                BackendHashes(defaultAsset, "spirv"),
                BackendHashes(sm62Asset, "spirv"));

            SourceMeta sm62SourceMeta = SourceMetaFiles.Load(sourcePath);
            AssetImportFingerprint currentFingerprint = importer.GetFingerprint(
                projectRoot,
                sourcePath,
                sm62SourceMeta)!;
            Assert.Equal(sm62Meta.ContentFingerprint, currentFingerprint.ContentFingerprint);

            var provider = new ShaderAssetProvider();
            ShaderAsset loaded = provider.Create(sm62Meta.AssetGuid, outputPath);
            Assert.Equal(sm62Asset.AssetGuid, loaded.AssetGuid);
            Assert.Equal(ReflectionSurface(sm62Asset), ReflectionSurface(loaded));
        }
        finally
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(SlangShaderCookProfiles.DefaultName, "sm_6_5", "glsl_460")]
    [InlineData(SlangShaderCookProfiles.D3D12ShaderModel62Name, "sm_6_2", "glsl_460")]
    public void BuiltInProfilesExposeReproducibleTargets(
        string name,
        string expectedDxil,
        string expectedSpirv)
    {
        SlangShaderCookProfile profile = SlangShaderCookProfiles.Resolve(name);

        Assert.Equal(name, profile.Name);
        Assert.Equal(expectedDxil, profile.DxilProfile);
        Assert.Equal(expectedSpirv, profile.SpirvProfile);
    }

    private static void WriteMeta(string sourcePath, SourceGuid sourceGuid, string profileName)
    {
        SourceMetaFiles.Save(
            sourcePath,
            new SourceMeta
            {
                SourceGuid = sourceGuid,
                Importer = nameof(SlangShaderImporter),
                ImporterSettings = JsonSerializer.SerializeToElement(
                    new SlangShaderImporterSettings { CookProfile = profileName },
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    }),
            });
    }

    private static int CountBackend(ShaderAsset asset, string backend)
        => asset.Variants!.Count(variant =>
            string.Equals(variant.Backend, backend, StringComparison.Ordinal));

    private static string[] ReflectionSurface(ShaderAsset asset)
        => asset.EntryPointReflections!
            .Select(reflection =>
                $"{reflection.Backend}:{reflection.Stage}:{reflection.EntryPoint}:"
                    + $"{reflection.Reflection?.Resources?.Count ?? 0}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string[] BackendHashes(ShaderAsset asset, string backend)
        => asset.Variants!
            .Where(variant => string.Equals(variant.Backend, backend, StringComparison.Ordinal))
            .Select(static variant => variant.ContentHash!)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
}
