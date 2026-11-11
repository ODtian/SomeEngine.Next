using Arch.Core;
using EasyEcs;
using Friflo.Engine.ECS;

namespace SomeEngine.ECS.Comparison;

public struct BufferItem : ITrackedComponent, EasyEcs.IComponent
{
    public int Value, Other;
}

[Friflo.Engine.ECS.BlittableType]
public struct ReferenceBuffer : Friflo.Engine.ECS.IComponent
{
    internal ReferenceBufferData Data;
    internal ulong Written;
}

internal sealed class ReferenceBufferData
{
    internal BufferItem[] Items = [];
    internal int Count;
    internal void Reserve(int count)
    {
        if (count <= Items.Length) return;
        var items = new BufferItem[Math.Max(count, Math.Max(4, Items.Length * 2))];
        Items.AsSpan(0, Count).CopyTo(items);
        Items = items;
    }
    internal void AddRange(ReadOnlySpan<BufferItem> values)
    {
        int start = Count;
        Reserve(start + values.Length);
        values.CopyTo(Items.AsSpan(start));
        Count = start + values.Length;
    }
    internal void Insert(int index, in BufferItem value)
    {
        int count = Count;
        Reserve(count + 1);
        BufferItem[] items = Items;
        items.AsSpan(index, count - index).CopyTo(items.AsSpan(index + 1));
        items[index] = value;
        Count = count + 1;
    }
    internal void RemoveAt(int index)
    {
        int last = Count - 1;
        BufferItem[] items = Items;
        items.AsSpan(index + 1, last - index).CopyTo(items.AsSpan(index));
        Count = last;
    }
}

internal sealed class ReferenceBufferPool
{
    private ReferenceBufferData[] _free = [];
    private int _count;
    internal ReferenceBufferData Rent(int capacity)
    {
        ReferenceBufferData data;
        if (_count == 0) data = new();
        else { data = _free[--_count]; _free[_count] = null!; }
        data.Reserve(capacity);
        return data;
    }
    internal ReferenceBufferData Clone(ReferenceBufferData source)
    {
        ReferenceBufferData copy = Rent(source.Count);
        source.Items.AsSpan(0, source.Count).CopyTo(copy.Items);
        copy.Count = source.Count;
        return copy;
    }
    internal void Return(ReferenceBufferData data)
    {
        data.Count = 0;
        if (data.Items.Length * 8 > 4096) return;
        if (_count == _free.Length) Array.Resize(ref _free, Math.Max(4, _count * 2));
        _free[_count++] = data;
    }
    internal void Clear() { _free = []; _count = 0; }
}

internal static class BufferInputs
{
    private static readonly BufferItem[] Four = Create(4), ThirtyTwo = Create(32);
    internal static BufferItem[] For(int length) => length == 4 ? Four : ThirtyTwo;
    private static BufferItem[] Create(int length)
    {
        var values = new BufferItem[length];
        for (int i = 0; i < length; i++) values[i].Value = i;
        return values;
    }
}

internal struct CountEasyBuffers : EasyEcs.IForEachBatch<EasyEcs.DynamicBuffer<BufferItem>>
{
    public static int Writes => 0;
    internal int Count;
    public void Execute(EasyEcs.EntitySpan entities, Span<EasyEcs.DynamicBuffer<BufferItem>> buffers) => Count += buffers.Length;
}

internal readonly struct PrepareEasyBuffers(BufferItem[] input, int capacity) : EasyEcs.IForEachBatch<EasyEcs.DynamicBuffer<BufferItem>>
{
    public static int Writes => 1;
    public void Execute(EasyEcs.EntitySpan entities, Span<EasyEcs.DynamicBuffer<BufferItem>> buffers)
    {
        foreach (ref var buffer in buffers)
        {
            buffer.EnsureCapacity(capacity);
            buffer.AddRange(input);
        }
    }
}

internal sealed partial class SomeEngineWorld
{
    private Query? _buffersChanged;
    private BufferItem[]? _bufferInput;

    private void PrepareBuffers(string workload)
    {
        if (_bufferInput is not null) return;
        using var structure = _world.OpenStructureWrite();
        BufferLength = workload == "buffer-4" ? 4 : 32;
        _bufferInput = BufferInputs.For(BufferLength);
        if (!_world.Has<Buffer<BufferItem>>(_entities[0]))
            foreach (var entity in _entities) _world.AddBuffer<BufferItem>(entity, BufferLength * 2 + 1);
        using (var scope = _world.Query<Buffer<BufferItem>>().Open())
            foreach (var rows in scope)
            {
                var buffers = rows.GetBuffers<BufferItem>();
                for (int row = 0; row < rows.Count; row++)
                {
                    using var buffer = buffers[row];
                    buffer.EnsureCapacity(BufferLength * 2 + 1);
                    buffer.AddRange(_bufferInput);
                }
            }
        _buffersChanged = _world.Query(new QueryDescription().WithChangedBuffer<BufferItem>(0));
    }

    internal override void BufferCycle()
    {
        using var structure = _world.OpenStructureWrite();
        ulong before = _world.Version;
        long total = 0;
        foreach (var entity in _entities)
        {
            using (var buffer = _world.GetBuffer<BufferItem>(entity))
            {
                buffer.AddRange(_bufferInput);
                buffer.Insert(BufferLength, new BufferItem { Value = 99 });
                buffer.RemoveAt(BufferLength);
            }
            var clone = _world.Clone(entity);
            using (var buffer = _world.GetBuffer<BufferItem>(clone))
            {
                buffer[0].Value++;
                total += buffer.Count + buffer[0].Value;
            }
            using (var buffer = _world.ReadBuffer<BufferItem>(entity)) total += buffer[0].Value;
            _world.Destroy(clone);
            using (var buffer = _world.GetBuffer<BufferItem>(entity)) { buffer.Clear(); buffer.AddRange(_bufferInput); }
        }
        ReadTotal = total;
        BufferChangedCount = CountQuery(_buffersChanged.GetValueOrDefault().Since(before));
    }
}

internal sealed partial class EasyEcsWorld
{
    private BufferItem[]? _bufferInput;
    private void PrepareBuffers(string workload)
    {
        if (_bufferInput is not null) return;
        BufferLength = workload == "buffer-4" ? 4 : 32;
        _bufferInput = BufferInputs.For(BufferLength);
        bool declared = _entities[0].Has<EasyEcs.DynamicBuffer<BufferItem>>();
        if (!declared)
            foreach (var entity in _entities) entity.Set(new EasyEcs.DynamicBuffer<BufferItem>());
        var prepare = new PrepareEasyBuffers(_bufferInput, BufferLength * 2 + 1);
        _world.ForEachBatch<EasyEcs.DynamicBuffer<BufferItem>, PrepareEasyBuffers>(ref prepare);
    }
    internal override void BufferCycle()
    {
        ulong since = _world.ChangeTick + 1;
        _world.Update();
        long total = 0;
        foreach (var entity in _entities)
        {
            ref var buffer = ref entity.Get<EasyEcs.DynamicBuffer<BufferItem>>();
            buffer.AddRange(_bufferInput);
            buffer.Insert(BufferLength, new BufferItem { Value = 99 });
            buffer.RemoveAt(BufferLength);
            var clone = entity.Clone();
            ref var copied = ref clone.Get<EasyEcs.DynamicBuffer<BufferItem>>();
            copied.AsSpan()[0].Value++;
            total += copied.Count + copied.AsReadOnlySpan()[0].Value + entity.Read<EasyEcs.DynamicBuffer<BufferItem>>().AsReadOnlySpan()[0].Value;
            clone.Destroy();
            ref var retained = ref entity.Get<EasyEcs.DynamicBuffer<BufferItem>>();
            retained.Clear();
            retained.AddRange(_bufferInput);
        }
        var filter = EasyEcs.QueryFilter.Create().Changed<EasyEcs.DynamicBuffer<BufferItem>>(since);
        var changed = new CountEasyBuffers();
        _world.ForEachBatch<EasyEcs.DynamicBuffer<BufferItem>, CountEasyBuffers>(ref changed, in filter);
        BufferChangedCount = changed.Count;
        ReadTotal = total;
    }
}

internal sealed partial class FrifloWorld
{
    private BufferItem[]? _bufferInput;
    private ReferenceBufferPool? _bufferPool;
    private ArchetypeQuery<ReferenceBuffer>? _buffers;
    private ulong _bufferVersion;
    private void PrepareBuffers(string workload)
    {
        if (_bufferInput is not null) return;
        BufferLength = workload == "buffer-4" ? 4 : 32;
        _bufferInput = BufferInputs.For(BufferLength);
        _bufferPool = new();
        bool declared = _entities[0].HasComponent<ReferenceBuffer>();
        if (!declared)
            foreach (var entity in _entities) entity.AddComponent(new ReferenceBuffer());
        _buffers = _world.Query<ReferenceBuffer>();
        foreach (var (buffers, _) in _buffers.Chunks)
            foreach (ref var value in buffers.Span)
            {
                ReferenceBufferData data = _bufferPool.Rent(BufferLength * 2 + 1);
                data.AddRange(_bufferInput);
                value = new ReferenceBuffer { Data = data, Written = ++_bufferVersion };
            }
    }
    internal override void BufferCycle()
    {
        ulong before = _bufferVersion;
        long total = 0;
        foreach (var entity in _entities)
        {
            ref var value = ref entity.GetComponent<ReferenceBuffer>();
            value.Written = ++_bufferVersion;
            ReferenceBufferData data = value.Data;
            data.AddRange(_bufferInput);
            data.Insert(BufferLength, new BufferItem { Value = 99 });
            data.RemoveAt(BufferLength);
            var clone = entity.CloneEntity();
            ref var copiedValue = ref clone.GetComponent<ReferenceBuffer>();
            copiedValue.Data = _bufferPool!.Clone(data);
            copiedValue.Written = ++_bufferVersion;
            ReferenceBufferData copied = copiedValue.Data;
            copied.Items[0].Value++;
            total += copied.Count + copied.Items[0].Value + entity.GetComponent<ReferenceBuffer>().Data.Items[0].Value;
            _bufferPool.Return(copied);
            clone.DeleteEntity();
            ref var retained = ref entity.GetComponent<ReferenceBuffer>();
            retained.Written = ++_bufferVersion;
            retained.Data.Count = 0;
            retained.Data.AddRange(_bufferInput);
        }
        int count = 0;
        foreach (var (buffers, _) in _buffers!.Chunks)
            foreach (ref readonly var value in buffers.Span) if (value.Written > before) count++;
        BufferChangedCount = count;
        ReadTotal = total;
    }
    private void ReleaseBuffers()
    {
        if (_buffers is null) return;
        foreach (var (buffers, _) in _buffers.Chunks)
            foreach (ref var value in buffers.Span) { _bufferPool!.Return(value.Data!); value = default; }
        _bufferPool!.Clear();
    }
}

internal sealed partial class ArchWorld
{
    private BufferItem[]? _bufferInput;
    private ReferenceBufferPool? _bufferPool;
    private Arch.Core.QueryDescription _buffers;
    private ulong _bufferVersion;
    private void PrepareBuffers(string workload)
    {
        if (_bufferInput is not null) return;
        BufferLength = workload == "buffer-4" ? 4 : 32;
        _bufferInput = BufferInputs.For(BufferLength);
        _bufferPool = new();
        bool declared = _world.Has<ReferenceBuffer>(_entities[0]);
        if (!declared)
            foreach (var entity in _entities) _world.Add(entity, new ReferenceBuffer());
        _buffers = new Arch.Core.QueryDescription().WithAll<ReferenceBuffer>();
        foreach (ref var chunk in _world.Query(in _buffers).GetChunkIterator())
            foreach (ref var value in chunk.GetSpan<ReferenceBuffer>()[..chunk.Count])
            {
                ReferenceBufferData data = _bufferPool.Rent(BufferLength * 2 + 1);
                data.AddRange(_bufferInput);
                value = new ReferenceBuffer { Data = data, Written = ++_bufferVersion };
            }
    }
    private Arch.Core.Entity CloneBufferEntity(Arch.Core.Entity source, ReferenceBufferData data)
    {
        var position = _world.Get<Position>(source);
        var velocity = _world.Get<Velocity>(source);
        var health = _world.Get<Health>(source);
        var buffer = new ReferenceBuffer { Data = _bufferPool!.Clone(data), Written = ++_bufferVersion };
        if (_world.Has<Sleeping>(source)) return _world.Create(position, velocity, health, new Team(), new Sleeping(), buffer);
        if (_world.Has<Team>(source)) return _world.Create(position, velocity, health, new Team(), buffer);
        return _world.Create(position, velocity, health, buffer);
    }
    internal override void BufferCycle()
    {
        ulong before = _bufferVersion;
        long total = 0;
        foreach (var entity in _entities)
        {
            ref var value = ref _world.Get<ReferenceBuffer>(entity);
            value.Written = ++_bufferVersion;
            ReferenceBufferData data = value.Data;
            data.AddRange(_bufferInput);
            data.Insert(BufferLength, new BufferItem { Value = 99 });
            data.RemoveAt(BufferLength);
            var clone = CloneBufferEntity(entity, data);
            ref var copiedValue = ref _world.Get<ReferenceBuffer>(clone);
            copiedValue.Written = ++_bufferVersion;
            ReferenceBufferData copied = copiedValue.Data;
            copied.Items[0].Value++;
            total += copied.Count + copied.Items[0].Value + _world.Get<ReferenceBuffer>(entity).Data.Items[0].Value;
            _bufferPool!.Return(copied);
            _world.Destroy(clone);
            ref var retained = ref _world.Get<ReferenceBuffer>(entity);
            retained.Written = ++_bufferVersion;
            retained.Data.Count = 0;
            retained.Data.AddRange(_bufferInput);
        }
        int count = 0;
        foreach (ref var chunk in _world.Query(in _buffers).GetChunkIterator())
            foreach (ref readonly var value in chunk.GetSpan<ReferenceBuffer>()[..chunk.Count]) if (value.Written > before) count++;
        BufferChangedCount = count;
        ReadTotal = total;
    }
    private void ReleaseBuffers()
    {
        if (_bufferPool is null) return;
        foreach (ref var chunk in _world.Query(in _buffers).GetChunkIterator())
            foreach (ref var value in chunk.GetSpan<ReferenceBuffer>()[..chunk.Count]) { _bufferPool.Return(value.Data!); value = default; }
        _bufferPool.Clear();
    }
}

internal sealed partial class DefaultEcsWorld
{
    private BufferItem[]? _bufferInput;
    private ReferenceBufferPool? _bufferPool;
    private ulong _bufferVersion;
    private void PrepareBuffers(string workload)
    {
        if (_bufferInput is not null) return;
        BufferLength = workload == "buffer-4" ? 4 : 32;
        _bufferInput = BufferInputs.For(BufferLength);
        _bufferPool = new();
        if (!_entities[0].Has<ReferenceBuffer>())
            foreach (var entity in _entities) entity.Set(new ReferenceBuffer());
        foreach (ref var value in _world.GetAll<ReferenceBuffer>())
        {
            ReferenceBufferData data = _bufferPool.Rent(BufferLength * 2 + 1);
            data.AddRange(_bufferInput);
            value = new ReferenceBuffer { Data = data, Written = ++_bufferVersion };
        }
    }
    internal override void BufferCycle()
    {
        ulong before = _bufferVersion;
        long total = 0;
        foreach (var entity in _entities)
        {
            ref var value = ref entity.Get<ReferenceBuffer>();
            value.Written = ++_bufferVersion;
            ReferenceBufferData data = value.Data;
            data.AddRange(_bufferInput);
            data.Insert(BufferLength, new BufferItem { Value = 99 });
            data.RemoveAt(BufferLength);
            var clone = entity.CopyTo(_world);
            ref var copiedValue = ref clone.Get<ReferenceBuffer>();
            copiedValue.Data = _bufferPool!.Clone(data);
            copiedValue.Written = ++_bufferVersion;
            ReferenceBufferData copied = copiedValue.Data;
            copied.Items[0].Value++;
            total += copied.Count + copied.Items[0].Value + entity.Get<ReferenceBuffer>().Data.Items[0].Value;
            _bufferPool.Return(copied);
            clone.Dispose();
            ref var retained = ref entity.Get<ReferenceBuffer>();
            retained.Written = ++_bufferVersion;
            retained.Data.Count = 0;
            retained.Data.AddRange(_bufferInput);
        }
        int count = 0;
        foreach (ref readonly var value in _world.GetAll<ReferenceBuffer>()) if (value.Written > before) count++;
        BufferChangedCount = count;
        ReadTotal = total;
    }
    private void ReleaseBuffers()
    {
        if (_bufferPool is null) return;
        foreach (ref var value in _world.GetAll<ReferenceBuffer>()) { _bufferPool.Return(value.Data!); value = default; }
        _bufferPool.Clear();
    }
}
