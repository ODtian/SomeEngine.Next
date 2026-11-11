namespace SomeEngine.ECS.Storage;

internal readonly record struct PodColumn(int TypeId, int Size);

internal sealed class Transition(Chunk target, PodColumn[] pod, ComponentType[] managed, ComponentType? removed)
{
    internal Chunk Target = target;
    internal readonly PodColumn[] Pod = pod;
    internal readonly ComponentType[] Managed = managed;
    internal readonly ComponentType? Removed = removed;
}

internal sealed class Archetype
{
    internal readonly World World;
    internal readonly int Id;
    internal readonly TypeSet Types;
    internal readonly ComponentType[] Components;
    internal readonly PodColumn[] Pod;
    internal readonly ComponentType[] Managed;
    internal readonly ComponentType[] Tracked;
    internal readonly ComponentType[] OwnedValues;
    internal readonly ComponentType[] Indexed;
    internal readonly bool RetainsOnDestroy;
    internal readonly bool HasEntityReferences;
    internal readonly bool CanClonePodAndBuffers;
    internal readonly ComponentType[] Shared;
    internal readonly ComponentType[] Groupable;
    internal readonly ComponentType[] Initializers;
    internal readonly ComponentType[] Computed;
    internal readonly ComponentType[] Cleanup;
    internal readonly GroupTables? Groups;
    internal readonly int[] GroupKeys;
    internal GroupTables? GroupTargets;
    internal GroupTables?[] GroupAdditions = [];
    internal readonly Chunk PrimaryChunk;
    internal Chunk WritableChunk;
    private Chunk[]? _chunks;
    private Chunk? _available;
    internal int ChunkCount = 1;
    internal ReadOnlySpan<Chunk> AllChunks => _chunks is null
        ? new ReadOnlySpan<Chunk>(in PrimaryChunk) : _chunks.AsSpan(0, ChunkCount);
    internal Transition?[] Additions = [];
    internal Transition?[] Removals = [];
    internal Dictionary<TypeSet, Transition>? AdditionsByTypes;
    internal TypeSet? DestroyColumns;
    internal CachedComponent[]? Cached;
    internal int TransferKinds;
    internal ComponentBits Optional;
    internal MembershipMatch[] MembershipQueries = [];
    internal int MembershipCacheVersion = -1;

    internal Archetype(World world, int id, TypeSet types, int capacity, GroupTables? groups = null, int[]? groupKeys = null)
    {
        World = world;
        Id = id;
        Groups = GroupTargets = groups;
        GroupKeys = groupKeys ?? [];
        Types = types;
        ComponentColumns columns = types.Columns;
        Components = columns.Components;
        Pod = columns.Pod;
        Managed = columns.Managed;
        Tracked = columns.Tracked;
        OwnedValues = columns.OwnedValues;
        Indexed = columns.Indexed;
        Shared = columns.Shared;
        Groupable = columns.Groupable;
        Initializers = columns.Initializers;
        Computed = columns.Computed;
        Cleanup = columns.Cleanup;
        RetainsOnDestroy = columns.RetainsOnDestroy;
        HasEntityReferences = columns.HasEntityReferences;
        CanClonePodAndBuffers = columns.CanCopyPodAndBuffers && Indexed.Length == 0
            && Groupable.Length == 0 && Initializers.Length == 0;
        TransferKinds = Managed.Length == 0 ? 0 : 1;
        if (Tracked.Length != 0) TransferKinds |= 8;
        if (Shared.Length != 0) TransferKinds |= 16;
        PrimaryChunk = WritableChunk = new Chunk(this, world.ChunkCapacity == 0 ? capacity : world.ChunkCapacity);
        if (world.ChunkCapacity != 0) MakeAvailable(PrimaryChunk);
    }

    internal void Reserve(int capacity)
    {
        if (World.ChunkCapacity == 0) { PrimaryChunk.Reserve(capacity); return; }
        int available = 0;
        foreach (Chunk chunk in AllChunks) available += chunk.Capacity;
        while (available < capacity) { AppendChunk(); available += World.ChunkCapacity; }
    }

    internal void ReserveRows(int capacity)
    {
        if (World.ChunkCapacity == 0) { PrimaryChunk.ReserveRows(capacity); return; }
        int available = 0;
        foreach (Chunk chunk in AllChunks) available += chunk.Capacity;
        while (available < capacity)
        {
            int count = Math.Min(World.ChunkCapacity, capacity - available);
            AppendChunk(count);
            available += count;
        }
    }

    internal Chunk NextWritableChunk()
    {
        while (_available is { } chunk && chunk.Count == chunk.Capacity)
        {
            _available = chunk.NextAvailable;
            chunk.NextAvailable = null;
            chunk.InAvailable = false;
        }
        if (_available is null) AppendChunk();
        return WritableChunk = _available!;
    }

    private void AppendChunk(int capacity = 0)
    {
        if (_chunks is null) { _chunks = new Chunk[4]; _chunks[0] = PrimaryChunk; }
        else if (ChunkCount == _chunks.Length) Array.Resize(ref _chunks, ChunkCount * 2);
        var chunk = new Chunk(this, capacity == 0 ? World.ChunkCapacity : capacity);
        _chunks[ChunkCount++] = chunk;
        MakeAvailable(chunk);
        World.HasMultipleChunks = true;
    }

    internal Chunk RestoreChunk(int index, int count)
    {
        if (index == ChunkCount) AppendChunk(count);
        Chunk chunk = AllChunks[index];
        chunk.Reserve(count);
        return chunk;
    }

    internal void MakeAvailable(Chunk chunk)
    {
        if (chunk.InAvailable) return;
        chunk.NextAvailable = _available;
        chunk.InAvailable = true;
        _available = chunk;
    }

    internal void ResetWritableChunks()
    {
        WritableChunk = PrimaryChunk;
        if (World.ChunkCapacity == 0) return;
        _available = null;
        ReadOnlySpan<Chunk> chunks = AllChunks;
        for (int i = chunks.Length - 1; i >= 0; i--)
        {
            chunks[i].InAvailable = false;
            MakeAvailable(chunks[i]);
        }
    }

    internal void AddCachedComponent(CachedComponent data)
    {
        TransferKinds |= 2;
        int length = Cached?.Length ?? 0;
        Array.Resize(ref Cached, length + 1);
        Cached[length] = data;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal bool TryGroup(int type, out int value)
    {
        ColumnEntry[] columns = PrimaryChunk.Columns;
        if ((uint)type < (uint)columns.Length && columns[type].GroupPlusOne != 0)
        {
            value = GroupKeys[columns[type].GroupPlusOne - 1];
            return true;
        }
        value = 0;
        return false;
    }
}
