using System.Globalization;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

internal static class ShadowVisibilityContent
{
    internal static async Task BuildStandardAsync(string engine, string sourceContent, string content)
    {
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
        string file = Path.Combine(shaders, name + ".shader");
        await WriteShaderAsync(file, shader, "Source material shadow visibility", name + ".slang", 0,
            ClusterShadingPath.Deferred);
        var project = new AssetProject(content, [],
            shaderIncludeDirectories: [Path.Combine(engine, "assets", "Shaders")]);
        await project.BuildAsync(file);
        AssetEntry entry = project.Manifest.List(MaterialAsset.FileType).Single(value => value.Name == "Standard");
        using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(content, entry.Path));
        material.GetBlock<MaterialBlock>().Shader = new(shader);
        project.RegisterAsset(await material.SaveAsync(Path.Combine(content, entry.Path)));
    }

    internal static async Task BuildAsync(string engine, string sourceContent, string content, IEnumerable<Guid> roots,
        ClusterShadingPath shadingPath = ClusterShadingPath.Deferred)
    {
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
        AssetManifest sourceManifest = AssetManifest.Load(Path.Combine(sourceContent, "Library", "AssetManifest"));
        var project = new AssetProject(content, [],
            shaderIncludeDirectories: [Path.Combine(engine, "assets", "Shaders")]);
        await using var files = new AssetLoader(project.CreateStorage());
        var required = new HashSet<Guid>();
        var dependencies = new Stack<Guid>(roots);
        while (dependencies.TryPop(out Guid asset))
        {
            if (!required.Add(asset)) continue;
            foreach (Guid dependency in sourceManifest.Assets[asset].Dependencies)
                dependencies.Push(dependency);
        }
        var models = new Dictionary<SourceGuid, ModelRoot>();
        var variants = new Dictionary<(float Cutoff, float NormalScale, bool DoubleSided, bool HasNormalMap), Guid>();
        foreach (AssetEntry entry in project.Manifest.List(MaterialAsset.FileType)
            .Where(entry => required.Contains(entry.AssetGuid)).ToArray())
        {
            using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(content, entry.Path));
            MaterialBlock values = material.GetBlock<MaterialBlock>();
            AssetEntry sourceEntry = sourceManifest.Assets[entry.AssetGuid];
            float cutoff = -1;
            bool doubleSided = false;
            if (sourceEntry.SubAssetKey.StartsWith("material:index:", StringComparison.Ordinal))
            {
                if (!models.TryGetValue(sourceEntry.SourceGuid, out ModelRoot? model))
                {
                    string sourcePath = sourceManifest.Sources[sourceEntry.SourceGuid];
                    model = ModelRoot.Load(Path.Combine(sourceContent, sourcePath));
                    models.Add(sourceEntry.SourceGuid, model);
                }
                int index = int.Parse(sourceEntry.SubAssetKey.AsSpan("material:index:".Length), CultureInfo.InvariantCulture);
                var original = model.LogicalMaterials[index];
                doubleSided = original.DoubleSided;
                if (original.Alpha == AlphaMode.MASK)
                    cutoff = original.AlphaCutoff / Math.Max(original.FindChannel("BaseColor")?.Color.W ?? 1, 1e-8f);
            }
            using MaterialBlock complete = await material.ResolveAsync(files);
            float normalScale = complete.GetParameter("NormalScale")?.Float ?? 1;
            MaterialParameter? normalMap = complete.GetParameter("NormalMap");
            bool hasNormalMap = normalMap.HasValue && normalMap.Value.Kind == MaterialParameterKind.Texture &&
                !normalMap.Value.Texture.IsEmpty;
            var key = (cutoff, normalScale, doubleSided, hasNormalMap);
            if (!variants.TryGetValue(key, out Guid shader))
            {
                string name = $"shadow_visibility_{variants.Count}";
                string file = Path.Combine(shaders, name + ".shader");
                if (File.Exists(file))
                {
                    AssetEntry previous = AssetText.Describe(await File.ReadAllBytesAsync(file));
                    if (previous.AssetType != ShaderAsset.FileType || previous.Name != name)
                        throw new InvalidDataException("The generated visibility path contains another asset.");
                    shader = previous.AssetGuid;
                }
                else
                    shader = Guid.NewGuid();
                string source = (doubleSided ? "#define CLUSTER_RASTER_TWO_SIDED 1\n" : "") +
                    $"#define SHADOW_FORWARD_VISIBILITY {(shadingPath == ClusterShadingPath.Forward ? 1 : 0)}\n" +
                    $"#define SHADOW_ALPHA_CUTOFF {cutoff.ToString("R", CultureInfo.InvariantCulture)}\n" +
                    $"#define SHADOW_NORMAL_SCALE {normalScale.ToString("R", CultureInfo.InvariantCulture)}\n" +
                    $"#define SHADOW_HAS_NORMAL_MAP {(hasNormalMap ? 1 : 0)}\n" +
                    "#include \"shadow_visibility_material.slang\"\n";
                await File.WriteAllTextAsync(Path.Combine(shaders, name + ".slang"), source);
                await WriteShaderAsync(file, shader, name, name + ".slang", 1, shadingPath);
                await project.BuildAsync(file);
                variants.Add(key, shader);
            }
            values.Shader = new(shader);
            project.RegisterAsset(await material.SaveAsync(Path.Combine(content, entry.Path)));
        }
    }

    private static async Task WriteShaderAsync(string path, Guid id, string name, string sourceName,
        uint visibilityDepth, ClusterShadingPath shadingPath)
    {
        using var shader = new ShaderAsset(id) { Name = name };
        ShaderSourceBlock source = shader.GetBlock<ShaderSourceBlock>();
        source.SourceNames = [sourceName];
        string depth = visibilityDepth.ToString(CultureInfo.InvariantCulture);
        source.Specializations.Add(new() { EntryPoint = "ClusterShadowRequests", Arguments = [depth, "0"] });
        if (shadingPath == ClusterShadingPath.Forward)
        {
            source.Specializations.Add(new() { EntryPoint = "ClusterForwardShade", Arguments = [depth, "0"] });
        }
        await shader.SaveSourceAsync(path);
    }

}
