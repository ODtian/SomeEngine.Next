namespace SomeEngine.Assets;

/// <summary>
/// Runtime-visible identities written into cooked assets. Importers consume these constants, but
/// runtime readers do not reference the importer assembly merely to validate cooked content.
/// </summary>
public static class AssetFormatVersions
{
    public const string SlangShaderImporterName = "SlangShaderImporter";
    public const uint SlangShaderImporterVersion = 25;
    public const uint ShaderAssetSchemaVersion = 7;
}
