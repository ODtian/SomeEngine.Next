using System.Collections.Generic;

namespace SomeEngine.Assets;

public readonly record struct ImportedAsset
{
    private ImportedAsset(
        string subAssetKey,
        string outputPath,
        AssetEntry info)
    {
        Entry = info with { Path = outputPath, SubAssetKey = subAssetKey };
    }

    public AssetEntry Entry { get; }
    public Guid AssetGuid => Entry.AssetGuid;
    public string Name => Entry.Name;
    public string SubAssetKey => Entry.SubAssetKey;
    public string OutputPath => Entry.Path;
    public string AssetType => Entry.AssetType;
    public ulong SchemaFingerprint => Entry.SchemaFingerprint;
    public IReadOnlyList<Guid> Dependencies => Entry.Dependencies;

    public static ImportedAsset Create<T>(
        Asset<T> asset,
        string subAssetKey,
        string outputPath)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        AssetEntry info = asset.File.Entry;
        return new ImportedAsset(
            subAssetKey ?? string.Empty,
            outputPath,
            info);
    }

    internal AssetEntry Info
        => Entry;
}

public enum AssetDiagnosticKind
{
    OrphanSourceMeta,
    MissingAssetFile,
    DanglingReference,
}

public enum AssetDiagnosticSeverity
{
    Warning,
    Error,
}

public sealed class AssetDiagnostic
{
    public required AssetDiagnosticKind Kind { get; init; }
    public required AssetDiagnosticSeverity Severity { get; init; }
    public required string Path { get; init; }
    public required string Message { get; init; }
    public Guid AssetGuid { get; init; }
    public SourceGuid SourceGuid { get; init; }
    public Guid RelatedAssetGuid { get; init; }
    public bool HasAsset => AssetGuid != System.Guid.Empty;
    public bool HasRelatedAsset => (RelatedAssetGuid != System.Guid.Empty);
}

public sealed class FileDependency
{
    public required string RelativePath { get; init; }
    public required string ContentHash { get; init; }
}

