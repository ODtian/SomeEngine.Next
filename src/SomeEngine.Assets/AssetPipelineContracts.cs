using System.Collections.Generic;

namespace SomeEngine.Assets;

public interface IAssetImporter
{
    string ImporterName { get; }
    IReadOnlyList<string> SourceExtensions { get; }
    bool MatchesSourcePath(string sourcePath);
    AssetImportFingerprint? GetFingerprint(string projectRoot, string sourcePath, SourceMeta sourceMeta);
    ValueTask<IReadOnlyList<ImportedAsset>> ImportAsync(
        string projectRoot,
        string sourcePath,
        CancellationToken cancellationToken = default);
}

public sealed class AssetImportFingerprint
{
    public required string ContentFingerprint { get; init; }
    public required IReadOnlyList<DependencyEntryData> Dependencies { get; init; }
    public required uint ImporterVersion { get; init; }
}

