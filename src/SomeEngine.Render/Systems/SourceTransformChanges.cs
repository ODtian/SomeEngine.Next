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

    internal void Collect(ReadOnlyQueryPacket packet, uint lastSystemVersion)
    {
        ReadOnlySpan<WorldTransform> transforms = packet.Read<WorldTransform>();
        for (int row = 0; row < packet.Count; row++)
            if (packet.RowChangedSince<WorldTransform>(row, lastSystemVersion))
                Add(packet.Entities[row], new RenderTransform(transforms[row].Qvvs));
    }

    internal void Merge(RenderExtractionContext context)
    {
        foreach (var value in _values) context.CollectTransformChange(value.Source, value.Transform);
        _values.Clear();
    }
}
