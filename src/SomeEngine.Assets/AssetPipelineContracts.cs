using System.Collections.Generic;

namespace SomeEngine.Assets;

public interface IAssetImporter
{
    string ImporterName { get; }
    IReadOnlyList<string> SourceExtensions { get; }
    bool MatchesSourcePath(string sourcePath);
    ImportInputs? GetInputs(string projectRoot, string sourcePath, SourceMeta sourceMeta);
    ValueTask<IReadOnlyList<ImportedAsset>> ImportAsync(
        string projectRoot,
        string sourcePath,
        CancellationToken cancellationToken = default);
}

public sealed class ImportInputs
{
    public required string InputHash { get; init; }
    public required IReadOnlyList<FileDependency> Dependencies { get; init; }
    public required uint ImporterVersion { get; init; }
}

