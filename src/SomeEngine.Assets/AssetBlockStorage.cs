using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using System.Collections.Frozen;

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

/// <summary>One block's stored chunk descriptions and access to its selected file version.</summary>
public sealed class AssetBlockSource : IDisposable
{
    private readonly AssetFile _file;
    private readonly FrozenSet<ulong> _chunkKeys;
    private readonly AssetByteCache _byteCache;
    private readonly Guid _version;
    private readonly Guid _byteSource;
    private int _disposed;
    public AssetBlockDescription Description { get; }
    public Guid Version => _version;
    internal AssetByteCache ByteCache => _byteCache;

    public AssetBlockSource(AssetFile file, AssetBlockDescription description)
    {
        _file = file.Retain();
        Description = description;
        _chunkKeys = description.ChunkKeys.ToFrozenSet();
        _byteCache = file.ByteCache;
        _version = file.Version;
        _byteSource = file.ByteSource;
    }
    private AssetBlockSource(AssetBlockSource source)
    {
        _file = source._file.Retain();
        Description = source.Description;
        _chunkKeys = source._chunkKeys;
        _byteCache = source._byteCache;
        _version = source._version;
        _byteSource = source._byteSource;
    }
    public AssetBlockSource Retain() => new(this);

    public async ValueTask ReadIntoAsync<T>(T target, CancellationToken cancellationToken = default)
        where T : class, IBinarySerializable<T>, IBinaryObjectReader<T>
    {
        if (Description.EncodingTypeId != T.TypeId || Description.SchemaHash != T.SchemaFingerprint
            || Description.Epoch != T.SchemaEpoch || Description.EncodingCompatibility != T.Compatibility)
            throw new InvalidDataException("The block codec differs from the persisted type or schema.");
        ReadOnlyMemory<byte> bytes = await ReadMetadataAsync(cancellationToken).ConfigureAwait(false);
        BinarySerializer.DeserializeInto(bytes.Span, target);
    }
    public async ValueTask<ReadOnlyMemory<byte>> ReadMetadataAsync(CancellationToken cancellationToken = default)
        => await _file.ReadBytesAsync(Description.MetadataKey, cancellationToken).ConfigureAwait(false);

    public ulong GetStorageKey(ulong key)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_chunkKeys.Contains(key)) throw new KeyNotFoundException($"Chunk {key:X16} is not part of this block.");
        return AssetBlockWriter.StorageKey(Description.BlockTypeId, key);
    }

    internal AssetByteKey GetByteKey(ulong key, long offset = -1, int length = -1)
        => new(_byteSource, GetStorageKey(key), offset, length);

    public ValueTask<ReadOnlyMemory<byte>> ReadBytesAsync(ulong key, CancellationToken cancellationToken = default)
    {
        AssetByteKey dataKey = GetByteKey(key);
        return _byteCache.ReadAsync(dataKey, (File: _file, Key: dataKey.ChunkKey), static async input =>
        {
            await using AssetFile file = input.File.Retain();
            return await file.ReadBytesAsync(input.Key).ConfigureAwait(false);
        }, cancellationToken);
    }
    public ValueTask<IRangeSource> OpenChunkRangeSourceAsync(ulong key, CancellationToken cancellationToken = default)
        => _file.OpenChunkRangeSourceAsync(GetStorageKey(key), cancellationToken);
    public ValueTask<BinaryChunkEntry> DescribeChunkAsync(ulong key, CancellationToken cancellationToken = default)
        => _file.DescribeChunkAsync(GetStorageKey(key), cancellationToken);
    internal ValueTask CopyChunkAsync(BinaryDocumentWriter writer, ulong key, ulong destinationKey,
        CancellationToken cancellationToken, uint? ordinal = null)
        => _file.AddChunkAsync(writer, destinationKey, cancellationToken, ordinal, sourceKey: GetStorageKey(key));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _file.Dispose();
    }

}

/// <summary>Feeds a block's metadata and immutable bytes into the existing binary document writer.</summary>
public sealed class AssetBlockWriter : IDisposable
{
    private readonly BinaryDocumentWriter _writer;
    private readonly List<IDisposable> _reads = [];
    private readonly List<ulong> _chunkKeys = [];
    private AssetBlockDescription? _description;

    internal AssetBlockWriter(BinaryDocumentWriter writer) => _writer = writer;

    public void WriteMetadata<T>(Guid blockTypeId, T value) where T : IBinarySerializable<T>
    {
        var bytes = new System.Buffers.ArrayBufferWriter<byte>();
        BinarySerializer.Write(bytes, value);
        WriteMetadata(blockTypeId, new(T.TypeId, T.SchemaFingerprint, T.Compatibility, T.SchemaEpoch), bytes.WrittenMemory);
    }

    /// <summary>Writes metadata in the extension's chosen encoding without imposing binary fields.</summary>
    public void WriteMetadata(Guid blockTypeId, BinaryWireTypeDescriptor encoding, ReadOnlyMemory<byte> metadata)
    {
        if (_description is not null) throw new InvalidOperationException("Block metadata was already written.");
        if (blockTypeId == Guid.Empty) throw new ArgumentException("A block requires a persistent type identity.", nameof(blockTypeId));
        ulong key = BinaryFieldKey.FromName($"SomeEngine.Assets.Block.{blockTypeId:N}.Metadata");
        _writer.AddContract(encoding).AddChunk(key, metadata, encoding.SchemaFingerprint);
        _description = new() { BlockTypeId = blockTypeId, EncodingTypeId = encoding.TypeId, SchemaHash = encoding.SchemaFingerprint,
            EncodingCompatibility = encoding.Compatibility, Epoch = encoding.SchemaEpoch, MetadataKey = key };
    }

    internal static ulong StorageKey(Guid blockTypeId, ulong localKey)
    {
        const string prefix = "SomeEngine.Assets.Block.";
        const string separator = ".Chunk.";
        Span<char> name = stackalloc char[prefix.Length + 32 + separator.Length + 16];
        prefix.AsSpan().CopyTo(name);
        blockTypeId.TryFormat(name[prefix.Length..], out _, "N");
        separator.AsSpan().CopyTo(name[(prefix.Length + 32)..]);
        localKey.TryFormat(name[(prefix.Length + 32 + separator.Length)..], out _, "X16");
        return BinaryFieldKey.FromName(name);
    }

    public void WriteChunk(ulong key, ReadOnlyMemory<byte> bytes, ChunkCompression compression = ChunkCompression.None, int alignment = 16,
        uint ordinal = 0)
    {
        AssetBlockDescription description = _description ?? throw new InvalidOperationException("Write the block metadata before its chunks.");
        _writer.AddChunk(StorageKey(description.BlockTypeId, key), bytes, BinaryFieldKey.FromName("SomeEngine.Assets.RawBytes.v1"), compression, alignment, ordinal);
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
