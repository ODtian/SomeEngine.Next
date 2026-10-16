using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Game.Assets;
using ImportedScenes;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;

namespace Game.Build;

public static class ImportedSceneBuild
{
    public static async Task RefreshShadersAsync(string applicationDirectory, string projectRoot, string engineRoot)
    {
        var project = new AssetProject(Path.GetFullPath(projectRoot), [],
            sourceFormats: [new ShaderSourceFormat([Path.GetFullPath(Path.Combine(engineRoot, "assets/Shaders"))])]);
        CopySharedShaders(project, Path.GetFullPath(applicationDirectory));
        ImportedSceneImporter.WriteSurfaceShader(Path.Combine(project.RootPath, "assets/Shaders"),
            Path.Combine(engineRoot, "templates/DefaultProject/assets/Shaders"));
        ImportedSceneImporter.WriteSurfaceSources(Path.Combine(project.RootPath, "assets/Shaders"), engineRoot);
        foreach (string source in Directory.EnumerateFiles(Path.Combine(project.RootPath, "assets/Shaders"), "scene_*.shader"))
            await project.BuildAsync(source);
        Console.WriteLine($"Refreshed scene shaders: {project.RootPath}");
    }

    public static async Task BuildAsync(string applicationDirectory, string sourcePath, string projectRoot, string engineRoot)
    {
        applicationDirectory = Path.GetFullPath(applicationDirectory);
        sourcePath = Path.GetFullPath(sourcePath);
        projectRoot = Path.GetFullPath(projectRoot);
        engineRoot = Path.GetFullPath(engineRoot);
        MaterialTypes.Register();
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
            new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings);
        var sceneImporter = new ImportedSceneImporter(sourceSettings);
        var importer = new GltfSourceImporter(services =>
        {
            Game.Importing.ProjectAssetCapabilities.AddAssetCapabilities(services);
            ImportedSceneCapabilities.AddAssetCapabilities(services);
            services.AddSingleton(sceneImporter);
            services.AddSingleton<IClusterVertexEncoder, SceneVertexEncoder>();
        }, "imported-scenes-3;" + ImportedSceneCapabilities.BuildVersion, ImportedSceneImporter.ImporterId);
        var project = new AssetProject(projectRoot, [importer],
            sourceFormats: [new ShaderSourceFormat([Path.Combine(engineRoot, "assets/Shaders")])]);
        CopySharedShaders(project, applicationDirectory);
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
            assets = metadata.Outputs.Count, importSeconds,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Imported {name}: {instances} mesh instances, {metadata.Outputs.Count} assets in {importSeconds:F2}s. {projectRoot}");
    }

    private static void CopySharedShaders(AssetProject project, string applicationDirectory)
    {
        AssetManifest applicationAssets = AssetManifest.Load(Path.Combine(applicationDirectory, "Library", "AssetManifest"));
        foreach (AssetEntry entry in applicationAssets.Assets.Values)
        {
            if (!entry.Path.StartsWith("assets/Shaders/", StringComparison.OrdinalIgnoreCase)
                && !entry.Path.StartsWith("assets/Pipelines/", StringComparison.OrdinalIgnoreCase)) continue;
            CopyIfChanged(Path.Combine(applicationDirectory, entry.Path), Path.Combine(project.RootPath, entry.Path));
            project.RegisterAsset(entry);
        }
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
