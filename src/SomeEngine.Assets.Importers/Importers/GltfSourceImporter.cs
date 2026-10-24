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
    public const uint ImporterVersion = 16;
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
        ModelRoot source = GltfCompressionInput.Load(sourcePath);
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
        SourceImporterSettings[] selections = SelectImporters(overrides?.Value.Importers, metadata.Importers, available, sets);
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
        context.ConfigureSettings(SourceImporterId, settings);
        var provider = _importers.BuildServiceProvider();
        context.Hold(provider);
        AsyncServiceScope scope = provider.CreateAsyncScope();
        context.Hold(scope);

        context.ConfigureAssets<GltfMesh, MeshAsset>(new GltfMeshSource(settings, context.SourcePath));
        context.ConfigureAssets<Scene, WorldAsset>(new GltfSceneSource(settings, context.SourcePath));
        context.ConfigureAssets<GltfMaterial, MaterialAsset>(new GltfMaterialSource(settings, context.SourcePath));
        context.ConfigureAssets<GltfTexture, TextureAsset>(new GltfTextureSource(settings, context.SourcePath));
        context.ConfigureAssets<Skin, SkeletonAsset>(new GltfSkeletonSource(settings, context.SourcePath));
        context.ConfigureAssets<Animation, AnimationClipAsset>(new GltfAnimationClipSource(settings, context.SourcePath));

        var capabilities = scope.ServiceProvider.GetServices<AssetImporterCapability>().ToDictionary(static value => value.Id);
        SourceImporterSettings[] selections = SelectImporters(overrides?.Value.Importers, context.Selections,
            capabilities.Values.ToArray(), scope.ServiceProvider.GetServices<AssetImporterSetCapability>().ToArray());
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
        EnableSelected<GltfMaterial, MaterialAsset>(scope.ServiceProvider, selections, context);
        bool hasMaterialProducer = selections.Any(selection => selection.Enabled
            && capabilities.TryGetValue(selection.ImporterId, out AssetImporterCapability? capability)
            && capability.SourceType == typeof(GltfMaterial)
            && capability.ProducedBlocks.Any(block => block.Id == MaterialBlock.BlockTypeId));
        if (!hasMaterialProducer)
            context.EnableImporter<GltfMaterial, MaterialAsset>(new SceneMaterialImporter(sceneCapability.Id, assembly));
        EnableSelected<GltfTexture, TextureAsset>(scope.ServiceProvider, selections, context);
        EnableSelected<Skin, SkeletonAsset>(scope.ServiceProvider, selections, context);
        EnableSelected<Animation, AnimationClipAsset>(scope.ServiceProvider, selections, context);

        if (context.HasImporters<Skin, SkeletonAsset>())
            foreach (Skin skin in source.LogicalSkins)
                await context.GetAssetAsync<Skin, SkeletonAsset>(skin, cancellationToken).ConfigureAwait(false);
        if (context.HasImporters<Animation, AnimationClipAsset>())
            foreach (Animation animation in source.LogicalAnimations)
                await context.GetAssetAsync<Animation, AnimationClipAsset>(animation, cancellationToken).ConfigureAwait(false);
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
        IReadOnlyList<SourceImporterSettings> saved, IReadOnlyList<AssetImporterCapability> available,
        IReadOnlyList<AssetImporterSetCapability> sets)
    {
        var selected = (overrides ?? saved).Where(selection => selection.ImporterId != SourceImporterId).ToList();
        if (overrides is not null)
        {
            var neutralMembers = sets.Where(set => set.Neutral).SelectMany(set => set.Importers).ToHashSet();
            foreach (SourceImporterSettings selection in saved.Where(value => value.SetMemberId is { } id && neutralMembers.Contains(id)))
            {
                AssetImporterCapability member = available.Single(value => value.Id == selection.SetMemberId);
                if (!selected.Any(value => value.ImporterId == selection.ImporterId
                    || available.Any(candidate => candidate.Id == value.ImporterId && ReplacesMember(candidate, member))))
                    selected.Add(selection);
            }
            if (!selected.Any(selection => available.Any(capability =>
                capability.Id == selection.ImporterId && capability.SourceType == typeof(Scene))))
                selected.AddRange(saved.Where(selection => available.Any(capability =>
                    capability.Id == selection.ImporterId && capability.SourceType == typeof(Scene))));
        }
        return selected.ToArray();
    }

    private static bool ReplacesMember(AssetImporterCapability candidate, AssetImporterCapability member)
        => candidate.SourceType == member.SourceType
            && candidate.ProducedBlocks.Any(block => member.ProducedBlocks.Any(produced => produced.Id == block.Id));

    private static void EnsureDefaultSelections(SourceMeta metadata, IReadOnlyList<AssetImporterCapability> available,
        IReadOnlyList<AssetImporterSetCapability> sets, Guid? sceneImporterId)
    {
        Type[] sourceTypes = [typeof(GltfMesh), typeof(GltfMaterial), typeof(GltfTexture), typeof(Scene), typeof(Skin), typeof(Animation)];
        metadata.ImporterSets ??= metadata.UseDefaultImporters
            ? sets.Where(set => set.Default && !set.Neutral).Select(set => set.Id).ToList() : [];
        foreach (Guid id in metadata.ImporterSets)
            if (!sets.Any(set => set.Id == id))
                throw new InvalidOperationException($"Selected glTF importer set {id} is unavailable in this project.");
        AssetImporterSetCapability[] enabledSets = sets.Where(set => set.Neutral || metadata.ImporterSets.Contains(set.Id)).ToArray();
        var members = new HashSet<Guid>();
        foreach (AssetImporterSetCapability set in enabledSets)
        foreach (Guid id in set.Importers)
        {
            AssetImporterCapability member = available.SingleOrDefault(value => value.Id == id)
                ?? throw new InvalidOperationException($"Importer set {set.Id} member {id} is unavailable in this project.");
            if (sourceTypes.Contains(member.SourceType)) members.Add(id);
        }
        metadata.Importers.RemoveAll(selection => selection.SetMemberId is { } id && !members.Contains(id));
        foreach (Guid id in members.Order())
        {
            AssetImporterCapability member = available.Single(value => value.Id == id);
            int index = metadata.Importers.FindIndex(selection => selection.SetMemberId == id
                || selection.ImporterId == id
                || selection.SetMemberId is null && available.Any(candidate => candidate.Id == selection.ImporterId
                    && ReplacesMember(candidate, member)));
            if (index < 0) metadata.Importers.Add(new(id, member.GetDefaultSettings(metadata), SetMemberId: id));
            else metadata.Importers[index] = metadata.Importers[index] with { SetMemberId = id };
        }
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
        if (!hasScene) metadata.Importers.Add(new(selectedSceneImporter, sceneImporters.Single(value => value.Id == selectedSceneImporter).GetDefaultSettings(metadata)));
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
