using SlangShaderSharp;
using SomeEngine.Render;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class DefaultMaterialImportTests
{
    [Theory]
    [InlineData("standard", "StandardScalars", new string[] { "AlbedoMap", "NormalMap", "ARMMap", "MaterialSampler" })]
    [InlineData("unlit", "UnlitScalars", new string[] { "AlbedoMap", "MaterialSampler" })]
    [InlineData("deformed-phong", "UnlitScalars", new string[] { "AlbedoMap", "MaterialSampler" })]
    public async Task DefaultMaterialsKeepTextureAndScalarBindingsAcrossOpaqueAndLayerEntries(
        string materialName, string scalarType, string[] resources)
    {
        string repository = Repository();
        string definitionPath = Path.Combine(repository, "samples", "DefaultGame", "assets", "Shaders",
            materialName + ".shader");
        string engineShaders = Path.Combine(repository, "assets", "Shaders");
        SlangCompileTarget[] targets = [SlangCompileTarget.Dxil, SlangCompileTarget.Spirv];
        using ShaderAsset definition = await ShaderAsset.OpenSourceAsync(definitionPath);
        using ShaderAsset built = SlangShaderImporter.CompileDefinition(definition, definitionPath,
            targets, includeDirectories: [engineShaders], projectRoot: repository);
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(definitionPath,
            SlangShaderImporter.CreateTargets(slang, targets), [engineShaders]);
        using var library = new ShaderLibrary(slang, settings);

        Shader shader = await library.CreateAsync(built);

        foreach (string entryName in new[] { "ClusterShadeMode", "LayerShadeMode" })
        {
            Shader variant = shader.RequireSpecialization(entryName, ["0"]);
            foreach (SlangCompileTarget target in targets)
            {
                int targetIndex = variant.RequireSlangTarget(target);
                EntryPointReflection entry = variant.RequireSlangEntry(targetIndex, entryName, SlangStage.Compute);
                Assert.True(variant.GetSlangEntryBytes(targetIndex, entry).Length > 0);
                string[] names = ShaderParameterResources.Create(variant, target, entryName).Resources
                    .ToArray().Where(resource => resource.IsUsed).Select(resource => resource.Name).ToArray();
                foreach (string resource in resources) Assert.Contains(resource, names);
                Assert.Contains("MaterialScalarRegion", names);
                TypeReflection scalars = variant.GetSlangReflection(targetIndex).FindTypeByName(scalarType)!.Value;
                Assert.Contains(Enumerable.Range(0, (int)scalars.FieldCount)
                    .Select(index => scalars.GetFieldByIndex((uint)index).Name), name => name == "BaseColorTint");
            }
        }
    }

    private static string Repository()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent!;
        return directory.FullName;
    }
}
