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
    public static async Task RefreshShadersAsync(string applicationDirectory, string projectRoot, string engineRoot)
    {
        applicationDirectory = Path.GetFullPath(applicationDirectory);
        projectRoot = Path.GetFullPath(projectRoot);
        engineRoot = Path.GetFullPath(engineRoot);
        if (!File.Exists(Path.Combine(projectRoot, "Library", "AssetManifest", AssetManifest.CatalogFileName))
            || !File.Exists(Path.Combine(applicationDirectory, "Library", "AssetManifest", AssetManifest.CatalogFileName)))
            throw new DirectoryNotFoundException("Shader refresh requires existing scene and application projects.");
        var project = new AssetProject(projectRoot, [],
            shaderIncludeDirectories: [Path.Combine(engineRoot, "assets/Shaders")]);
        await CopySharedShadersAsync(project, applicationDirectory);
        string directory = Path.Combine(project.RootPath, "assets/Shaders");
        ImportedSceneImporter.WriteSurfaceShader(directory, engineRoot);
        ImportedSceneImporter.WriteSurfaceSources(directory, engineRoot);
        // Preserve the incoming refresh scope: existing scene definitions only, with no glTF reimport.
        foreach (string source in Directory.EnumerateFiles(directory, "scene_*.shader"))
            await project.BuildAsync(source);
        await PublishScenePackAsync(project);
        Console.WriteLine($"Refreshed scene shaders: {project.RootPath}");
    }

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
        var sceneImporter = new ImportedSceneImporter();
        var services = new ServiceCollection();
        SomeEngine.Assets.Importers.EngineAssetCapabilities.AddAssetCapabilities(services);
        ImportedSceneCapabilities.AddAssetCapabilities(services);
        services.AddSingleton(sceneImporter);
        services.AddSingleton<IClusterVertexEncoder, SceneVertexEncoder>();
        var importer = new GltfSourceImporter(services, "imported-scenes-4",
            ImportedSceneImporter.ImporterId);
        var project = new AssetProject(projectRoot, [importer],
            shaderIncludeDirectories: [Path.Combine(engineRoot, "assets/Shaders")]);
        await CopySharedShadersAsync(project, applicationDirectory);
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
        await PublishScenePackAsync(project);
        int instances = Node.Flatten(model.DefaultScene).Count(static node => node.Mesh is not null);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            source = sourcePath, meshes = metadata.Outputs.Count(value => value.SubAssetKey.StartsWith("mesh:")),
            instances, materials = metadata.Outputs.Count(value => value.SubAssetKey.StartsWith("material:")),
            textures = metadata.Outputs.Count(value => value.SubAssetKey.StartsWith("texture:")),
            assets = metadata.Outputs.Count, importSeconds, cachedSeconds, unchangedReimport = true,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Imported {name}: {instances} mesh instances, {metadata.Outputs.Count} assets in {importSeconds:F2}s. {projectRoot}");
    }

    private static async ValueTask PublishScenePackAsync(AssetProject project)
    {
        // Include shared shaders, pipelines and runtime settings as well as imported glTF outputs.
        const string scenePack = "Library/scene.assetpack";
        await project.BuildPackAsync(scenePack, project.Manifest.Assets.Keys);
        AssetContent.WritePackPaths(project.RootPath, scenePack);
    }

    private static async ValueTask CopySharedShadersAsync(AssetProject project, string applicationDirectory)
    {
        AssetManifest applicationAssets = AssetManifest.Load(Path.Combine(applicationDirectory, "Library", "AssetManifest"));
        foreach (AssetEntry entry in applicationAssets.Assets.Values)
        {
            if (!entry.Path.StartsWith("assets/Shaders/", StringComparison.OrdinalIgnoreCase)
                && !entry.Path.StartsWith("assets/Pipelines/", StringComparison.OrdinalIgnoreCase)) continue;
            if (project.Manifest.TryAssetPath(entry.Path, out AssetEntry existing) && existing.AssetGuid != entry.AssetGuid)
                throw new InvalidDataException("The shared asset destination belongs to a different identity.");
            if (entry.DefinitionPath.Length != 0 && project.Resolve(entry.DefinitionPath) is Guid defined && defined != entry.AssetGuid)
                throw new InvalidDataException("The shared definition destination belongs to a different identity.");
            // Extract selected bytes rather than overwriting an identically named scene pack.
            byte[] content = await AssetCommands.ReadAssetBytesAsync(applicationDirectory, entry);
            string destination = ProjectPath(project.RootPath, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination) || !File.ReadAllBytes(destination).AsSpan().SequenceEqual(content))
                await File.WriteAllBytesAsync(destination, content);
            if (entry.DefinitionPath.Length != 0)
            {
                string definition = ProjectPath(applicationDirectory, entry.DefinitionPath);
                if (File.Exists(definition)) CopyIfChanged(definition, ProjectPath(project.RootPath, entry.DefinitionPath));
            }
            project.RegisterAsset(entry with { PackPath = "" });
        }
    }

    private static string ProjectPath(string root, string path)
    {
        string fullPath = Path.GetFullPath(path, root);
        string relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative == "."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Shared content paths must remain within their project root.");
        return fullPath;
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
