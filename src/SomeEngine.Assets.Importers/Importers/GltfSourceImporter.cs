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
    public const uint ImporterVersion = 14;
    public static readonly Guid SourceImporterId = new("b0c10195-d8d1-46ab-81b8-f8c54db7d0aa");
    private readonly IServiceCollection _importers;
    private readonly string _capabilitiesVersion;
    private readonly Guid? _sceneImporterId;

    public GltfSourceImporter(IServiceCollection importers, string capabilitiesVersion,
        Guid? sceneImporterId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilitiesVersion);
        _importers = importers;
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
        AssetImporterCapability[] available = _importers.Select(static service => service.ImplementationInstance)
            .OfType<AssetImporterCapability>().ToArray();
        AssetImporterSetCapability[] sets = _importers.Select(static service => service.ImplementationInstance)
            .OfType<AssetImporterSetCapability>().ToArray();
        EnsureDefaultSelections(metadata, available, sets, _sceneImporterId);
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
        context.SetSettings(SourceImporterId, settings);
        var provider = _importers.BuildServiceProvider();
        context.Hold(provider);
        AsyncServiceScope scope = provider.CreateAsyncScope();
        context.Hold(scope);

        context.ConfigureAssets<GltfMesh, MeshAsset>(new GltfMeshSource(settings, context.SourcePath));
        context.ConfigureAssets<Scene, WorldAsset>(new GltfSceneSource(settings, context.SourcePath));
        context.ConfigureAssets<GltfMaterial, MaterialAsset>(new GltfMaterialSource(settings, context.SourcePath));
        context.ConfigureAssets<GltfTexture, TextureAsset>(new GltfTextureSource(settings, context.SourcePath));

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
        if (sceneCapability.GetImporter(scope.ServiceProvider) is not GltfSceneImporter assembly)
            throw new InvalidOperationException("The selected glTF scene importer must derive from GltfSceneImporter.");
        context.EnableImporter<Scene, WorldAsset>(sceneCapability.Bind<Scene>(scope.ServiceProvider, scenes[0].Settings));
        context.EnableImporter<GltfMesh, MeshAsset>(new SceneMeshImporter(sceneCapability.Id, assembly));
        context.EnableImporter<GltfMaterial, MaterialAsset>(new SceneMaterialImporter(sceneCapability.Id, assembly));
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

    private static void EnsureDefaultSelections(SourceMeta metadata, IReadOnlyList<AssetImporterCapability> available,
        IReadOnlyList<AssetImporterSetCapability> sets, Guid? sceneImporterId)
    {
        bool hasScene = metadata.Importers.Any(selection => available.Any(capability =>
            capability.Id == selection.ImporterId && capability.SourceType == typeof(Scene)));
        if (!hasScene && metadata.UseDefaultImporters && sceneImporterId is { } requestedScene)
        {
            AssetImporterCapability selected = available.Single(capability => capability.Id == requestedScene);
            metadata.Importers.Add(new(selected.Id, selected.GetDefaultSettings()));
        }
        IEnumerable<AssetImporterSetCapability> selectedSets = sets.Where(set => set.Neutral
            || metadata.ImporterSets.Contains(set.Id)
            || metadata.ImporterSets.Count == 0 && metadata.UseDefaultImporters && set.Default);
        foreach (AssetImporterSetCapability set in selectedSets)
            foreach (Guid id in set.Importers)
            {
                if (metadata.Importers.Any(selection => selection.ImporterId == id)) continue;
                AssetImporterCapability importer = available.Single(capability => capability.Id == id);
                if (metadata.Importers.Any(selection => selection.Enabled && available.Any(capability =>
                    capability.Id == selection.ImporterId && capability.ProducedBlocks.Any(block =>
                        importer.ProducedBlocks.Any(produced => produced.Id == block.Id))))) continue;
                metadata.Importers.Add(new(importer.Id, importer.GetDefaultSettings()));
            }
    }

    private sealed class SceneMeshImporter(Guid id, GltfSceneImporter importer) : IAssetImporter<GltfMesh>
    {
        public Guid Id => id;
        public IReadOnlyList<Guid> ProducedBlocks { get; } = [MeshMaterialsBlock.BlockTypeId];
        public ValueTask ImportAsync(GltfMesh source, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
            => importer.ImportMeshMaterialsAsync(source, context, output, cancellationToken);
    }

    private sealed class SceneMaterialImporter(Guid id, GltfSceneImporter importer) : IAssetImporter<GltfMaterial>
    {
        public Guid Id => id;
        public IReadOnlyList<Guid> ProducedBlocks { get; } = [MaterialBlock.BlockTypeId];
        public ValueTask ImportAsync(GltfMaterial source, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
            => importer.ImportMaterialAsync(source, context, output, cancellationToken);
    }
}
