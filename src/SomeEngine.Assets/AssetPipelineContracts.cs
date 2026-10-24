using System.Collections.Generic;

namespace SomeEngine.Assets;

/// <summary>A readable native definition already owns its identity and does not need source companion metadata.</summary>
public interface IAssetSourceFormat
{
    string SourceExtension { get; }
    ValueTask<ImportInputs?> GetInputsAsync(string projectRoot, string sourcePath,
        IReadOnlyList<FileDependency> previousInputs, CancellationToken cancellationToken = default);
    ValueTask<AssetSourceResult> BuildAsync(string projectRoot, string sourcePath, string outputPath,
        CancellationToken cancellationToken = default);
}

/// <summary>A saved native definition and the actual inputs used to build it.</summary>
public sealed record AssetSourceResult(AssetEntry Asset, ImportInputs Inputs);

public sealed class ImportInputs
{
    public required string InputHash { get; init; }
    public required IReadOnlyList<FileDependency> Dependencies { get; init; }
    public required uint ImporterVersion { get; init; }
}

