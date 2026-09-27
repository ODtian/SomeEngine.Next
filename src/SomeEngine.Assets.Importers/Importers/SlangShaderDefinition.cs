using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Importers;

public static partial class SlangShaderImporter
{
    public static ImportInputs? GetDefinitionInputs(ShaderAsset definition, string definitionPath,
        IReadOnlyList<FileDependency> previousInputs, IReadOnlyList<string>? includeDirectories = null,
        string? projectRoot = null)
    {
        using var copy = definition.Duplicate();
        string fullPath = Path.GetFullPath(definitionPath);
        projectRoot = Path.GetFullPath(projectRoot ?? Path.GetDirectoryName(fullPath)!);
        return SlangDeps.Refresh(previousInputs, projectRoot, ImporterVersion,
            SlangShaderCookProfiles.BuildSetting(copy.GetBlock<ShaderSourceBlock>().CookProfile),
            DefinitionSettings(copy.GetBlock<ShaderSourceBlock>(), fullPath, projectRoot, includeDirectories));
    }

    private static string DefinitionSettings(ShaderSourceBlock source, string definitionPath, string projectRoot,
        IReadOnlyList<string>? includeDirectories)
    {
        var fields = new System.Buffers.ArrayBufferWriter<byte>();
        SomeEngine.Serialization.TextSerializer.Write(fields, source, indented: false);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(fields.WrittenSpan);
        hash.AppendData(Encoding.UTF8.GetBytes($"{ShaderAsset.SchemaHash:X16}:{ShaderSourceBlock.SchemaFingerprint:X16}:{ShaderBlock.SchemaFingerprint:X16}"));
        hash.AppendData(Encoding.UTF8.GetBytes(GlobalSession.GetBuildTagString()));
        foreach (string path in includeDirectories ?? [])
        {
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(SlangDeps.RelPath(projectRoot,
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(definitionPath)!, path)))));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Builds an independent .shader source. The source owns its ID and names every input explicitly.</summary>
    /// <remarks>Dependency paths are relative to projectRoot, or to the definition directory when it is omitted.</remarks>
    public static async ValueTask<AssetEntry> BuildAsync(string definitionPath, string? outputPath = null,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? includeDirectories = null,
        string? projectRoot = null)
    {
        if (!definitionPath.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A shader definition must use the .shader suffix.");
        using ShaderAsset source = await ShaderAsset.OpenSourceAsync(definitionPath, cancellationToken: cancellationToken).ConfigureAwait(false);
        using ShaderAsset built = CompileDefinition(source, definitionPath, cancellationToken, includeDirectories, projectRoot);
        return await built.SaveAsync(outputPath ?? definitionPath + ".asset", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compiles explicitly selected source content; it does not create metadata or save an output.</summary>
    public static ShaderAsset CompileDefinition(ShaderAsset source, string definitionPath,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? includeDirectories = null,
        string? projectRoot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ShaderAsset asset = source.Duplicate();
        ISession? session = null;
        IModule[] sourceUnits = [];
        var entries = new List<CookedEntryPoint>();
        try
        {
            ShaderSourceBlock definition = asset.GetBlock<ShaderSourceBlock>();
            ShaderBlock code = asset.GetBlock<ShaderBlock>();
            if (definition.SourceFiles.Count == 0) throw new InvalidDataException("The shader definition must select at least one source file.");
            string fullPath = Path.GetFullPath(definitionPath);
            string directory = Path.GetDirectoryName(fullPath)!;
            projectRoot = Path.GetFullPath(projectRoot ?? directory);
            var context = new ShaderImportContext(fullPath, projectRoot);
            TargetDesc[] targets = SlangShaderCookProfiles.CreateTargets(GlobalSession, definition.CookProfile);
            string[] paths = definition.SourceFiles.Select(path =>
            {
                if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
                    throw new InvalidDataException("Shader source paths must be relative to the definition.");
                return Path.GetFullPath(Path.Combine(directory, path));
            }).ToArray();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                throw new InvalidDataException("Shader source files must be distinct.");
            SessionDesc sessionDesc = CreateSessionDesc(fullPath, targets);
            sessionDesc.SearchPaths = paths.Select(path => Path.GetDirectoryName(path)!)
                .Concat(sessionDesc.SearchPaths ?? []).Concat((includeDirectories ?? []).Select(path => Path.GetFullPath(Path.Combine(directory, path))))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            SlangResult sessionResult = GlobalSession.CreateSession(sessionDesc, out ISession createdSession);
            if (sessionResult.Failed || createdSession is null)
                throw new InvalidDataException($"Slang session creation failed with {sessionResult}.");
            session = createdSession;
            sourceUnits = new IModule[paths.Length];
            var texts = new string[paths.Length];
            var dependencies = new List<FileDependency>();
            for (int i = 0; i < paths.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                texts[i] = File.ReadAllText(paths[i]);
                ISlangBlob blob = Slang.CreateBlob(Encoding.UTF8.GetBytes(texts[i]));
                IModule? sourceUnit;
                ISlangBlob? diagnostics;
                try
                {
                    sourceUnit = session.LoadModuleFromSource(
                        Path.GetFileNameWithoutExtension(paths[i]),
                        paths[i],
                        blob,
                        out diagnostics);
                }
                finally
                {
                    ReleaseSlang(blob);
                }
                string diagnosticText = GetString(diagnostics) ?? "no diagnostics";
                ReleaseSlang(diagnostics);
                sourceUnits[i] = sourceUnit ?? throw new InvalidDataException(
                    $"Failed to load shader source '{paths[i]}': {diagnosticText}");
                SlangResult serializeResult = sourceUnits[i].Serialize(out ISlangBlob serializedSource);
                try
                {
                    if (serializeResult.Failed || serializedSource is null || serializedSource.GetBufferSize() == 0)
                        throw new InvalidDataException($"Failed to serialize Slang source '{paths[i]}'.");
                    definition.SetSerializedSource(i, serializedSource.Buffer);
                }
                finally
                {
                    ReleaseSlang(serializedSource);
                }
                dependencies.AddRange(SlangDeps.Collect(sourceUnit, paths[i], projectRoot));
            }
            if (File.Exists(fullPath)) dependencies.Add(new FileDependency
                { RelativePath = SlangDeps.RelPath(projectRoot, fullPath), ContentHash = ImportHash.FileSha256(fullPath) });
            FileDependency[] inputs = dependencies.DistinctBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value.RelativePath, StringComparer.Ordinal).ToArray();
            string settings = DefinitionSettings(definition, fullPath, projectRoot, includeDirectories);
            string hash = ImportHash.ComputeInputHash(
                inputs,
                ImporterVersion,
                SlangShaderCookProfiles.BuildSetting(definition.CookProfile),
                settings);
            code.ImportTrace = CreateImportTrace(context, hash, inputs);
            foreach (ulong key in code.Chunks.Keys.ToArray()) code.Chunks.Remove(key);
            code.Programs.Clear();
            code.EntryPointAttributes = [];
            code.Reflections = [];
            code.EntryPointReflections = [];
            code.EntryPointMetadata = [];
            code.Metadata = CreateMetadata();
            var state = new ShaderImportState(context, targets, session, sourceUnits[0], inputs,
                asset, CreateBackendResourceMaps(targets), []);
            for (int i = 0; i < sourceUnits.Length; i++)
                CollectModuleMaterialMetadata(state, texts[i], sourceUnits[i]);
            var components = new List<IComponentType>(sourceUnits);
            void AddEntry(IEntryPoint entry)
            {
                entries.Add(new(entry, SlangEntryMeta.Read(entry.GetFunctionReflection())));
                components.Add(entry);
            }
            foreach (IModule sourceUnit in sourceUnits)
            {
                for (int i = 0; i < sourceUnit.GetDefinedEntryPointCount(); i++)
                {
                    SlangResult result = sourceUnit.GetDefinedEntryPoint(i, out IEntryPoint entry);
                    if (result.Failed || entry is null)
                        throw new InvalidDataException("Slang could not return a declared shader entry point.");
                    AddEntry(entry);
                }
            }
            if (entries.Count == 0) throw new InvalidDataException("The shader definition selects no entry points.");
            cancellationToken.ThrowIfCancellationRequested();
            CompileEntries(state, components.ToArray(), entries.ToArray(), definition.Specializations.ToArray());
            ShaderMetadata metadata = code.Metadata!;
            SlangMaterialMeta.SortInstanceProperties(ref metadata);
            code.Metadata = metadata;
            AddBackendReflections(code, state.BackendResourceMaps);
            cancellationToken.ThrowIfCancellationRequested();
            return asset;
        }
        catch
        {
            asset.Dispose();
            throw;
        }
        finally
        {
            for (int index = entries.Count - 1; index >= 0; index--)
                ReleaseSlang(entries[index].EntryPoint);
            ReleaseSlangReverse(sourceUnits);
            ReleaseSlang(session);
        }
    }
}
