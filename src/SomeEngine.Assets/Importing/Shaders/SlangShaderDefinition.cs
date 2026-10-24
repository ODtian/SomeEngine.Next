using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public static partial class SlangShaderImporter
{
    private const string DefaultTargetSetting = "slang-targets:dxil-sm_6_6|spirv-glsl_460";

    public static ImportInputs? GetDefinitionInputs(
        ShaderAsset definition,
        string definitionPath,
        IReadOnlyList<FileDependency> previousInputs,
        IReadOnlyList<string>? includeDirectories = null,
        string? projectRoot = null)
    {
        using ShaderAsset copy = definition.Duplicate();
        string fullPath = Path.GetFullPath(definitionPath);
        projectRoot = Path.GetFullPath(projectRoot ?? Path.GetDirectoryName(fullPath)!);
        return SlangDeps.Refresh(
            previousInputs,
            projectRoot,
            ImporterVersion,
            DefaultTargetSetting,
            DefinitionSettings(
                copy.GetBlock<ShaderSourceBlock>(),
                fullPath,
                projectRoot,
                includeDirectories));
    }

    public static async ValueTask<AssetEntry> BuildAsync(
        string definitionPath,
        string? outputPath = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? includeDirectories = null,
        string? projectRoot = null)
    {
        if (!definitionPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A shader definition must use the .shader suffix.");
        using ShaderAsset source = await ShaderAsset.OpenSourceAsync(
            definitionPath,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using ShaderAsset built = CompileDefinition(
            source,
            definitionPath,
            cancellationToken,
            includeDirectories,
            projectRoot);
        return await built.SaveAsync(
            outputPath ?? definitionPath + ".asset",
            cancellationToken).ConfigureAwait(false);
    }

    public static ShaderAsset CompileDefinition(
        ShaderAsset source,
        string definitionPath,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? includeDirectories = null,
        string? projectRoot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShaderAsset asset = source.Duplicate();
        try
        {
            ShaderSourceBlock definition = asset.GetBlock<ShaderSourceBlock>();
            string fullPath = Path.GetFullPath(definitionPath);
            projectRoot = Path.GetFullPath(projectRoot ?? Path.GetDirectoryName(fullPath)!);
            string? definitionHash = File.Exists(fullPath) ? ImportHash.FileSha256(fullPath) : null;
            TargetDesc[] targets = CreateDefaultTargets(GlobalSession);
            SessionDesc sessionDescription = CreateSessionDescription(fullPath, targets, includeDirectories);
            if (string.IsNullOrWhiteSpace(asset.Name)) asset.Name = Path.GetFileNameWithoutExtension(fullPath);
            string settings = DefinitionSettings(definition, fullPath, projectRoot, includeDirectories);
            ShaderCompilation compiled = asset.CompileAsync(GlobalSession, sessionDescription,
                cancellationToken).AsTask().GetAwaiter().GetResult();
            definition = asset.GetBlock<ShaderSourceBlock>();
            ShaderBlock code = asset.GetBlock<ShaderBlock>();
            var dependencies = compiled.Files.Select(file => new FileDependency
            {
                RelativePath = SlangDeps.RelPath(projectRoot, file.Path), ContentHash = file.ContentHash,
            }).ToList();
            if (definitionHash is not null)
                dependencies.Add(new FileDependency { RelativePath = SlangDeps.RelPath(projectRoot, fullPath), ContentHash = definitionHash });
            FileDependency[] inputs = dependencies.DistinctBy(static value => value.RelativePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value.RelativePath, StringComparer.Ordinal).ToArray();
            string contentHash = ImportHash.ComputeInputHash(inputs, ImporterVersion, DefaultTargetSetting, settings);
            definition.DataVersion = new Guid(Convert.FromHexString(contentHash).AsSpan(0, 16));
            code.InputVersions = code.InputVersions.Select(input =>
                input.Input.BlockTypeId == ShaderSourceBlock.BlockTypeId
                    ? input with { DataVersion = definition.DataVersion } : input).ToList();
            code.ImportTrace = CreateImportTrace(fullPath, projectRoot, contentHash, inputs);
            code.SchemaVersion = ShaderBlock.Version;
            cancellationToken.ThrowIfCancellationRequested();
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }
    private static string DefinitionSettings(
        ShaderSourceBlock source,
        string definitionPath,
        string projectRoot,
        IReadOnlyList<string>? includeDirectories)
    {
        var fields = new System.Buffers.ArrayBufferWriter<byte>();
        SomeEngine.Serialization.TextSerializer.Write(fields, source, indented: false);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(fields.WrittenSpan);
        hash.AppendData(Encoding.UTF8.GetBytes(
            $"{ShaderAsset.SchemaHash:X16}:{ShaderSourceBlock.SchemaFingerprint:X16}:" +
            $"{ShaderBlock.SchemaFingerprint:X16}:{ShaderBlock.Version}"));
        hash.AppendData(Encoding.UTF8.GetBytes(GlobalSession.GetBuildTagString()));
        foreach (string path in includeDirectories ?? [])
        {
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(SlangDeps.RelPath(
                projectRoot,
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(definitionPath)!, path)))));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
