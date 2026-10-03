using SomeEngine.ECS.Entities;

namespace SomeEngine.Views;

/// <summary>The ordered entities observed by one Each call, available during the current build.</summary>
public readonly ref struct QueryRows
{
    private readonly ReadOnlySpan<Entity> _entities;

    internal QueryRows(ReadOnlySpan<Entity> entities) => _entities = entities;

    public ReadOnlySpan<Entity>.Enumerator GetEnumerator() => _entities.GetEnumerator();
}
