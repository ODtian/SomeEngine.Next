using System.Runtime.CompilerServices;
using SomeEngine.ECS.Storage;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS;

internal enum WorldValueKind : byte { Table, Sparse, Resource, Changes, SparseChanges }

internal readonly record struct WorldValueAccess(int Type, WorldValueKind Kind, bool Write, bool Tracked, bool Structural = false);

internal interface IQueryExecutionPolicy
{
    void EnterQuery(Query query, ReadOnlySpan<int> writes);
    bool CanBorrowStructureRead { get; }
    void Exit();
}

internal readonly struct WorldExecutionQueryPolicy(WorldExecution execution) : IQueryExecutionPolicy
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnterQuery(Query query, ReadOnlySpan<int> writes) => execution.EnterQuery(query, writes, false);

    public bool CanBorrowStructureRead
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => execution.CanBorrowStructureRead;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Exit() => execution.Exit();
}

public abstract class WorldExecution
{
    /// <summary>Executes row ranges under this World's current access scope.</summary>
    public void ExecuteRanges<TWork, TItem>(TItem[] items, int count, in TWork work, JobScheduleOptions options = default)
        where TWork : struct, IRangeWork<TItem>
        => Scheduler!.ExecuteRanges(items, count, work, CurrentAccess, options);

    public virtual Scheduler? Scheduler => null;
    public virtual JobResourceAccess[] StructureRead => [];
    public virtual JobResourceAccess[] StructureWrite => [];
    public virtual JobResourceAccess[] CollectReadAccess(Query query) => [];
    internal virtual JobHandle CurrentAccess => default;
    internal virtual void RegisterEventAccess(JobResourceAccess access) { }
    internal virtual void CheckActive() { }
    internal virtual void ValidateDisposePreconditions() { }
    internal abstract void Attach(World world);
    internal virtual WorldAccess OpenStructure(bool write)
    {
        EnterStructure(write);
        return new(this);
    }
    internal virtual WorldAccess OpenValues(ReadOnlySpan<WorldValueAccess> values)
    {
        EnterValues(values);
        return new(this);
    }
    internal abstract void EnterStructure(bool write);
    internal abstract void EnterValues(ReadOnlySpan<WorldValueAccess> values);
    internal abstract void EnterQuery(Query query, ReadOnlySpan<int> writes, bool writeAll);
    internal abstract void RequireValue(in WorldValueAccess value);
    internal virtual bool CanBorrowStructureRead => false;
    internal abstract bool HasRangeWrite { get; }
    internal abstract void RequireWriteRow(Chunk chunk, int row);
    internal abstract void Exit();
    internal abstract void BeginDispose();
    internal abstract void BeginClear();
    internal abstract void EndClear();
    internal abstract void Release();
    internal abstract ulong CurrentWriteVersion { get; }
}

internal readonly struct WorldAccess(WorldExecution? execution) : IDisposable
{
    public void Dispose() => execution?.Exit();
}

public sealed partial class World
{
    internal bool HasRangeWrite => Execution?.HasRangeWrite == true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RequireWriteRow(Chunk chunk, int row) => Execution?.RequireWriteRow(chunk, row);

    internal void RequireWriteRange(Chunk chunk, int start, int count)
    {
        if (count == 0 || Execution is not { HasRangeWrite: true } execution) return;
        execution.RequireWriteRow(chunk, start);
        execution.RequireWriteRow(chunk, start + count - 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RequireWriteEntity(int id)
    {
        if (Execution is not { HasRangeWrite: true } execution) return;
        ref EntityRecord record = ref Records[id];
        execution.RequireWriteRow(record.Chunk!, record.Row);
    }

    public WorldExecution? Execution { get; }
    private int _nextChunk;

    internal int NextChunkId() => ++_nextChunk;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal WorldAccess OpenStructure(bool write = true)
    {
        WorldExecution? execution = Execution;
        return execution is null ? default : execution.OpenStructure(write);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal WorldAccess OpenValues<T>(bool write, WorldValueKind kind = WorldValueKind.Table) where T : struct
    {
        WorldExecution? execution = Execution;
        return execution is null ? default : execution.OpenValues([AccessFor<T>(write, kind)]);
    }

    internal WorldAccess OpenResourceValues<T>(bool write)
    {
        WorldExecution? execution = Execution;
        return execution is null ? default : execution.OpenValues([new(ResourceType<T>.Id, WorldValueKind.Resource, write, false)]);
    }

    internal WorldAccess OpenQuery(Query query, ReadOnlySpan<int> writes, bool writeAll = false)
    {
        WorldExecution? execution = Execution;
        execution?.EnterQuery(query, writes, writeAll);
        return new(execution);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BeginValueRead<T>(bool write, WorldValueKind kind = WorldValueKind.Table) where T : struct
    {
        if (Execution is { } execution)
            BeginValueReadWithExecution<T>(execution, write, kind);
        else BeginRead(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BeginValueReadWithExecution<T>(WorldExecution execution, bool write, WorldValueKind kind) where T : struct
    {
        execution.EnterValues([AccessFor<T>(write, kind)]);
        BeginRead(true);
    }

    internal void BeginStructureRead()
    {
        Execution?.EnterStructure(false);
        BeginRead();
    }

    private static WorldValueAccess AccessFor<T>(bool write, WorldValueKind kind) where T : struct
        => new(ComponentType<T>.TypeId, kind, write, ComponentType<T>.TracksWritesType,
            kind == WorldValueKind.Table && ComponentType<T>.Value.UsesStructureWrites);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EndValueRead()
    {
        if (Execution is { } execution) EndValueReadWithExecution(execution);
        else EndRead(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndValueReadWithExecution(WorldExecution execution)
    {
        EndRead(true);
        execution.Exit();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RequireValue<T>(bool write, WorldValueKind kind = WorldValueKind.Table) where T : struct
    {
        RequireCallingThread();
        if (Execution is { } execution)
            RequireValueWithExecution<T>(execution, write, kind);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RequireValueWithExecution<T>(WorldExecution execution, bool write, WorldValueKind kind) where T : struct
        => execution.RequireValue(AccessFor<T>(write, kind));

    public T ReadValue<T>(Entities.Entity entity) where T : struct
    {
        using WorldAccess access = OpenValues<T>(false);
        if (ComponentType<T>.IsSharedType)
            return ReadSharedValue<T>(entity, SharedValueArray<T>());
        return Access<T>(entity, false);
    }
}
