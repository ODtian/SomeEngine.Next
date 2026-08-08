using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    /// <summary>
    /// Returns a zero-copy view of the immutable index-bucket generation current
    /// at this call. Later component changes publish another generation and do
    /// not change a previously captured span.
    /// </summary>
    /// <remarks>
    /// Immutable bucket publication does not replace normal World/query ownership:
    /// rebuilding a dirty index from live chunks still requires that no concurrent
    /// writer owns those component chunks.
    /// </remarks>
    public ReadOnlySpan<Entity> GetByIndex<TComponent, TKey>(TKey key)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        using WorldJobAdmissionScope admission =
            EnterJobComponent<TComponent>(WorldStorageAccess.Read);
        int componentId = ComponentMetadata<TComponent>.Id;
        _bundles.ThrowIfPendingIndexBackfill(
            componentId,
            _indices.RequiresBackfill(componentId));
        return _indices.Get<TComponent, TKey>(key, _tables.All);
    }
}

