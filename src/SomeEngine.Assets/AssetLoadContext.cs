using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

/// <summary>
/// Restricted access to one active AssetLoader operation. A streamed asset may open exactly one
/// document and transfer that document to itself without creating another asset or backing.
/// </summary>
public sealed class AssetLoadContext
{
    private readonly AssetLoader _loader;
    private readonly AssetGuid _owner;
    private readonly AssetEntry _entry;
    private readonly CancellationToken _cancellationToken;
    private object? _document;
    private object? _openedRoot;
    private object? _transferredOwner;
    private int _openState;
    private int _active = 1;

    internal AssetLoadContext(
        AssetLoader loader,
        AssetGuid owner,
        AssetEntry entry,
        CancellationToken cancellationToken)
    {
        _loader = loader;
        _owner = owner;
        _entry = entry;
        _cancellationToken = cancellationToken;
    }

    public AssetGuid AssetGuid => _entry.AssetGuid;
    public string AssetType => _entry.AssetType;
    public CancellationToken CancellationToken => _cancellationToken;

    /// <summary>Returns true when storage declares this exact generated asset type.</summary>
    public bool Is<T>()
        where T : class
    {
        ThrowIfSealed();
        return StringComparer.Ordinal.Equals(_entry.AssetType, AssetType<T>.Descriptor.AssetType);
    }

    /// <summary>
    /// Opens and validates the operation's one asset document. The context owns it until loading
    /// finishes or ownership is explicitly transferred to the returned asset.
    /// </summary>
    public async ValueTask<BinaryDocument<TAsset>> OpenAsync<TAsset>(
        BinaryReadLimits? limits = null)
        where TAsset : class, IBinaryContract<TAsset>
    {
        ThrowIfSealed();
        if (Interlocked.CompareExchange(ref _openState, 1, 0) != 0)
            throw new InvalidOperationException("An asset load may open exactly one document.");

        BinaryDocument<TAsset> document = await AssetProject.OpenAsync<TAsset>(
            _loader.Storage,
            _entry,
            limits,
            _cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _document, document);
        Volatile.Write(ref _openedRoot, document.Root);
        return document;
    }

    /// <summary>
    /// Transfers the already-open document to its own root asset. The loader verifies that exact
    /// object identity before publication and disposes the root on every failure path.
    /// </summary>
    public TAsset Transfer<TAsset>(
        BinaryDocument<TAsset> document,
        TAsset asset)
        where TAsset : class, IBinaryContract<TAsset>, IAsyncDisposable
    {
        ThrowIfSealed();
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(asset);
        if (!ReferenceEquals(Volatile.Read(ref _document), document))
            throw new InvalidOperationException("Only this load context's opened document can be transferred.");
        if (!ReferenceEquals(document.Root, asset))
            throw new InvalidOperationException("An asset document can be transferred only to its own root asset.");
        if (Interlocked.CompareExchange(ref _transferredOwner, asset, null) is not null)
            throw new InvalidOperationException("The asset document has already been transferred.");
        return asset;
    }

    public Task<T> LoadDependencyAsync<T>(AssetId<T> asset)
        where T : class
    {
        ThrowIfSealed();
        if (!asset.IsValid)
            throw new ArgumentException("An asset ID must be valid.", nameof(asset));
        return _loader.LoadDependencyAsync(_owner, asset, _cancellationToken).AsTask();
    }

    public TOptions GetOptions<TOptions>(TOptions fallback)
        where TOptions : notnull
    {
        ThrowIfSealed();
        return _loader.GetOptions(fallback);
    }

    internal bool TryFind<T>(AssetGuid guid, out T? value)
        where T : class
    {
        ThrowIfSealed();
        return _loader.TryFind(guid, out value);
    }

    internal bool Owns(object value)
        => ReferenceEquals(Volatile.Read(ref _transferredOwner), value);

    internal void Commit(object asset)
    {
        object? openedRoot = Volatile.Read(ref _openedRoot);
        if (openedRoot is null)
            throw new InvalidOperationException("An asset loader must open exactly one typed asset document.");
        if (!ReferenceEquals(openedRoot, asset))
            throw new InvalidOperationException("An asset loader must return the opened document root itself.");

        object? owner = Volatile.Read(ref _transferredOwner);
        if (owner is null)
            return;
        if (!ReferenceEquals(owner, asset))
        {
            throw new InvalidOperationException(
                "The asset document was transferred to an object other than the returned asset.");
        }

        Interlocked.Exchange(ref _transferredOwner, null);
        Interlocked.Exchange(ref _document, null);
        Interlocked.Exchange(ref _openedRoot, null);
    }

    internal async ValueTask DisposeAsync()
    {
        object? owner = Interlocked.Exchange(ref _transferredOwner, null);
        object? document = Interlocked.Exchange(ref _document, null);
        Interlocked.Exchange(ref _openedRoot, null);
        if (owner is IAsyncDisposable asyncOwner)
            await asyncOwner.DisposeAsync().ConfigureAwait(false);
        else if (owner is IDisposable disposableOwner)
            disposableOwner.Dispose();
        else if (document is IAsyncDisposable asyncDocument)
            await asyncDocument.DisposeAsync().ConfigureAwait(false);
        else if (document is IDisposable disposableDocument)
            disposableDocument.Dispose();
    }

    internal void Seal() => Volatile.Write(ref _active, 0);

    private void ThrowIfSealed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _active) == 0, this);
}
