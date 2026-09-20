using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Assets;

/// <summary>One file access, sharing its document and existing chunk scheduler with other accesses.</summary>
public sealed class AssetFile : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SharedData<FileContent> _content;

    internal AssetFile(BinaryDocument document, string path, PageArena pages, long decodedBudgetBytes)
        => _content = new(new FileContent(document, path, pages, decodedBudgetBytes));
    private AssetFile(SharedData<FileContent> content) => _content = content;

    public string Path { get { lock (_gate) return _content.Read.Path; } }
    public Guid Version { get { lock (_gate) return _content.Read.Document.Generation; } }
    public BinaryWireTypeDescriptor RootType { get { lock (_gate) return _content.Read.Document.RootType; } }
    public long RootByteLength { get { lock (_gate) return _content.Read.Document.RootByteLength; } }
    internal uint ChunkCount { get { lock (_gate) return _content.Read.Document.ChunkCount; } }
    public PageArena Pages { get { lock (_gate) return _content.Read.Pages; } }
    public ChunkStreamingMetrics StreamingMetrics { get { lock (_gate) return _content.Read.Chunks.Metrics; } }

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
        PageArena? pages = null, BinaryReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        pages ??= PageArena.Shared;
        BinaryDocument document = await BinaryDocument.OpenAsync(FileRangeSource.Open(fullPath), type,
            ownsSource: true, limits, cancellationToken).ConfigureAwait(false);
        try { return new(document, fullPath, pages, pages.Residency.Budget(ResidencyClass.DecodedCpu)); }
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

    public async ValueTask<PageBuffer> AcquireBufferAsync(ulong key, ChunkRequestOptions options = default,
        CancellationToken cancellationToken = default)
    {
        await using AssetFile access = Retain();
        using var acquired = await access._content.Read.Chunks.AcquireAsync(key, options, cancellationToken).ConfigureAwait(false);
        return acquired.DuplicatePages();
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

    public async ValueTask<PageRange<byte>> AcquireRangeAsync(ulong key, long offset = 0, long? length = null,
        ChunkRequestOptions options = default, CancellationToken cancellationToken = default)
    {
        await using AssetFile access = Retain();
        using var acquired = await access._content.Read.Chunks.AcquireAsync(key, options, cancellationToken).ConfigureAwait(false);
        return acquired.RetainPages(offset, length);
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

    private sealed class FileContent : IAsyncDisposable
    {
        internal readonly BinaryDocument Document;
        internal readonly ChunkRequestScheduler Chunks;
        internal readonly string Path;
        internal readonly PageArena Pages;
        internal FileContent(BinaryDocument document, string path, PageArena pages, long decodedBudgetBytes)
        {
            Document = document; Path = path; Pages = pages;
            Chunks = ChunkRequestScheduler.CreateForDocument(document, pages, decodedBudgetBytes);
        }
        public async ValueTask DisposeAsync()
        {
            try { await Chunks.DisposeAsync().ConfigureAwait(false); }
            finally { await Document.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
