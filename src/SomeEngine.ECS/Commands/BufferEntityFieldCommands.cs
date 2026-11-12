using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Commands;

public readonly record struct BufferEntityField(int Element, int Field, DeferredEntity Entity);
internal readonly record struct BufferEntityFieldPatch(int Element, int Field, Entity Target);

public sealed partial class CommandBuffer
{
    private void BufferFields<T, TFields>(CommandKind kind, Entity entity, ReadOnlySpan<T> values, ReadOnlySpan<BufferEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    {
        var bucket = Bucket<BufferEntityFieldCommands<T, TFields>>(out int type);
        if (bucket.Count == 0) Use(type);
        Append(kind, entity, type, bucket.Add(this, values, fields));
    }
    public void SetBuffer<T, TFields>(Entity entity, ReadOnlySpan<T> values, ReadOnlySpan<BufferEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    { RequireRecording(); BufferFields<T, TFields>(CommandKind.SetBuffer, entity, values, fields); }
    public void SetBuffer<T, TFields>(DeferredEntity entity, ReadOnlySpan<T> values, ReadOnlySpan<BufferEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    { RequireRecording(); BufferFields<T, TFields>(CommandKind.SetBuffer, Encode(entity), values, fields); }
    public void AppendBuffer<T, TFields>(Entity entity, ReadOnlySpan<T> values, ReadOnlySpan<BufferEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    { RequireRecording(); BufferFields<T, TFields>(CommandKind.AppendBuffer, entity, values, fields); }
    public void AppendBuffer<T, TFields>(DeferredEntity entity, ReadOnlySpan<T> values, ReadOnlySpan<BufferEntityField> fields)
        where T : struct where TFields : struct, IEntityFieldWriter<T>
    { RequireRecording(); BufferFields<T, TFields>(CommandKind.AppendBuffer, Encode(entity), values, fields); }
}

internal sealed class BufferEntityFieldCommands<T, TFields> : CommandBucket
    where T : struct where TFields : struct, IEntityFieldWriter<T>
{
    private readonly record struct Range(int Start, int Count, int FieldStart, int FieldCount);
    private T[] _values = [];
    private Range[] _ranges = [];
    private BufferEntityFieldPatch[] _fields = [];
    private int _valueCount, _fieldCount;
    internal int Count;
    public BufferEntityFieldCommands() { }

    internal int Add(CommandBuffer commands, ReadOnlySpan<T> values, ReadOnlySpan<BufferEntityField> fields)
    {
        if (_valueCount + values.Length > _values.Length)
            Array.Resize(ref _values, Math.Max(_valueCount + values.Length, Math.Max(8, _values.Length * 2)));
        if (_fieldCount + fields.Length > _fields.Length)
            Array.Resize(ref _fields, Math.Max(_fieldCount + fields.Length, Math.Max(8, _fields.Length * 2)));
        if (Count == _ranges.Length) Array.Resize(ref _ranges, Math.Max(8, Count * 2));
        for (int i = 0; i < fields.Length; i++)
            _fields[_fieldCount + i] = new(fields[i].Element, fields[i].Field, commands.Encode(fields[i].Entity));
        int index = Count++;
        _ranges[index] = new(_valueCount, values.Length, _fieldCount, fields.Length);
        values.CopyTo(_values.AsSpan(_valueCount));
        _valueCount += values.Length; _fieldCount += fields.Length;
        return index;
    }

    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        Range range = _ranges[command.DataIndex];
        for (int i = range.FieldStart; i < range.FieldStart + range.FieldCount; i++)
        {
            BufferEntityFieldPatch field = _fields[i];
            TFields.Set(ref _values[range.Start + field.Element], field.Field, results!.Decode(field.Target));
        }
        Entity target = command.Target.Id >= 0 ? command.Target : results!.Decode(command.Target);
        if (!world.Has<Buffer<T>>(target)) world.AddBuffer<T>(target, range.Count);
        using var buffer = world.GetBuffer<T>(target);
        if (command.Kind == CommandKind.SetBuffer) buffer.Clear();
        buffer.AddRange(_values.AsSpan(range.Start, range.Count));
        return default;
    }

    internal override void Clear(World world)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(_values, 0, _valueCount);
        Count = _valueCount = _fieldCount = 0;
    }
}
