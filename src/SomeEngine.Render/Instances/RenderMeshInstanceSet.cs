using System.Buffers;
using System.Collections.ObjectModel;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Instances;

/// <summary>Convenience writer for the Render-core spatial property pair.</summary>
internal delegate void RenderMeshInstanceWriter(
    int sourceStart,
    Span<RenderTransform> current,
    Span<RenderPreviousTransform> previous);

/// <summary>Declares whether source values change only on demand or every rendered frame.</summary>
public enum RenderMeshInstanceUpdateMode : byte
{
    OnDemand,
    EveryFrame,
}

/// <summary>
/// User-facing instanced-mesh resource. It owns shared mesh/material state and one arbitrary
/// layout-driven instance source. Material-specific per-instance values are canonical properties
/// reflected from material contracts; this type does not define semantic convenience channels.
/// </summary>
public sealed class RenderMeshInstanceSet : IDisposable
{
    private readonly object _gate = new();
    private AssetHandle<Mesh> _mesh;
    private ReadOnlyCollection<AssetHandle<Material>> _materials;
    private IRenderInstanceSource _source;
    private float _boundsExpansion;
    private RenderMeshInstanceUpdateMode _updateMode;
    private ulong _revision = 1ul;
    private bool _ownsSource;
    private bool _disposed;

    /// <summary>
    /// Creates a transform-only procedural set. This is a convenience over the generic property
    /// source, not a renderer-recognized procedural source kind.
    /// </summary>
    internal RenderMeshInstanceSet(
        AssetHandle<Mesh> mesh,
        IReadOnlyList<AssetHandle<Material>> materials,
        int instanceCount,
        RenderMeshInstanceWriter writer,
        float boundsExpansion = 0.0f,
        RenderMeshInstanceUpdateMode updateMode = RenderMeshInstanceUpdateMode.OnDemand)
        : this(
            mesh,
            materials,
            CreateTransformSource(instanceCount, writer),
            boundsExpansion,
            updateMode,
            ownsSource: true)
    {
    }

    /// <summary>
    /// Creates a set over any compatible instance-property source. Ownership is explicit because
    /// sources may be shared by tools, streaming systems, or several scene objects.
    /// </summary>
    internal RenderMeshInstanceSet(
        AssetHandle<Mesh> mesh,
        IReadOnlyList<AssetHandle<Material>> materials,
        IRenderInstanceSource source,
        float boundsExpansion = 0.0f,
        RenderMeshInstanceUpdateMode updateMode = RenderMeshInstanceUpdateMode.OnDemand,
        bool ownsSource = false)
    {
        ValidateMesh(mesh);
        _materials = SnapshotMaterials(materials);
        _source = ValidateSource(source);
        ValidateBoundsExpansion(boundsExpansion);
        _mesh = mesh;
        _boundsExpansion = boundsExpansion;
        _updateMode = updateMode;
        _ownsSource = ownsSource;
    }

    /// <summary>Creates engine-owned editable memory for the built-in transform contract.</summary>
    public static RenderMeshInstanceSet CreateBuffered(
        AssetHandle<Mesh> mesh,
        IReadOnlyList<AssetHandle<Material>> materials,
        int capacity = 0,
        float boundsExpansion = 0.0f,
        RenderMeshInstanceUpdateMode updateMode = RenderMeshInstanceUpdateMode.OnDemand) =>
        CreateBuffered(
            mesh,
            materials,
            RenderInstanceTransformProperties.Layout,
            capacity,
            boundsExpansion,
            updateMode);

    /// <summary>
    /// Creates engine-owned editable memory for an exact property contract. The contract must
    /// contain the Render-core current/previous transform properties and may contain any canonical
    /// material or pipeline-neutral properties contributed by the caller.
    /// </summary>
    public static RenderMeshInstanceSet CreateBuffered(
        AssetHandle<Mesh> mesh,
        IReadOnlyList<AssetHandle<Material>> materials,
        RenderInstancePropertyLayout layout,
        int capacity = 0,
        float boundsExpansion = 0.0f,
        RenderMeshInstanceUpdateMode updateMode = RenderMeshInstanceUpdateMode.OnDemand)
    {
        var buffer = new RenderInstanceBuffer(layout, capacity);
        try
        {
            return new RenderMeshInstanceSet(
                mesh,
                materials,
                buffer,
                boundsExpansion,
                updateMode,
                ownsSource: true);
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public AssetHandle<Mesh> Mesh => Read(static set => set._mesh);

    public IReadOnlyList<AssetHandle<Material>> Materials =>
        Read(static set => set._materials);

    internal IRenderInstanceSource Source => Read(static set => set._source);

    public RenderInstanceBuffer? Buffer => Read(
        static set => set._source as RenderInstanceBuffer);

    public RenderInstancePropertyLayout InstanceLayout =>
        Read(static set => set._source.Layout);

    public int Count => Read(static set => set._source.Count);

    public int Capacity => Read(static set => set._source.Capacity);

    public float BoundsExpansion => Read(static set => set._boundsExpansion);

    public RenderMeshInstanceUpdateMode UpdateMode => Read(static set => set._updateMode);

    /// <summary>Revision of shared draw state and source ownership, not source contents.</summary>
    public ulong Revision => Read(static set => set._revision);

    public ulong DataRevision => Read(static set => set._source.Revision);

    public void SetMesh(AssetHandle<Mesh> mesh)
    {
        ValidateMesh(mesh);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_mesh == mesh)
                return;
            _mesh = mesh;
            _revision = NextRevision(_revision);
        }
    }

    public void SetMaterials(IReadOnlyList<AssetHandle<Material>> materials)
    {
        ReadOnlyCollection<AssetHandle<Material>> snapshot = SnapshotMaterials(materials);
        lock (_gate)
        {
            ThrowIfDisposed();
            _materials = snapshot;
            _revision = NextRevision(_revision);
        }
    }

    public void SetBoundsExpansion(float boundsExpansion)
    {
        ValidateBoundsExpansion(boundsExpansion);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_boundsExpansion == boundsExpansion)
                return;
            _boundsExpansion = boundsExpansion;
            _revision = NextRevision(_revision);
        }
    }

    public void SetUpdateMode(RenderMeshInstanceUpdateMode updateMode)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_updateMode == updateMode)
                return;
            _updateMode = updateMode;
            _revision = NextRevision(_revision);
        }
    }

    /// <summary>Atomically replaces the logical source observed by future snapshots.</summary>
    internal void SetSource(IRenderInstanceSource source, bool ownsSource = false)
    {
        source = ValidateSource(source);
        IDisposable? dispose = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (ReferenceEquals(_source, source))
            {
                if (_ownsSource != ownsSource)
                {
                    _ownsSource = ownsSource;
                    _revision = NextRevision(_revision);
                }
                return;
            }

            if (_ownsSource)
                dispose = _source as IDisposable;
            _source = source;
            _ownsSource = ownsSource;
            _revision = NextRevision(_revision);
        }
        dispose?.Dispose();
    }

    /// <summary>Replaces the source with a transform-only procedural population.</summary>
    internal void SetData(
        int instanceCount,
        RenderMeshInstanceWriter writer,
        RenderMeshInstanceUpdateMode? updateMode = null)
    {
        RenderInstanceProceduralSource source =
            CreateTransformSource(instanceCount, writer);
        IDisposable? dispose = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_ownsSource)
                dispose = _source as IDisposable;
            _source = source;
            _ownsSource = true;
            _updateMode = updateMode ?? _updateMode;
            _revision = NextRevision(_revision);
        }
        dispose?.Dispose();
    }

    /// <summary>
    /// Invalidates all values in an externally observed procedural source. Editable buffers
    /// publish property-local revisions themselves; other source implementations own their own
    /// invalidation contract.
    /// </summary>
    internal void Invalidate()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_source is RenderInstanceProceduralSource procedural)
                procedural.InvalidateAll();
        }
    }

    /// <summary>Acquires one coherent shared-state and data-source revision.</summary>
    internal RenderMeshInstanceSnapshot Capture(ulong previousDataRevision = 0ul)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            RenderInstanceSourceSnapshot source = _source.Capture(previousDataRevision);
            try
            {
                return new RenderMeshInstanceSnapshot(
                    _mesh,
                    _materials,
                    _boundsExpansion,
                    _updateMode,
                    _revision,
                    source);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Captures only shared draw state. This does not acquire a source-data read lease and is used
    /// by material/pipeline registries that do not inspect per-instance values.
    /// </summary>
    internal RenderMeshInstanceSharedSnapshot CaptureShared()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return new RenderMeshInstanceSharedSnapshot(
                _mesh,
                _materials,
                _boundsExpansion,
                _updateMode,
                _revision);
        }
    }

    public void Dispose()
    {
        IDisposable? dispose = null;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_ownsSource)
                dispose = _source as IDisposable;
            _ownsSource = false;
        }
        dispose?.Dispose();
    }

    private T Read<T>(Func<RenderMeshInstanceSet, T> selector)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return selector(this);
        }
    }

    private static RenderInstanceProceduralSource CreateTransformSource(
        int instanceCount,
        RenderMeshInstanceWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instanceCount);
        ArgumentNullException.ThrowIfNull(writer);

        RenderInstancePropertyLayout layout = RenderInstanceTransformProperties.Layout;
        ResolvedRenderInstanceProperty<RenderTransform> currentProperty =
            layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        ResolvedRenderInstanceProperty<RenderPreviousTransform> previousProperty =
            layout.Resolve(RenderInstanceTransformProperties.PreviousTransform);
        return new RenderInstanceProceduralSource(
            layout,
            instanceCount,
            (sourceStart, destination) =>
            {
                int count = destination.Count;
                RenderTransform[] currentValues =
                    ArrayPool<RenderTransform>.Shared.Rent(Math.Max(1, count));
                RenderPreviousTransform[] previousValues =
                    ArrayPool<RenderPreviousTransform>.Shared.Rent(Math.Max(1, count));
                try
                {
                    Span<RenderTransform> current = currentValues.AsSpan(0, count);
                    Span<RenderPreviousTransform> previous = previousValues.AsSpan(0, count);
                    writer(sourceStart, current, previous);
                    if (destination.Properties.Contains(currentProperty.Key))
                        destination.Write(currentProperty, current);
                    if (destination.Properties.Contains(previousProperty.Key))
                        destination.Write(previousProperty, previous);
                }
                finally
                {
                    ArrayPool<RenderTransform>.Shared.Return(currentValues);
                    ArrayPool<RenderPreviousTransform>.Shared.Return(previousValues);
                }
            });
    }

    private static IRenderInstanceSource ValidateSource(IRenderInstanceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = source.Layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
        _ = source.Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform);
        return source;
    }

    private static void ValidateMesh(AssetHandle<Mesh> mesh)
    {
        if (!mesh.IsValid)
            throw new ArgumentException("An instanced-mesh set requires a valid mesh.", nameof(mesh));
    }

    private static ReadOnlyCollection<AssetHandle<Material>> SnapshotMaterials(
        IReadOnlyList<AssetHandle<Material>> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        AssetHandle<Material>[] values = [.. materials];
        if (values.Length == 0)
            throw new ArgumentException("An instanced-mesh set requires at least one material.", nameof(materials));
        for (int index = 0; index < values.Length; index++)
        {
            if (!values[index].IsValid)
            {
                throw new ArgumentException(
                    $"Instanced-mesh material {index} is invalid.",
                    nameof(materials));
            }
        }
        return Array.AsReadOnly(values);
    }

    private static void ValidateBoundsExpansion(float boundsExpansion)
    {
        if (!float.IsFinite(boundsExpansion) || boundsExpansion < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(boundsExpansion));
    }

    private static ulong NextRevision(ulong revision) => checked(revision + 1ul);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Immutable shared draw state of one instanced-mesh resource.</summary>
internal readonly record struct RenderMeshInstanceSharedSnapshot(
    AssetHandle<Mesh> Mesh,
    IReadOnlyList<AssetHandle<Material>> Materials,
    float BoundsExpansion,
    RenderMeshInstanceUpdateMode UpdateMode,
    ulong Revision);

/// <summary>One coherent logical revision of a <see cref="RenderMeshInstanceSet"/>.</summary>
internal sealed class RenderMeshInstanceSnapshot : IDisposable
{
    private RenderInstanceSourceSnapshot? _source;

    internal RenderMeshInstanceSnapshot(
        AssetHandle<Mesh> mesh,
        ReadOnlyCollection<AssetHandle<Material>> materials,
        float boundsExpansion,
        RenderMeshInstanceUpdateMode updateMode,
        ulong revision,
        RenderInstanceSourceSnapshot source)
    {
        Mesh = mesh;
        Materials = materials;
        BoundsExpansion = boundsExpansion;
        UpdateMode = updateMode;
        Revision = revision;
        _source = source;
    }

    public AssetHandle<Mesh> Mesh { get; }

    public IReadOnlyList<AssetHandle<Material>> Materials { get; }

    public RenderInstancePropertyLayout InstanceLayout => Source.Layout;

    public int Count => Source.Count;

    public int Capacity => Source.Capacity;

    public float BoundsExpansion { get; }

    public RenderMeshInstanceUpdateMode UpdateMode { get; }

    public ulong Revision { get; }

    public ulong DataRevision => Source.Revision;

    public RenderInstanceChangeSet Changes => Source.Changes;

    public void Write(int sourceStart, RenderInstanceWriteSlice destination) =>
        Source.Write(sourceStart, destination);

    public void Dispose()
    {
        RenderInstanceSourceSnapshot? source = Interlocked.Exchange(ref _source, null);
        source?.Dispose();
    }

    private RenderInstanceSourceSnapshot Source =>
        Volatile.Read(ref _source)
        ?? throw new ObjectDisposedException(nameof(RenderMeshInstanceSnapshot));
}
