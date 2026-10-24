using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Containers;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfMesh = SharpGLTF.Schema2.Mesh;
using GltfTexture = SharpGLTF.Schema2.Texture;
using SharpGLTF.Schema2;

namespace SomeEngine.Assets.Importers;

public sealed class GltfSourceImporter : IAssetSourceImporter<ModelRoot, GltfImportOverrides>
{
    public const uint ImporterVersion = 13;
    public static readonly Guid SourceImporterId = new("b0c10195-d8d1-46ab-81b8-f8c54db7d0aa");
    private readonly Action<IServiceCollection> _configureServices;
    private readonly string _capabilitiesVersion;
    private readonly Guid? _sceneImporterId;

    /// <param name="pages">Caller-held storage for imported assets. The caller keeps it alive until import completes.</param>
    public GltfSourceImporter(Action<IServiceCollection> configureServices, string capabilitiesVersion,
        Guid? sceneImporterId = null)
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilitiesVersion);
        _configureServices = configureServices;
        _capabilitiesVersion = capabilitiesVersion;
        _sceneImporterId = sceneImporterId;
    }

    public Guid Id => SourceImporterId;
    public IReadOnlyList<string> SourceExtensions { get; } = [".gltf", ".glb"];

    public ValueTask<ModelRoot> ReadAsync(string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelRoot source = ModelRoot.Load(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(source);
    }

    public ValueTask<ImportInputs?> GetInputsAsync(string projectRoot, string sourcePath, SourceMeta metadata,
        AssetImportSettings<GltfImportOverrides>? overrides, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GltfImporterSettings settings = GetSettings(metadata, overrides?.Value.Settings);
        string encodedOverrides = overrides is null ? "" : JsonSerializer.Serialize(overrides.Value, GltfSettingsJson.Default.GltfImportOverrides);
        var services = new ServiceCollection();
        _configureServices(services);
        AssetImporterCapability[] available = services.Select(static service => service.ImplementationInstance)
            .OfType<AssetImporterCapability>().ToArray();
        EnsureDefaultSelections(metadata, available, _sceneImporterId);
        SourceImporterSettings[] selections = SelectImporters(overrides?.Value.Importers, metadata.Importers, available);
        Guid[] selectedIds = selections.Where(static selection => selection.Enabled).Select(static selection => selection.ImporterId).Distinct().ToArray();

        AssetImporterCapability[] descriptions = available.Where(capability => selectedIds.Contains(capability.Id))
            .OrderBy(static capability => capability.Id).ToArray();
        foreach (Guid selected in selectedIds)
            if (!descriptions.Any(capability => capability.Id == selected))
                throw new InvalidOperationException($"Selected glTF importer {selected} is unavailable in this project.");
        string selectedVersions = string.Join(";", descriptions.Select(capability => $"{capability.Id:N}:{capability.ImplementationVersion}"));
        return ValueTask.FromResult(GltfDeps.ReadInputs(projectRoot, GltfDeps.FullPath(projectRoot, sourcePath), settings,
            settings.BuildVersion + ";" + _capabilitiesVersion + ";" + selectedVersions,
            encodedOverrides));
    }

    public async ValueTask ImportAsync(ModelRoot source, ImportContext context, AssetImportSettings<GltfImportOverrides>? overrides,
        CancellationToken cancellationToken)
    {
        GltfImporterSettings settings = overrides?.Value.Settings
            ?? context.GetSettings(SourceImporterId, new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings);
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        _configureServices(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        context.Hold(provider);
        AsyncServiceScope scope = provider.CreateAsyncScope();
        context.Hold(scope);

        context.ConfigureAssets<GltfMesh, MeshAsset>(
            mesh => ElementKey(settings, "mesh", mesh.LogicalIndex, mesh),
            (mesh, id) => new MeshAsset(id) { Name = mesh.Name ?? $"Mesh_{mesh.LogicalIndex}" },
            (_, id) => OutputPath(context.SourcePath, "mesh", id),
            static (asset, path, token) => asset.SaveAsync(path, token));
        context.ConfigureAssets<Scene, WorldAsset>(
            scene => ElementKey(settings, "scene", scene.LogicalIndex, scene),
            (scene, id) => new WorldAsset(id) { Name = scene.Name ?? $"Scene_{scene.LogicalIndex}" },
            (_, id) => OutputPath(context.SourcePath, "scene", id),
            static (asset, path, token) => asset.SaveAsync(path, token));
        context.ConfigureAssets<GltfMaterial, MaterialAsset>(
            material => ElementKey(settings, "material", material.LogicalIndex, material),
            (material, id) => new MaterialAsset(id) { Name = material.Name ?? $"Material_{material.LogicalIndex}" },
            (_, id) => OutputPath(context.SourcePath, "material", id),
            static (asset, path, token) => asset.SaveAsync(path, token));
        context.ConfigureAssets<GltfTexture, TextureAsset>(
            texture => ElementKey(settings, "texture", texture.LogicalIndex, texture),
            (texture, id) => new TextureAsset(id) { Name = texture.Name ?? texture.PrimaryImage?.Name ?? $"Texture_{texture.LogicalIndex}" },
            (_, id) => OutputPath(context.SourcePath, "texture", id),
            static (asset, path, token) => asset.SaveAsync(path, token));

        var capabilities = scope.ServiceProvider.GetServices<AssetImporterCapability>().ToDictionary(static value => value.Id);
        SourceImporterSettings[] selections = SelectImporters(overrides?.Value.Importers, context.Selections,
            capabilities.Values.ToArray());
        EnableSelected<GltfMesh, MeshAsset>(scope.ServiceProvider, selections, context);
        SourceImporterSettings[] scenes = selections.Where(selection => selection.Enabled
            && capabilities.TryGetValue(selection.ImporterId, out AssetImporterCapability? capability)
            && capability.SourceType == typeof(Scene)).ToArray();
        if (scenes.Length != 1)
            throw new InvalidOperationException("Select one glTF scene importer to assemble the source scenes and their assets.");
        AssetImporterCapability sceneCapability = capabilities[scenes[0].ImporterId];
        if (scope.ServiceProvider.GetRequiredService(sceneCapability.ImporterType) is not GltfSceneImporter assembly)
            throw new InvalidOperationException("The selected glTF scene importer must derive from GltfSceneImporter.");
        context.EnableImporter<Scene, WorldAsset>(sceneCapability.Bind<Scene>(scope.ServiceProvider, scenes[0].Settings));
        context.EnableImporter<GltfMesh, MeshAsset>(sceneCapability.Id,
            [MeshMaterialsBlock.BlockTypeId], assembly.ImportMeshMaterialsAsync);
        context.EnableImporter<GltfMaterial, MaterialAsset>(sceneCapability.Id,
            [MaterialBlock.BlockTypeId], assembly.ImportMaterialAsync);
        EnableSelected<GltfTexture, TextureAsset>(scope.ServiceProvider, selections, context);

        foreach (Scene scene in source.LogicalScenes)
            await context.GetAssetAsync<Scene, WorldAsset>(scene, cancellationToken).ConfigureAwait(false);
    }

    private static void EnableSelected<TSource, TAsset>(IServiceProvider services,
        IReadOnlyList<SourceImporterSettings> selections, ImportContext context)
        where TSource : class where TAsset : SomeEngine.Assets.Asset
    {
        var capabilities = services.GetServices<AssetImporterCapability>().ToDictionary(static value => value.Id);
        foreach (SourceImporterSettings selection in selections)
        {
            if (!selection.Enabled || !capabilities.TryGetValue(selection.ImporterId, out AssetImporterCapability? capability)
                || capability.SourceType != typeof(TSource)) continue;
            context.EnableImporter<TSource, TAsset>(capability.Bind<TSource>(services, selection.Settings));
        }
    }

    private static GltfImporterSettings GetSettings(SourceMeta metadata, GltfImporterSettings? overrides)
        => overrides ?? metadata.GetSettings(SourceImporterId, new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings);

    private static SourceImporterSettings[] SelectImporters(IReadOnlyList<SourceImporterSettings>? overrides,
        IReadOnlyList<SourceImporterSettings> saved, IReadOnlyList<AssetImporterCapability> available)
    {
        SourceImporterSettings[] selected = (overrides ?? saved).Where(selection => selection.ImporterId != SourceImporterId).ToArray();
        if (overrides is null || selected.Any(selection => available.Any(capability =>
                capability.Id == selection.ImporterId && capability.SourceType == typeof(Scene)))) return selected;
        return [.. selected, .. saved.Where(selection => available.Any(capability =>
            capability.Id == selection.ImporterId && capability.SourceType == typeof(Scene)))];
    }

    private static Guid ImporterId<TImporter>(IReadOnlyList<AssetImporterCapability> available)
        => available.Single(value => value.ImporterType == typeof(TImporter)).Id;

    private static void EnsureDefaultSelections(SourceMeta metadata, IReadOnlyList<AssetImporterCapability> available,
        Guid? sceneImporterId)
    {
        GltfImporterSettings settings = GetSettings(metadata, null);
        bool hasScene = metadata.Importers.Any(selection =>
            available.Any(capability => capability.Id == selection.ImporterId && capability.SourceType == typeof(Scene)));
        if (!metadata.UseDefaultImporters) return;
        AssetImporterCapability[] sceneImporters = available.Where(static capability => capability.SourceType == typeof(Scene)).ToArray();
        Guid selectedSceneImporter = hasScene ? metadata.Importers.First(selection =>
            sceneImporters.Any(capability => capability.Id == selection.ImporterId)).ImporterId
            : sceneImporterId ?? (sceneImporters.Length == 1
            ? sceneImporters[0].Id
            : throw new InvalidOperationException("Select one project glTF scene importer."));
        if (!sceneImporters.Any(capability => capability.Id == selectedSceneImporter))
            throw new InvalidOperationException($"Selected glTF scene importer {selectedSceneImporter} is unavailable in this project.");
        Guid meshId = ImporterId<GltfMeshImporter>(available);
        Guid clusterId = ImporterId<ClusterGltfMeshImporter>(available);
        Guid textureId = ImporterId<GltfTextureImporter>(available);
        JsonElement geometry = JsonSerializer.SerializeToElement(new GltfMeshSettings(settings.GenerateTangents), GltfMeshSettingsJson.Default.GltfMeshSettings);
        if (!metadata.Importers.Any(selection => selection.ImporterId == meshId)) metadata.Importers.Add(new(meshId, geometry));
        if (!metadata.Importers.Any(selection => selection.ImporterId == clusterId)) metadata.Importers.Add(new(clusterId, geometry));
        if (!metadata.Importers.Any(selection => selection.ImporterId == textureId))
            metadata.Importers.Add(new(textureId,
                JsonSerializer.SerializeToElement(new GltfTextureSettings(), GltfSettingsJson.Default.GltfTextureSettings)));
        if (!hasScene) metadata.Importers.Add(new(selectedSceneImporter,
            JsonSerializer.SerializeToElement(new GltfSceneSettings(), GltfSceneSettingsJson.Default.GltfSceneSettings)));
    }

    private static string ElementKey(GltfImporterSettings settings, string kind, int index, ExtraProperties source)
    {
        string? persistentId = source.Extras?[settings.PersistentIdProperty]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(persistentId)) return $"{kind}:id:{persistentId}";
        if (settings.RequirePersistentElementIds)
            throw new InvalidDataException($"glTF {kind} {index} lacks extras.{settings.PersistentIdProperty}; ordinal identity is disabled for this source.");
        Trace.TraceWarning($"glTF {kind} {index} uses a persisted ordinal identity. Reordering elements cannot preserve identity without extras.{settings.PersistentIdProperty}.");
        return $"{kind}:index:{index}";
    }

    private static string OutputPath(string sourcePath, string kind, Guid id)
        => Path.Combine(Path.GetDirectoryName(sourcePath)!, $"{Path.GetFileNameWithoutExtension(sourcePath)}.{id:N}.{kind}.asset");
}
