namespace SomeEngine.Assets.Importers;

/// <summary>
/// Editor/cooker-only importer composition. Keeping authoring in a separate assembly prevents
/// runtime consumers from acquiring compiler and source-format dependencies transitively.
/// </summary>
public static class AssetAuthoring
{
    public static IReadOnlyList<IAssetImporter> CreateImporters()
        => [new GltfSourceImporter(), new SlangSourceImporter()];

    public static AssetProject CreateProject(
        string projectRoot,
        string? manifestDirectory = null)
        => new(
            projectRoot,
            CreateImporters(),
            manifestDirectory);
}
