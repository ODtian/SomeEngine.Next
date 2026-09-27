using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using SlangShaderSharp;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangShaderCookProfileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceProfilePreservesIdentityAndTracksSelectedDependencyDirectory(bool useProjectDirectory)
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
            string? dependencyRoot = useProjectDirectory ? projectRoot : null;
            string dependencyPrefix = useProjectDirectory ? "assets/Shaders/" : "";
            string definitionPath = Path.ChangeExtension(sourcePath, ".shader");
            using var definition = new ShaderAsset();
            definition.GetBlock<ShaderSourceBlock>().SourceFiles = ["profile_test.slang"];
            definition.GetBlock<ShaderSourceBlock>().CookProfile = SlangShaderCookProfiles.DefaultName;
            await definition.SaveSourceAsync(definitionPath);
            AssetEntry first = await SlangShaderImporter.BuildAsync(definitionPath, projectRoot: dependencyRoot);
            using ShaderAsset defaultAsset = await ShaderAsset.OpenAsync(first.Path);
            definition.GetBlock<ShaderSourceBlock>().CookProfile = SlangShaderCookProfiles.D3D12ShaderModel62Name;
            await definition.SaveSourceAsync(definitionPath);
            AssetEntry second = await SlangShaderImporter.BuildAsync(definitionPath, projectRoot: dependencyRoot);
            using ShaderAsset sm62Asset = await ShaderAsset.OpenAsync(second.Path);
            Assert.Equal(defaultAsset.Id, sm62Asset.Id);
            Assert.Equal(definition.Id, sm62Asset.Id);
            var defaultTrace = defaultAsset.GetBlock<ShaderBlock>().ImportTrace;
            var sm62Trace = sm62Asset.GetBlock<ShaderBlock>().ImportTrace;
            Assert.NotNull(defaultTrace);
            Assert.NotNull(sm62Trace);
            Assert.NotEqual(defaultTrace.ContentFingerprint, sm62Trace.ContentFingerprint);
            Assert.Equal(SlangShaderImporter.ImporterVersion, sm62Trace.ImporterVersion);
            Assert.Contains(sm62Trace.Dependencies!, dependency => dependency.Path == dependencyPrefix + "profile_test.slang");
            Assert.Contains(sm62Trace.Dependencies!, dependency => dependency.Path == dependencyPrefix + "profile_test.shader");
            Assert.Equal(1, CountBackend(defaultAsset, "dxil"));
            Assert.Equal(1, CountBackend(sm62Asset, "dxil"));
            Assert.Equal(1, CountBackend(defaultAsset, "spirv"));
            Assert.Equal(1, CountBackend(sm62Asset, "spirv"));
            Assert.Equal(ReflectionSurface(defaultAsset), ReflectionSurface(sm62Asset));
            Assert.Equal(BackendHashes(defaultAsset, "spirv"), BackendHashes(sm62Asset, "spirv"));
            var inputs = sm62Trace.Dependencies!.Select(value => new FileDependency
                { RelativePath = value.Path!, ContentHash = value.ContentHash! }).ToArray();
            ImportInputs current = SlangShaderImporter.GetDefinitionInputs(definition, definitionPath, inputs, projectRoot: dependencyRoot)!;
            Assert.Equal(sm62Trace.ContentFingerprint, current.InputHash);
            using ShaderAsset loaded = await ShaderAsset.OpenAsync(second.Path);
            Assert.Equal(sm62Asset.Id, loaded.Id);
            Assert.Equal(ReflectionSurface(sm62Asset), ReflectionSurface(loaded));
            Assert.False(File.Exists(SourceMeta.GetMetaPath(sourcePath)));
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
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        TargetDesc[] targets = SlangShaderCookProfiles.CreateTargets(slang, name);

        Assert.Equal(2, targets.Length);
        Assert.Equal(SlangCompileTarget.Dxil, targets[0].Format);
        Assert.Equal(slang.FindProfile(expectedDxil), targets[0].Profile);
        Assert.Equal(SlangCompileTarget.Spirv, targets[1].Format);
        Assert.Equal(slang.FindProfile(expectedSpirv), targets[1].Profile);
    }

    private static int CountBackend(ShaderAsset asset, string backend)
        => asset.GetBlock<ShaderBlock>().Programs!.Count(program =>
            string.Equals(program.Backend, backend, StringComparison.Ordinal));

    private static string[] ReflectionSurface(ShaderAsset asset)
        => asset.GetBlock<ShaderBlock>().EntryPointReflections!
            .Select(reflection =>
                $"{reflection.Backend}:{reflection.Stage}:{reflection.EntryPoint}:"
                    + $"{reflection.Reflection?.Resources?.Count ?? 0}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string[] BackendHashes(ShaderAsset asset, string backend)
        => asset.GetBlock<ShaderBlock>().Programs!
            .Where(program => string.Equals(program.Backend, backend, StringComparison.Ordinal))
            .Select(static program => program.ContentHash!)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
}
