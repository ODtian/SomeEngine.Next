using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets;

/// <summary>Editable JSON source containing identity and independently encoded typed blocks.</summary>
public static class AssetText
{
    private const string BlockFormat = "SomeEngine.AssetSource.2";
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64,
    };

    /// <summary>Opens one immutable source document. Each extension reads the block types it defines.</summary>
    public static async ValueTask<SourceReader> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(path);
        JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false), ReadOptions);
        try { return new(document, Path.GetDirectoryName(fullPath)!); }
        catch { document.Dispose(); throw; }
    }

    public static void Write<TAsset>(IBufferWriter<byte> destination, TAsset asset, Action<BlockWriter, TAsset> writeBlocks)
        where TAsset : Asset, IBinarySerializable<TAsset>
    {
        using var copy = (TAsset)asset.Duplicate();
        using var writer = new Utf8JsonWriter(destination, new() { Indented = true });
        Write(writer, copy, writeBlocks);
        writer.Flush();
    }

    /// <summary>Writes explicitly selected typed blocks. Other blocks can keep their own storage and codec.</summary>
    public static async ValueTask<AssetEntry> SaveAsync<TAsset>(string path, TAsset asset,
        Action<BlockWriter, TAsset> writeBlocks, CancellationToken cancellationToken = default)
        where TAsset : Asset, IBinarySerializable<TAsset>
    {
        ArgumentNullException.ThrowIfNull(writeBlocks);
        using var copy = (TAsset)asset.Duplicate();
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return await Asset.WriteFileAsync(path, async (stream, token) =>
        {
            var selected = new List<BlockFiles>();
            Exception? failure = null;
            try
            {
                using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
                Write(writer, copy, writeBlocks, selected);
                await writer.FlushAsync(token).ConfigureAwait(false);
                foreach (BlockFiles content in selected)
                    await CheckFilesAsync(content, directory, token).ConfigureAwait(false);
                return new AssetEntry(copy.Id, copy.AssetType, TAsset.SchemaFingerprint, Guid.Empty)
                    { Name = copy.Name, Path = Path.GetFullPath(path) };
            }
            catch (Exception error) { failure = error; throw; }
            finally { ReleaseFiles(selected, failure); }
        }, async (destination, token) =>
        {
            AssetEntry existing = Describe(await File.ReadAllBytesAsync(destination, token).ConfigureAwait(false));
            if (existing.AssetGuid != copy.Id || existing.AssetType != copy.AssetType)
                throw new InvalidDataException("The destination source belongs to another asset identity or type.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void Write<TAsset>(Utf8JsonWriter writer, TAsset asset, Action<BlockWriter, TAsset> writeBlocks,
        List<BlockFiles>? selected = null)
        where TAsset : Asset, IBinarySerializable<TAsset>
    {
        writer.WriteStartObject();
        writer.WriteString("Format", BlockFormat);
        writer.WriteString("TypeId", TAsset.TypeId);
        writer.WriteString("Schema", TAsset.SchemaFingerprint.ToString("X16", CultureInfo.InvariantCulture));
        writer.WriteStartObject("Fields");
        writer.WriteString("Id", asset.Id);
        writer.WriteString("Name", asset.Name);
        writer.WriteString("AssetType", asset.AssetType);
        writer.WriteEndObject();
        writer.WriteStartArray("Blocks");
        writeBlocks(new(writer, selected), asset);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public sealed class BlockWriter
    {
        private readonly Utf8JsonWriter _writer;
        private readonly HashSet<Guid> _types = [];
        private readonly List<BlockFiles>? _selected;
        internal BlockWriter(Utf8JsonWriter writer, List<BlockFiles>? selected = null)
        {
            _writer = writer;
            _selected = selected;
        }

        public void WriteBlock<TBlock>(TBlock block, IReadOnlyDictionary<ulong, string>? chunkFiles = null)
            where TBlock : AssetPageBlock, IAssetBlock<TBlock>, IBinarySerializable<TBlock>, ITextSerializable<TBlock>
        {
            if (block.BlockId != TBlock.BlockTypeId || !_types.Add(TBlock.BlockTypeId))
                throw new InvalidDataException("The source repeats a block type or the block has a different persistent identity.");
            TBlock? copy = (TBlock)block.Duplicate();
            try
            {
                KeyValuePair<ulong, string>[] files = chunkFiles?.OrderBy(pair => pair.Key).ToArray() ?? [];
                var keys = files.Select(static file => file.Key).ToHashSet();
                foreach (ulong key in copy.ContentKeys)
                    if (!keys.Contains(key))
                        throw new InvalidDataException($"Block chunk {key:X16} needs a source file reference.");
                _writer.WriteStartObject();
                _writer.WriteString("TypeId", TBlock.BlockTypeId);
                _writer.WriteString("Schema", TBlock.SchemaFingerprint.ToString("X16", CultureInfo.InvariantCulture));
                _writer.WritePropertyName("Fields");
                TBlock.WriteText(_writer, copy);
                _writer.WriteStartObject("Chunks");
                foreach (var chunk in files)
                {
                    CheckRelativePath(chunk.Value);
                    _writer.WriteString(chunk.Key.ToString("X16", CultureInfo.InvariantCulture), chunk.Value.Replace('\\', '/'));
                }
                _writer.WriteEndObject();
                _writer.WriteEndObject();
                if (_selected is not null)
                {
                    _selected.Add(new(copy, files));
                    copy = null;
                }
            }
            finally { copy?.Dispose(); }
        }
    }

    internal sealed record BlockFiles(AssetPageBlock Content, KeyValuePair<ulong, string>[] Files);

    private static void ReleaseFiles(List<BlockFiles> files, Exception? failure)
    {
        List<Exception>? errors = null;
        foreach (BlockFiles file in files)
            try { file.Content.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is null) return;
        if (failure is not null) errors.Insert(0, failure);
        if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
        throw new AggregateException(errors);
    }

    private static async ValueTask CheckFilesAsync(BlockFiles files, string directory, CancellationToken cancellationToken)
    {
        if (files.Files.Length == 0) return;
        using AssetBlockSource? source = files.Content.RetainSource();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var file in files.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var external = FileRangeSource.Open(Path.Combine(directory, file.Value));
                long length;
                Digest256 expected;
                if (files.Content.Chunks.Contains(file.Key))
                {
                    using PageRange<byte> bytes = files.Content.Chunks.Retain(file.Key);
                    length = bytes.Sequence.Length;
                    using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    foreach (ReadOnlyMemory<byte> segment in bytes.Sequence)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        contentHash.AppendData(segment.Span);
                    }
                    expected = Digest256.Finish(contentHash);
                }
                else if (source is not null && source.Description.ChunkKeys.Contains(file.Key))
                {
                    BinaryChunkEntry chunk = await source.DescribeChunkAsync(file.Key, cancellationToken).ConfigureAwait(false);
                    length = chunk.DecodedLength;
                    expected = chunk.ContentHash;
                }
                else
                {
                    // A newly declared external payload is itself the source of the bytes.
                    // There is no previously supplied or persisted content to compare.
                    continue;
                }
                if (external.Length != length)
                    throw new InvalidDataException($"External payload '{file.Value}' differs from block chunk {file.Key:X16}.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long offset = 0;
                while (offset < length)
                {
                    int count = (int)Math.Min(64 * 1024, length - offset);
                    await external.ReadExactlyAsync(offset, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer.AsSpan(0, count));
                    offset += count;
                }
                if (!expected.FixedTimeEquals(Digest256.Finish(hash)))
                    throw new InvalidDataException($"External payload '{file.Value}' differs from block chunk {file.Key:X16}.");
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public sealed class SourceReader : IDisposable
    {
        private readonly JsonDocument _document;
        private readonly string _directory;
        private readonly Dictionary<Guid, JsonElement> _blocks = [];
        public AssetEntry Description { get; }
        public Guid AssetTypeId { get; }

        internal SourceReader(JsonDocument document, string directory)
        {
            _document = document;
            _directory = directory;
            JsonElement root = document.RootElement;
            if (root.GetProperty("Format").GetString() != BlockFormat)
                throw new InvalidDataException("The source does not contain independently encoded blocks.");
            Description = Describe(root);
            AssetTypeId = root.GetProperty("TypeId").GetGuid();
            foreach (JsonElement block in root.GetProperty("Blocks").EnumerateArray())
                _blocks.Add(block.GetProperty("TypeId").GetGuid(), block);
        }

        public void RequireAsset<TAsset>(string assetType) where TAsset : Asset, IBinarySerializable<TAsset>
        {
            if (AssetTypeId != TAsset.TypeId || Description.SchemaFingerprint != TAsset.SchemaFingerprint
                || Description.AssetType != assetType)
                throw new InvalidDataException("The source identity type or root schema differs from the requested asset.");
        }

        public bool ContainsBlock<TBlock>() where TBlock : AssetBlock, IAssetBlock<TBlock>
            => _blocks.ContainsKey(TBlock.BlockTypeId);

        public async ValueTask ReadBlockIntoAsync<TBlock>(TBlock target, CancellationToken cancellationToken = default)
            where TBlock : AssetPageBlock, IAssetBlock<TBlock>, IBinarySerializable<TBlock>, ITextSerializable<TBlock>, ITextObjectReader<TBlock>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_blocks.TryGetValue(TBlock.BlockTypeId, out JsonElement block))
                throw new KeyNotFoundException($"Source block {TBlock.BlockTypeId} is absent.");
            if (target.BlockId != TBlock.BlockTypeId || ParseKey(block.GetProperty("Schema").GetString()!) != TBlock.SchemaFingerprint)
                throw new InvalidDataException("The source block schema differs from the requested codec.");
            TBlock.ReadTextInto(block.GetProperty("Fields"), target);
            foreach (ulong key in target.Chunks.Keys.ToArray()) target.Chunks.Remove(key);
            await ReadChunkFilesAsync(block.GetProperty("Chunks"), _directory, target.Chunks, target.Pages, cancellationToken).ConfigureAwait(false);
        }

        public void Dispose() => _document.Dispose();
    }

    private static async ValueTask ReadChunkFilesAsync(JsonElement chunks, string directory, PageBufferMap destination,
        PageArena pages, CancellationToken cancellationToken)
    {
        var sources = new List<(ulong Key, FileRangeSource Source)>();
        try
        {
            long required = 0;
            foreach (JsonProperty chunk in chunks.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong key = ParseKey(chunk.Name);
                var source = FileRangeSource.Open(Path.Combine(directory, ReadRelativePath(chunk.Value)));
                sources.Add((key, source));
                required = checked(required + pages.GetBufferCapacity(source.Length));
            }
            if (required > pages.Residency.Budget(ResidencyClass.DecodedCpu))
                throw new InvalidDataException("The source payloads exceed the decoded memory budget.");
            pages.Residency.RequestReclaim(ResidencyClass.DecodedCpu, required);
            if (required > pages.Residency.Available(ResidencyClass.DecodedCpu))
                throw new InvalidDataException("The source payloads and current page usage exceed the decoded memory budget.");
            foreach (var item in sources)
            {
                using PageBuffer bytes = await PageBuffer.ReadAsync(item.Source, 0, item.Source.Length, pages, cancellationToken).ConfigureAwait(false);
                destination.Set(item.Key, bytes);
            }
        }
        finally
        {
            foreach (var item in sources) await item.Source.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads identity and type without installing or constructing the business type.</summary>
    public static AssetEntry Describe(ReadOnlyMemory<byte> source)
    {
        using var document = JsonDocument.Parse(source, ReadOptions);
        return Describe(document.RootElement);
    }

    private static AssetEntry Describe(JsonElement root)
    {
        if (root.GetProperty("Format").GetString() != BlockFormat)
            throw new InvalidDataException("Unsupported asset source format.");
        CheckFields(root, ["Format", "TypeId", "Schema", "Fields", "Blocks"]);
        if (root.GetProperty("TypeId").GetGuid() == Guid.Empty)
            throw new InvalidDataException("The source needs a type identity.");
        ulong schema = ParseKey(root.GetProperty("Schema").GetString()!);
        JsonElement fields = root.GetProperty("Fields");
        CheckFields(fields, ["Id", "Name", "AssetType"]);
        if (!fields.TryGetProperty("Id", out _) || !fields.TryGetProperty("Name", out _) || !fields.TryGetProperty("AssetType", out _))
            throw new JsonException("The source must contain Id, Name and AssetType.");
        Guid id = fields.GetProperty("Id").GetGuid();
        string? type = fields.GetProperty("AssetType").GetString();
        string? name = fields.GetProperty("Name").GetString();
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(type) || name is null)
            throw new InvalidDataException("The source needs an ID, asset type and name.");
        var types = new HashSet<Guid>();
        foreach (JsonElement block in root.GetProperty("Blocks").EnumerateArray())
        {
            CheckFields(block, ["TypeId", "Schema", "Fields", "Chunks"]);
            Guid blockType = block.GetProperty("TypeId").GetGuid();
            if (blockType == Guid.Empty || !types.Add(blockType)) throw new JsonException("The source repeats a block type or uses an empty type identity.");
            _ = ParseKey(block.GetProperty("Schema").GetString()!);
            CheckFields(block.GetProperty("Fields"));
            CheckChunks(block.GetProperty("Chunks"));
        }
        return new(id, type, schema, Guid.Empty) { Name = name };
    }

    private static void CheckChunks(JsonElement chunks)
    {
        CheckFields(chunks);
        var keys = new HashSet<ulong>();
        foreach (var chunk in chunks.EnumerateObject())
        {
            if (!keys.Add(ParseKey(chunk.Name))) throw new JsonException("Duplicate chunk key.");
            ReadRelativePath(chunk.Value);
        }
    }

    private static void CheckFields(JsonElement value, string[]? expected = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Expected an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (!names.Add(field.Name) || expected is not null && !expected.Contains(field.Name, StringComparer.Ordinal))
                throw new JsonException("Duplicate or unknown field: " + field.Name);
        if (expected is not null && names.Count != expected.Length) throw new JsonException("Missing source description fields.");
    }

    private static ulong ParseKey(string text) => text.Length == 16
        && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value)
        ? value : throw new JsonException("Expected a 16-digit hexadecimal value.");

    private static string ReadRelativePath(JsonElement value)
    {
        string path = value.GetString() ?? throw new JsonException("A chunk needs a file path.");
        CheckRelativePath(path);
        return path;
    }

    private static void CheckRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new InvalidDataException("Chunk source paths must be relative to the asset source.");
    }
}
