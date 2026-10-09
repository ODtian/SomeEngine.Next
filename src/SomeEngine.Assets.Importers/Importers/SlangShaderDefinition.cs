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
            ShaderBlock code = asset.GetBlock<ShaderBlock>();
            string fullPath = Path.GetFullPath(definitionPath);
            projectRoot = Path.GetFullPath(projectRoot ?? Path.GetDirectoryName(fullPath)!);
            string? definitionHash = File.Exists(fullPath) ? ImportHash.FileSha256(fullPath) : null;
            TargetDesc[] targets = CreateDefaultTargets(GlobalSession);
            SessionDesc sessionDescription = CreateSessionDescription(fullPath, targets, includeDirectories);
            ShaderCompilation compiled = definition.CompileAsync(GlobalSession, sessionDescription,
                string.IsNullOrWhiteSpace(asset.Name) ? Path.GetFileNameWithoutExtension(fullPath) : asset.Name,
                cancellationToken).AsTask().GetAwaiter().GetResult();
            for (int index = 0; index < compiled.SerializedSources.Count; index++)
                definition.SetSerializedSource(index, compiled.SerializedSources[index].Span);
            foreach (ulong key in code.Chunks.Keys.ToArray()) code.RemoveBytes(key);
            for (int target = 0; target < targets.Length; target++)
            {
                ISlangBlob? targetCode = compiled.Shader.GetSlangTargetBlob(target);
                if (targetCode is not null) code.SetTargetCode(sessionDescription, target, targetCode.Buffer);
                for (int entry = 0; entry < compiled.Shader.SlangEntryCount; entry++)
                {
                    code.SetEntryCode(sessionDescription, target, entry, compiled.Shader.GetSlangEntryBlob(target, entry)!.Buffer);
                    code.SetEntryUsage(sessionDescription, target, entry, compiled.Shader.GetSlangUsedParameters(target, entry));
                }
            }
            var dependencies = compiled.Files.Select(file => new FileDependency
            {
                RelativePath = SlangDeps.RelPath(projectRoot, file.Path), ContentHash = file.ContentHash,
            }).ToList();
            if (definitionHash is not null)
                dependencies.Add(new FileDependency { RelativePath = SlangDeps.RelPath(projectRoot, fullPath), ContentHash = definitionHash });
            FileDependency[] inputs = dependencies.DistinctBy(static value => value.RelativePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value.RelativePath, StringComparer.Ordinal).ToArray();
            string settings = DefinitionSettings(definition, fullPath, projectRoot, includeDirectories);
            string contentHash = ImportHash.ComputeInputHash(inputs, ImporterVersion, DefaultTargetSetting, settings);
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
