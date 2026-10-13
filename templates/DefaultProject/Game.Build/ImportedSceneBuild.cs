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

namespace Game.Build;

public static class ImportedSceneBuild
{
    public static async Task BuildAsync(string applicationDirectory, string sourcePath, string projectRoot, string engineRoot)
    {
        applicationDirectory = Path.GetFullPath(applicationDirectory);
        sourcePath = Path.GetFullPath(sourcePath);
        projectRoot = Path.GetFullPath(projectRoot);
        engineRoot = Path.GetFullPath(engineRoot);
        SceneMaterialAsset.Register();
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
        GltfImporterSettings sourceSettings = sourceMetadata.GetSettings(GltfSourceImporter.SourceImporterId,
            new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings)
            with { LitMaterialTemplate = "assets/Materials/Standard.material.asset" };
        var sceneImporter = new ImportedSceneImporter(sourceSettings);
        var importer = new GltfSourceImporter(services =>
        {
            SomeEngine.Assets.Importers.EngineAssetCapabilities.AddAssetCapabilities(services);
            ImportedSceneCapabilities.AddAssetCapabilities(services);
            services.AddSingleton(sceneImporter);
            services.AddSingleton<IClusterVertexEncoder, SceneVertexEncoder>();
        }, "imported-scenes-3;" + ImportedSceneCapabilities.BuildVersion, ImportedSceneImporter.ImporterId);
        var project = new AssetProject(projectRoot, [importer],
            sourceFormats: [new ShaderSourceFormat([Path.Combine(engineRoot, "assets/Shaders")])]);
        AssetManifest applicationAssets = AssetManifest.Load(Path.Combine(applicationDirectory, "Library", "AssetManifest"));
        foreach (AssetEntry entry in applicationAssets.Assets.Values)
        {
            if (!entry.Path.StartsWith("assets/Shaders/", StringComparison.OrdinalIgnoreCase)
                && !entry.Path.StartsWith("assets/Pipelines/", StringComparison.OrdinalIgnoreCase)) continue;
            string destination = Path.Combine(projectRoot, entry.Path);
            CopyIfChanged(Path.Combine(applicationDirectory, entry.Path), destination);
            project.RegisterAsset(entry);
        }
        ModelRoot model = ModelRoot.Load(copiedSource);
        foreach (var material in model.LogicalMaterials)
            foreach (var channel in material.Channels)
                if (channel.Texture is not null && channel.TextureCoordinate != 0)
                    throw new NotSupportedException($"Material {material.Name} uses UV{channel.TextureCoordinate}; this scene shader consumes UV0.");
        await sceneImporter.BuildAsync(project, model, engineRoot);

        var timer = Stopwatch.StartNew();
        await project.ImportAsync(copiedSource);
        double importSeconds = timer.Elapsed.TotalSeconds;
        SourceMeta metadata = SourceMeta.Load(copiedSource);
        var previous = metadata.Outputs.ToDictionary(value => value.AssetGuid, value => (value.Version, value.ContentHash));
        timer.Restart();
        await project.ImportAsync(copiedSource);
        double cachedSeconds = timer.Elapsed.TotalSeconds;
        SourceMeta repeated = SourceMeta.Load(copiedSource);
        if (repeated.Outputs.Count != previous.Count || repeated.Outputs.Any(value =>
                !previous.TryGetValue(value.AssetGuid, out var old) || old != (value.Version, value.ContentHash)))
            throw new InvalidDataException("Unchanged reimport changed generated assets.");

        string sceneKey = model.DefaultScene.Extras?["someengine_id"]?.GetValue<string>() is { Length: > 0 } id
            ? $"scene:id:{id}" : $"scene:index:{model.DefaultScene.LogicalIndex}";
        Guid sceneId = metadata.ElementIds[sceneKey];
        string settingsPath = Path.Combine(projectRoot, "assets", "Runtime", "default.runtime.asset");
        using (ApplicationSettingsAsset settings = await ApplicationSettingsAsset.OpenAsync(settingsPath))
        {
            settings.GetBlock<ApplicationSettingsBlock>().Scene = new(sceneId);
            project.RegisterAsset(await settings.SaveAsync(settingsPath));
        }
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
