using System.Runtime.CompilerServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Coordinates the batches for one RenderWorld and graphics device. Producers write directly
/// into graph upload pages, and consumers bind the graph buffers through read views.
/// </summary>
public sealed class RenderInstanceStorageSystem : IDisposable
{
    private static readonly ConditionalWeakTable<RenderWorld, WorldClaims> s_worldClaims = new();

    private readonly RenderWorld _world;
    private readonly Device _device;
    private readonly RenderInstanceResources _storage;
    private readonly WorldClaims _claims;
    private readonly object _claim = new();
    private bool _disposed;

    public RenderInstanceStorageSystem(
        IGraphicsBackend backend,
        Device device,
        RenderWorld world,
        RenderInstancePropertyLayout layout,
        RenderInstanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(device);
        _world = world ?? throw new ArgumentNullException(nameof(world));
        ArgumentNullException.ThrowIfNull(layout);

        _claims = s_worldClaims.GetValue(world, static _ => new WorldClaims());
        lock (_claims.Gate)
        {
            if (_claims.Owners.ContainsKey(device))
            {
                throw new InvalidOperationException(
                    "This RenderWorld already has instance storage for the graphics device.");
            }
            _claims.Owners.Add(device, _claim);
        }

        try
        {
            _storage = new RenderInstanceResources(
                device,
                layout,
                options);
        }
        catch
        {
            ReleaseClaim(device);
            throw;
        }
        _device = device;
    }

    public RenderWorld World => _world;

    public RenderInstancePropertyLayout Layout => Storage.Layout;

    public RenderInstanceOptions Options => Storage.Options;

    public int PropertyDataBytes => Storage.PropertyDataBytes;

    public GraphBufferId PropertyData => Storage.PropertyData;

    public GraphBufferId BatchMetadata => Storage.BatchMetadata;

    /// <summary>Installs the two persistent device buffers in the rendering graph.</summary>
    public void Install(global::SomeEngine.RenderGraph.RenderGraph graph) => Storage.Install(graph);

    internal RenderInstanceResources Storage
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _storage;
        }
    }

    internal bool RequiresValueWrite => Storage.RequiresValueWrite;

    internal RenderInstanceBorrowerLease AcquireBorrower() => Storage.AcquireBorrower();

    internal RenderInstanceWriteScope OpenWrite(RenderPrepareScope scope, RenderGraphNextFrame? nextFrame) =>
        Storage.BeginPrepare(scope, nextFrame);

    /// <summary>
    /// Allocates an unclassified logical batch for a pipeline system. The returned capability can
    /// write only this allocation and must publish or dispose before the prepare boundary closes.
    /// </summary>
    internal RenderInstanceWriteHandle AllocateBatch(
        RenderPrepareScope scope,
        RenderInstanceWriteScope write,
        RenderInstancePropertyLayout exactLayout,
        int instanceCount)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireWriteScope(scope, write);
        ArgumentNullException.ThrowIfNull(exactLayout);
        RenderInstanceBatchComposition composition =
            write.BeginBatch(exactLayout, instanceCount);
        return new RenderInstanceWriteHandle(write, composition);
    }

    /// <summary>
    /// Opens selected properties of an existing batch for an in-place value rewrite. Membership
    /// changes must authorize and rewrite the complete exact layout.
    /// </summary>
    internal RenderInstanceWriteHandle RewriteBatch(
        RenderPrepareScope scope,
        RenderInstanceWriteScope write,
        RenderInstanceBatch batch,
        RenderInstancePropertyLayout properties,
        RenderInstancePropertyLayout? replacedProperties)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireWriteScope(scope, write);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(properties);

        RenderInstanceBatchComposition composition =
            write.BeginBatchUpdate(batch, properties, replacedProperties);
        return new RenderInstanceWriteHandle(write, composition);
    }

    /// <summary>Retires one live batch at an exclusive prepare boundary.</summary>
    internal void Retire(
        RenderPrepareScope scope,
        RenderInstanceWriteScope write,
        RenderInstanceBatch batch)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireWriteScope(scope, write);
        ArgumentNullException.ThrowIfNull(batch);
        write.ReleaseBatch(batch);
    }

    internal RenderInstanceStorageView OpenRead(RenderReadScope readScope, scoped ref RenderGraphFrame graph) =>
        Storage.OpenRead(readScope, ref graph);

    public RenderInstanceDiagnostics CaptureDiagnostics() => Storage.CaptureDiagnostics();

    public void Shutdown(RenderPrepareScope scope) => Storage.Shutdown(scope);

    public void Dispose()
    {
        if (_disposed)
            return;
        _storage.Dispose();
        ReleaseClaim(_device);
        _disposed = true;
    }

    private void ReleaseClaim(Device device)
    {
        lock (_claims.Gate)
        {
            if (!_claims.Owners.TryGetValue(device, out object? owner)
                || !ReferenceEquals(owner, _claim))
            {
                throw new InvalidOperationException(
                    "Render-instance storage ownership was lost.");
            }
            _claims.Owners.Remove(device);
        }
    }

    private void RequireWriteScope(
        RenderPrepareScope scope,
        RenderInstanceWriteScope write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (!write.BelongsTo(scope, Storage))
        {
            throw new ArgumentException(
                "The render-instance write capability belongs to a different prepare boundary or storage owner.",
                nameof(write));
        }
    }

    private sealed class WorldClaims
    {
        internal object Gate { get; } = new();

        internal Dictionary<Device, object> Owners { get; } =
            new(ReferenceEqualityComparer.Instance);
    }
}
