using System.Text.Json;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangShaderCookProfileTests
{
    [Fact]
    public async Task SourceProfile_ChangesImportFingerprintAndPreservesAssetIdentityAndReflection()
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

            ImportedAsset defaultImport = Assert.Single(
                await importer.ImportAsync(projectRoot, sourcePath));
            ShaderData defaultAsset = await ShaderData.ReadAsync(defaultImport.OutputPath);
            SourceOutput defaultMeta = SourceMeta.Load(sourcePath).FindOutput(
                Path.ChangeExtension(sourcePath, ".shader.asset"))!;

            WriteMeta(sourcePath, sourceGuid, SlangShaderCookProfiles.D3D12ShaderModel62Name);
            ImportedAsset sm62Import = Assert.Single(
                await importer.ImportAsync(projectRoot, sourcePath));
            ShaderData sm62Asset = await ShaderData.ReadAsync(sm62Import.OutputPath);
            string outputPath = Path.ChangeExtension(sourcePath, ".shader.asset");
            SourceOutput sm62Meta = SourceMeta.Load(sourcePath).FindOutput(outputPath)!;

            Assert.Equal(defaultAsset.AssetGuid, sm62Asset.AssetGuid);
            Assert.Equal(defaultMeta.AssetGuid, sm62Meta.AssetGuid);
            Assert.Equal(sourceGuid, SourceMeta.Load(sourcePath).SourceGuid);
            Assert.NotEqual(defaultMeta.InputHash, sm62Meta.InputHash);
            Assert.Equal(
                sm62Asset.ImportTrace!.ContentFingerprint,
                sm62Meta.InputHash);
            Assert.Equal(SlangShaderImporter.ImporterVersion, SourceMeta.Load(sourcePath).ImporterVersion);
            Assert.Collection(
                SourceMeta.Load(sourcePath).Inputs,
                dependency => Assert.Equal(
                    "assets/Shaders/profile_test.slang",
                    dependency.RelativePath));

            Assert.Equal(1, CountBackend(defaultAsset, "dxil"));
            Assert.Equal(1, CountBackend(sm62Asset, "dxil"));
            Assert.Equal(1, CountBackend(defaultAsset, "spirv"));
            Assert.Equal(1, CountBackend(sm62Asset, "spirv"));
            Assert.Equal(
                ReflectionSurface(defaultAsset),
                ReflectionSurface(sm62Asset));
            Assert.Equal(
                BackendHashes(defaultAsset, "spirv"),
                BackendHashes(sm62Asset, "spirv"));

            SourceMeta sm62SourceMeta = SourceMeta.Load(sourcePath);
            ImportInputs currentFingerprint = importer.GetInputs(
                projectRoot,
                sourcePath,
                sm62SourceMeta)!;
            Assert.Equal(sm62Meta.InputHash, currentFingerprint.InputHash);

            await using BinaryDocument<ShaderData> document =
                await AssetTestFiles.OpenAsync<Shader, ShaderData>(outputPath);
            ShaderData loaded = document.Root;
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
    [InlineData(SlangShaderCookProfiles.D3D12ShaderModel66Name, "sm_6_6", "glsl_460")]
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
        SourceMeta.Save(
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

    private static int CountBackend(ShaderData asset, string backend)
        => asset.Programs!.Count(program =>
            string.Equals(program.Backend, backend, StringComparison.Ordinal));

    private static string[] ReflectionSurface(ShaderData asset)
        => asset.EntryPointReflections!
            .Select(reflection =>
                $"{reflection.Backend}:{reflection.Stage}:{reflection.EntryPoint}:"
                    + $"{reflection.Reflection?.Resources?.Count ?? 0}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string[] BackendHashes(ShaderData asset, string backend)
        => asset.Programs!
            .Where(program => string.Equals(program.Backend, backend, StringComparison.Ordinal))
            .Select(static program => program.ContentHash!)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
}
