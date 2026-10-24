using SomeEngine.Assets.Importing;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization;
using System.Globalization;
using System.Text.Json;

namespace SomeEngine.Assets;

public enum AssetFileKind { BinaryAsset, NativeSource, ImportedSource }

public sealed record AssetDiscoveredFile(string Path, AssetFileKind Kind, AssetEntry? Asset, SourceGuid SourceGuid = default);

/// <summary>A statically typed editor for relative paths in one block's readable metadata.</summary>
public abstract class AssetBlockPathEditor
{
    public abstract Guid BlockTypeId { get; }
    internal abstract ulong Schema { get; }
    internal abstract void WriteFields(Utf8JsonWriter writer, JsonElement fields, string sourceDirectory, string destinationDirectory);

}

public class AssetBlockPathEditor<TBlock> : AssetBlockPathEditor
    where TBlock : AssetBlock, IAssetBlock<TBlock>, IBinarySerializable<TBlock>, ITextSerializable<TBlock>
{
    public override Guid BlockTypeId => TBlock.BlockTypeId;
    internal override ulong Schema => TBlock.SchemaFingerprint;
    protected virtual void Relocate(TBlock fields, string sourceDirectory, string destinationDirectory) { }
    internal override void WriteFields(Utf8JsonWriter writer, JsonElement data, string sourceDirectory, string destinationDirectory)
    {
        using TBlock fields = TBlock.ReadText(data);
        Relocate(fields, sourceDirectory, destinationDirectory);
        TBlock.WriteText(writer, fields);
    }
}

public sealed partial class AssetProject
{
    /// <summary>Lists asset identities without loading their business types or compiling source files.</summary>
    public async ValueTask<IReadOnlyList<AssetDiscoveredFile>> DiscoverAsync(string directory = ".", CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await DiscoverFilesAsync(FullPath(directory), cancellationToken).ConfigureAwait(false); }
        finally { _importGate.Release(); }
    }

    private async ValueTask<IReadOnlyList<AssetDiscoveredFile>> DiscoverFilesAsync(string directory, CancellationToken cancellationToken)
    {
        var result = new List<AssetDiscoveredFile>();
        foreach (string path in EnumerateProjectFiles(directory).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".import.json", StringComparison.OrdinalIgnoreCase)
                || IsContained(_manifestDirectory, path) || IsBuildDirectory(path)) continue;
            if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new(path, AssetFileKind.BinaryAsset, await DescribeFileAsync(path, cancellationToken).ConfigureAwait(false)));
            }
            else if (path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
            {
                AssetEntry entry = AssetText.Describe(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
                result.Add(new(path, AssetFileKind.NativeSource, entry with { Path = path }));
            }
            else if (File.Exists(SourceMeta.GetMetaPath(path)))
            {
                SourceMeta metadata = SourceMeta.Load(path);
                result.Add(new(path, AssetFileKind.ImportedSource, null, metadata.SourceGuid));
            }
            else if (MatchImporter(path) is not null)
                result.Add(new(path, AssetFileKind.ImportedSource, null));
        }
        return result;
    }

    /// <summary>Reconstructs the directory from files and source identities. No import or compilation is performed.</summary>
    public async ValueTask RebuildAsync(CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetImportFiles? files = null;
        try
        {
            IReadOnlyList<AssetDiscoveredFile> discovered = await DiscoverFilesAsync(_projectRoot, cancellationToken).ConfigureAwait(false);
            var next = new AssetManifest();
            var outputs = new Dictionary<Guid, (SourceGuid Source, string Element)>();
            foreach (AssetDiscoveredFile source in discovered.Where(static item => item.Kind == AssetFileKind.ImportedSource))
            {
                if (source.SourceGuid.IsEmpty) continue;
                SourceMeta metadata = SourceMeta.Load(source.Path);
                next.AddSource(metadata.SourceGuid, ToManifestPath(source.Path));
                foreach ((string element, Guid id) in metadata.ElementIds)
                    if (!outputs.TryAdd(id, (metadata.SourceGuid, element)))
                        throw new InvalidDataException($"Asset {id} has conflicting source identities including '{source.Path}'.");
            }
            var definitions = new Dictionary<Guid, AssetDiscoveredFile>();
            foreach (AssetDiscoveredFile definition in discovered.Where(static item => item.Kind == AssetFileKind.NativeSource))
            {
                Guid id = definition.Asset!.Value.AssetGuid;
                if (!definitions.TryAdd(id, definition))
                    throw new InvalidDataException($"Asset {id} is defined by both '{definitions[id].Path}' and '{definition.Path}'.");
            }
            foreach (AssetDiscoveredFile item in discovered.Where(static item => item.Kind == AssetFileKind.BinaryAsset))
            {
                AssetEntry entry = item.Asset!.Value;
                if (next.TryGetAsset(entry.AssetGuid, out AssetEntry duplicate))
                    throw new InvalidDataException($"Asset {entry.AssetGuid} occurs in both '{duplicate.Path}' and '{item.Path}'.");
                SourceGuid sourceGuid = default;
                string key = "";
                IReadOnlyList<Guid> references = entry.Dependencies;
                if (outputs.TryGetValue(entry.AssetGuid, out var imported))
                {
                    sourceGuid = imported.Source;
                    key = imported.Element;
                }
                else if (Manifest.TryGetAsset(entry.AssetGuid, out AssetEntry previous) && previous.Version == entry.Version)
                    references = previous.Dependencies;
                string definition = definitions.TryGetValue(entry.AssetGuid, out AssetDiscoveredFile? source)
                    ? ToManifestPath(source.Path) : "";
                next.AddAsset(entry.AssetGuid, entry.Name, ToManifestPath(item.Path), entry.AssetType, entry.SchemaFingerprint,
                    sourceGuid, key, references, definition, entry.Version);
            }
            foreach ((Guid id, AssetDiscoveredFile definition) in definitions)
            {
                if (next.Assets.ContainsKey(id)) continue;
                AssetEntry entry = definition.Asset!.Value;
                next.AddAsset(id, entry.Name, ToManifestPath(definition.Path + ".asset"), entry.AssetType,
                    AssetHeader.SchemaFingerprint, dependencies: entry.Dependencies, definitionPath: ToManifestPath(definition.Path));
            }
            files = new AssetImportFiles();
            next.AddFiles(files, _manifestDirectory);
            lock (_gate) { files.Commit(cancellationToken); Manifest = next; }
        }
        catch (Exception error) { files?.RecordFailure(error); throw; }
        finally { try { files?.Dispose(); } finally { _importGate.Release(); } }
    }

    /// <summary>Moves a source and its companions, or an asset file, while preserving their identities.</summary>
    public ValueTask MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => MoveFilesAsync(sourcePath, destinationPath, null, cancellationToken);

    /// <summary>Moves a typed block source and relocates its external chunk paths.</summary>
    /// <remarks>The standard external chunk paths are rewritten automatically without reading their payloads.</remarks>
    public ValueTask MoveAsync<TBlock>(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
        where TBlock : AssetBlock, IAssetBlock<TBlock>, IBinarySerializable<TBlock>, ITextSerializable<TBlock>
    {
        return MoveAsync(sourcePath, destinationPath, [new AssetBlockPathEditor<TBlock>()], cancellationToken);
    }

    public ValueTask MoveAsync(string sourcePath, string destinationPath, IReadOnlyList<AssetBlockPathEditor> blockEditors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(blockEditors);
        Dictionary<Guid, AssetBlockPathEditor> editors = blockEditors.ToDictionary(static editor => editor.BlockTypeId);
        return MoveFilesAsync(sourcePath, destinationPath, editors, cancellationToken);
    }

    private async ValueTask MoveFilesAsync(string sourcePath, string destinationPath,
        IReadOnlyDictionary<Guid, AssetBlockPathEditor>? editors, CancellationToken cancellationToken)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetImportFiles? files = null;
        try
        {
            string source = RequireProjectPath(_projectRoot, sourcePath), destination = RequireProjectPath(_projectRoot, destinationPath);
            if (PathsEqual(source, destination)) return;
            if (editors is null && !PathsEqual(Path.GetDirectoryName(source)!, Path.GetDirectoryName(destination)!))
                throw new NotSupportedException("Moving a source or asset to another directory requires migrating its external references. Rename it within its directory or use an importer-aware project move.");
            RequireNewPath(destination);
            bool hasMetadata = File.Exists(SourceMeta.GetMetaPath(source));
            if (hasMetadata) { RequireNewPath(SourceMeta.GetMetaPath(destination)); RequireNewPath(SourceMeta.GetBuildPath(destination)); }
            files = new AssetImportFiles();
            if (editors is null) await StageCopyAsync(files, source, destination, cancellationToken).ConfigureAwait(false);
            else
            {
                string stage = files.CreateStagePath(destination);
                files.AddFile(destination, stage);
                await RelocateSourceAsync(source, stage, editors, cancellationToken).ConfigureAwait(false);
            }
            files.AddDelete(source);
            AssetManifest next;
            lock (_gate) next = Manifest.Copy();
            if (hasMetadata)
            {
                SourceMeta metadata = SourceMeta.Load(source);
                metadata.InputHash = "";
                SourceMeta.AddFiles(files, destination, metadata);
                files.AddDelete(SourceMeta.GetMetaPath(source));
                if (File.Exists(SourceMeta.GetBuildPath(source))) files.AddDelete(SourceMeta.GetBuildPath(source));
                next.RemoveSource(metadata.SourceGuid);
                next.AddSource(metadata.SourceGuid, ToManifestPath(destination));
            }
            else if (File.Exists(SourceMeta.GetBuildPath(source)))
            {
                // The definition keeps its identity. Rebuild its path-dependent derived record.
                files.AddDelete(SourceMeta.GetBuildPath(source));
            }
            foreach (AssetEntry entry in next.Assets.Values.ToArray())
            {
                bool moveFile = PathsEqual(FullPath(entry.Path), source);
                bool moveDefinition = entry.DefinitionPath.Length != 0 && PathsEqual(FullPath(entry.DefinitionPath), source);
                if (!moveFile && !moveDefinition) continue;
                if (moveFile && !entry.SourceGuid.IsEmpty && next.TrySourcePath(entry.SourceGuid, out string importedSource))
                {
                    string sourceFile = RequireProjectPath(_projectRoot, importedSource);
                    SourceMeta metadata = SourceMeta.Load(sourceFile);
                    int index = metadata.Outputs.FindIndex(output => output.AssetGuid == entry.AssetGuid);
                    if (index >= 0) metadata.Outputs[index] = metadata.Outputs[index] with { AssetPath = destination };
                    SourceMeta.AddFiles(files, sourceFile, metadata);
                }
                next.AddAsset(entry.AssetGuid, entry.Name, moveFile ? ToManifestPath(destination) : entry.Path, entry.AssetType,
                    entry.SchemaFingerprint, entry.SourceGuid, entry.SubAssetKey, entry.Dependencies,
                    moveDefinition ? ToManifestPath(destination) : entry.DefinitionPath, entry.Version);
            }
            next.AddFiles(files, _manifestDirectory);
            lock (_gate) { files.Commit(cancellationToken); Manifest = next; }
        }
        catch (Exception error) { files?.RecordFailure(error); throw; }
        finally { try { files?.Dispose(); } finally { _importGate.Release(); } }
    }

    /// <summary>Copies an imported source with new source and element identities. Derived outputs are rebuilt by import.</summary>
    /// <remarks>Copy within the source directory. Moving relative inputs to another directory requires format-specific rewriting.</remarks>
    public async ValueTask<SourceGuid> CopySourceAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetImportFiles? files = null;
        try
        {
            string source = FullPath(sourcePath), destination = RequireProjectPath(_projectRoot, destinationPath);
            if (!File.Exists(SourceMeta.GetMetaPath(source)) || source.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"'{source}' is not an imported source with companion metadata. Copy native assets through their typed codec.");
            if (!PathsEqual(Path.GetDirectoryName(source)!, Path.GetDirectoryName(destination)!))
                throw new NotSupportedException("Copying an imported source to another directory requires rewriting its relative inputs. Copy it within its directory or use a format-specific copy.");
            RequireNewPath(destination);
            RequireNewPath(SourceMeta.GetMetaPath(destination));
            RequireNewPath(SourceMeta.GetBuildPath(destination));
            SourceMeta previous = SourceMeta.Load(source);
            var metadata = new SourceMeta
            {
                SourceGuid = SourceGuid.New(),
                Importers = previous.Importers.ToList(), ImporterSets = previous.ImporterSets?.ToList(),
                UseDefaultImporters = previous.UseDefaultImporters,
                ElementIds = previous.ElementIds.ToDictionary(static item => item.Key, static _ => Guid.NewGuid(), StringComparer.Ordinal),
            };
            files = new AssetImportFiles();
            await StageCopyAsync(files, source, destination, cancellationToken).ConfigureAwait(false);
            SourceMeta.AddFiles(files, destination, metadata);
            AssetManifest next;
            lock (_gate) next = Manifest.Copy();
            next.AddSource(metadata.SourceGuid, ToManifestPath(destination));
            next.AddFiles(files, _manifestDirectory);
            lock (_gate) { files.Commit(cancellationToken); Manifest = next; }
            return metadata.SourceGuid;
        }
        catch (Exception error) { files?.RecordFailure(error); throw; }
        finally { try { files?.Dispose(); } finally { _importGate.Release(); } }
    }

    /// <summary>Copies a typed asset to a new identity using its own codec and registers the result.</summary>
    public async ValueTask<Guid> CopyAssetAsync<TAsset>(TAsset source, string destinationPath,
        CancellationToken cancellationToken = default) where TAsset : Asset
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetImportFiles? files = null;
        try
        {
            string destination = RequireProjectPath(_projectRoot, destinationPath);
            RequireNewPath(destination);
            using TAsset copy = (TAsset)source.CreateCopy();
            files = new AssetImportFiles();
            string stage = files.CreateStagePath(destination);
            files.AddFile(destination, stage);
            AssetEntry entry = await copy.SaveAsync(stage, cancellationToken).ConfigureAwait(false);
            if (entry.AssetGuid != copy.Id || !PathsEqual(Path.GetFullPath(entry.Path), stage))
                throw new InvalidDataException("The copied asset identity or saved path differs from the requested copy.");
            AssetManifest next;
            lock (_gate) next = Manifest.Copy();
            next.AddAsset(entry.AssetGuid, entry.Name, ToManifestPath(destination), entry.AssetType,
                entry.SchemaFingerprint, dependencies: entry.Dependencies, version: entry.Version);
            next.AddFiles(files, _manifestDirectory);
            lock (_gate) { files.Commit(cancellationToken); Manifest = next; }
            return entry.AssetGuid;
        }
        catch (Exception error) { files?.RecordFailure(error); throw; }
        finally { try { files?.Dispose(); } finally { _importGate.Release(); } }
    }

    /// <summary>Deletes an imported source, its companions and its outputs as one project edit.</summary>
    public async ValueTask DeleteSourceAsync(string sourcePath, bool allowDanglingReferences = false,
        CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetImportFiles? files = null;
        try
        {
            string source = RequireProjectPath(_projectRoot, sourcePath);
            byte[]? originalMetadata = ReadOptional(SourceMeta.GetMetaPath(source));
            if (originalMetadata is null) throw new InvalidOperationException($"Source '{source}' has no identity metadata.");
            SourceMeta metadata = SourceMeta.Load(source);
            AssetManifest next;
            lock (_gate) next = Manifest.Copy();
            if (next.TrySourcePath(metadata.SourceGuid, out string registeredSource)
                && !PathsEqual(FullPath(registeredSource), source))
                throw new InvalidDataException($"Source identity {metadata.SourceGuid} belongs to '{registeredSource}'.");
            var outputs = next.AssetsBySource(metadata.SourceGuid).Select(id => next.Assets[id])
                .ToDictionary(static entry => entry.AssetGuid, entry => RequireProjectPath(_projectRoot, entry.Path));
            foreach (SourceOutput output in metadata.Outputs)
            {
                if (next.TryGetAsset(output.AssetGuid, out AssetEntry registered)
                    && (registered.SourceGuid != metadata.SourceGuid || !PathsEqual(FullPath(registered.Path), output.AssetPath)))
                    throw new InvalidDataException($"Output {output.AssetGuid} belongs to a different source or path.");
                if (next.TryAssetPath(ToManifestPath(output.AssetPath), out AssetEntry pathEntry)
                    && (pathEntry.AssetGuid != output.AssetGuid || pathEntry.SourceGuid != metadata.SourceGuid))
                    throw new InvalidDataException($"Output path '{output.AssetPath}' belongs to another asset.");
                outputs[output.AssetGuid] = RequireProjectPath(_projectRoot, output.AssetPath);
            }
            RequireNoReferencers(next, outputs.Keys.ToHashSet(), allowDanglingReferences);
            foreach ((Guid id, string path) in outputs)
                await CheckOutputFileAsync(path, id, cancellationToken).ConfigureAwait(false);
            files = new AssetImportFiles();
            foreach ((Guid id, string path) in outputs) { files.AddDelete(path); next.RemoveAsset(id); }
            files.AddDelete(source);
            files.AddDelete(SourceMeta.GetMetaPath(source));
            files.AddDelete(SourceMeta.GetBuildPath(source));
            next.RemoveSource(metadata.SourceGuid);
            next.AddFiles(files, _manifestDirectory);
            CheckUnchanged(SourceMeta.GetMetaPath(source), originalMetadata);
            lock (_gate) { files.Commit(cancellationToken); Manifest = next; }
        }
        catch (Exception error) { files?.RecordFailure(error); throw; }
        finally { try { files?.Dispose(); } finally { _importGate.Release(); } }
    }

    /// <summary>Deletes one asset and its native definition, or removes one imported output while keeping its source.</summary>
    public async ValueTask DeleteAssetAsync(Guid assetId, bool allowDanglingReferences = false,
        CancellationToken cancellationToken = default)
    {
        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AssetImportFiles? files = null;
        try
        {
            AssetManifest next;
            lock (_gate) next = Manifest.Copy();
            if (!next.TryGetAsset(assetId, out AssetEntry entry)) throw new KeyNotFoundException($"Asset {assetId} is not registered.");
            RequireNoReferencers(next, [assetId], allowDanglingReferences);
            string path = RequireProjectPath(_projectRoot, entry.Path);
            await CheckOutputFileAsync(path, assetId, cancellationToken).ConfigureAwait(false);
            files = new AssetImportFiles();
            files.AddDelete(path);
            if (entry.DefinitionPath.Length != 0)
            {
                string definition = RequireProjectPath(_projectRoot, entry.DefinitionPath);
                if (File.Exists(definition))
                {
                    AssetEntry description = AssetText.Describe(await File.ReadAllBytesAsync(definition, cancellationToken).ConfigureAwait(false));
                    if (description.AssetGuid != assetId) throw new InvalidDataException($"Definition '{definition}' belongs to another asset.");
                }
                files.AddDelete(definition);
                files.AddDelete(SourceMeta.GetBuildPath(definition));
            }
            string? metadataPath = null;
            byte[]? originalMetadata = null;
            if (!entry.SourceGuid.IsEmpty && next.TrySourcePath(entry.SourceGuid, out string source))
            {
                string fullSource = RequireProjectPath(_projectRoot, source);
                metadataPath = SourceMeta.GetMetaPath(fullSource);
                originalMetadata = ReadOptional(metadataPath);
                SourceMeta metadata = SourceMeta.Load(fullSource);
                if (metadata.SourceGuid != entry.SourceGuid) throw new InvalidDataException("The source metadata identity differs from its directory entry.");
                metadata.Outputs.RemoveAll(output => output.AssetGuid == assetId);
                metadata.InputHash = "";
                SourceMeta.AddFiles(files, fullSource, metadata);
            }
            next.RemoveAsset(assetId);
            next.AddFiles(files, _manifestDirectory);
            if (metadataPath is not null) CheckUnchanged(metadataPath, originalMetadata);
            lock (_gate) { files.Commit(cancellationToken); Manifest = next; }
        }
        catch (Exception error) { files?.RecordFailure(error); throw; }
        finally { try { files?.Dispose(); } finally { _importGate.Release(); } }
    }

    private static void RequireNoReferencers(AssetManifest manifest, HashSet<Guid> removing, bool allowDanglingReferences)
    {
        if (allowDanglingReferences) return;
        foreach (Guid id in removing)
            foreach (Guid reference in manifest.GetReferencers(id))
                if (!removing.Contains(reference))
                    throw new InvalidOperationException($"Asset {id} is referenced by asset {reference} at '{manifest.Assets[reference].Path}'.");
    }

    private static async ValueTask StageCopyAsync(AssetImportFiles files, string source, string destination, CancellationToken token)
    {
        string stage = files.CreateStagePath(destination);
        files.AddFile(destination, stage);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
        await using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
        await input.CopyToAsync(output, token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private static async ValueTask RelocateSourceAsync(string sourcePath, string destinationPath,
        IReadOnlyDictionary<Guid, AssetBlockPathEditor> editors, CancellationToken cancellationToken)
    {
        using AssetText.SourceReader source = await AssetText.OpenAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false),
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        JsonElement blocks = document.RootElement.GetProperty("Blocks");
        foreach (JsonElement block in blocks.EnumerateArray())
        {
            Guid typeId = block.GetProperty("TypeId").GetGuid();
            if (!editors.TryGetValue(typeId, out AssetBlockPathEditor? editor))
                throw new NotSupportedException($"Moving this source requires a typed path editor for block {typeId}.");
            if (ulong.Parse(block.GetProperty("Schema").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture) != editor.Schema)
                throw new InvalidDataException($"Source block {typeId} differs from its relocation codec schema.");
        }
        string sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        await using var stream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name != "Blocks") { property.WriteTo(writer); continue; }
                writer.WriteStartArray("Blocks");
                foreach (JsonElement block in blocks.EnumerateArray())
                {
                    AssetBlockPathEditor editor = editors[block.GetProperty("TypeId").GetGuid()];
                    writer.WriteStartObject();
                    writer.WriteString("TypeId", editor.BlockTypeId);
                    writer.WriteString("Schema", editor.Schema.ToString("X16", CultureInfo.InvariantCulture));
                    writer.WritePropertyName("Fields");
                    editor.WriteFields(writer, block.GetProperty("Fields"), sourceDirectory, destinationDirectory);
                    writer.WriteStartObject("Chunks");
                    foreach (JsonProperty chunk in block.GetProperty("Chunks").EnumerateObject())
                    {
                        string path = chunk.Value.GetString() ?? throw new InvalidDataException("A chunk path cannot be null.");
                        if (Path.IsPathRooted(path)) throw new InvalidDataException("A source chunk path must be relative.");
                        string fullPath = Path.GetFullPath(Path.Combine(sourceDirectory, path));
                        if (PathsEqual(fullPath, sourcePath))
                            throw new InvalidDataException("A source cannot relocate an external chunk that refers to its own source file.");
                        writer.WriteString(chunk.Name, Path.GetRelativePath(destinationDirectory, fullPath).Replace('\\', '/'));
                    }
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        stream.Flush(flushToDisk: true);
    }

    private static async ValueTask<AssetEntry> DescribeFileAsync(string path, CancellationToken token)
    {
        await using var source = FileRangeSource.Open(path);
        var metadata = await BinaryDocument.InspectAsync(source, cancellationToken: token).ConfigureAwait(false);
        await using AssetFile file = await AssetFile.OpenAsync(path,
            new(metadata.TypeId, metadata.SchemaFingerprint, metadata.Compatibility, metadata.SchemaEpoch), cancellationToken: token).ConfigureAwait(false);
        return file.Describe();
    }

    private static void RequireNewPath(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"The destination already exists: '{path}'.");
    }

    private static bool IsContained(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private bool IsBuildDirectory(string path)
    {
        string relative = Path.GetRelativePath(_projectRoot, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(static part => part is "obj" or "bin" or ".git" or ".cache");
    }

    private IEnumerable<string> EnumerateProjectFiles(string directory)
    {
        foreach (string path in Directory.EnumerateFiles(directory)) yield return path;
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (IsContained(_manifestDirectory, child) || IsBuildDirectory(child)
                || (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (string path in EnumerateProjectFiles(child)) yield return path;
        }
    }
}
