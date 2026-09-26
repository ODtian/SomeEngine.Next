using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SomeEngine.Assets.Importing;

/// <summary>The extension's statically generated settings codec and default values.</summary>
public interface IAssetImporterSettings<TSettings> where TSettings : notnull
{
    TSettings DefaultSettings { get; }
    JsonTypeInfo<TSettings> TypeInfo { get; }
}

/// <summary>A selected, typed conversion whose settings have already been decoded.</summary>
public interface IConfiguredAssetImporter<TSource> where TSource : class
{
    Guid Id { get; }
    IReadOnlyList<Guid> ProducedBlocks { get; }
    ValueTask ImportAsync(TSource source, ImportContext context, ImportOutput output, CancellationToken cancellationToken = default);
}

public sealed class ConfiguredAssetImporter<TSource, TSettings> : IConfiguredAssetImporter<TSource>
    where TSource : class where TSettings : notnull
{
    private readonly IAssetImporter<TSource, TSettings> _importer;
    private readonly TSettings _settings;
    public Guid Id { get; }
    public IReadOnlyList<Guid> ProducedBlocks { get; }

    public ConfiguredAssetImporter(Guid id, IReadOnlyList<Guid> producedBlocks,
        IAssetImporter<TSource, TSettings> importer, TSettings settings)
    {
        Id = id;
        ArgumentNullException.ThrowIfNull(producedBlocks);
        if (id == Guid.Empty || producedBlocks.Any(value => value == Guid.Empty)
            || producedBlocks.Distinct().Count() != producedBlocks.Count)
            throw new ArgumentException("Importer identities and produced block types must be nonempty and unique.");
        ProducedBlocks = Array.AsReadOnly(producedBlocks.ToArray());
        _importer = importer;
        _settings = settings;
    }

    public ValueTask ImportAsync(TSource source, ImportContext context, ImportOutput output, CancellationToken cancellationToken = default)
        => _importer.ImportAsync(source, _settings, context, output, cancellationToken);
}

/// <summary>A DI-registered typed settings decoder. The source traversal never interprets business settings.</summary>
public interface IAssetImporterFactory<TSource> where TSource : class
{
    Guid Id { get; }
    IConfiguredAssetImporter<TSource> Configure(JsonElement? settings = null);
}

public sealed class AssetImporterFactory<TSource, TSettings> : IAssetImporterFactory<TSource>
    where TSource : class where TSettings : notnull
{
    private readonly Func<IAssetImporter<TSource, TSettings>> _createImporter;
    private readonly IAssetImporterSettings<TSettings> _settings;
    private readonly IReadOnlyList<Guid> _producedBlocks;
    public Guid Id { get; }
    public AssetImporterFactory(Guid id, IReadOnlyList<Guid> producedBlocks,
        Func<IAssetImporter<TSource, TSettings>> createImporter, IAssetImporterSettings<TSettings> settings)
    { Id = id; _producedBlocks = producedBlocks; _createImporter = createImporter; _settings = settings; }
    public IConfiguredAssetImporter<TSource> Configure(JsonElement? value = null)
    {
        TSettings selected = value is null || value.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? _settings.DefaultSettings
            : value.Value.Deserialize(_settings.TypeInfo) ?? throw new InvalidDataException($"Importer {Id} has null settings.");
        IAssetImporter<TSource, TSettings> importer = _createImporter();
        return new ConfiguredAssetImporter<TSource, TSettings>(Id, _producedBlocks, importer, selected);
    }
}
