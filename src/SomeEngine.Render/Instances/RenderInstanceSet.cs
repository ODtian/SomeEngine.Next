using SomeEngine.ECS.Systems;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Writes one complete logical revision of a user-owned instance set into an exact render
/// property layout. The callback receives no allocator, entity, physical row, or renderer-specific
/// storage object; it can only bind and fill the set's declared properties.
/// </summary>
internal delegate void RenderInstanceSetWriter(RenderInstanceWriteSlice destination);

/// <summary>
/// A storage-backed logical instance set. It is deliberately independent from ECS entities,
/// meshes, materials, and any particular rendering pipeline. Higher-level resources such as
/// <see cref="RenderMeshInstanceSet"/> project their snapshots through this type, while the
/// engine-wide <see cref="RenderInstanceStorageSystem"/> remains the sole physical storage owner.
/// </summary>
internal sealed class RenderInstanceSet :
    ISystem<RenderPrepareSystemContext>,
    IRenderInstanceBatchSource<RenderInstanceSingleGroup>,
    IDisposable
{
    private readonly object _gate = new();
    private readonly RenderInstancePropertyLayout _layout;
    private PendingRevision _pending;
    private RenderInstanceSetWriter? _fullWriter;
    private RenderInstanceBatch? _batch;
    private RenderInstanceBatches<RenderInstanceSingleGroup>? _current;
    private ulong _publishedRevision;
    private bool _created;
    private bool _disposed;

    public RenderInstanceSet(RenderInstancePropertyLayout layout)
        : this(layout, 0, null)
    {
    }

    public RenderInstanceSet(
        RenderInstancePropertyLayout layout,
        int instanceCount,
        RenderInstanceSetWriter? writer)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
        if (instanceCount != 0)
            ArgumentNullException.ThrowIfNull(writer);

        _pending = new PendingRevision(
            instanceCount,
            writer,
            Authorization: layout,
            Kind: PublicationKind.Full,
            Revision: 1ul);
        _fullWriter = writer;
    }

    public RenderInstancePropertyLayout Layout => _layout;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _pending.Count;
            }
        }
    }

    public ulong Revision
    {
        get
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _pending.Revision;
            }
        }
    }

    public bool HasPublishedData => Volatile.Read(ref _current) is not null;

    public RenderInstanceBatches<RenderInstanceSingleGroup>? Current =>
        Volatile.Read(ref _current);

    /// <summary>
    /// Atomically replaces the logical population observed at the next prepare boundary. A count
    /// change replaces the physical batch; an equal-count update rewrites it in place in the next
    /// upload generation.
    /// </summary>
    public void SetData(int instanceCount, RenderInstanceSetWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
        ArgumentNullException.ThrowIfNull(writer);
        lock (_gate)
        {
            ThrowIfDisposed();
            _pending = new PendingRevision(
                instanceCount,
                writer,
                Authorization: _layout,
                Kind: PublicationKind.Full,
                NextRevision(_pending.Revision));
            _fullWriter = writer;
        }
    }

    /// <summary>
    /// Stages a property-local rewrite of the currently published logical population. Membership,
    /// row order, and count remain unchanged; the writer may touch any subset of the authorized
    /// properties and the existing batch retains every other value and binding.
    /// </summary>
    internal void Rewrite(
        RenderInstancePropertyLayout authorization,
        RenderInstanceSetWriter writer)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(writer);
        foreach (RenderInstancePropertyDescriptor property in authorization.Properties)
            _ = _layout.RequireCompatible(property, nameof(authorization));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_pending.Count == 0 || _batch is null)
            {
                throw new InvalidOperationException(
                    "A property-local rewrite requires an already published non-empty instance set.");
            }
            _pending = new PendingRevision(
                _pending.Count,
                writer,
                authorization,
                PublicationKind.Partial,
                NextRevision(_pending.Revision));
        }
    }

    /// <summary>Marks externally owned source data dirty without changing its count or writer.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RenderInstanceSetWriter writer = _fullWriter
                ?? throw new InvalidOperationException(
                    "The render-instance set has no full-population writer to invalidate.");
            _pending = new PendingRevision(
                _pending.Count,
                writer,
                _layout,
                PublicationKind.Full,
                NextRevision(_pending.Revision));
        }
    }

    /// <summary>
    /// Changes the logical population while retaining the current writer. This is used when a
    /// higher-level set changes visible count without replacing its data source.
    /// </summary>
    public void SetCount(int instanceCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (instanceCount != 0 && _pending.Writer is null)
            {
                throw new InvalidOperationException(
                    "A non-empty render-instance set requires a writer.");
            }
            if (instanceCount == _pending.Count)
                return;
            RenderInstanceSetWriter? writer = _fullWriter;
            if (instanceCount != 0 && writer is null)
                throw new InvalidOperationException(
                    "A non-empty render-instance set requires a full-population writer.");
            _pending = new PendingRevision(
                instanceCount,
                writer,
                _layout,
                PublicationKind.Full,
                NextRevision(_pending.Revision));
        }
    }

    /// <summary>Publishes an empty set at the next prepare boundary.</summary>
    public void Clear() => SetCount(0);

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        ThrowIfDisposed();
        if (_created)
            throw new InvalidOperationException("The render-instance set is already created.");
        if (!_layout.Equals(context.InstanceLayout))
        {
            throw new InvalidOperationException(
                "A render-instance set must use the exact property layout owned by its prepare group.");
        }
        _created = true;
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        ThrowIfDisposed();
        if (!_created)
            throw new InvalidOperationException("The render-instance set was not created.");

        PendingRevision revision;
        lock (_gate)
            revision = _pending;

        if (revision.Revision == _publishedRevision)
            return;

        if (revision.Count == 0)
        {
            RetireCurrent(ref context);
            _publishedRevision = revision.Revision;
            return;
        }

        RenderInstanceSetWriter writer = revision.Writer
            ?? throw new InvalidOperationException("A non-empty render-instance set has no writer.");

        if (revision.Kind == PublicationKind.Partial)
        {
            RenderInstanceBatch batch = _batch
                ?? throw new InvalidOperationException(
                    "A property-local rewrite lost its published batch.");
            RenderInstancePropertyLayout authorization = revision.Authorization
                ?? throw new InvalidOperationException(
                    "A property-local rewrite has no property authorization.");
            if (batch.InstanceCount != revision.Count)
            {
                throw new InvalidOperationException(
                    "A property-local rewrite cannot change instance membership or row count.");
            }
            using RenderInstanceWriteHandle update =
                context.RewriteBatch(batch, authorization);
            writer(update.OpenWrite(authorization));
            _ = update.Publish();
            _publishedRevision = revision.Revision;
            return;
        }

        if (_batch is not null && _batch.InstanceCount == revision.Count)
        {
            using RenderInstanceWriteHandle update = context.RewriteBatch(_batch, _layout);
            writer(update.OpenWrite(_layout));
            _ = update.Publish();
            _publishedRevision = revision.Revision;
            return;
        }

        using RenderInstanceWriteHandle replacement =
            context.AllocateBatch(_layout, revision.Count);
        writer(replacement.OpenWrite(_layout));
        RenderInstanceBatch nextBatch = replacement.Publish();
        RenderInstanceBatch? previousBatch = _batch;
        if (previousBatch is not null)
        {
            try
            {
                context.Retire(previousBatch);
            }
            catch
            {
                context.Retire(nextBatch);
                throw;
            }
        }

        _batch = nextBatch;
        Volatile.Write(ref _current, CreatePublishedView(nextBatch));
        _publishedRevision = revision.Revision;
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        if (!_created)
            return;
        RetireCurrent(ref context);
        _publishedRevision = 0;
        _created = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_created)
            {
                throw new InvalidOperationException(
                    "A render-instance set must be removed from its prepare group before disposal.");
            }
            _disposed = true;
            _pending = default;
            _fullWriter = null;
        }
    }

    private void RetireCurrent(ref RenderPrepareSystemContext context)
    {
        RenderInstanceBatch? batch = _batch;
        _batch = null;
        Volatile.Write(ref _current, null);
        if (batch is not null)
            context.Retire(batch);
    }

    private RenderInstanceBatches<RenderInstanceSingleGroup> CreatePublishedView(
        RenderInstanceBatch batch)
    {
        return new RenderInstanceBatches<RenderInstanceSingleGroup>(
            entityCount: 0,
            groups:
            [
                new RenderInstanceBatchGroup<RenderInstanceSingleGroup>(
                    default,
                    _layout,
                    batch),
            ],
            entityOffsets: [0],
            addresses: []);
    }

    private static ulong NextRevision(ulong revision) => checked(revision + 1ul);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct PendingRevision(
        int Count,
        RenderInstanceSetWriter? Writer,
        RenderInstancePropertyLayout? Authorization,
        PublicationKind Kind,
        ulong Revision);

    private enum PublicationKind : byte
    {
        Full,
        Partial,
    }
}
