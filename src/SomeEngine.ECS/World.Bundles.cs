using System.Buffers;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public interface IBundleWriter<TState> where TState : struct
{
    void Write(BundleWriteView view, in TState state);
}

/// <summary>Writes the fields of one entity in its complete component layout.</summary>
public readonly ref struct BundleWriteView
{
    private readonly World _world;
    private readonly bool _created;
    public Entity Entity { get; }
    public int Index { get; }
    internal BundleWriteView(World world, Entity entity, int index, bool created)
        => (_world, Entity, Index, _created) = (world, entity, index, created);
    public void Write<T>(in T value) where T : struct
    {
        if (ComponentType<T>.IsSparseType) _world.Set(Entity, value);
        else _world.WriteBundleValue(Entity, value, _created);
    }
    public void WriteBuffer<T>(in ReadOnlyMemory<T> values) where T : struct
        => _world.WriteBundleBuffer(Entity, values.Span, _created);
}

internal static class BundleTypes<TState, TWriter>
    where TState : struct where TWriter : struct, IBundleWriter<TState>
{
    private static TypeSet? s_types;
    internal static TypeSet Get(ReadOnlySpan<int> componentIds)
    {
        if (Volatile.Read(ref s_types) is { } types) return types;
        foreach (int id in componentIds)
        {
            ComponentType component = ComponentTypes.Get(id);
            if (component.InitializesEntity || component.IsComputed)
                throw new InvalidOperationException("Create relationship values through their typed entry.");
        }
        var created = new TypeSet(componentIds.ToArray());
        return Interlocked.CompareExchange(ref s_types, created, null) ?? created;
    }
}

internal readonly struct ComponentBundleWriter<TBundle> : IBundleWriter<TBundle>
    where TBundle : struct, Components.IComponentBundle<TBundle>
{
    public void Write(BundleWriteView view, in TBundle state) => TBundle.Write(view, state);
}

public sealed partial class World
{
    /// <summary>Creates one entity holding every component of the bundle in one structural move.</summary>
    public Entity Spawn<TBundle>(in TBundle bundle) where TBundle : struct, Components.IComponentBundle<TBundle>
        => ExecuteBundleSpawn(TBundle.ComponentIds, TBundle.SparseComponentIds, bundle, new ComponentBundleWriter<TBundle>());

    /// <summary>Creates one entity per bundle value.</summary>
    public void Spawn<TBundle>(ReadOnlySpan<TBundle> bundles, Span<Entity> entities)
        where TBundle : struct, Components.IComponentBundle<TBundle>
        => ExecuteBundleSpawn(entities, TBundle.ComponentIds, TBundle.SparseComponentIds, bundles, new ComponentBundleWriter<TBundle>());

    /// <summary>Creates one entity per destination element from the same bundle value.</summary>
    public void Spawn<TBundle>(in TBundle bundle, Span<Entity> entities)
        where TBundle : struct, Components.IComponentBundle<TBundle>
        => ExecuteBundleSpawn(entities, TBundle.ComponentIds, TBundle.SparseComponentIds, bundle, new ComponentBundleWriter<TBundle>());

    /// <summary>Adds every component of the bundle to an entity that holds none of them, in one structural move.</summary>
    public void AddBundle<TBundle>(Entity entity, in TBundle bundle) where TBundle : struct, Components.IComponentBundle<TBundle>
        => ExecuteBundleAdd(entity, TBundle.ComponentIds, TBundle.SparseComponentIds, bundle, new ComponentBundleWriter<TBundle>());

    /// <summary>Replaces every component of the bundle on an entity that holds all of them.</summary>
    public void ReplaceBundle<TBundle>(Entity entity, in TBundle bundle) where TBundle : struct, Components.IComponentBundle<TBundle>
        => ExecuteBundleReplace(entity, TBundle.ComponentIds, TBundle.SparseComponentIds, bundle, new ComponentBundleWriter<TBundle>());

    /// <summary>Reserves rows in the archetype of the bundle.</summary>
    public void ReserveBundle<TBundle>(int capacity) where TBundle : struct, Components.IComponentBundle<TBundle>
        => ReserveBundle(TBundle.ComponentIds, capacity);

    public void ReserveBundle(ReadOnlySpan<int> componentIds, int capacity)
        => ReserveTypes(new TypeSet(false, componentIds.ToArray()), capacity);

    public Entity ExecuteBundleSpawn<TState, TWriter>(ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds, in TState state, in TWriter writer)
        where TState : struct where TWriter : struct, IBundleWriter<TState>
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        using BatchVersionScope version = BeginBatchVersion(0);
        Chunk chunk = Find(BundleTypes<TState, TWriter>.Get(componentIds)).WritableChunk;
        Entity entity = CreateKnown(ref chunk);
        using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
        writer.Write(new(this, entity, 0, true), in state);
        CompleteBundleValues(entity, created: true);
        return entity;
    }

    public Entity ExecuteBundleSpawn<TState, TWriter>(ReadOnlySpan<int> componentIds, in TState state, in TWriter writer)
        where TState : struct where TWriter : struct, IBundleWriter<TState>
        => ExecuteBundleSpawn(componentIds, [], state, writer);

    public void ExecuteBundleAdd<TState, TWriter>(Entity entity, ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds, in TState state, in TWriter writer)
        where TState : struct where TWriter : struct, IBundleWriter<TState>
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        TypeSet types = BundleTypes<TState, TWriter>.Get(componentIds);
        ref EntityRecord record = ref Resolve(entity);
        foreach (int type in types.Types)
            if ((uint)type < (uint)record.Chunk!.Columns.Length && record.Chunk.Columns[type].Has(record.Row))
                throw new InvalidOperationException("Adding a bundle requires absent component values.");
        foreach (int type in sparseComponentIds)
            if (_sparse is { } sparse && (uint)type < (uint)sparse.Types.Length && sparse.Types[type]?.Has(entity) == true)
                throw new InvalidOperationException("Adding a bundle requires absent sparse values.");
        using BatchVersionScope version = BeginBatchVersion(0);
        using MembershipEdit membership = BeginMembershipEdit(entity);
        EnsureColumns(entity, types);
        RestoreBundleTags(entity, types);
        writer.Write(new(this, entity, 0, false), in state);
        CompleteBundleValues(entity, created: false);
    }

    public void ExecuteBundleReplace<TState, TWriter>(Entity entity, ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds, in TState state, in TWriter writer)
        where TState : struct where TWriter : struct, IBundleWriter<TState>
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        TypeSet types = BundleTypes<TState, TWriter>.Get(componentIds);
        ref EntityRecord record = ref Resolve(entity);
        foreach (int type in types.Types)
            if ((uint)type >= (uint)record.Chunk!.Columns.Length || !record.Chunk.Columns[type].Has(record.Row))
                throw new InvalidOperationException("Replacing a bundle requires its existing component values.");
        foreach (int type in sparseComponentIds)
            if (_sparse is not { } sparse || (uint)type >= (uint)sparse.Types.Length || sparse.Types[type]?.Has(entity) != true)
                throw new InvalidOperationException("Replacing a bundle requires its existing sparse values.");
        using BatchVersionScope version = BeginBatchVersion(0);
        using MembershipEdit membership = BeginMembershipEdit(entity);
        writer.Write(new(this, entity, 0, false), in state);
        CompleteBundleValues(entity, created: false);
    }

    public void ExecuteBundleSpawn<TState, TWriter>(Span<Entity> entities, ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds, ReadOnlySpan<TState> states, in TWriter writer)
        where TState : struct where TWriter : struct, IBundleWriter<TState>
    {
        if (states.Length != entities.Length) throw new ArgumentException("Provide one bundle per entity.", nameof(states));
        using WorldAccess access = OpenStructure();
        RequireStructure();
        using BatchVersionScope version = BeginBatchVersion(0);
        Archetype archetype = Find(BundleTypes<TState, TWriter>.Get(componentIds));
        if (ChunkCapacity == 0) archetype.ReserveRows(archetype.PrimaryChunk.Count + entities.Length);
        int offset = 0;
        while (offset < entities.Length)
        {
            Chunk chunk = archetype.WritableChunk.EnsureRow();
            int count = Math.Min(entities.Length - offset, chunk.Capacity - chunk.Count);
            Span<Entity> created = entities.Slice(offset, count);
            BeginBundleRows(chunk, created);
            try
            {
                for (int index = 0; index < count; index++)
                    writer.Write(new(this, created[index], offset + index, true), in states[offset + index]);
            }
            finally { CompleteBundleRows(chunk, created); }
            offset += count;
        }
    }

    /// <summary>Creates one entity per destination element, each written from the same bundle state.</summary>
    public void ExecuteBundleSpawn<TState, TWriter>(Span<Entity> entities, ReadOnlySpan<int> componentIds,
        ReadOnlySpan<int> sparseComponentIds, in TState state, in TWriter writer)
        where TState : struct where TWriter : struct, IBundleWriter<TState>
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        using BatchVersionScope version = BeginBatchVersion(0);
        Archetype archetype = Find(BundleTypes<TState, TWriter>.Get(componentIds));
        if (ChunkCapacity == 0) archetype.ReserveRows(archetype.PrimaryChunk.Count + entities.Length);
        int offset = 0;
        while (offset < entities.Length)
        {
            Chunk chunk = archetype.WritableChunk.EnsureRow();
            int count = Math.Min(entities.Length - offset, chunk.Capacity - chunk.Count);
            Span<Entity> created = entities.Slice(offset, count);
            BeginBundleRows(chunk, created);
            try
            {
                for (int index = 0; index < count; index++)
                    writer.Write(new(this, created[index], offset + index, true), in state);
            }
            finally { CompleteBundleRows(chunk, created); }
            offset += count;
        }
    }

    public void ExecuteBundleSpawnBatch<TState, TWriter>(ReadOnlySpan<int> componentIds, int count,
        in TState state, in TWriter writer)
        where TState : struct where TWriter : struct, IBundleWriter<TState>
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        using BatchVersionScope version = BeginBatchVersion(0);
        Archetype archetype = Find(BundleTypes<TState, TWriter>.Get(componentIds));
        if (ChunkCapacity == 0) archetype.ReserveRows(archetype.PrimaryChunk.Count + count);
        int offset = 0;
        while (offset < count)
        {
            Chunk chunk = archetype.WritableChunk.EnsureRow();
            int length = Math.Min(count - offset, chunk.Capacity - chunk.Count);
            Span<Entity> created = chunk.Entities.AsSpan(chunk.Count, length);
            BeginBundleRows(chunk, created);
            try
            {
                for (int index = 0; index < length; index++)
                    writer.Write(new(this, created[index], offset + index, true), in state);
            }
            finally { CompleteBundleRows(chunk, created); }
            offset += length;
        }
    }

    private void BeginBundleRows(Chunk chunk, Span<Entity> entities)
    {
        int start = AllocateCreatedRows(entities, chunk);
        CompleteCreatedRows(chunk, start, entities.Length, publishMembership: false);
    }

    private void CompleteBundleRows(Chunk chunk, ReadOnlySpan<Entity> entities)
    {
        if (!chunk.Archetype.Types.Bits.Intersects(_groupedTypes))
        {
            foreach (Entity entity in entities) CompleteBundleValues(entity, created: true);
            PublishCreatedMembership(entities);
            return;
        }
        Entity[] grouped = ArrayPool<Entity>.Shared.Rent(entities.Length);
        entities.CopyTo(grouped);
        try
        {
            foreach (Entity entity in grouped.AsSpan(0, entities.Length))
            {
                using MembershipEdit membership = BeginMembershipEdit(entity, created: true);
                CompleteBundleValues(entity, created: true);
            }
        }
        finally { ArrayPool<Entity>.Shared.Return(grouped); }
    }

    private void CompleteBundleValues(Entity entity, bool created)
    {
        ref EntityRecord record = ref Records[entity.Id];
        Chunk chunk = record.Chunk!;
        if (created)
            foreach (ComponentType type in chunk.Archetype.Shared)
                if (chunk.Columns[type.Id].Has(record.Row)) CopySharedMember(entity, chunk, type.Id, record.Row);
        foreach (ComponentType type in chunk.Archetype.Indexed)
            if (chunk.Columns[type.Id].Has(record.Row) && (uint)type.Id < (uint)_valueIndexes.Length)
                _valueIndexes[type.Id]?.AddFromEntity(entity, in chunk.Columns[type.Id], record.Row);
        if (chunk.Archetype.Groupable.Length != 0) ArrangeCreatedGroups(entity);
    }

    private void RestoreBundleTags(Entity entity, TypeSet types)
    {
        ref EntityRecord record = ref Records[entity.Id];
        Chunk chunk = record.Chunk!;
        foreach (ComponentType type in types.Columns.Components)
            if (type.IsTag && !chunk.Columns[type.Id].Has(record.Row))
            {
                chunk.RestorePresence(type.Id, record.Row);
                if (type.TracksWrites)
                    chunk.Changes![chunk.Columns[type.Id].ColumnPlusOne - 1].Stamp(record.Row, NextVersion(), true, type.TracksAdded);
            }
    }

    internal void WriteBundleValue<T>(Entity entity, in T value, bool created) where T : struct
    {
        ref EntityRecord record = ref Resolve(entity);
        if (ComponentType<T>.IsTagType) return;
        if (created && ComponentType<T>.IsSharedType)
            ColumnEntry.At<int>(ref record.Chunk!.Columns[ComponentType<T>.TypeId], record.Row) = Shared<T>().Intern(value);
        else if (created) WriteCreatedValue(entity, record.Chunk!, record.Row, in value);
        else if (ComponentType<T>.IsSharedType) SetSharedValue(entity, in value);
        else WriteColumn(entity, ref record, in value, ComponentType<T>.TracksWritesType ? NextVersion() : 0);
    }

    internal void WriteBundleBuffer<T>(Entity entity, ReadOnlySpan<T> values, bool created) where T : struct
    {
        ref EntityRecord record = ref Resolve(entity);
        Chunk chunk = record.Chunk!;
        int type = ComponentType<Buffer<T>>.TypeId;
        ref ColumnEntry column = ref chunk.Columns[type];
        bool added = created || !column.Has(record.Row);
        ref Buffer<T> previous = ref ColumnEntry.At<Buffer<T>>(ref column, record.Row);
        if (!added) ComponentType<Buffer<T>>.ReleaseValue(this, ref previous);
        Buffer<T> buffer = BufferPool<T>().Rent();
        buffer.Reserve(values.Length);
        values.CopyTo(buffer.Items);
        buffer.Count = values.Length;
        previous = buffer;
        chunk.RestorePresence(type, record.Row);
        if (ComponentType<Buffer<T>>.TracksWritesType) Stamp<Buffer<T>>(chunk, record.Row, NextVersion(), added);
    }
}
