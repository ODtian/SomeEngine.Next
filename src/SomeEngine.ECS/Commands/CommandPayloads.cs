using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Commands;

internal sealed class ValueCommands<T> : CommandBucket where T : struct
{
    private T[] _values = [];
    private bool[]? _transferred;
    internal int Count;
    public ValueCommands() { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Add(World world, in T value)
    {
        if (Count == _values.Length) Grow();
        int index = Count;
        if (ComponentType<T>.HasValueOperationsType)
        {
            _values[index] = ComponentType<T>.CloneValue(world, value);
            _transferred![index] = false;
        }
        else _values[index] = value;
        Count++;
        return index;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow()
    {
        int capacity = Math.Max(8, Count * 2);
        Array.Resize(ref _values, capacity);
        if (ComponentType<T>.HasValueOperationsType) Array.Resize(ref _transferred, capacity);
    }

    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        if (command.Kind == CommandKind.Create) return world.Create(_values[command.DataIndex]);
        Entity target = command.Target.Id >= 0 ? command.Target : results!.Decode(command.Target);
        bool transfer = false;
        switch (command.Kind)
        {
            case CommandKind.Set: world.Set(target, _values[command.DataIndex]); transfer = true; break;
            case CommandKind.Add: world.Add(target, _values[command.DataIndex]); transfer = true; break;
            case CommandKind.Remove: world.Remove<T>(target); break;
            case CommandKind.SetSparse: world.SetSparse(target, _values[command.DataIndex]); transfer = true; break;
            case CommandKind.RemoveSparse: world.RemoveSparse<T>(target); break;
        }
        if (ComponentType<T>.HasValueOperationsType && transfer) _transferred![command.DataIndex] = true;
        return default;
    }

    internal override void Clear(World world)
    {
        if (ComponentType<T>.HasValueOperationsType)
            for (int i = 0; i < Count; i++)
                if (!_transferred![i]) ComponentType<T>.ReleaseValue(world, ref _values[i]);
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(_values, 0, Count);
        Count = 0;
    }
}

internal sealed class EnableCommands<T> : CommandBucket where T : struct, IEnableableComponent
{
    public EnableCommands() { }
    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        Entity target = command.Target.Id >= 0 ? command.Target : results!.Decode(command.Target);
        world.SetEnabled<T>(target, command.Kind == CommandKind.Enable);
        return default;
    }
    internal override void Clear(World world) { }
}

internal sealed class BufferCommands<T> : CommandBucket where T : struct
{
    private readonly record struct Range(int Start, int Count);
    private T[] _values = [];
    private Range[] _ranges = [];
    private int _valueCount;
    internal int Count;
    public BufferCommands() { }

    internal int Add(ReadOnlySpan<T> values)
    {
        if (_valueCount + values.Length > _values.Length)
            Array.Resize(ref _values, Math.Max(_valueCount + values.Length, Math.Max(8, _values.Length * 2)));
        if (Count == _ranges.Length) Array.Resize(ref _ranges, Math.Max(8, Count * 2));
        int range = Count++;
        _ranges[range] = new(_valueCount, values.Length);
        values.CopyTo(_values.AsSpan(_valueCount));
        _valueCount += values.Length;
        return range;
    }

    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        Entity target = command.Target.Id >= 0 ? command.Target : results!.Decode(command.Target);
        if (command.Kind == CommandKind.RemoveBuffer) world.RemoveBuffer<T>(target);
        else
        {
            Range range = _ranges[command.DataIndex];
            if (!world.Has<Buffer<T>>(target)) world.AddBuffer<T>(target, range.Count);
            using var buffer = world.GetBuffer<T>(target);
            if (command.Kind == CommandKind.SetBuffer) buffer.Clear();
            buffer.AddRange(_values.AsSpan(range.Start, range.Count));
        }
        return default;
    }

    internal override void Clear(World world)
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(_values, 0, _valueCount);
        Count = _valueCount = 0;
    }
}
