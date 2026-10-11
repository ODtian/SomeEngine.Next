using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Assets.Importers;

public static partial class SlangShaderImporter
{
    private const string DefaultTargetSetting = "slang-targets:dxil-sm_6_6|spirv-spirv_1_5";

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
            string settings = DefinitionSettings(definition, fullPath, projectRoot, includeDirectories);
            string shaderName = string.IsNullOrWhiteSpace(asset.Name) ? Path.GetFileNameWithoutExtension(fullPath) : asset.Name;
            ShaderCompilation compiled = definition.CompileAsync(GlobalSession, sessionDescription, shaderName,
                cancellationToken).AsTask().GetAwaiter().GetResult();
            definition.Specializations = compiled.Specializations.ToArray();
            for (int index = 0; index < compiled.SerializedSources.Count; index++)
                definition.SetSerializedSource(index, compiled.SerializedSources[index].Span);
            foreach (ulong key in code.ContentKeys.ToArray()) code.RemoveBytes(key);
            var dependencies = new List<FileDependency>();
            SaveCode(compiled, null, []);
            foreach (ShaderSpecialization specialization in definition.Specializations)
            {
                string[] arguments = specialization.Arguments.ToArray();
                ShaderCompilation variant = definition.CompileAsync(GlobalSession, sessionDescription,
                    shaderName, specialization.EntryPoint,
                    arguments.Select(SpecializationArg.FromExpression).ToArray(), cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                SaveCode(variant, specialization.EntryPoint, arguments);
            }

            void SaveCode(ShaderCompilation result, string? entryPoint, string[] arguments)
            {
                bool hasUnboundEntries = entryPoint is null && Enumerable.Range(0, result.Shader.SlangEntryCount)
                    .Any(entry => result.Shader.GetSlangEntryPoint(entry).GetSpecializationParamCount() != 0);
                for (int target = 0; target < targets.Length; target++)
                {
                    ISlangBlob? targetCode = hasUnboundEntries ? null : result.Shader.GetSlangTargetBlob(target);
                    if (targetCode is not null)
                        code.SetTargetCode(sessionDescription, target, targetCode.Buffer, arguments, entryPoint);
                    for (int entry = 0; entry < result.Shader.SlangEntryCount; entry++)
                    {
                        if (entryPoint is null && result.Shader.GetSlangEntryPoint(entry).GetSpecializationParamCount() != 0)
                            continue;
                        code.SetEntryCode(sessionDescription, target, entry,
                            result.Shader.GetSlangEntryBlob(target, entry)!.Buffer, arguments, entryPoint);
                        code.SetEntryUsage(sessionDescription, target, entry,
                            result.Shader.GetSlangUsedParameters(target, entry), arguments, entryPoint);
                    }
                }
                dependencies.AddRange(result.Files.Select(file => new FileDependency
                {
                    RelativePath = SlangDeps.RelPath(projectRoot, file.Path), ContentHash = file.ContentHash,
                }));
            }
            if (definitionHash is not null)
                dependencies.Add(new FileDependency { RelativePath = SlangDeps.RelPath(projectRoot, fullPath), ContentHash = definitionHash });
            FileDependency[] inputs = dependencies.DistinctBy(static value => value.RelativePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(static value => value.RelativePath, StringComparer.Ordinal).ToArray();
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
