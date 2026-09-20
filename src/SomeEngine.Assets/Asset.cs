using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

/// <summary>Common file access and save flow. Concrete fields belong to the generated asset type.</summary>
public abstract class Asset<T> : IDisposable, IAsyncDisposable where T : class
{
    protected internal static readonly ulong ByteSchema = BinaryFieldKey.FromName("SomeEngine.Assets.RawBytes.v1");
    private BinaryDocument? _source;
    private int _disposed;
    private bool _writingBytes;
    private string _name = "";
    protected readonly object Access = new();

    protected Asset(AssetId<T> id = default) => Id = id.IsEmpty ? new(Guid.NewGuid()) : id;

    protected Asset(Asset<T> source, AssetId<T> id = default)
    {
        lock (source.Access)
        {
            source.RequireIdle();
            Id = id.IsEmpty ? source.Id : id;
            _name = source._name;
            SourceVersion = source.SourceVersion;
            SourcePath = source.SourcePath;
            _source = source._source?.Retain();
        }
    }

    public AssetId<T> Id { get; }
    public string Name
    {
        get { RequireOpen(); return _name; }
        set { lock (Access) { RequireIdle(); _name = value; } }
    }
    public Guid SourceVersion { get; private set; }
    public string SourcePath { get; private set; } = "";
    internal long RootByteLength => _source?.RootByteLength ?? 0;
    protected BinaryDocument? Source { get { RequireOpen(); return _source; } }

    protected void RequireOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    protected void RequireIdle()
    {
        RequireOpen();
        if (_writingBytes) throw new InvalidOperationException("Finish the byte edit before retaining, saving, or loading the asset.");
    }

    protected void AttachSource(BinaryDocument document)
    {
        _source = document.Retain();
        SourceVersion = document.Generation;
    }

    public BinaryDocument? RetainSource()
    {
        lock (Access)
        {
            RequireIdle();
            return _source?.Retain();
        }
    }

    protected void SetSourcePath(string path) => SourcePath = path;

    protected static IReadOnlyList<Guid> CollectDependencies(IEnumerable<Guid> references)
        => references.Where(static id => id != Guid.Empty).Distinct().Order().ToArray();

    protected AssetEntry Describe(string fileType, ulong schemaHash, IEnumerable<Guid> references)
        => new(Id.Value, fileType, schemaHash, SourceVersion)
        {
            Name = Name, Path = SourcePath, Dependencies = CollectDependencies(references),
        };

    protected static async ValueTask<BinaryDocument> OpenFileAsync(string path, string pathSuffix,
        BinaryWireTypeDescriptor wireType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset path '{path}' must end with '{pathSuffix}'.");
        cancellationToken.ThrowIfCancellationRequested();
        return await BinaryDocument.OpenAsync(FileRangeSource.Open(Path.GetFullPath(path)), wireType,
            ownsSource: true, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    protected async ValueTask<AssetEntry> SaveAsync(BinaryDocumentWriter writer, string path,
        string pathSuffix, AssetEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset path '{path}' must end with '{pathSuffix}'.");
        cancellationToken.ThrowIfCancellationRequested();
        RequireIdle();
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Guid version;
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await writer.WriteAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                version = await BinaryDocument.ReadVersionAsync(stream, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
            else File.Move(temporaryPath, fullPath);
            return entry with { Path = fullPath, Version = version };
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    protected ByteEdit BeginByteEdit(Memory<byte> bytes)
    {
        lock (Access)
        {
            RequireIdle();
            _writingBytes = true;
            return new(this, bytes);
        }
    }

    public sealed class ByteEdit : IDisposable
    {
        private Asset<T>? _asset;
        private readonly Memory<byte> _bytes;
        internal ByteEdit(Asset<T> asset, Memory<byte> bytes) { _asset = asset; _bytes = bytes; }
        public Span<byte> Span
        {
            get { ObjectDisposedException.ThrowIf(_asset is null, this); return _bytes.Span; }
        }
        public void Dispose()
        {
            Asset<T>? asset = Interlocked.Exchange(ref _asset, null);
            if (asset is not null) lock (asset.Access) asset._writingBytes = false;
        }
    }

    // Disposal may be called through an erased cache entry. Each generated override directly
    // releases its concrete storage; there is no field-operation registry.
    protected abstract void ReleaseFields();

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        BinaryDocument? source;
        lock (Access)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            source = Interlocked.Exchange(ref _source, null);
        }
        try { ReleaseFields(); }
        finally { if (source is not null) await source.DisposeAsync().ConfigureAwait(false); }
    }
}
