using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS;

public readonly record struct DestroyedEntity(Entity Entity, ulong Sequence);

public sealed class DestroyedReader : IDisposable
{
    internal World? World;
    internal readonly EventReader<DestroyedEntity> Reader;
    internal DestroyedReader(World world, EventReader<DestroyedEntity> reader) => (World, Reader) = (world, reader);
    public ulong Position => Reader.Position;
    public JobHandle Ready => Reader.Ready;
    public JobResourceAccess Read => Reader.Read;
    public bool IsCompleted => Reader.IsDisposed;
    public DestroyedRead Open() => new(Reader.Open(World?.Execution?.CurrentAccess ?? default));
    public void Advance(ulong through) => Reader.Advance(through);
    internal void Reset(ulong position) => Reader.Reset(position);
    public void Dispose() { Reader.Dispose(); World = null; }
}

public readonly ref struct DestroyedRead
{
    private readonly EventRead<DestroyedEntity> _read;
    internal DestroyedRead(EventRead<DestroyedEntity> read) => _read = read;
    public ulong Through => _read.Through;
    public int Count => _read.Count;
    public ReadOnlySpan<DestroyedEntity> First => _read.First;
    public ReadOnlySpan<DestroyedEntity> Second => _read.Second;
    public DestroyedEnumerator GetEnumerator() => new(_read.First, _read.Second);
    public void Dispose() => _read.Dispose();
}

public ref struct DestroyedEnumerator
{
    private readonly ReadOnlySpan<DestroyedEntity> _first, _second;
    private int _index;
    internal DestroyedEnumerator(ReadOnlySpan<DestroyedEntity> first, ReadOnlySpan<DestroyedEntity> second)
    {
        _first = first;
        _second = second;
        _index = -1;
    }
    public readonly DestroyedEntity Current => _index < _first.Length ? _first[_index] : _second[_index - _first.Length];
    public bool MoveNext() => ++_index < _first.Length + _second.Length;
}

internal interface IWorldLifecycle : IDisposable
{
    bool RequiresIndividualDestroy(Archetype archetype);
    void BeforeDestroy(Entity entity, bool deferred);
    void Clear();
}

internal static class LifecycleTypes
{
    private static int _next;
    internal static int Next() => Interlocked.Increment(ref _next) - 1;
}

internal static class LifecycleType<T> where T : class, IWorldLifecycle
{
    internal static readonly int Id = LifecycleTypes.Next();
}

public sealed partial class World
{
    private IWorldLifecycle[] _lifecycles = [];
    private IWorldLifecycle?[] _lifecycleTypes = [];
    private int _lifecycleCount;
    private EventLog<DestroyedEntity>? _destroyedLog;
    private Scheduler? _eventScheduler;
    private EventLog<DestroyedEntity> DestroyedLog => _destroyedLog ??= new(_eventScheduler);
    private bool HasDestroyedReaders => _destroyedLog?.HasReaders == true;

    public void Flush()
    {
        using WorldAccess access = OpenStructure();
        for (int index = 0; index < _lifecycleCount; index++)
            if (_lifecycles[index] is IWorldDataService service) service.CompleteData();
    }

    internal void BindEventScheduler(Scheduler scheduler)
    {
        _eventScheduler = scheduler;
        DestroyedLog.BindScheduler(scheduler);
        foreach (QueryData? query in _queryBindings) query?.MembershipLog?.BindScheduler(scheduler);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T? FindLifecycle<T>() where T : class, IWorldLifecycle
    {
        int type = LifecycleType<T>.Id;
        IWorldLifecycle?[] types = _lifecycleTypes;
        return (uint)type < (uint)types.Length ? Unsafe.As<T>(types[type]) : null;
    }

    internal void AddLifecycle<T>(T lifecycle) where T : class, IWorldLifecycle
    {
        RequireStructure();
        int type = LifecycleType<T>.Id;
        if (type >= _lifecycleTypes.Length) Array.Resize(ref _lifecycleTypes, Math.Max(type + 1, Math.Max(4, _lifecycleTypes.Length * 2)));
        _lifecycleTypes[type] = lifecycle;
        if (_lifecycleCount == _lifecycles.Length) Array.Resize(ref _lifecycles, Math.Max(4, _lifecycleCount * 2));
        _lifecycles[_lifecycleCount++] = lifecycle;
    }

    public DestroyedReader WatchDestroyed()
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        return new(this, DestroyedLog.Watch());
    }

    public DestroyedReader WatchDestroyed(Guid consumerId)
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        return new(this, DestroyedLog.Watch(consumerId));
    }

    private void PublishDestroyed(Entity entity)
    {
        if (!HasDestroyedReaders) return;
        using EventWriter<DestroyedEntity> writer = DestroyedLog.Reserve(1, Execution?.CurrentAccess ?? default);
        writer.Values[0] = new(entity, writer.Range.First + 1);
        DestroyedLog.Publish(writer.Range);
    }

    private void PublishDestroyed(ReadOnlySpan<Entity> entities)
    {
        if (!HasDestroyedReaders || entities.IsEmpty) return;
        using EventWriter<DestroyedEntity> writer = DestroyedLog.Reserve(entities.Length, Execution?.CurrentAccess ?? default);
        for (int index = 0; index < entities.Length; index++)
            writer.Values[index] = new(entities[index], writer.Range.First + (ulong)index + 1);
        DestroyedLog.Publish(writer.Range);
    }

    private void ClearLifecycle()
    {
        for (int index = 0; index < _lifecycleCount; index++) _lifecycles[index].Clear();
        _destroyedLog?.Clear(Execution?.CurrentAccess ?? default);
        foreach (QueryData? query in _queryBindings) query?.MembershipLog?.Clear(Execution?.CurrentAccess ?? default);
    }

    private void DisposeLifecycle()
    {
        for (int index = 0; index < _lifecycleCount; index++) _lifecycles[index].Dispose();
        _lifecycles = [];
        _lifecycleTypes = [];
        _lifecycleCount = 0;
    }

    /// <summary>
    /// Ends one step of this World's event producers (destroyed entities and query membership).
    /// System groups call this once per update; a reader that stays behind for the configured
    /// number of steps is reported in Debug builds.
    /// </summary>
    public void EndStep()
    {
        using WorldAccess access = OpenStructure(false);
        _destroyedLog?.EndStep();
        foreach (QueryData query in _membershipQueries) query.MembershipLog?.EndStep();
    }

    private void DisposeEventLogs()
    {
        _destroyedLog?.Dispose();
        _destroyedLog = null;
        foreach (QueryData query in _membershipQueries)
            if (query.MembershipLog is { } log) { log.Dispose(); query.MembershipLog = null; }
        _membershipQueries.Clear();
        _membershipEdits = [];
    }

    public void DestroyDeferred(Entity entity)
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        if (_lifecycleCount != 0 || HasDestroyedReaders) DestroyObserved(entity, true);
        else
        {
            ref var record = ref Resolve(entity);
            if (HasDestroyed(in record)) throw new InvalidOperationException("Destroy an active entity.");
            DestroyComponents(entity, ref record);
        }
    }

    private void DestroyObserved(Entity entity, bool deferred)
    {
        using MembershipEdit membership = BeginMembershipEdit(entity);
        ref var before = ref Resolve(entity);
        if (HasDestroyed(in before)) throw new InvalidOperationException("Destroy an active entity.");
        IWorldLifecycle[] lifecycles = _lifecycles;
        int count = _lifecycleCount;
        for (int i = 0; i < count; i++) lifecycles[i].BeforeDestroy(entity, deferred);
        ref var record = ref Resolve(entity);
        DestroyComponents(entity, ref record);
        PublishDestroyed(entity);
    }
}
