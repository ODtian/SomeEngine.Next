using SomeEngine.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

internal sealed class SourceTransformChanges
{
    private readonly List<(Entity Source, RenderTransform Transform)> _values = [];

    internal void Clear() => _values.Clear();

    internal void Add(Entity source, in RenderTransform transform) => _values.Add((source, transform));

    internal void Merge(RenderExtractionContext context)
    {
        foreach (var value in _values) context.CollectTransformChange(value.Source, value.Transform);
        _values.Clear();
    }
}
