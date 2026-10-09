using System.Globalization;
using System.Text.Json.Nodes;
using Game.Assets;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Cluster.Tests;

internal static class ShadowVisibilityContent
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        MaterialTypes.Register();
        ImportedScenes.SceneMaterialAsset.Register();
        return true;
    });

    internal static async Task BuildStandardAsync(string engine, string sourceContent, string content)
    {
        _ = Registered.Value;
        string shaders = Path.Combine(content, "assets", "Shaders");
        string sourceShaders = Path.Combine(sourceContent, "assets", "Shaders");
        foreach (string source in Directory.EnumerateFiles(sourceShaders, "*.slang", SearchOption.AllDirectories))
        {
            string target = Path.Combine(shaders, Path.GetRelativePath(sourceShaders, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        string tests = Path.Combine(engine, "tests", "SomeEngine.Render.Cluster.Tests", "Shaders");
        const string name = "shadow_reference_visibility";
        File.Copy(Path.Combine(tests, name + ".slang"), Path.Combine(shaders, name + ".slang"), overwrite: true);
        Guid shader = Guid.NewGuid();
        JsonNode definition = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(tests, "shadow_visibility.shader")))!;
        definition["Fields"]!["Id"] = shader.ToString();
        definition["Fields"]!["Name"] = "Source material shadow visibility";
        definition["Blocks"]![0]!["Fields"]!["SourceNames"] = new JsonArray(name + ".slang");
        string file = Path.Combine(shaders, name + ".shader");
        await File.WriteAllTextAsync(file, definition.ToJsonString());
        var project = new AssetProject(content, [],
            sourceFormats: [new ShaderSourceFormat([Path.Combine(engine, "assets", "Shaders")])]);
        await project.BuildAsync(file);
        AssetEntry entry = project.Manifest.List(MaterialAsset.FileType).Single(value => value.Name == "Standard");
        using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(content, entry.Path));
        material.GetBlock<MaterialBlock>().Shader = new(shader);
        project.RegisterAsset(await material.SaveAsync(Path.Combine(content, entry.Path)));
    }

    internal static async Task BuildAsync(string engine, string sourceContent, string content, string geometry,
        bool spheres)
    {
        _ = Registered.Value;
        string shaders = Path.Combine(content, "assets", "Shaders");
        string sourceShaders = Path.Combine(sourceContent, "assets", "Shaders");
        foreach (string source in Directory.EnumerateFiles(sourceShaders, "*.slang", SearchOption.AllDirectories))
        {
            string target = Path.Combine(shaders, Path.GetRelativePath(sourceShaders, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        string tests = Path.Combine(engine, "tests", "SomeEngine.Render.Cluster.Tests", "Shaders");
        File.Copy(Path.Combine(tests, "shadow_visibility_material.slang"),
            Path.Combine(shaders, "shadow_visibility_material.slang"), overwrite: true);
        ModelRoot model = ModelRoot.Load(geometry);
        var project = new AssetProject(content, [],
            sourceFormats: [new ShaderSourceFormat([Path.Combine(engine, "assets", "Shaders")])]);
        var variants = new Dictionary<(float Cutoff, float NormalScale), Guid>();
        foreach (AssetEntry entry in project.Manifest.List(MaterialAsset.FileType).ToArray())
        {
            using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(content, entry.Path));
            MaterialBlock values = material.GetBlock<MaterialBlock>();
            float cutoff = -1;
            if (!spheres && entry.SubAssetKey.StartsWith("material:index:", StringComparison.Ordinal))
            {
                int index = int.Parse(entry.SubAssetKey.AsSpan("material:index:".Length), CultureInfo.InvariantCulture);
                var original = model.LogicalMaterials[index];
                if (original.Alpha == AlphaMode.MASK)
                    cutoff = original.AlphaCutoff / Math.Max(original.FindChannel("BaseColor")?.Color.W ?? 1, 1e-8f);
            }
            float normalScale = values.GetParameter("NormalScale")?.Float ?? 1;
            var key = (cutoff, normalScale);
            if (!variants.TryGetValue(key, out Guid shader))
            {
                shader = Guid.NewGuid();
                string name = $"shadow_visibility_{variants.Count}";
                string source = $"#define SHADOW_ALPHA_CUTOFF {cutoff.ToString("R", CultureInfo.InvariantCulture)}\n" +
                    $"#define SHADOW_NORMAL_SCALE {normalScale.ToString("R", CultureInfo.InvariantCulture)}\n" +
                    "#include \"shadow_visibility_material.slang\"\n";
                await File.WriteAllTextAsync(Path.Combine(shaders, name + ".slang"), source);
                JsonNode definition = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(tests, "shadow_visibility.shader")))!;
                definition["Fields"]!["Id"] = shader.ToString();
                definition["Fields"]!["Name"] = name;
                definition["Blocks"]![0]!["Fields"]!["SourceNames"] = new JsonArray(name + ".slang");
                string file = Path.Combine(shaders, name + ".shader");
                await File.WriteAllTextAsync(file, definition.ToJsonString());
                await project.BuildAsync(file);
                variants.Add(key, shader);
            }
            values.Shader = new(shader);
            project.RegisterAsset(await material.SaveAsync(Path.Combine(content, entry.Path)));
        }
    }
}
