using System.Runtime.ExceptionServices;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.Streaming;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

/// <summary>Identity, selected file access and common saving for independently declared asset content.</summary>
public abstract partial class Asset : IDisposable, IAsyncDisposable
{
    private AssetFile? _file;
    private bool _disposed;
    private Task? _close;
    private Guid _id;
    private string _name = "";
    private string _assetType = "";
    protected object SyncRoot { get; } = new();

    protected Asset(Guid id = default)
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id;
    }

    protected Asset(Asset source)
    {
        lock (source.SyncRoot)
        {
            source.RequireIdle();
            _id = source._id;
            _name = source._name;
            AssetType = source.AssetType;
            _file = source._file?.Retain();
            CopyBlocksFrom(source);
        }
    }

    public Guid Id
    {
        get { lock (SyncRoot) { RequireIdle(); return _id; } }
        protected set
        {
            lock (SyncRoot)
            {
                RequireIdle();
                if (value == Guid.Empty) throw new InvalidDataException("An asset ID cannot be empty.");
                if (_file is not null && value != _id)
                    throw new InvalidDataException("Content from another identity cannot be decoded into this file access.");
                _id = value;
            }
        }
    }

    public string Name
    {
        get { lock (SyncRoot) { RequireIdle(); return _name; } }
        set { lock (SyncRoot) { RequireIdle(); _name = value ?? throw new ArgumentNullException(nameof(value)); } }
    }

    public string AssetType
    {
        get { lock (SyncRoot) { RequireIdle(); return _assetType; } }
        protected set
        {
            lock (SyncRoot)
            {
                RequireIdle();
                if (_file is not null && value != _assetType)
                    throw new InvalidDataException("Content from another type cannot be decoded into this file access.");
                _assetType = value ?? throw new ArgumentNullException(nameof(value));
            }
        }
    }
    [BinaryIgnore] public Guid SourceVersion { get { lock (SyncRoot) { RequireIdle(); return _file?.Version ?? Guid.Empty; } } }
    [BinaryIgnore] public string SourcePath { get { lock (SyncRoot) { RequireIdle(); return _file?.Path ?? ""; } } }

    protected void RequireOpen() => ObjectDisposedException.ThrowIf(_disposed, this);
    protected void RequireIdle() => RequireOpen();

    protected void AttachFile(AssetFile file)
    {
        AssetEntry entry = file.Describe();
        lock (SyncRoot)
        {
            RequireIdle();
            if (entry.AssetGuid != _id || entry.AssetType != _assetType)
                throw new InvalidDataException("The file identity or type differs from the decoded asset.");
            if (_file is not null) throw new InvalidOperationException("This asset already has a selected file.");
            ValidateFileContent();
            _file = file.Retain();
        }
    }

    protected AssetFile? RetainFile()
    {
        lock (SyncRoot) { RequireIdle(); return _file?.Retain(); }
    }

    public abstract Asset Duplicate();

    public virtual async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using Asset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, ".asset", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shares content with a new independent identity. Source chunks remain available for later saving.</summary>
    public virtual Asset CreateCopy(Guid id = default)
    {
        lock (SyncRoot)
        {
            RequireIdle();
            Guid identity = id == Guid.Empty ? Guid.NewGuid() : id;
            if (identity == _id) throw new ArgumentException("Use Duplicate to keep the same asset identity.", nameof(id));
            Asset copy = Duplicate();
            copy._id = identity;
            return copy;
        }
    }

    protected virtual void ValidateContent() { }
    protected virtual void ValidateFileContent() { }

    protected AssetEntry Describe(ulong schema, IEnumerable<Guid> references)
        => new(Id, AssetType, schema, SourceVersion)
        {
            Name = Name, Path = SourcePath,
            Dependencies = references.Where(static id => id != Guid.Empty).Distinct().Order().ToArray(),
        };

    protected static async ValueTask<AssetEntry> SaveFileAsync(BinaryDocumentWriter writer, string path,
        string pathSuffix, AssetEntry entry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.EndsWith(pathSuffix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Asset path '{path}' must end with '{pathSuffix}'.");
        Guid version = await WriteFileAsync(path, new BinaryAssetFileWriter(writer, entry.AssetGuid),
            cancellationToken).ConfigureAwait(false);
        return entry with { Path = Path.GetFullPath(path), Version = version };
    }

    internal static async ValueTask<TResult> WriteFileAsync<TResult>(string path,
        IAssetFileWriter<TResult> writer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            TResult result;
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                result = await writer.WriteAsync(stream, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(fullPath))
            {
                await writer.CheckDestinationAsync(fullPath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                File.Replace(temporaryPath, fullPath, null);
            }
            else File.Move(temporaryPath, fullPath);
            return result;
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    private sealed class BinaryAssetFileWriter(BinaryDocumentWriter writer, Guid id) : IAssetFileWriter<Guid>
    {
        public async ValueTask<Guid> WriteAsync(FileStream stream, CancellationToken cancellationToken)
        {
            await writer.WriteAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return await BinaryDocument.ReadVersionAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask CheckDestinationAsync(string destination, CancellationToken cancellationToken)
        {
            await using BinaryDocument existing = await BinaryDocument.OpenAsync(FileRangeSource.Open(destination),
                writer.RootDescriptor, ownsSource: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (existing.CreateRootReader().ReadGuid() != id)
                throw new InvalidDataException("The destination path belongs to a different asset identity.");
        }
    }

    protected virtual void ReleaseContent() { }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync()
    {
        AssetFile? file;
        Task[] blockReads;
        Exception? contentError = null;
        lock (SyncRoot)
        {
            if (_close is not null) return new(_close);
            RequireIdle();
            _disposed = true;
            file = _file;
            _file = null;
            blockReads = ActiveBlockReads();
            try
            {
                try { ReleaseContent(); }
                finally { ReleaseBlocks(); }
            }
            catch (Exception error) { contentError = error; }
            _close = CloseAsync(file, blockReads, contentError);
            return new(_close);
        }
    }

    private async Task CloseAsync(AssetFile? file, Task[] blockReads, Exception? contentError)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            try { _blockReadCancellation?.Cancel(); }
            catch (Exception error) { contentError = contentError is null ? error : new AggregateException(contentError, error); }
            try { if (blockReads.Length != 0) await Task.WhenAll(blockReads).ConfigureAwait(false); }
            catch { /* Acquisition reports its failure to its own callers; retained sources still complete cleanup. */ }
            if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (contentError is not null)
        { throw new AggregateException("Asset content and file cleanup failed.", contentError, error); }
        finally { _blockReadCancellation?.Dispose(); }
        if (contentError is not null) ExceptionDispatchInfo.Capture(contentError).Throw();
    }
}

internal interface IAssetFileWriter<TResult>
{
    ValueTask<TResult> WriteAsync(FileStream stream, CancellationToken cancellationToken);
    ValueTask CheckDestinationAsync(string path, CancellationToken cancellationToken);
}
