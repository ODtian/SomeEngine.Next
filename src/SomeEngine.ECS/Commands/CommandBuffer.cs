using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Commands;

internal enum CommandKind
{
    Create, Destroy, Set, Add, Remove, Enable, Disable,
    SetBuffer, AppendBuffer, RemoveBuffer, Composite,
}

internal readonly record struct CommandHeader(CommandKind Kind, Entity Target, int TypeId, int DataIndex);

internal abstract class CommandBucket
{
    internal abstract Entity Execute(World world, in CommandHeader command, CommandResults? results);
    internal abstract void Clear(World world);
}

internal static class CommandTypes
{
    private static int _next;
    internal static int Next() => Interlocked.Increment(ref _next) - 1;
}

internal static class CommandType<T> where T : CommandBucket
{
    internal static readonly int Id = CommandTypes.Next();
}

public sealed partial class CommandBuffer : IDisposable
{
    internal readonly World World;
    private readonly CommandBatch? _batch;
    private readonly int _producer;
    private CommandHeader[] _commands;
    private CommandBucket?[] _buckets = [];
    private int[] _usedTypes = [];
    private int _count, _usedCount, _createCount;
    private bool _closed, _disposed;
    private CommandResults? _results;

    public int Count => _count;
    public int CompletedCommands { get; private set; }
    internal int CreateCount => _createCount;
    internal int Producer => _producer;

    public CommandBuffer(World world, int capacity = 16)
        => (World, _commands) = (world, new CommandHeader[capacity]);

    internal CommandBuffer(World world, CommandBatch batch, int producer, int capacity, CommandResults results)
        : this(world, capacity) => (_batch, _producer, _results) = (batch, producer, results);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RequireRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_closed) throw new InvalidOperationException("Clear the command batch before recording its next sequence.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal TBucket Bucket<TBucket>(out int type) where TBucket : CommandBucket, new()
    {
        type = CommandType<TBucket>.Id;
        if ((uint)type < (uint)_buckets.Length && _buckets[type] is { } found) return Unsafe.As<TBucket>(found);
        return AddBucket<TBucket>(type);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private TBucket AddBucket<TBucket>(int type) where TBucket : CommandBucket, new()
    {
        if (type >= _buckets.Length) Array.Resize(ref _buckets, Math.Max(type + 1, Math.Max(4, _buckets.Length * 2)));
        var bucket = new TBucket();
        _buckets[type] = bucket;
        return bucket;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Use(int type)
    {
        if (_usedCount == _usedTypes.Length) GrowUsedTypes();
        _usedTypes[_usedCount++] = type;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowUsedTypes() => Array.Resize(ref _usedTypes, Math.Max(4, _usedCount * 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(CommandKind kind, Entity target, int type, int dataIndex)
    {
        if (_count == _commands.Length) GrowCommands();
        _commands[_count++] = new(kind, target, type, dataIndex);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void GrowCommands() => Array.Resize(ref _commands, Math.Max(16, _count * 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity Encode(DeferredEntity entity)
    {
        if (entity.Results is null || entity.Results != _results)
            throw new ArgumentException("The deferred entity belongs to another command batch.");
        return new(-entity.LocalId - 1, entity.Producer + 1);
    }

    internal Entity Encode(CommandEntity entity, int batchCreates = 0)
    {
        if (entity.Results is not null) return Encode(new DeferredEntity(entity.Results, entity.Generation, entity.Id));
        if (entity.Id < 0 && (entity.Generation != 0 || (uint)(-entity.Id - 1) >= (uint)batchCreates))
            throw new ArgumentException("The batch entity index must name a creation in this composite command.");
        return new(entity.Id, entity.Generation);
    }

    internal int ReserveCreates(Span<DeferredEntity> created)
    {
        int first = _createCount;
        if (created.IsEmpty) return first;
        CommandResults results = _results ??= new();
        for (int i = 0; i < created.Length; i++) created[i] = new(results, _producer, first + i);
        _createCount += created.Length;
        return first;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private DeferredEntity NewCreate(int type, int data)
    {
        CommandResults results = _results ??= new();
        int local = _createCount++;
        Append(CommandKind.Create, new(local, 0), type, data);
        return new(results, _producer, local);
    }

    public DeferredEntity Create()
    {
        RequireRecording();
        return NewCreate(-1, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DeferredEntity Create<T>(in T value) where T : struct
    {
        RequireRecording();
        var bucket = Bucket<ValueCommands<T>>(out int type);
        if (bucket.Count == 0) Use(type);
        int data = bucket.Add(World, value);
        return NewCreate(type, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Value<T>(CommandKind kind, Entity target, in T value) where T : struct
    {
        if (ComponentType<T>.IsControlledRemovalType)
            throw new InvalidOperationException("Record relationship edits through their typed command entry.");
        var bucket = Bucket<ValueCommands<T>>(out int type);
        if (bucket.Count == 0) Use(type);
        Append(kind, target, type, bucket.Add(World, value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Typed<T>(CommandKind kind, Entity target) where T : struct
    {
        if (ComponentType<T>.IsControlledRemovalType)
            throw new InvalidOperationException("Record relationship edits through their typed command entry.");
        Bucket<ValueCommands<T>>(out int type);
        Append(kind, target, type, 0);
    }

    public void Set<T>(Entity entity, in T value) where T : struct
    { RequireRecording(); Value(CommandKind.Set, entity, value); }
    public void Set<T>(DeferredEntity entity, in T value) where T : struct
    { RequireRecording(); Value(CommandKind.Set, Encode(entity), value); }
    public void Add<T>(Entity entity, in T value) where T : struct
    { RequireRecording(); Value(CommandKind.Add, entity, value); }
    public void Add<T>(DeferredEntity entity, in T value) where T : struct
    { RequireRecording(); Value(CommandKind.Add, Encode(entity), value); }
    public void Remove<T>(Entity entity) where T : struct
    { RequireRecording(); Typed<T>(CommandKind.Remove, entity); }
    public void Remove<T>(DeferredEntity entity) where T : struct
    { RequireRecording(); Typed<T>(CommandKind.Remove, Encode(entity)); }
    public void Destroy(Entity entity)
    { RequireRecording(); Append(CommandKind.Destroy, entity, -1, 0); }
    public void Destroy(DeferredEntity entity)
    { RequireRecording(); Append(CommandKind.Destroy, Encode(entity), -1, 0); }

    private void Enabled<T>(Entity target, bool enabled) where T : struct, IEnableableComponent
    {
        Bucket<EnableCommands<T>>(out int type);
        Append(enabled ? CommandKind.Enable : CommandKind.Disable, target, type, 0);
    }
    public void Enable<T>(Entity entity) where T : struct, IEnableableComponent
    { RequireRecording(); Enabled<T>(entity, true); }
    public void Enable<T>(DeferredEntity entity) where T : struct, IEnableableComponent
    { RequireRecording(); Enabled<T>(Encode(entity), true); }
    public void Disable<T>(Entity entity) where T : struct, IEnableableComponent
    { RequireRecording(); Enabled<T>(entity, false); }
    public void Disable<T>(DeferredEntity entity) where T : struct, IEnableableComponent
    { RequireRecording(); Enabled<T>(Encode(entity), false); }

    private void Buffer<T>(CommandKind kind, Entity target, ReadOnlySpan<T> values) where T : struct
    {
        var bucket = Bucket<BufferCommands<T>>(out int type);
        if (bucket.Count == 0) Use(type);
        Append(kind, target, type, bucket.Add(values));
    }
    public void SetBuffer<T>(Entity entity, ReadOnlySpan<T> values) where T : struct
    { RequireRecording(); Buffer(CommandKind.SetBuffer, entity, values); }
    public void SetBuffer<T>(DeferredEntity entity, ReadOnlySpan<T> values) where T : struct
    { RequireRecording(); Buffer(CommandKind.SetBuffer, Encode(entity), values); }
    public void AppendBuffer<T>(Entity entity, ReadOnlySpan<T> values) where T : struct
    { RequireRecording(); Buffer(CommandKind.AppendBuffer, entity, values); }
    public void AppendBuffer<T>(DeferredEntity entity, ReadOnlySpan<T> values) where T : struct
    { RequireRecording(); Buffer(CommandKind.AppendBuffer, Encode(entity), values); }
    public void RemoveBuffer<T>(Entity entity) where T : struct
    { RequireRecording(); Bucket<BufferCommands<T>>(out int type); Append(CommandKind.RemoveBuffer, entity, type, 0); }
    public void RemoveBuffer<T>(DeferredEntity entity) where T : struct
    { RequireRecording(); Bucket<BufferCommands<T>>(out int type); Append(CommandKind.RemoveBuffer, Encode(entity), type, 0); }

    public void Playback()
    {
        using WorldAccess access = World.OpenStructure();
        if (_batch is { } batch) { batch.Playback(); return; }
        Seal();
        World.RequireStructure();
        CommandResults? results = _results;
        if (results is not null) results.Created = new Entity[_createCount];
        int created = 0;
        try { Play(World, 0, ref created); }
        finally { if (results is not null) Volatile.Write(ref results.CompletedCount, created + results.CompletedCompositeCreates); }
    }

    internal void Seal() { RequireRecording(); _closed = true; }

    internal void Play(World world, int offset, ref int created)
    {
        CommandResults? results = _results;
        ReadOnlySpan<CommandHeader> commands = _commands.AsSpan(0, _count);
        CommandBucket?[] buckets = _buckets;
        for (int i = 0; i < commands.Length; i++)
        {
            ref readonly CommandHeader command = ref commands[i];
            if (command.Kind == CommandKind.Create)
            {
                Entity entity = command.TypeId < 0 ? world.Create() : buckets[command.TypeId]!.Execute(world, command, results);
                results!.Created[offset + command.Target.Id] = entity;
                created++;
            }
            else if (command.Kind == CommandKind.Destroy)
                world.Destroy(command.Target.Id >= 0 ? command.Target : results!.Decode(command.Target));
            else buckets[command.TypeId]!.Execute(world, command, results);
            CompletedCommands = i + 1;
        }
        ClearPayloads();
        _count = 0;
    }

    private void ClearPayloads()
    {
        for (int i = 0; i < _usedCount; i++) _buckets[_usedTypes[i]]!.Clear(World);
        _usedCount = 0;
    }

    internal void Reset(CommandResults? results)
    {
        ClearPayloads();
        _count = _createCount = CompletedCommands = 0;
        _results = results;
        _closed = false;
    }

    public void Clear()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CommandBuffer));
        if (_batch is { } batch) batch.Clear(); else Reset(null);
    }

    internal void Release()
    {
        ClearPayloads();
        _commands = []; _buckets = []; _usedTypes = []; _results = null;
        _count = _createCount = CompletedCommands = 0;
        _closed = _disposed = true;
    }

    public void Dispose()
    {
        if (_batch is { } batch) batch.Dispose(); else Release();
    }
}

public sealed class CommandBatch : IDisposable
{
    private readonly World _world;
    private readonly CommandBuffer[] _producers;
    private CommandResults _results = new();
    private bool _disposed;
    public int ProducerCount => _producers.Length;
    public CommandBuffer this[int producer] => _producers[producer];

    public CommandBatch(World world, int producers, int capacityPerProducer = 16)
    {
        _world = world;
        _producers = new CommandBuffer[producers];
        for (int i = 0; i < producers; i++) _producers[i] = new(world, this, i, capacityPerProducer, _results);
    }

    public void Playback()
    {
        using WorldAccess access = _world.OpenStructure();
        foreach (CommandBuffer producer in _producers) producer.Seal();
        _world.RequireStructure();
        int count = 0;
        if (_producers.Length > 1)
        {
            _results.Offsets = new int[_producers.Length];
            for (int i = 0; i < _producers.Length; i++)
            {
                _results.Offsets[i] = count;
                count += _producers[i].CreateCount;
            }
        }
        else if (_producers.Length == 1) count = _producers[0].CreateCount;
        _results.Created = new Entity[count];
        int created = 0;
        try
        {
            for (int i = 0; i < _producers.Length; i++)
                _producers[i].Play(_world, _results.Offsets is { } offsets ? offsets[i] : 0, ref created);
        }
        finally { Volatile.Write(ref _results.CompletedCount, created + _results.CompletedCompositeCreates); }
    }

    public void Clear()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CommandBatch));
        _results = new();
        foreach (CommandBuffer producer in _producers) producer.Reset(_results);
    }

    public void Dispose()
    {
        foreach (CommandBuffer producer in _producers) producer.Release();
        _results = null!;
        _disposed = true;
    }
}
