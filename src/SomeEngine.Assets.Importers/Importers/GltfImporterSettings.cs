using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;

namespace SomeEngine.Assets.Importers;

public sealed record GltfImporterSettings
{
    public string LitMaterialTemplate { get; init; } = "assets/Materials/Standard.material.asset";
    public string UnlitMaterialTemplate { get; init; } = "assets/Materials/TestUnlit_1.material.asset";
    public bool GenerateTangents { get; init; }
    public bool RequirePersistentElementIds { get; init; } = true;
    public string PersistentIdProperty { get; init; } = "someengine_id";
    /// <summary>Project code or build input version. Bump when project conversion behavior changes.</summary>
    public string BuildVersion { get; init; } = "default-project-gltf-6";
}

public sealed record GltfImportOverrides(GltfImporterSettings? Settings = null, IReadOnlyList<SourceImporterSettings>? Importers = null);
public sealed record GltfTextureSettings;

public sealed class GltfTextureSettingsProvider : IAssetImporterSettings<GltfTextureSettings>
{
    public GltfTextureSettings DefaultSettings => new();
    public JsonTypeInfo<GltfTextureSettings> TypeInfo => GltfSettingsJson.Default.GltfTextureSettings;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GltfImporterSettings))]
[JsonSerializable(typeof(GltfImportOverrides))]
[JsonSerializable(typeof(GltfTextureSettings))]
public sealed partial class GltfSettingsJson : JsonSerializerContext;

