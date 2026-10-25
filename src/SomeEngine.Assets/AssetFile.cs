using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;
using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Assets;

/// <summary>One file access. Reads produce independent managed arrays and never retain decoded results.</summary>
public sealed class AssetFile : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SharedData<FileContent> _content;

    internal AssetFile(BinaryDocument document, string path, long maxDecodedBytes = 256L * 1024 * 1024,
        AssetByteCache? byteCache = null)
        => _content = new(new FileContent(document, path, maxDecodedBytes, byteCache ?? AssetByteCache.Shared));
    private AssetFile(SharedData<FileContent> content) => _content = content;

    public string Path { get { lock (_gate) return _content.Read.Path; } }
    public Guid Version { get { lock (_gate) return _content.Read.Document.Generation; } }
    public BinaryWireTypeDescriptor RootType { get { lock (_gate) return _content.Read.Document.RootType; } }
    public long RootByteLength { get { lock (_gate) return _content.Read.Document.RootByteLength; } }
    internal AssetByteCache ByteCache { get { lock (_gate) return _content.Read.ByteCache; } }
    internal Guid ByteSource { get { lock (_gate) return _content.Read.ByteSource; } }
    internal uint ChunkCount { get { lock (_gate) return _content.Read.Document.ChunkCount; } }

    /// <summary>Reads the validated root without I/O. The caller keeps this file access open while using the reader.</summary>
    public BinaryDataReader CreateRootReader()
    {
        lock (_gate) return _content.Read.Document.CreateRootReader();
    }

    public AssetFile Retain()
    {
        lock (_gate) return new(_content.Retain());
    }

    public static async ValueTask<AssetFile> OpenAsync(string path, BinaryWireTypeDescriptor type,
        BinaryReadLimits? limits = null, CancellationToken cancellationToken = default, AssetByteCache? byteCache = null)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        BinaryDocument document = await BinaryDocument.OpenAsync(FileRangeSource.Open(fullPath), type,
            ownsSource: true, limits, cancellationToken).ConfigureAwait(false);
        try { return new(document, fullPath, byteCache: byteCache); }
        catch { await document.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Reads common identity fields without requiring the business type's decoder.</summary>
    public AssetEntry Describe()
    {
        using AssetFile access = Retain();
        FileContent content = access._content.Read;
        var reader = content.Document.CreateRootReader();
        Guid id = reader.ReadGuid();
        string name = reader.ReadString() ?? throw new InvalidDataException("An asset name cannot be null.");
        string type = reader.ReadString() ?? throw new InvalidDataException("An asset type name cannot be null.");
        Guid[] dependencies = [];
        if (content.Document.RootType == AssetHeader.WireType)
        {
            var header = new AssetHeader();
            content.Document.ReadRootInto(header);
            dependencies = header.BlockDescriptions.SelectMany(static block => block.References)
                .Where(static reference => reference != Guid.Empty).Distinct().Order().ToArray();
        }
        return new(id, type, content.Document.RootType.SchemaFingerprint, content.Document.Generation)
        { Name = name, Path = content.Path, Dependencies = dependencies };
    }

    public void ReadRootInto<T>(T target) where T : class, IBinarySerializable<T>, IBinaryObjectReader<T>
    {
        using AssetFile access = Retain();
        access._content.Read.Document.ReadRootInto(target);
    }

    public async ValueTask<byte[]> ReadBytesAsync(ulong key, CancellationToken cancellationToken = default)
    {
        Scheduler scheduler = JobSystem.Current;
        await using AssetFile access = Retain();
        FileContent content = access._content.Read;
        BinaryChunkEntry chunk = await content.Document.FindChunkAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Chunk {key:X16} is absent.");
        if (chunk.DecodedLength > content.MaxDecodedBytes)
            throw new InvalidDataException("The requested data exceeds the configured read limit.");
        if (chunk.Compression == ChunkCompression.Brotli)
        {
            BinaryChunkInput input = await content.Document.ReadChunkInputAsync(
                new BinaryChunkRef(key, chunk.DecodedLength), cancellationToken).ConfigureAwait(false);
            byte[] decoded = GC.AllocateUninitializedArray<byte>(checked((int)chunk.DecodedLength));
            JobHandle decode = await scheduler.ScheduleAsync(new DecodeChunk(input, decoded, cancellationToken),
                new JobScheduleOptions(JobPriority.Low) { Category = AssetDecodeJobs.GetCategory(scheduler) },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await decode;
            return decoded;
        }
        Memory<byte> bytes = await content.Document.CopyChunkAsync(new BinaryChunkRef(key, chunk.DecodedLength),
            cancellationToken).ConfigureAwait(false);
        MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)bytes, out ArraySegment<byte> array);
        return array.Array!;
    }

    private readonly struct DecodeChunk(BinaryChunkInput input, byte[] destination, CancellationToken cancellationToken) : IJob
    {
        public void Execute() => input.DecodeInto(destination, cancellationToken);
    }

    public async ValueTask<BinaryChunkEntry> DescribeChunkAsync(ulong key, CancellationToken cancellationToken = default)
    {
        using AssetFile access = Retain();
        return await access._content.Read.Document.FindChunkAsync(key, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Chunk {key:X16} is absent.");
    }

    // The mesh source retains this file access for the entire lifetime of the bounded range.
    internal async ValueTask<IRangeSource> OpenChunkRangeSourceAsync(ulong key, CancellationToken cancellationToken)
    {
        using AssetFile access = Retain();
        return await access._content.Read.Document.OpenChunkRangeSourceAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AddChunkAsync(BinaryDocumentWriter writer, ulong key,
        CancellationToken cancellationToken = default, uint? ordinal = null, ulong? sourceKey = null)
    {
        // The caller keeps this file access until the writer completes, because AddChunkAsync
        // borrows the original document for its later bounded copy.
        FileContent content;
        lock (_gate) content = _content.Read;
        await writer.AddChunkAsync(key, content.Document, cancellationToken, sourceKey, ordinal).ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync()
    {
        FileContent? content;
        lock (_gate)
            if (!_content.Release(out content)) return ValueTask.CompletedTask;
        return content.DisposeAsync();
    }

    private sealed class FileContent(BinaryDocument document, string path, long maxDecodedBytes, AssetByteCache byteCache) : IAsyncDisposable
    {
        internal readonly BinaryDocument Document = document;
        internal readonly string Path = path;
        internal readonly long MaxDecodedBytes = maxDecodedBytes;
        internal readonly AssetByteCache ByteCache = byteCache;
        internal readonly Guid ByteSource = Guid.NewGuid();
        public ValueTask DisposeAsync() => Document.DisposeAsync();
    }
}
