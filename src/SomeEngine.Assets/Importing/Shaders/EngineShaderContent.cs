using System.Text.Json;
using System.Text.Json.Serialization;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

/// <summary>Builds and locates the engine's shared shader assets.</summary>
public static class EngineShaderContent
{
    public const string ContentDirectoriesFileName = "content-directories.json";

    public static async Task BuildAsync(string engineRoot, IReadOnlyList<SlangCompileTarget> targets,
        CancellationToken cancellationToken = default)
    {
        engineRoot = Path.GetFullPath(engineRoot);
        string shaders = Path.Combine(engineRoot, "assets", "Shaders");
        var project = new AssetProject(engineRoot, [], shaderIncludeDirectories: [shaders], shaderTargets: targets);
        await project.BuildAllAsync(Directory.EnumerateFiles(shaders, "*.shader"), 8, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds one application shader after its referenced engine inputs.</summary>
    public static async Task BuildShaderAsync(string projectRoot, string definitionPath, string engineRoot,
        IReadOnlyList<SlangCompileTarget> targets, CancellationToken cancellationToken = default)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        engineRoot = Path.GetFullPath(engineRoot);
        definitionPath = Path.GetFullPath(definitionPath, projectRoot);
        string shaders = Path.Combine(engineRoot, "assets", "Shaders");
        string[] includes = [shaders];
        var engineInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await ReadInputsAsync(definitionPath).ConfigureAwait(false);
        var engine = new AssetProject(engineRoot, [], shaderIncludeDirectories: includes, shaderTargets: targets);
        await engine.BuildAllAsync(engineInputs, 8, cancellationToken).ConfigureAwait(false);
        var project = new AssetProject(projectRoot, [], shaderIncludeDirectories: includes, shaderTargets: targets);
        await project.BuildAllAsync([definitionPath], 8, cancellationToken).ConfigureAwait(false);

        async Task ReadInputsAsync(string path)
        {
            if (!visited.Add(path)) return;
            using ShaderAsset definition = await ShaderAsset.OpenSourceAsync(path, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            foreach (ShaderInputAsset input in ShaderImports.Read(definition.GetBlock<ShaderSourceBlock>(), path, includes))
            {
                string relative = Path.GetRelativePath(shaders, input.DefinitionPath);
                if (!Path.IsPathRooted(relative) && relative != ".."
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    engineInputs.Add(input.DefinitionPath);
                else await ReadInputsAsync(input.DefinitionPath).ConfigureAwait(false);
            }
        }
    }

    public static Guid GetShaderId(string engineRoot, string definitionName)
    {
        string path = Path.Combine(Path.GetFullPath(engineRoot), "assets", "Shaders", Path.GetFileName(definitionName));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("Fields").GetProperty("Id").GetGuid();
    }

    public static void SetContentDirectory(AssetProject project, string engineRoot)
    {
        engineRoot = Path.GetFullPath(engineRoot);
        string shaders = Path.Combine(engineRoot, "assets", "Shaders");
        HashSet<Guid> ids = Directory.EnumerateFiles(shaders, "*.shader")
            .Select(path => GetShaderId(engineRoot, path)).ToHashSet();
        foreach (AssetEntry entry in project.Manifest.Assets.Values.ToArray())
            if (entry.AssetType == ShaderAsset.FileType && ids.Contains(entry.AssetGuid))
                project.Manifest.RemoveAsset(entry.AssetGuid);
        SetContentDirectories(project, [engineRoot]);
    }

    public static void SetContentDirectories(AssetProject project, IReadOnlyList<string> directories)
    {
        string manifestDirectory = Path.Combine(project.RootPath, "Library", "AssetManifest");
        project.Manifest.Save(manifestDirectory);
        File.WriteAllText(Path.Combine(manifestDirectory, ContentDirectoriesFileName),
            JsonSerializer.Serialize(directories.Select(Path.GetFullPath).ToArray(), EngineContentJson.Default.StringArray));
    }

    public static AssetLoader OpenFiles(string contentDirectory, AssetManifest manifest, AssetLoaderOptions? options = null)
    {
        contentDirectory = Path.GetFullPath(contentDirectory);
        var stores = new List<IAssetStorage> { new LooseAssetStorage(contentDirectory, manifest) };
        string references = Path.Combine(contentDirectory, "Library", "AssetManifest", ContentDirectoriesFileName);
        if (File.Exists(references))
            foreach (string directory in JsonSerializer.Deserialize(File.ReadAllText(references), EngineContentJson.Default.StringArray)!)
            {
                string root = Path.GetFullPath(directory, contentDirectory);
                stores.Add(new LooseAssetStorage(root, AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest"))));
            }
        return new AssetLoader(stores, options);
    }
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class EngineContentJson : JsonSerializerContext;
