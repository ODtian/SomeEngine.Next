using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

internal sealed class AssetFormat<T>(
    string assetType,
    string pathSuffix,
    BinaryWireTypeDescriptor wireType,
    Func<IRangeSource, AssetEntry, BinaryReadLimits, CancellationToken, ValueTask<AssetFile>> read)
    where T : class
{
    private static AssetFormat<T>? _current;
    internal static AssetFormat<T> Current => Volatile.Read(ref _current)
        ?? throw new InvalidOperationException($"No generated asset format exists for '{typeof(T).FullName}'.");

    internal string AssetType { get; } = assetType;
    internal string PathSuffix { get; } = pathSuffix;
    internal BinaryWireTypeDescriptor WireType { get; } = wireType;

    internal static void Register(AssetFormat<T> format)
    {
        if (Interlocked.CompareExchange(ref _current, format, null) is not null)
            throw new InvalidOperationException($"More than one file format was generated for '{typeof(T).FullName}'.");
    }

    internal bool MatchesPath(string path) => path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase);

    internal void Validate(AssetEntry entry)
    {
        // A storage that cannot supply a version is read without a completed-file cache.
        // The opened document still supplies its verified content generation.
        if (!StringComparer.Ordinal.Equals(entry.AssetType, AssetType))
            throw new InvalidDataException($"Asset {entry.AssetGuid} contains '{entry.AssetType}', not '{AssetType}'.");
        if (entry.SchemaFingerprint != WireType.SchemaFingerprint)
            throw new BinarySchemaMismatchException(typeof(T), entry.SchemaFingerprint,
                WireType.SchemaFingerprint, WireType.SchemaEpoch, WireType.SchemaEpoch, WireType.Compatibility);
    }

    internal ValueTask<AssetFile> ReadAsync(IRangeSource source, AssetEntry entry,
        BinaryReadLimits limits, CancellationToken cancellationToken)
        => read(source, entry, limits, cancellationToken);
}
