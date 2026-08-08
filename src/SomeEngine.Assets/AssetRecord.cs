using System.Collections.Generic;

namespace SomeEngine.Assets;

public readonly record struct ImportedAsset
{
    private ImportedAsset(
        string subAssetKey,
        string outputPath,
        AssetDescription info)
    {
        AssetGuid = info.AssetGuid;
        Name = info.Name;
        SubAssetKey = subAssetKey;
        OutputPath = outputPath;
        AssetType = info.AssetType;
        SchemaFingerprint = info.SchemaFingerprint;
        Dependencies = info.Dependencies;
    }

    public AssetGuid AssetGuid { get; }
    public string Name { get; }
    public string SubAssetKey { get; }
    public string OutputPath { get; }
    public string AssetType { get; }
    public ulong SchemaFingerprint { get; }
    public IReadOnlyList<AssetGuid> Dependencies { get; }

    public static ImportedAsset Create<T>(
        T asset,
        string subAssetKey,
        string outputPath)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        AssetDescription info = AssetMetadata.Describe(asset, outputPath);
        return new ImportedAsset(
            subAssetKey ?? string.Empty,
            outputPath,
            info);
    }

    internal AssetDescription Info
        => new(AssetGuid, Name, AssetType, SchemaFingerprint, Dependencies);
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
    public AssetGuid AssetGuid { get; init; }
    public SourceGuid SourceGuid { get; init; }
    public AssetGuid RelatedAssetGuid { get; init; }
    public bool HasAsset => !AssetGuid.IsEmpty;
    public bool HasRelatedAsset => !RelatedAssetGuid.IsEmpty;
}

public sealed class DependencyEntryData
{
    public required string RelativePath { get; init; }
    public required string ContentHash { get; init; }
}

