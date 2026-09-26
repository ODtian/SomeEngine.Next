namespace SomeEngine.Assets.Importers;

/// <summary>Connects project-owned .shader definitions to the existing Slang compiler.</summary>
public sealed class ShaderSourceFormat(IReadOnlyList<string>? includeDirectories = null) : IAssetSourceFormat
{
    private readonly string[] _includeDirectories = includeDirectories?.ToArray() ?? [];
    public string SourceExtension => ".shader";

    private string[] IncludePaths(string projectRoot)
        => _includeDirectories.Select(path => Path.GetFullPath(Path.Combine(projectRoot, path))).ToArray();

    public async ValueTask<ImportInputs?> GetInputsAsync(string projectRoot, string sourcePath,
        IReadOnlyList<FileDependency> previousInputs, CancellationToken cancellationToken = default)
    {
        using var definition = await Schema.ShaderAsset.OpenSourceAsync(sourcePath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return SlangShaderImporter.GetDefinitionInputs(definition, sourcePath, previousInputs, IncludePaths(projectRoot), projectRoot);
    }

    public async ValueTask<AssetSourceResult> BuildAsync(string projectRoot, string sourcePath, string outputPath,
        CancellationToken cancellationToken = default)
    {
        using var definition = await Schema.ShaderAsset.OpenSourceAsync(sourcePath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var built = SlangShaderImporter.CompileDefinition(definition, sourcePath,
            cancellationToken, IncludePaths(projectRoot), projectRoot);
        var trace = built.GetBlock<Schema.ShaderBlock>().ImportTrace!;
        var inputs = new ImportInputs
        {
            InputHash = trace.ContentFingerprint!, ImporterVersion = trace.ImporterVersion,
            Dependencies = trace.Dependencies!.Select(static input => new FileDependency
                { RelativePath = input.Path!, ContentHash = input.ContentHash! }).ToArray(),
        };
        AssetEntry output = await built.SaveAsync(outputPath, cancellationToken).ConfigureAwait(false);
        return new(output, inputs);
    }
}
