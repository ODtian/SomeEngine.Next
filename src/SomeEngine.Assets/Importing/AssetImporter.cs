using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SomeEngine.Assets.Importing;

/// <summary>The extension's statically generated settings codec and default values.</summary>
public interface IAssetImporterSettings<TSettings> where TSettings : notnull
{
    TSettings DefaultSettings { get; }
    JsonTypeInfo<TSettings> TypeInfo { get; }
}

/// <summary>An importer whose settings have already been bound and can be called directly.</summary>
public interface IAssetImporter<TSource> where TSource : class
{
    Guid Id { get; }
    IReadOnlyList<Guid> ProducedBlocks { get; }
    ValueTask ImportAsync(TSource source, ImportContext context, ImportOutput output, CancellationToken cancellationToken = default);
}

public sealed class AssetImporter<TSource, TSettings> : IAssetImporter<TSource>
    where TSource : class where TSettings : notnull
{
    private readonly IAssetImporter<TSource, TSettings> _importer;
    private readonly TSettings _settings;
    public Guid Id { get; }
    public IReadOnlyList<Guid> ProducedBlocks { get; }

    public AssetImporter(Guid id, IReadOnlyList<Guid> producedBlocks,
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

public static class AssetImporter
{
    public static IAssetImporter<TSource> Bind<TSource, TSettings>(Guid id, IReadOnlyList<Guid> producedBlocks,
        IAssetImporter<TSource, TSettings> importer, IAssetImporterSettings<TSettings> settings, JsonElement? value = null)
        where TSource : class where TSettings : notnull
    {
        TSettings selected = value is null || value.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? settings.DefaultSettings
            : value.Value.Deserialize(settings.TypeInfo) ?? throw new InvalidDataException($"Importer {id} has null settings.");
        return new AssetImporter<TSource, TSettings>(id, producedBlocks, importer, selected);
    }
}
