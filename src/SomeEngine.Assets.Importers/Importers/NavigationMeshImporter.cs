using System.Text.Json.Serialization;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Job;
using SomeEngine.Navigation;

namespace SomeEngine.Assets.Importers;

public readonly record struct NavigationMeshImportVariant(
    NavigationMeshSelection Selection, NavigationProfile Profile, NavigationPolicy Policy);

/// <summary>Acquired geometry and immutable profile/policy selections for one navigation asset.</summary>
public sealed class NavigationMeshImportSource(
    NavigationBuildInput input, ReadOnlyMemory<NavigationMeshImportVariant> variants, Scheduler? scheduler = null)
{
    public NavigationMeshImportSource(NavigationBuildInput input, NavigationMeshImportVariant[] variants, Scheduler? scheduler = null)
        : this(input, (ReadOnlyMemory<NavigationMeshImportVariant>)variants, scheduler) { }

    public NavigationBuildInput Input { get; } = input;
    public ReadOnlyMemory<NavigationMeshImportVariant> Variants { get; } = variants;
    public Scheduler? Scheduler { get; } = scheduler;
}

[JsonSerializable(typeof(NavigationMeshSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
public sealed partial class NavigationMeshSettingsJson : JsonSerializerContext;

public sealed class NavigationMeshSettingsProvider : IAssetImporterSettings<NavigationMeshSettings>
{
    public NavigationMeshSettings DefaultSettings => new();
    public System.Text.Json.Serialization.Metadata.JsonTypeInfo<NavigationMeshSettings> TypeInfo => NavigationMeshSettingsJson.Default.NavigationMeshSettings;
}

/// <summary>Produces the same profile-specific meshes as game-side builds through the import capability selection.</summary>
[AssetImporter("a1906f62-273a-4ad5-b54a-879028b22d4c", typeof(NavigationMeshBlock),
    SettingsProvider = typeof(NavigationMeshSettingsProvider),
    VersionInputs = new[] { typeof(NavigationMeshBuilder), typeof(DotRecast.Recast.RcCompacts) })]
public sealed class NavigationMeshImporter : IAssetImporter<NavigationMeshImportSource, NavigationMeshSettings>
{
    public ValueTask ImportAsync(NavigationMeshImportSource source, NavigationMeshSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        float padding = source.Input.Settings.BorderSize;
        foreach (NavigationMeshImportVariant variant in source.Variants.Span)
            padding = MathF.Max(padding, NavigationSurfaceTraversal.GetRotationRadius(variant.Profile.Footprint.Span) + 2 * source.Input.Settings.CellSize);
        var input = new NavigationBuildInput(source.Input.Sources, source.Input.Region,
            source.Input.Settings with { BorderSize = padding });
        NavigationSurface surface = NavigationSurfaceBuilder.Schedule(input, scheduler: source.Scheduler).Complete().Surface;
        var builds = new NavigationBuild<NavigationMeshBuildState>[source.Variants.Length];
        for (int index = 0; index < builds.Length; index++)
        {
            NavigationMeshImportVariant variant = source.Variants.Span[index];
            builds[index] = NavigationMeshBuilder.Schedule(surface, variant.Profile, variant.Policy, settings, scheduler: source.Scheduler);
        }
        var block = new NavigationMeshBlock();
        try
        {
            for (int index = 0; index < builds.Length; index++)
                block.SetMesh(source.Variants.Span[index].Selection, builds[index].Complete().Mesh);
            cancellationToken.ThrowIfCancellationRequested();
            output.SetBlock(block);
            return ValueTask.CompletedTask;
        }
        catch { block.Dispose(); throw; }
    }
}
