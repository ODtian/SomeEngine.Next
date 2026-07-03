using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    public ReadOnlySpan<Entity> GetByIndex<TComponent, TKey>(TKey key)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        return _indices.Get<TComponent, TKey>(key, _tables.All);
    }

}

