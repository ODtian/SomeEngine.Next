using SomeEngine.Geometry.Cluster;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SomeEngine.Assets.Schema;
using ImportedScenes;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace DefaultGame.Content;

public static class ImportedSceneBuild
{
    public static async Task BuildAsync(string applicationDirectory, string sourcePath, string projectRoot, string engineRoot)
    {
        applicationDirectory = Path.GetFullPath(applicationDirectory);
        sourcePath = Path.GetFullPath(sourcePath);
        projectRoot = Path.GetFullPath(projectRoot);
        engineRoot = Path.GetFullPath(engineRoot);
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        string copiedSource = Path.Combine(projectRoot, "assets", "Imported", name, Path.GetFileName(sourcePath));
        if (!File.Exists(Path.Combine(projectRoot, "Library", "AssetManifest", AssetManifest.CatalogFileName)))
        {
            if (Directory.Exists(projectRoot) && Directory.EnumerateFileSystemEntries(projectRoot).Any())
                throw new ArgumentException("The output must be empty or a previously built scene application.");
            CopyFiles(Path.Combine(applicationDirectory, "assets"), Path.Combine(projectRoot, "assets"));
            CopyFiles(Path.Combine(applicationDirectory, "Library", "AssetManifest"),
                Path.Combine(projectRoot, "Library", "AssetManifest"));
        }
        CopyFiles(Path.GetDirectoryName(sourcePath)!, Path.GetDirectoryName(copiedSource)!);
        SourceMeta sourceMetadata = SourceMeta.GetOrCreate(copiedSource);
        if (!sourceMetadata.Importers.Any(selection => selection.ImporterId == GltfSourceImporter.SourceImporterId))
        {
            sourceMetadata.Importers.Add(new(GltfSourceImporter.SourceImporterId,
                JsonSerializer.SerializeToElement(new GltfImporterSettings { RequirePersistentElementIds = false },
                    GltfSettingsJson.Default.GltfImporterSettings)));
            SourceMeta.Save(copiedSource, sourceMetadata);
        }
        var (project, sceneImporter) = CreateProject(projectRoot, engineRoot);
        await EngineShaderContent.BuildAsync(engineRoot, project.ShaderTargets);
        EngineShaderContent.SetContentDirectory(project, engineRoot);
        AssetManifest applicationAssets = AssetManifest.Load(Path.Combine(applicationDirectory, "Library", "AssetManifest"));
        foreach (AssetEntry entry in applicationAssets.Assets.Values)
        {
            if (!entry.Path.StartsWith("assets/Shaders/", StringComparison.OrdinalIgnoreCase)
                && !entry.Path.StartsWith("assets/Pipelines/", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.AssetType == ShaderAsset.FileType &&
                File.Exists(Path.Combine(engineRoot, "assets", "Shaders", Path.GetFileName(entry.Path)[..^6]))) continue;
            string destination = Path.Combine(projectRoot, entry.Path);
            CopyIfChanged(Path.Combine(applicationDirectory, entry.Path), destination);
            project.RegisterAsset(entry);
        }
        var (model, metadata, importSeconds) = await ImportSceneAsync(project, sceneImporter, copiedSource, engineRoot);
        var previous = metadata.Outputs.ToDictionary(value => value.AssetGuid, value => (value.Version, value.ContentHash));
        var timer = Stopwatch.StartNew();
        await project.ImportAsync(copiedSource);
        double cachedSeconds = timer.Elapsed.TotalSeconds;
        SourceMeta repeated = SourceMeta.Load(copiedSource);
        if (repeated.Outputs.Count != previous.Count || repeated.Outputs.Any(value =>
                !previous.TryGetValue(value.AssetGuid, out var old) || old != (value.Version, value.ContentHash)))
            throw new InvalidDataException("Unchanged reimport changed generated assets.");

        SampleContent.BuildApplicationSettings(project, engineRoot, GetSceneId(model, metadata));
        int instances = Node.Flatten(model.DefaultScene).Count(static node => node.Mesh is not null);
        File.WriteAllText(Path.Combine(projectRoot, "scene-import.json"), JsonSerializer.Serialize(new
        {
            source = sourcePath, meshes = metadata.Outputs.Count(value => value.SubAssetKey.StartsWith("mesh:")),
            instances, materials = metadata.Outputs.Count(value => value.SubAssetKey.StartsWith("material:")),
            textures = metadata.Outputs.Count(value => value.SubAssetKey.StartsWith("texture:")),
            assets = metadata.Outputs.Count, importSeconds, cachedSeconds, unchangedReimport = true,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Imported {name}: {instances} mesh instances, {metadata.Outputs.Count} assets in {importSeconds:F2}s. {projectRoot}");
    }

    public static async Task ReimportAsync(string projectRoot, string engineRoot)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        engineRoot = Path.GetFullPath(engineRoot);
        string sourcePath = Directory.EnumerateFiles(Path.Combine(projectRoot, "assets", "Imported"), "*", SearchOption.AllDirectories)
            .Single(path => Path.GetExtension(path).Equals(".gltf", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase));
        var (project, sceneImporter) = CreateProject(projectRoot, engineRoot);
        EngineShaderContent.SetContentDirectory(project, engineRoot);
        var (model, metadata, _) = await ImportSceneAsync(project, sceneImporter, sourcePath, engineRoot);
        SampleContent.BuildApplicationSettings(project, engineRoot, GetSceneId(model, metadata));
        Console.WriteLine($"Reimported {Path.GetFileNameWithoutExtension(sourcePath)}: {metadata.Outputs.Count} assets. {projectRoot}");
    }

    private static AssetId<WorldAsset> GetSceneId(ModelRoot model, SourceMeta metadata)
    {
        string key = model.DefaultScene.Extras?["someengine_id"]?.GetValue<string>() is { Length: > 0 } id
            ? $"scene:id:{id}" : $"scene:index:{model.DefaultScene.LogicalIndex}";
        return new(metadata.ElementIds[key]);
    }

    private static (AssetProject Project, ImportedSceneImporter SceneImporter) CreateProject(string projectRoot, string engineRoot)
    {
        var sceneImporter = new ImportedSceneImporter();
        var services = new ServiceCollection();
        SomeEngine.Assets.Importers.EngineAssetCapabilities.AddAssetCapabilities(services);
        ImportedSceneCapabilities.AddAssetCapabilities(services);
        services.AddSingleton(sceneImporter);
        services.AddSingleton<IClusterVertexEncoder, SceneVertexEncoder>();
        var importer = new GltfSourceImporter(services, "imported-scenes-4;" + ImportedSceneCapabilities.BuildVersion,
            ImportedSceneImporter.ImporterId);
        var project = new AssetProject(projectRoot, [importer],
            shaderIncludeDirectories: [Path.Combine(engineRoot, "assets/Shaders")],
            shaderTargets: [SlangShaderSharp.SlangCompileTarget.Dxil]);
        return (project, sceneImporter);
    }

    private static async Task<(ModelRoot Model, SourceMeta Metadata, double Seconds)> ImportSceneAsync(
        AssetProject project, ImportedSceneImporter sceneImporter, string sourcePath, string engineRoot)
    {
        ModelRoot model = ModelRoot.Load(sourcePath);
        foreach (var material in model.LogicalMaterials)
            foreach (var channel in material.Channels)
                if (channel.Texture is not null && channel.TextureCoordinate != 0)
                    throw new NotSupportedException($"Material {material.Name} uses UV{channel.TextureCoordinate}; this scene shader consumes UV0.");
        await sceneImporter.BuildAsync(project, model, engineRoot);
        var timer = Stopwatch.StartNew();
        await project.ImportAsync(sourcePath);
        return (model, SourceMeta.Load(sourcePath), timer.Elapsed.TotalSeconds);
    }

    private static void CopyFiles(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            CopyIfChanged(file, target);
        }
    }

    private static void CopyIfChanged(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            using var original = File.OpenRead(source);
            using var existing = File.OpenRead(destination);
            if (original.Length == existing.Length && SHA256.HashData(original).AsSpan().SequenceEqual(SHA256.HashData(existing)))
                return;
        }
        File.Copy(source, destination, overwrite: true);
    }
}
