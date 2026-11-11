using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Shared;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public readonly record struct SharedId<T> where T : struct
{
    public int Value { get; }
    internal SharedId(int value) => Value = value;
}

public sealed partial class World
{
    private SharedValues?[] _shared = [];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SharedValues<T> Shared<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        SharedValues?[] shared = _shared;
        if ((uint)type < (uint)shared.Length && shared[type] is { } found) return Unsafe.As<SharedValues<T>>(found);
        return AddSharedValues<T>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SharedValues<T> AddSharedValues<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        if (type >= _shared.Length) Array.Resize(ref _shared, Math.Max(type + 1, Math.Max(16, _shared.Length * 2)));
        var values = new SharedValues<T>(Records.Length);
        _shared[type] = values;
        return values;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SharedId<T> InternShared<T>(in T value) where T : struct, ISharedComponent
        => Execution is null ? InternSharedCore(value) : InternSharedWithExecution(value);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SharedId<T> InternSharedWithExecution<T>(in T value) where T : struct, ISharedComponent
    {
        using WorldAccess access = OpenStructure();
        return InternSharedCore(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SharedId<T> InternSharedCore<T>(in T value) where T : struct, ISharedComponent
    {
        RequireStructure();
        ComponentValues.Bind<T>();
        return new(Shared<T>().Intern(value));
    }

    internal int InternSharedValue<T>(in T value) where T : struct => Shared<T>().Intern(value);

    public int SharedValueCount<T>() where T : struct, ISharedComponent
    {
        using WorldAccess access = OpenStructure(false);
        int type = ComponentType<T>.TypeId;
        return (uint)type < (uint)_shared.Length && _shared[type] is { } values ? ((SharedValues<T>)values).Count : 0;
    }
    public ref readonly T ReadShared<T>(Entity entity) where T : struct, ISharedComponent => ref ReadSharedValue<T>(entity);
    public ref readonly T ReadShared<T>(SharedId<T> id) where T : struct, ISharedComponent
    {
        RequireValue<T>(false);
        return ref Shared<T>().Values[id.Value];
    }

    internal T[] SharedValueArray<T>() where T : struct
    {
        int type = ComponentType<T>.TypeId;
        return (uint)type < (uint)_shared.Length && _shared[type] is SharedValues<T> values ? values.Values : [];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T ReadSharedValueKnown<T>(Entity entity, T[] values) where T : struct
    {
        ref EntityRecord record = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Records), (nint)(uint)entity.Id);
        ColumnEntry[] columns = record.Chunk!.Columns;
        ref ColumnEntry column = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(columns), ComponentType<T>.TypeId);
        int id = ColumnEntry.At<int>(ref column, record.Row);
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values), (nint)(uint)id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T ReadSharedValue<T>(Entity entity, T[] values) where T : struct
    {
        ref EntityRecord record = ref Resolve(entity);
        ColumnEntry[] columns = record.Chunk!.Columns;
        int type = ComponentType<T>.TypeId;
        if ((uint)type >= (uint)columns.Length) MissingSharedValue();
        ref ColumnEntry column = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(columns), type);
        if (!column.Has(record.Row)) MissingSharedValue();
        int id = ColumnEntry.At<int>(ref column, record.Row);
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values), id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref readonly T ReadSharedValue<T>(Entity entity) where T : struct
    {
        RequireValue<T>(false);
        ref EntityRecord record = ref Resolve(entity);
        ColumnEntry[] columns = record.Chunk!.Columns;
        int type = ComponentType<T>.TypeId;
        if ((uint)type >= (uint)columns.Length) MissingSharedValue();
        ref ColumnEntry column = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(columns), type);
        if (!column.Has(record.Row)) MissingSharedValue();
        int id = ColumnEntry.At<int>(ref column, record.Row);
        var values = Unsafe.As<SharedValues<T>>(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_shared), type)!);
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values.Values), id);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MissingSharedValue() => throw new ArgumentException("Entity does not have this shared component.");

    internal ref readonly T ReadSharedColumn<T>(in ColumnEntry column, int row) where T : struct
    {
        RequireValue<T>(false);
        return ref Unsafe.As<SharedValues<T>>(_shared[ComponentType<T>.TypeId]!).Values[ColumnEntry.At<int>(ref Unsafe.AsRef(in column), row)];
    }

    internal SharedReadOnlySpan<T> ReadSharedRange<T>(in ColumnEntry column, int start, int count) where T : struct
    {
        RequireValue<T>(false);
        return new(Unsafe.As<SharedValues<T>>(_shared[ComponentType<T>.TypeId]!).Values,
            Unsafe.As<int[]>(column.Values!).AsSpan(start, count));
    }

    internal int SharedCount(int type, int value)
        => (uint)type < (uint)_shared.Length && _shared[type] is { } values ? values.Groups.Count(value) : 0;

    public SharedMembers FindShared<T>(SharedId<T> id) where T : struct, ISharedComponent
    {
        SharedValues<T> values = Shared<T>();
        BeginStructureRead();
        return new(this, values.Groups.Members(id.Value));
    }

    public void SetShared<T>(Entity entity, in T value) where T : struct, ISharedComponent => Set(entity, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetShared<T>(Entity entity, SharedId<T> id) where T : struct, ISharedComponent
    {
        if (Execution is not null) SetSharedWithExecution(entity, id);
        else SetSharedCore(entity, id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SetSharedWithExecution<T>(Entity entity, SharedId<T> id) where T : struct, ISharedComponent
    {
        using WorldAccess access = OpenStructure();
        SetSharedCore(entity, id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetSharedCore<T>(Entity entity, SharedId<T> id) where T : struct, ISharedComponent
    {
        ComponentValues.Bind<T>();
        SharedValues<T> values = Shared<T>();
        if (ComponentType<T>.IsIndexedType && ActiveIndex<T>() is { } index)
        {
            index.Set(entity, values.Values[id.Value], id.Value);
            return;
        }
        RequireStructure();
        ref EntityRecord record = ref Resolve(entity);
        if (ComponentType<T>.IsGroupedType && ActiveGrouping<T>() is { } grouping)
            grouping.SetKnown(entity, ref record, values.Values[id.Value], id.Value);
        else SetSharedId<T>(entity, ref record, id.Value);
    }

    public void RemoveShared<T>(Entity entity) where T : struct, ISharedComponent => Remove<T>(entity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetSharedValue<T>(Entity entity, in T value) where T : struct
    {
        RequireStructure();
        ref EntityRecord record = ref Resolve(entity);
        Chunk chunk = record.Chunk!;
        ColumnEntry[] columns = chunk.Columns;
        int type = ComponentType<T>.TypeId;
        SharedValues<T> values;
        if ((uint)type < (uint)columns.Length && columns[type].Has(record.Row))
        {
            values = Unsafe.As<SharedValues<T>>(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_shared), type)!);
            int current = ColumnEntry.At<int>(ref columns[type], record.Row);
            if (EqualityComparer<T>.Default.Equals(Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(values.Values), current), value))
            {
                if (ComponentType<T>.TracksWritesType) Stamp<T>(chunk, record.Row, NextVersion());
                return;
            }
        }
        else values = Shared<T>();
        SetSharedId<T>(entity, ref record, values.Intern(value));
    }

    private void SetSharedId<T>(Entity entity, ref EntityRecord record, int id, bool added = false) where T : struct
    {
        using MembershipEdit membership = BeginMembershipEdit(entity);
        Chunk chunk = record.Chunk!;
        int type = ComponentType<T>.TypeId;
        bool columnExists = (uint)type < (uint)chunk.Columns.Length && chunk.Columns[type].ColumnPlusOne != 0;
        bool present = !added && columnExists && chunk.Columns[type].Has(record.Row);
        int old = present ? ColumnEntry.At<int>(ref chunk.Columns[type], record.Row) : -1;
        if (old == id)
        {
            if (ComponentType<T>.TracksWritesType) Stamp<T>(chunk, record.Row, NextVersion());
            return;
        }
        if (!columnExists)
        {
            Transition target = GetTransition(chunk, type, true, record.Row);
            MoveKnown(entity, ref record, chunk, target);
            chunk = record.Chunk!;
        }
        bool active = !present || chunk.IsEnabled(type, record.Row);
        _shared[type]!.Groups.Set(entity, old, id, active);
        ColumnEntry.At<int>(ref chunk.Columns[type], record.Row) = id;
        if (!present) chunk.RestorePresence(type, record.Row);
        if (ComponentType<T>.TracksWritesType) Stamp<T>(chunk, record.Row, NextVersion(), !present);
    }

    private void RemoveSharedMember(Entity entity, Chunk chunk, int type, int row)
    {
        int id = ColumnEntry.At<int>(ref chunk.Columns[type], row);
        _shared[type]!.Groups.Remove(entity.Id, id, chunk.IsEnabled(type, row));
    }

    private void CopySharedMember(Entity entity, Chunk chunk, int type, int row)
    {
        int id = ColumnEntry.At<int>(ref chunk.Columns[type], row);
        _shared[type]!.Groups.Set(entity, -1, id, chunk.IsEnabled(type, row));
    }
}

public readonly ref struct SharedMembers
{
    private readonly World _world;
    public ReadOnlySpan<Entity> Entities { get; }
    internal SharedMembers(World world, ReadOnlySpan<Entity> entities) { _world = world; Entities = entities; }
    public int Count => Entities.Length;
    public ReadOnlySpan<Entity>.Enumerator GetEnumerator() => Entities.GetEnumerator();
    public void Dispose() => _world.EndValueRead();
}

public readonly ref struct SharedReadOnlySpan<T> where T : struct
{
    private readonly T[] _values;
    private readonly ReadOnlySpan<int> _ids;
    internal SharedReadOnlySpan(T[] values, ReadOnlySpan<int> ids) { _values = values; _ids = ids; }
    public int Length => _ids.Length;
    public ref readonly T this[int row] => ref _values[_ids[row]];
}
