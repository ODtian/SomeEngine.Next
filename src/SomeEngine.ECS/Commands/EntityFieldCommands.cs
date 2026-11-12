using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Commands;

public readonly record struct DeferredEntityField(int Field, DeferredEntity Entity);
internal readonly record struct EntityFieldPatch(int Field, Entity Target);

public sealed partial class CommandBuffer
{
    public DeferredEntity Create<T, TFields>(in T value, ReadOnlySpan<DeferredEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    {
        RequireRecording();
        var bucket = Bucket<EntityFieldCommands<T, TFields>>(out int type);
        if (bucket.Count == 0) Use(type);
        return NewCreate(type, bucket.Add(this, value, fields));
    }

    private void Fields<T, TFields>(CommandKind kind, Entity target, in T value, ReadOnlySpan<DeferredEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    {
        var bucket = Bucket<EntityFieldCommands<T, TFields>>(out int type);
        if (bucket.Count == 0) Use(type);
        Append(kind, target, type, bucket.Add(this, value, fields));
    }
    public void Set<T, TFields>(Entity entity, in T value, ReadOnlySpan<DeferredEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    { RequireRecording(); Fields<T, TFields>(CommandKind.Set, entity, value, fields); }
    public void Set<T, TFields>(DeferredEntity entity, in T value, ReadOnlySpan<DeferredEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    { RequireRecording(); Fields<T, TFields>(CommandKind.Set, Encode(entity), value, fields); }
    public void Add<T, TFields>(Entity entity, in T value, ReadOnlySpan<DeferredEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    { RequireRecording(); Fields<T, TFields>(CommandKind.Add, entity, value, fields); }
    public void Add<T, TFields>(DeferredEntity entity, in T value, ReadOnlySpan<DeferredEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    { RequireRecording(); Fields<T, TFields>(CommandKind.Add, Encode(entity), value, fields); }
}

internal sealed class EntityFieldCommands<T, TFields> : CommandBucket
    where T : struct where TFields : struct, IEntityFieldWriter<T>
{
    private readonly record struct Range(int Start, int Count);
    private T[] _values = [];
    private Range[] _ranges = [];
    private EntityFieldPatch[] _fields = [];
    private bool[]? _transferred;
    private int _fieldCount;
    internal int Count;
    public EntityFieldCommands() { }

    internal int Add(CommandBuffer commands, in T value, ReadOnlySpan<DeferredEntityField> fields)
    {
        if (Count == _values.Length)
        {
            int capacity = Math.Max(8, Count * 2);
            Array.Resize(ref _values, capacity); Array.Resize(ref _ranges, capacity);
            if (ComponentType<T>.HasValueOperationsType) Array.Resize(ref _transferred, capacity);
        }
        if (_fieldCount + fields.Length > _fields.Length)
            Array.Resize(ref _fields, Math.Max(_fieldCount + fields.Length, Math.Max(8, _fields.Length * 2)));
        for (int i = 0; i < fields.Length; i++) _fields[_fieldCount + i] = new(fields[i].Field, commands.Encode(fields[i].Entity));
        int index = Count;
        _values[index] = ComponentType<T>.HasValueOperationsType ? ComponentType<T>.CloneValue(commands.World, value) : value;
        _ranges[index] = new(_fieldCount, fields.Length);
        if (_transferred is not null) _transferred[index] = false;
        _fieldCount += fields.Length; Count++;
        return index;
    }

    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        int index = command.DataIndex;
        T value = _values[index];
        Range range = _ranges[index];
        for (int i = range.Start; i < range.Start + range.Count; i++)
        {
            EntityFieldPatch field = _fields[i];
            TFields.Set(ref value, field.Field, results!.Decode(field.Target));
        }
        if (command.Kind == CommandKind.Create) return world.Create(value);
        Entity entity = command.Target.Id >= 0 ? command.Target : results!.Decode(command.Target);
        if (command.Kind == CommandKind.Set) world.Set(entity, value); else world.Add(entity, value);
        if (_transferred is not null) _transferred[index] = true;
        return default;
    }

    internal override void Clear(World world)
    {
        if (ComponentType<T>.HasValueOperationsType)
            for (int i = 0; i < Count; i++)
                if (!_transferred![i]) ComponentType<T>.ReleaseValue(world, ref _values[i]);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(_values, 0, Count);
        Count = _fieldCount = 0;
    }
}
