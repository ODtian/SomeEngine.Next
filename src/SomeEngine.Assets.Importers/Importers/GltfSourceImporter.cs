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
    public const uint ImporterVersion = 8;
    public static readonly Guid SourceImporterId = new("b0c10195-d8d1-46ab-81b8-f8c54db7d0aa");
    private readonly Action<IServiceCollection> _configureServices;
    private readonly string _capabilitiesVersion;
    private readonly PageArena _pages;

    /// <param name="pages">Caller-held storage for imported assets. The caller keeps it alive until import completes.</param>
    public GltfSourceImporter(Action<IServiceCollection> configureServices, string capabilitiesVersion, PageArena? pages = null)
    {
        ArgumentNullException.ThrowIfNull(configureServices);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilitiesVersion);
        _configureServices = configureServices;
        _capabilitiesVersion = capabilitiesVersion;
        _pages = pages ?? PageArena.Shared;
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
        EnsureDefaultSelections(metadata, available);
        SourceImporterSettings[] selections = (overrides?.Value.Importers ?? metadata.Importers)
            .Where(selection => selection.ImporterId != SourceImporterId).ToArray();
        Guid[] selectedIds = selections.Where(static selection => selection.Enabled).Select(static selection => selection.ImporterId).Distinct().ToArray();

        AssetImporterCapability[] descriptions = available.Where(capability => selectedIds.Contains(capability.Id))
            .OrderBy(static capability => capability.Id).ToArray();
        foreach (Guid selected in selectedIds)
            if (!descriptions.Any(capability => capability.Id == selected))
                throw new InvalidOperationException($"Selected glTF importer {selected} is unavailable in this project.");
        string selectedVersions = string.Join(";", descriptions.Select(capability => $"{capability.Id:N}:{capability.ImplementationVersion}"));
        bool materialTemplates = descriptions.Any(static capability => capability.ImporterType == typeof(GltfMaterialImporter));
        return ValueTask.FromResult(GltfDeps.ReadInputs(projectRoot, GltfDeps.FullPath(projectRoot, sourcePath), settings,
            settings.BuildVersion + ";" + _capabilitiesVersion + ";" + selectedVersions,
            encodedOverrides, materialTemplates));
    }

    public async ValueTask ImportAsync(ModelRoot source, ImportContext context, AssetImportSettings<GltfImportOverrides>? overrides,
        CancellationToken cancellationToken)
    {
        GltfImporterSettings settings = overrides?.Value.Settings
            ?? context.GetSettings(SourceImporterId, new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings);
        IReadOnlyList<SourceImporterSettings> selections = overrides?.Value.Importers ?? context.Selections;

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        _configureServices(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        context.Hold(provider);
        AsyncServiceScope scope = provider.CreateAsyncScope();
        context.Hold(scope);

        context.ConfigureAssets<GltfMesh, MeshAsset>(
            mesh => ElementKey(settings, "mesh", mesh.LogicalIndex, mesh),
            (mesh, id) => new MeshAsset(_pages, id) { Name = mesh.Name ?? $"Mesh_{mesh.LogicalIndex}" },
            (_, id) => OutputPath(context.SourcePath, "mesh", id),
            static (asset, path, token) => asset.SaveAsync(path, token));
        context.ConfigureAssets<GltfMaterial, MaterialAsset>(
            material => ElementKey(settings, "material", material.LogicalIndex, material),
            (material, id) => new MaterialAsset(_pages, id) { Name = material.Name ?? $"Material_{material.LogicalIndex}" },
            (_, id) => OutputPath(context.SourcePath, "material", id),
            static (asset, path, token) => asset.SaveAsync(path, token));
        context.ConfigureAssets<GltfTexture, TextureAsset>(
            texture => ElementKey(settings, "texture", texture.LogicalIndex, texture),
            (texture, id) => new TextureAsset(_pages, id) { Name = texture.Name ?? texture.PrimaryImage?.Name ?? $"Texture_{texture.LogicalIndex}" },
            (_, id) => OutputPath(context.SourcePath, "texture", id),
            static (asset, path, token) => asset.SaveAsync(path, token));

        EnableSelected<GltfMesh, MeshAsset>(scope.ServiceProvider, selections, context);
        EnableSelected<GltfMaterial, MaterialAsset>(scope.ServiceProvider, selections, context);
        EnableSelected<GltfTexture, TextureAsset>(scope.ServiceProvider, selections, context);

        if (context.HasImporters<GltfMesh, MeshAsset>())
            foreach (GltfMesh mesh in source.LogicalMeshes)
                await context.GetAssetAsync<GltfMesh, MeshAsset>(mesh, cancellationToken).ConfigureAwait(false);
    }

    private static void EnableSelected<TSource, TAsset>(IServiceProvider services,
        IReadOnlyList<SourceImporterSettings> selections, ImportContext context)
        where TSource : class where TAsset : SomeEngine.Assets.Asset
    {
        var capabilities = services.GetServices<AssetImporterCapability>().ToDictionary(static value => value.Id);
        var factories = services.GetServices<IAssetImporterFactory<TSource>>().ToDictionary(static value => value.Id);
        foreach (SourceImporterSettings selection in selections)
        {
            if (!selection.Enabled || !capabilities.TryGetValue(selection.ImporterId, out AssetImporterCapability? capability)
                || capability.SourceType != typeof(TSource)) continue;
            if (!factories.TryGetValue(selection.ImporterId, out IAssetImporterFactory<TSource>? factory))
                throw new InvalidOperationException($"Selected importer {selection.ImporterId} has no typed settings factory.");
            context.EnableImporter<TSource, TAsset>(factory.Configure(selection.Settings));
        }
    }

    private static GltfImporterSettings GetSettings(SourceMeta metadata, GltfImporterSettings? overrides)
        => overrides ?? metadata.GetSettings(SourceImporterId, new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings);

    private static Guid ImporterId<TImporter>(IReadOnlyList<AssetImporterCapability> available)
        => available.Single(value => value.ImporterType == typeof(TImporter)).Id;

    private static void EnsureDefaultSelections(SourceMeta metadata, IReadOnlyList<AssetImporterCapability> available)
    {
        GltfImporterSettings settings = GetSettings(metadata, null);
        if (!metadata.UseDefaultImporters || metadata.Importers.Any(selection => selection.ImporterId != SourceImporterId)) return;
        JsonElement geometry = JsonSerializer.SerializeToElement(new GltfMeshSettings(settings.GenerateTangents), GltfMeshSettingsJson.Default.GltfMeshSettings);
        JsonElement materials = JsonSerializer.SerializeToElement(new GltfMaterialSettings(), GltfSettingsJson.Default.GltfMaterialSettings);
        metadata.Importers.Add(new(ImporterId<GltfMeshImporter>(available), geometry));
        metadata.Importers.Add(new(ImporterId<ClusterGltfMeshImporter>(available), geometry));
        metadata.Importers.Add(new(ImporterId<GltfMeshMaterialsImporter>(available), JsonSerializer.SerializeToElement(new GltfMeshMaterialSettings(), GltfSettingsJson.Default.GltfMeshMaterialSettings)));
        metadata.Importers.Add(new(ImporterId<GltfMaterialImporter>(available), materials));
        metadata.Importers.Add(new(ImporterId<GltfTextureImporter>(available), JsonSerializer.SerializeToElement(new GltfTextureSettings(), GltfSettingsJson.Default.GltfTextureSettings)));
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
