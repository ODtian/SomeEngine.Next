using System.ComponentModel;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

/// <summary>
/// Source-generated, closed generic operations for one concrete asset type. There is one static
/// slot per T: no assembly scan, reflection dispatch, interface value, or boxed asset participates.
/// </summary>
public static class AssetType<T>
    where T : class
{
    private static AssetTypeDescriptor<T>? _descriptor;

    internal static AssetTypeDescriptor<T> Descriptor
        => Volatile.Read(ref _descriptor)
            ?? throw new InvalidOperationException(
                $"Asset type '{typeof(T).FullName}' is not marked with [Asset].");

    internal static bool IsRegistered => Volatile.Read(ref _descriptor) is not null;

    /// <summary>The generated, fully qualified domain name stored in asset publications.</summary>
    public static string Name => Descriptor.AssetType;

    /// <summary>The exact path suffix accepted by this asset type.</summary>
    public static string PathSuffix => Descriptor.PathSuffix;

    /// <summary>The current exact-schema fingerprint for this asset type.</summary>
    public static ulong SchemaFingerprint => Descriptor.SchemaFingerprint;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RegisterGenerated(AssetTypeDescriptor<T> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        AssetTypeDescriptor<T>? existing = Interlocked.CompareExchange(
            ref _descriptor,
            descriptor,
            comparand: null);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Asset type '{typeof(T).FullName}' was generated more than once.");
        }
    }
}

/// <summary>Infrastructure payload emitted once for each concrete <c>[Asset]</c> type.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class AssetTypeDescriptor<T>
    where T : class
{
    public AssetTypeDescriptor(
        string assetType,
        string pathSuffix,
        BinaryWireTypeDescriptor wireType,
        Func<T, AssetGuid> getAssetGuid,
        Action<T, AssetGuid> setAssetGuid,
        Func<T, string> getName,
        Func<T, string, IReadOnlyList<AssetGuid>> getDependencies,
        Func<T, BinaryDocumentWriter> createWriter,
        Func<AssetLoadContext, CancellationToken, ValueTask<T>> load)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetType);
        ArgumentNullException.ThrowIfNull(getAssetGuid);
        ArgumentNullException.ThrowIfNull(setAssetGuid);
        ArgumentNullException.ThrowIfNull(getName);
        ArgumentNullException.ThrowIfNull(getDependencies);
        ArgumentNullException.ThrowIfNull(createWriter);
        ArgumentNullException.ThrowIfNull(load);
        AssetType = assetType;
        PathSuffix = pathSuffix ?? string.Empty;
        WireType = wireType;
        GetAssetGuid = getAssetGuid;
        SetAssetGuid = setAssetGuid;
        GetName = getName;
        GetDependencies = getDependencies;
        CreateWriter = createWriter;
        Load = load;
    }

    public string AssetType { get; }
    public string PathSuffix { get; }
    public BinaryWireTypeDescriptor WireType { get; }
    public ulong SchemaFingerprint => WireType.SchemaFingerprint;
    public Func<T, AssetGuid> GetAssetGuid { get; }
    public Action<T, AssetGuid> SetAssetGuid { get; }
    public Func<T, string> GetName { get; }
    public Func<T, string, IReadOnlyList<AssetGuid>> GetDependencies { get; }
    public Func<T, BinaryDocumentWriter> CreateWriter { get; }
    public Func<AssetLoadContext, CancellationToken, ValueTask<T>> Load { get; }

    internal bool MatchesPath(string path)
        => PathSuffix.Length != 0
            && path.EndsWith(PathSuffix, StringComparison.OrdinalIgnoreCase);

    internal bool Accepts(AssetEntry entry)
        => StringComparer.Ordinal.Equals(entry.AssetType, AssetType)
            && entry.SchemaFingerprint == SchemaFingerprint;
}
