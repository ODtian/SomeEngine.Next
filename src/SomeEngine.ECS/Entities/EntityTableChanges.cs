using System.Collections.Immutable;

namespace SomeEngine.ECS.Entities;

/// <summary>
/// Observes entity creation, destruction and table membership changes. Component values and
/// sparse values retain their own change tracking. Read batches remain available until accepted.
/// </summary>
public sealed class EntityTableChanges : IDisposable
{
    private World? _world;
    internal EntityTableChanges(World world) => _world = world;
    public EntityTableChangeBatch Read() => RequireWorld().ReadEntityTableChanges(this);
    public void Accept(EntityTableChangeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!ReferenceEquals(batch.Changes, this))
            throw new ArgumentException("The batch belongs to another table observer.", nameof(batch));
        RequireWorld().AcceptEntityTableChanges(this, batch.Versions);
    }
    public void Dispose()
    {
        World? world = _world;
        if (world is null) return;
        world.RemoveEntityTableChanges(this);
        _world = null;
    }
    internal World RequireWorld() => _world ?? throw new ObjectDisposedException(nameof(EntityTableChanges));
}

/// <summary>Entity identities whose table membership may differ from the last accepted batch.</summary>
public sealed class EntityTableChangeBatch
{
    internal EntityTableChangeBatch(EntityTableChanges changes, ImmutableDictionary<Entity, long> versions)
    {
        Changes = changes;
        Versions = versions;
    }
    internal EntityTableChanges Changes { get; }
    internal ImmutableDictionary<Entity, long> Versions { get; }
    public IEnumerable<Entity> Entities => Versions.Keys;
    public int Count => Versions.Count;
}
