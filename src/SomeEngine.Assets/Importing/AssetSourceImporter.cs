namespace SomeEngine.Assets.Importing;

public interface IAssetSourceImporter
{
    Guid Id { get; }
    IReadOnlyList<string> SourceExtensions => [];
    bool MatchesSourcePath(string path) => SourceExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    ValueTask<IReadOnlyList<Guid>> ImportAsync(AssetProject project, string sourcePath, CancellationToken cancellationToken);
}

public interface IAssetSourceImporter<TSource> : IAssetSourceImporter where TSource : class
{
    ValueTask<TSource> ReadAsync(string sourcePath, CancellationToken cancellationToken);
    ValueTask ImportAsync(TSource source, ImportContext context, CancellationToken cancellationToken);
    ValueTask<ImportInputs?> GetInputsAsync(string projectRoot, string sourcePath, SourceMeta metadata,
        CancellationToken cancellationToken) => ValueTask.FromResult<ImportInputs?>(null);
    ValueTask<IReadOnlyList<Guid>> IAssetSourceImporter.ImportAsync(AssetProject project, string sourcePath, CancellationToken cancellationToken)
        => project.ImportAsync(sourcePath, this, cancellationToken);
}

public interface IAssetSourceImporter<TSource, TSettings> : IAssetSourceImporter<TSource> where TSource : class
{
    ValueTask ImportAsync(TSource source, ImportContext context, AssetImportSettings<TSettings>? overrides,
        CancellationToken cancellationToken);
    ValueTask<ImportInputs?> GetInputsAsync(string projectRoot, string sourcePath, SourceMeta metadata,
        AssetImportSettings<TSettings>? overrides, CancellationToken cancellationToken);
    ValueTask IAssetSourceImporter<TSource>.ImportAsync(TSource source, ImportContext context, CancellationToken cancellationToken)
        => ImportAsync(source, context, null, cancellationToken);
    ValueTask<ImportInputs?> IAssetSourceImporter<TSource>.GetInputsAsync(string projectRoot, string sourcePath,
        SourceMeta metadata, CancellationToken cancellationToken) => GetInputsAsync(projectRoot, sourcePath, metadata, null, cancellationToken);
}

public sealed record AssetImportSettings<T>(T Value);
