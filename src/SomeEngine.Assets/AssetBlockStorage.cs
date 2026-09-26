using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

/// <summary>Persistent type identity and the ranges belonging to one block, without decoding its content.</summary>
[BinarySerializable, BinaryValue]
[BinaryFields(nameof(BlockTypeId), nameof(EncodingTypeId), nameof(SchemaHash), nameof(Epoch), nameof(EncodingCompatibility), nameof(MetadataKey), nameof(ChunkKeys), nameof(References))]
public sealed partial record AssetBlockDescription
{
    public Guid BlockTypeId { get; init; }
    public Guid EncodingTypeId { get; init; }
    public ulong SchemaHash { get; init; }
    public uint Epoch { get; init; }
    public BinaryCompatibility EncodingCompatibility { get; init; }
    public ulong MetadataKey { get; init; }
    public ImmutableArray<ulong> ChunkKeys { get; init; } = [];
    public ImmutableArray<Guid> References { get; init; } = [];

    public ulong GetStorageKey(ulong localKey)
    {
        if (!ChunkKeys.Contains(localKey)) throw new KeyNotFoundException($"Chunk {localKey:X16} is not part of this block.");
        return AssetBlockWriter.StorageKey(BlockTypeId, localKey);
    }
}

/// <summary>Retained document access for a block codec; all reads use the document's existing scheduler.</summary>
public sealed class AssetBlockSource : IDisposable
{
    private readonly AssetFile _file;
    public AssetBlockDescription Description { get; }
    public PageArena Pages => _file.Pages;

    public AssetBlockSource(AssetFile file, AssetBlockDescription description)
    {
        _file = file.Retain();
        Description = description;
    }

    public AssetBlockSource Retain() => new(_file, Description);

    public async ValueTask ReadIntoAsync<T>(T target, CancellationToken cancellationToken = default)
        where T : class, IBinarySerializable<T>, IBinaryObjectReader<T>
    {
        if (Description.EncodingTypeId != T.TypeId || Description.SchemaHash != T.SchemaFingerprint
            || Description.Epoch != T.SchemaEpoch || Description.EncodingCompatibility != T.Compatibility)
            throw new InvalidDataException("The block codec differs from the persisted type or schema.");
        using PageBuffer bytes = await AcquireMetadataAsync(cancellationToken).ConfigureAwait(false);
        using PageRange<byte> read = bytes.Retain();
        BinarySerializer.DeserializeInto(read.Sequence, target);
    }

    /// <summary>Reads metadata bytes for extension-defined text, binary or external-reference codecs.</summary>
    public ValueTask<PageBuffer> AcquireMetadataAsync(CancellationToken cancellationToken = default)
        => _file.AcquireBufferAsync(Description.MetadataKey, cancellationToken: cancellationToken);

    /// <summary>Maps a block-local key into the shared document. It never selects a business type.</summary>
    public ulong GetStorageKey(ulong key)
    {
        RequireChunk(key);
        return Description.GetStorageKey(key);
    }

    public ValueTask<PageBuffer> AcquireBufferAsync(ulong key, CancellationToken cancellationToken = default)
    {
        RequireChunk(key);
        return _file.AcquireBufferAsync(GetStorageKey(key), cancellationToken: cancellationToken);
    }

    public ValueTask<IRangeSource> OpenChunkRangeSourceAsync(ulong key, CancellationToken cancellationToken = default)
    {
        RequireChunk(key);
        return _file.OpenChunkRangeSourceAsync(GetStorageKey(key), cancellationToken);
    }

    public ValueTask<BinaryChunkEntry> DescribeChunkAsync(ulong localKey, CancellationToken cancellationToken = default)
        => _file.DescribeChunkAsync(GetStorageKey(localKey), cancellationToken);

    internal ValueTask CopyChunkAsync(BinaryDocumentWriter writer, ulong key, ulong destinationKey, CancellationToken cancellationToken, uint? ordinal = null)
        => _file.AddChunkAsync(writer, destinationKey, cancellationToken, ordinal, sourceKey: GetStorageKey(key));

    private void RequireChunk(ulong key)
    {
        if (!Description.ChunkKeys.Contains(key)) throw new KeyNotFoundException($"Chunk {key:X16} is not part of this block.");
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>Feeds a block's metadata and retained pages into the existing binary document writer.</summary>
public sealed class AssetBlockWriter : IDisposable
{
    private readonly BinaryDocumentWriter _writer;
    private readonly PageArena _pages;
    private readonly List<IDisposable> _reads = [];
    private readonly List<ulong> _chunkKeys = [];
    private AssetBlockDescription? _description;

    internal AssetBlockWriter(BinaryDocumentWriter writer, PageArena pages) { _writer = writer; _pages = pages; }

    public void WriteMetadata<T>(Guid blockTypeId, T value) where T : IBinarySerializable<T>
    {
        using var bytes = new PageBufferWriter(_pages);
        BinarySerializer.Write(bytes, value);
        using PageBuffer buffer = bytes.Duplicate();
        WriteMetadata(blockTypeId, new(T.TypeId, T.SchemaFingerprint, T.Compatibility, T.SchemaEpoch), buffer);
    }

    /// <summary>Writes metadata in the extension's chosen encoding without imposing binary fields.</summary>
    public void WriteMetadata(Guid blockTypeId, BinaryWireTypeDescriptor encoding, PageBuffer metadata)
    {
        if (_description is not null) throw new InvalidOperationException("Block metadata was already written.");
        if (blockTypeId == Guid.Empty) throw new ArgumentException("A block requires a persistent type identity.", nameof(blockTypeId));
        ulong key = BinaryFieldKey.FromName($"SomeEngine.Assets.Block.{blockTypeId:N}.Metadata");
        PageRange<byte> read = metadata.Retain();
        _reads.Add(read);
        _writer.AddContract(encoding).AddChunk(key, read.Sequence, encoding.SchemaFingerprint);
        _description = new() { BlockTypeId = blockTypeId, EncodingTypeId = encoding.TypeId, SchemaHash = encoding.SchemaFingerprint,
            EncodingCompatibility = encoding.Compatibility, Epoch = encoding.SchemaEpoch, MetadataKey = key };
    }

    internal static ulong StorageKey(Guid blockTypeId, ulong localKey)
        => BinaryFieldKey.FromName($"SomeEngine.Assets.Block.{blockTypeId:N}.Chunk.{localKey:X16}");

    public void WriteChunk(ulong key, PageBufferMap chunks, ChunkCompression compression = ChunkCompression.None, int alignment = 16,
        ulong? sourceKey = null, uint ordinal = 0)
    {
        PageRange<byte> read = chunks.Retain(sourceKey ?? key);
        _reads.Add(read);
        AssetBlockDescription description = _description ?? throw new InvalidOperationException("Write the block metadata before its chunks.");
        _writer.AddChunk(StorageKey(description.BlockTypeId, key), read.Sequence, BinaryFieldKey.FromName("SomeEngine.Assets.RawBytes.v1"), compression, alignment, ordinal);
        _chunkKeys.Add(key);
    }

    public async ValueTask CopyChunkAsync(AssetBlockSource source, ulong key, CancellationToken cancellationToken = default,
        ulong? destinationKey = null, uint? ordinal = null)
    {
        AssetBlockSource read = source.Retain();
        _reads.Add(read);
        AssetBlockDescription description = _description ?? throw new InvalidOperationException("Write the block metadata before its chunks.");
        ulong target = destinationKey ?? key;
        await read.CopyChunkAsync(_writer, key, StorageKey(description.BlockTypeId, target), cancellationToken, ordinal).ConfigureAwait(false);
        _chunkKeys.Add(target);
    }

    internal AssetBlockDescription Complete(AssetBlock block)
    {
        if (_description is null || _description.BlockTypeId != block.BlockId)
            throw new InvalidDataException("The block did not write metadata for its declared persistent type.");
        return _description with { ChunkKeys = _chunkKeys.Order().ToImmutableArray(),
            References = block.References().Where(static id => id != Guid.Empty).Distinct().Order().ToImmutableArray() };
    }

    public void Dispose()
    {
        foreach (IDisposable read in _reads) read.Dispose();
        _reads.Clear();
    }
}
