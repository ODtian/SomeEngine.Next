using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public readonly record struct OptionalComponent<T>(bool IsPresent, bool IsEnabled, T Value) where T : struct;

public readonly partial struct QueryChunk
{
    public OptionalComponent<T> Optional<T>(int row) where T : struct
    {
        _chunk.Archetype.World.RequireValue<T>(false);
        if ((uint)row >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(row));
        int type = ComponentType<T>.TypeId;
        ColumnEntry[] columns = _chunk.Columns;
        int index = _start + row;
        if ((uint)type >= (uint)columns.Length || !columns[type].Has(index)) return default;
        T value = ComponentType<T>.IsTagType ? default
            : ComponentType<T>.IsSharedType ? _chunk.Archetype.World.ReadSharedColumn<T>(columns[type], index)
            : ColumnEntry.At<T>(ref columns[type], index);
        return new(true, _chunk.IsEnabled(type, index), value);
    }
}
