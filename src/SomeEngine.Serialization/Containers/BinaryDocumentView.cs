using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Containers;

/// <summary>
/// Binary-document open path that retains the verified root range lease and exposes the generated
/// long-lived view instead of eagerly constructing the root object graph. This root-only handle is
/// deliberately separate from <see cref="BinaryDocument{T}"/>, whose API promises a materialized
/// <c>Root</c> and chunk streaming operations.
/// </summary>
public sealed class BinaryDocumentView<TContract, TView> : IAsyncDisposable
    where TContract : IBinarySerializable<TContract>, IBinaryViewContract<TContract, TView>
    where TView : struct
{
    private readonly IRangeSource _source;
    private readonly bool _ownsSource;
    private readonly BinaryContractViewOwner _rootOwner;
    private int _disposed;

    private BinaryDocumentView(
        IRangeSource source,
        bool ownsSource,
        BinaryDocumentHeader header,
        BinaryWireTypeDescriptor[] typeCatalog,
        BinaryContractViewOwner rootOwner,
        TView root)
    {
        _source = source;
        _ownsSource = ownsSource;
        _rootOwner = rootOwner;
        Header = header;
        TypeCatalog = typeCatalog;
        Root = root;
        SourceGeneration = source.Generation;
    }

    internal BinaryDocumentHeader Header { get; }

    public TView Root { get; }
    public Guid Generation => Header.Generation;
    public uint ChunkCount => Header.ChunkCount;
    public ulong SchemaFingerprint => Header.SchemaFingerprint;
    public long TotalLength => Header.TotalLength;
    public string SourceGeneration { get; }
    public IReadOnlyList<BinaryWireTypeDescriptor> TypeCatalog { get; }

    public static async ValueTask<BinaryDocumentView<TContract, TView>> OpenAsync(
        IRangeSource source,
        bool ownsSource = false,
        BinaryReadLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        RangeLease? rootLease = null;
        BinaryContractViewOwner? rootOwner = null;
        try
        {
            var content = await BinaryDocument.ReadContentAsync(source,
                new(TContract.TypeId, TContract.SchemaFingerprint, TContract.Compatibility, TContract.SchemaEpoch),
                limits, cancellationToken).ConfigureAwait(false);
            rootLease = content.Root;
            rootOwner = BinaryContractViewOwner.Take(rootLease);
            rootLease = null;
            TView root = TContract.CreateView(rootOwner, content.Limits);

            if (!StringComparer.Ordinal.Equals(content.SourceGeneration, source.Generation))
                throw new IOException("Range source generation changed while opening an binary root view.");

            var result = new BinaryDocumentView<TContract, TView>(
                source,
                ownsSource,
                content.Header,
                content.Types,
                rootOwner,
                root);
            rootOwner = null;
            return result;
        }
        catch
        {
            rootOwner?.Dispose();
            rootLease?.Dispose();
            if (ownsSource)
                await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _rootOwner.Dispose();
        if (_ownsSource)
            await _source.DisposeAsync().ConfigureAwait(false);
    }

}
