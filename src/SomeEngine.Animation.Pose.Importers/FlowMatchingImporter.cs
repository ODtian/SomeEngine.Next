using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SomeEngine.Assets;
using SomeEngine.Assets.Importing;

namespace SomeEngine.Animation.Pose.Importers;

public sealed record FlowMatchingSettings(Guid SkeletonAssetId = default);

[JsonSerializable(typeof(FlowMatchingSettings))]
public sealed partial class FlowMatchingSettingsJson : JsonSerializerContext;

public sealed class FlowMatchingSettingsProvider : IAssetImporterSettings<FlowMatchingSettings>
{
    public FlowMatchingSettings DefaultSettings => new();
    public JsonTypeInfo<FlowMatchingSettings> TypeInfo => FlowMatchingSettingsJson.Default.FlowMatchingSettings;
}

/// <summary>Preserves a trained export and its selected skeleton reference in the model asset.</summary>
[AssetImporter("8ef55c78-4b44-4f2d-a1ec-c47d53a7ce3d", typeof(FlowMatchingBlock),
    SettingsProvider = typeof(FlowMatchingSettingsProvider), VersionInputs = new[] { typeof(FlowMatchingModel) })]
public sealed class FlowMatchingImporter : IAssetImporter<FlowMatchingModel, FlowMatchingSettings>
{
    public static readonly Guid Id = new("8ef55c78-4b44-4f2d-a1ec-c47d53a7ce3d");

    public ValueTask ImportAsync(FlowMatchingModel source, FlowMatchingSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new FlowMatchingBlock(new(settings.SkeletonAssetId), source));
        return ValueTask.CompletedTask;
    }
}
