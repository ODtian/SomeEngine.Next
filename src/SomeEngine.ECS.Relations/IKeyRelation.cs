using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Relations;

/// <summary>A relation whose result depends on the indexed keys of its two components.</summary>
public interface IKeyRelation<TRelation, TComponent, TKey> : IRelationImplementation<TRelation>
    where TRelation : struct, IRelation<TComponent, TComponent>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    bool Matches(in TRelation relation, in TKey first, in TKey second);
}
