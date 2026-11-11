using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private IValueIndex?[] _valueIndexes = [];

    public ValueIndex<T, TKey> Index<T, TKey>(IComparer<TKey>? compare = null)
        where T : struct, IIndexedComponent<TKey> where TKey : notnull
    {
        int type = ComponentType<T>.TypeId;
        if ((uint)type < (uint)_valueIndexes.Length && _valueIndexes[type] is { } found) return (ValueIndex<T, TKey>)found;
        using WorldAccess access = OpenStructure();
        RequireStructure();
        if (type >= _valueIndexes.Length) Array.Resize(ref _valueIndexes, Math.Max(type + 1, Math.Max(16, _valueIndexes.Length * 2)));
        var index = new ValueIndex<T, TKey>(this, Records.Length, compare);
        for (int i = 0; i < ArchetypeCount; i++)
        {
            foreach (Chunk chunk in Archetypes[i].AllChunks)
            {
                if ((uint)type >= (uint)chunk.Columns.Length || chunk.Columns[type].ColumnPlusOne == 0) continue;
                ref ColumnEntry column = ref chunk.Columns[type];
                if (!ComponentType<T>.IsSharedType && !ComponentType<T>.IsTagType)
                {
                    Span<T> values = ColumnEntry.Span<T>(ref column, chunk.Count);
                    if (column.Presence is null && !ComponentType<T>.IsEnableableType)
                        for (int row = 0; row < values.Length; row++)
                            ((IValueIndex<T>)index).Added(chunk.Entities[row], values[row]);
                    else
                        for (int row = 0; row < values.Length; row++)
                            if (column.Has(row) && chunk.IsEnabled(type, row))
                                ((IValueIndex<T>)index).Added(chunk.Entities[row], values[row]);
                }
                else
                    for (int row = 0; row < chunk.Count; row++)
                        if (column.Has(row) && chunk.IsEnabled(type, row))
                            ((IValueIndex)index).AddFromEntity(chunk.Entities[row], in column, row);
            }
        }
        _valueIndexes[type] = index;
        return index;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IValueIndex<T>? ActiveIndex<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        IValueIndex?[] indexes = _valueIndexes;
        return (uint)type < (uint)indexes.Length && indexes[type] is { } index ? Unsafe.As<IValueIndex<T>>(index) : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IndexComponentActive<T>(in EntityRecord record) where T : struct
    {
        if (!ComponentType<T>.IsEnableableType) return true;
        Chunk chunk = record.Chunk!;
        int type = ComponentType<T>.TypeId;
        return (uint)type >= (uint)chunk.Columns.Length || !chunk.Columns[type].Has(record.Row) || chunk.IsEnabled(type, record.Row);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetIndexedValue<T>(Entity entity, ref EntityRecord record, in T value, int sharedId) where T : struct
    {
        if (ComponentType<T>.IsGroupedType && ActiveGrouping<T>() is { } grouping)
            grouping.SetKnown(entity, ref record, value, sharedId);
        else if (ComponentType<T>.IsSharedType)
            SetSharedId<T>(entity, ref record, sharedId >= 0 ? sharedId : Shared<T>().Intern(value));
        else SetValueKnown(entity, ref record, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IndexCreated<T>(Entity entity, in T value) where T : struct
    {
        if (ActiveIndex<T>() is not { } index) return;
        if (ComponentType<T>.HasValueOperationsType || ComponentType<T>.IsSharedType)
            index.Added(entity, Read<T>(entity));
        else index.Added(entity, value);
    }

    private void RemoveIndexMembers(Entity entity, Chunk chunk, int row)
    {
        foreach (ComponentType type in chunk.Archetype.Shared)
            if (chunk.Columns[type.Id].Has(row)) RemoveSharedMember(entity, chunk, type.Id, row);
        foreach (ComponentType type in chunk.Archetype.Indexed)
            if ((uint)type.Id < (uint)_valueIndexes.Length && chunk.Columns[type.Id].Has(row)) _valueIndexes[type.Id]?.Remove(entity.Id);
    }

    private void ClearIndexes()
    {
        foreach (IValueIndex? index in _valueIndexes) index?.Clear();
    }
}
