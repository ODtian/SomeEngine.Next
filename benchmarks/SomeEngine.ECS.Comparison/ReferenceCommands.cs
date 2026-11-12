using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Comparison;

internal interface ICommandValues<TEntity, TKey> where TEntity : struct where TKey : struct
{
    TKey Key(TEntity entity);
    TEntity Entity(TKey key);
    TEntity Create<T>(in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    void Set<T>(TEntity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    void Add<T>(TEntity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    void Remove<T>(TEntity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    bool Has<T>(TEntity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    T Read<T>(TEntity entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
    bool Alive(TEntity entity);
    void Destroy(TEntity entity);
}

internal sealed class ReferenceCommandResults<TEntity> where TEntity : struct
{
    internal TEntity[] Created = [];
    internal int[]? Offsets;
    internal int Completed;
    internal int Index(int producer, int local) => (Offsets is { } offsets ? offsets[producer] : 0) + local;
}

internal readonly struct ReferenceDeferredEntity<TEntity>(ReferenceCommandResults<TEntity> results, int producer, int local)
    where TEntity : struct
{
    internal readonly ReferenceCommandResults<TEntity> Results = results;
    internal readonly int Producer = producer, Local = local;
    internal TEntity Resolve()
    {
        int completed = Volatile.Read(ref Results.Completed);
        int index = Results.Index(Producer, Local);
        return index < completed ? Results.Created[index]
            : throw new InvalidOperationException("Create results become available when playback completes the command.");
    }
}

internal enum ReferenceCommandKind { Create, Set, Add, Remove, Destroy }
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct ReferenceCommandHeader<TKey>(TKey Key, int Code, int Data, int Temporary) where TKey : struct;
internal readonly record struct ReferenceCommandTarget(int Producer, int Local);
internal static class ReferenceCommandTypes
{
    private static int _next;
    internal static int Next() => Interlocked.Increment(ref _next) - 1;
}
internal static class ReferenceCommandType<T> { internal static readonly int Id = ReferenceCommandTypes.Next(); }

internal sealed class ReferenceCommands<TEntity, TKey, TValues> : IDisposable
    where TEntity : struct where TKey : struct where TValues : struct, ICommandValues<TEntity, TKey>
{
    internal readonly TValues Values;
    private readonly Writer[] _writers;
    private ReferenceCommandResults<TEntity> _results = new();
    internal Writer this[int producer] => _writers[producer];

    internal ReferenceCommands(TValues values, int producers)
    {
        Values = values;
        _writers = new Writer[producers];
        for (int i = 0; i < producers; i++) _writers[i] = new(this, i, _results);
    }

    internal void Playback()
    {
        int count = 0;
        if (_writers.Length > 1) _results.Offsets = new int[_writers.Length];
        for (int i = 0; i < _writers.Length; i++)
        {
            _writers[i].Seal();
            if (_results.Offsets is { } offsets) offsets[i] = count;
            count += _writers[i].CreateCount;
        }
        _results.Created = new TEntity[count];
        int completed = 0;
        try
        {
            for (int i = 0; i < _writers.Length; i++)
                _writers[i].Play(_results.Offsets is { } offsets ? offsets[i] : 0, ref completed);
        }
        finally { Volatile.Write(ref _results.Completed, completed); }
    }

    internal void Clear()
    {
        _results = new();
        foreach (Writer writer in _writers) writer.Reset(_results);
    }

    public void Dispose()
    {
        foreach (Writer writer in _writers) writer.Release();
        _results = null!;
    }

    private abstract class Bucket
    {
        internal abstract TEntity Execute(TValues values, ReferenceCommandKind kind, TEntity entity, int data);
        internal abstract void Clear();
    }
    private sealed class Bucket<T> : Bucket where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    {
        private T[] _values = [];
        internal int Count;
        internal int Add(in T value)
        {
            if (Count == _values.Length) Array.Resize(ref _values, Math.Max(8, Count * 2));
            int index = Count++;
            _values[index] = value;
            return index;
        }
        internal override TEntity Execute(TValues values, ReferenceCommandKind kind, TEntity entity, int data)
        {
            switch (kind)
            {
                case ReferenceCommandKind.Create: return values.Create(_values[data]);
                case ReferenceCommandKind.Set: values.Set(entity, _values[data]); break;
                case ReferenceCommandKind.Add: values.Add(entity, _values[data]); break;
                case ReferenceCommandKind.Remove: values.Remove<T>(entity); break;
            }
            return default;
        }
        internal override void Clear()
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>()) Array.Clear(_values, 0, Count);
            Count = 0;
        }
    }

    internal sealed class Writer
    {
        private readonly ReferenceCommands<TEntity, TKey, TValues> _commands;
        private readonly int _producer;
        private ReferenceCommandResults<TEntity> _results;
        private ReferenceCommandHeader<TKey>[] _headers = new ReferenceCommandHeader<TKey>[16];
        private Bucket?[] _buckets = [];
        private int[] _used = [];
        private ReferenceCommandTarget[] _foreign = [];
        private int _count, _usedCount, _foreignCount;
        private bool _closed;
        internal int CreateCount, CompletedCommands;

        internal Writer(ReferenceCommands<TEntity, TKey, TValues> commands, int producer, ReferenceCommandResults<TEntity> results)
            => (_commands, _producer, _results) = (commands, producer, results);

        private void Recording()
        {
            if (_closed) throw new InvalidOperationException("Clear the command batch before recording its next sequence.");
        }
        private Bucket<T> Values<T>(out int type) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
        {
            type = ReferenceCommandType<T>.Id;
            if ((uint)type < (uint)_buckets.Length && _buckets[type] is { } found) return (Bucket<T>)found;
            if (type >= _buckets.Length) Array.Resize(ref _buckets, Math.Max(type + 1, Math.Max(4, _buckets.Length * 2)));
            var bucket = new Bucket<T>(); _buckets[type] = bucket; return bucket;
        }
        private int Data<T>(in T value, out int type) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
        {
            var bucket = Values<T>(out type);
            if (bucket.Count == 0)
            {
                if (_usedCount == _used.Length) Array.Resize(ref _used, Math.Max(4, _usedCount * 2));
                _used[_usedCount++] = type;
            }
            return bucket.Add(value);
        }
        private void Append(ReferenceCommandKind kind, int type, int data, TKey key = default, int temporary = 0, int mode = 0)
        {
            if (_count == _headers.Length) Array.Resize(ref _headers, Math.Max(16, _count * 2));
            _headers[_count++] = new(key, (type << 8) | mode | (int)kind, data, temporary);
        }
        private (int Mode, int Index) Encode(ReferenceDeferredEntity<TEntity> entity)
        {
            if (entity.Results != _results) throw new ArgumentException("The deferred entity belongs to another command batch.");
            if (entity.Producer == _producer) return (16, entity.Local);
            if (_foreignCount == _foreign.Length) Array.Resize(ref _foreign, Math.Max(4, _foreignCount * 2));
            int index = _foreignCount++; _foreign[index] = new(entity.Producer, entity.Local); return (32, index);
        }
        internal ReferenceDeferredEntity<TEntity> Create<T>(in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
        {
            Recording(); int data = Data(value, out int type), local = CreateCount++;
            Append(ReferenceCommandKind.Create, type, data, temporary: local);
            return new(_results, _producer, local);
        }
        internal void Set<T>(TEntity entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
        { Recording(); int data = Data(value, out int type); Append(ReferenceCommandKind.Set, type, data, _commands.Values.Key(entity)); }
        internal void Set<T>(ReferenceDeferredEntity<TEntity> entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
        { Recording(); var target = Encode(entity); int data = Data(value, out int type); Append(ReferenceCommandKind.Set, type, data, temporary: target.Index, mode: target.Mode); }
        internal void Add<T>(ReferenceDeferredEntity<TEntity> entity, in T value) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
        { Recording(); var target = Encode(entity); int data = Data(value, out int type); Append(ReferenceCommandKind.Add, type, data, temporary: target.Index, mode: target.Mode); }
        internal void Remove<T>(ReferenceDeferredEntity<TEntity> entity) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
        { Recording(); var target = Encode(entity); Values<T>(out int type); Append(ReferenceCommandKind.Remove, type, 0, temporary: target.Index, mode: target.Mode); }
        internal void Destroy(ReferenceDeferredEntity<TEntity> entity)
        { Recording(); var target = Encode(entity); Append(ReferenceCommandKind.Destroy, 0, 0, temporary: target.Index, mode: target.Mode); }

        internal void Seal() { Recording(); _closed = true; }
        internal void Play(int offset, ref int completed)
        {
            TValues values = _commands.Values;
            for (int i = 0; i < _count; i++)
            {
                var header = _headers[i];
                var kind = (ReferenceCommandKind)(header.Code & 15);
                int type = header.Code >> 8;
                if (kind == ReferenceCommandKind.Create)
                {
                    _results.Created[offset + header.Temporary] = _buckets[type]!.Execute(values, kind, default, header.Data);
                    completed++;
                }
                else
                {
                    int mode = header.Code & 48;
                    TEntity entity;
                    if (mode == 0) entity = values.Entity(header.Key);
                    else if (mode == 16) entity = _results.Created[offset + header.Temporary];
                    else
                    {
                        var target = _foreign[header.Temporary];
                        entity = _results.Created[_results.Index(target.Producer, target.Local)];
                    }
                    if (kind == ReferenceCommandKind.Destroy) values.Destroy(entity);
                    else _buckets[type]!.Execute(values, kind, entity, header.Data);
                }
                CompletedCommands = i + 1;
            }
            ClearPayloads(); _count = 0;
        }
        private void ClearPayloads()
        {
            for (int i = 0; i < _usedCount; i++) _buckets[_used[i]]!.Clear();
            _usedCount = _foreignCount = 0;
        }
        internal void Reset(ReferenceCommandResults<TEntity> results)
        {
            ClearPayloads(); _count = CreateCount = CompletedCommands = 0; _closed = false; _results = results;
        }
        internal void Release()
        {
            ClearPayloads(); _headers = []; _buckets = []; _used = []; _foreign = []; _results = null!; _closed = true;
            _count = CreateCount = CompletedCommands = 0;
        }
    }
}
