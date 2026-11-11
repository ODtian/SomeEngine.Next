using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Shared;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Serialization;

public enum CheckpointProgress : byte { Rebuild, Preserve }

public sealed class WorldCheckpoint : IDisposable
{
    private readonly World _values = new();
    internal EntityRecord[] Records = [];
    internal int Used, FreeHead, FreeTail, FreeCount, EntityCount;
    internal ulong CaptureNumber;
    internal ulong Version;
    internal CheckpointProgress Progress;
    internal ChunkData[] Chunks = [];
    internal int ChunkCount;
    internal SharedValues?[] Shared = [];
    internal SparseStore?[] Sparse = [];
    internal ResourceEntries Resources;
    internal WorldServiceData[] Services = [];
    internal int ServiceCount;
    internal WorldEvents Events;
    internal QueryMembershipData[] Membership = [];
    internal ulong[][] SystemVersions = [];
    internal int[] SystemVersionCounts = [];
    internal int SystemCount;
    private bool[] _tableTypes = [];
    private readonly List<WorldValueAccess> _accesses = [];

    public WorldCheckpoint Capture(World world, CheckpointProgress progress = CheckpointProgress.Rebuild,
        params ISystemVersions[] systems)
        => Capture(world, null, default, progress, systems);

    internal WorldCheckpoint Capture(World world, SerializationRegistry? registry, SerializeOptions options,
        CheckpointProgress progress, ISystemVersions[] systems)
    {
        foreach (ISystemVersions system in systems) system.Complete();
        ReleaseValues(keepChunks: true);
        if (progress == CheckpointProgress.Rebuild)
        {
            using WorldAccess maintenance = world.OpenStructure();
            foreach (IWorldLifecycle service in world.LifecycleServices) ((IWorldDataService)service).CompleteData();
        }
        using WorldAccess structure = world.OpenStructure(false);
        if (_tableTypes.Length < ComponentTypes.Count) Array.Resize(ref _tableTypes, ComponentTypes.Count);
        Array.Clear(_tableTypes);
        _accesses.Clear();
        BuildCaptureAccesses(world, _tableTypes, _accesses);
        using WorldAccess values = world.OpenValues(CollectionsMarshal.AsSpan(_accesses));
        world.BeginRead();
        try
        {
            if (registry is not null) WorldSerializer.CheckCaptureLimits(world, registry, options, progress);
            Progress = progress;
            Version = world.Version;
            EntityCount = world.EntityCount;
            Used = world.Used;
            if (Records.Length < Used + 1) Records = new EntityRecord[Used + 1];
            for (int id = 1; id <= Used; id++)
            {
                Records[id].Row = world.Records[id].Row;
                Records[id].Generation = world.Records[id].Generation;
            }
            FreeHead = world.FirstFreeEntity;
            FreeTail = FreeCount = 0;
            for (int id = FreeHead; id != 0; id = Records[id].Row) { FreeTail = id; FreeCount++; }
            int chunkCount = 0;
            for (int i = 0; i < world.ArchetypeCount; i++)
                foreach (Chunk chunk in world.Archetypes[i].AllChunks)
                {
                    if (chunkCount == Chunks.Length) Array.Resize(ref Chunks, Math.Max(4, chunkCount * 2));
                    ChunkData? data = Chunks[chunkCount];
                    if (data is not null && ReferenceEquals(data.Types, chunk.Archetype.Types)
                        && ReferenceEquals(data.Groups, chunk.Archetype.GroupKeys)) data.Capture(_values, chunk);
                    else
                    {
                        data?.Release(_values, clearArrays: true);
                        Chunks[chunkCount] = new(_values, chunk);
                    }
                    chunkCount++;
                }
            for (int i = chunkCount; i < ChunkCount; i++) Chunks[i].Release(_values, clearArrays: true);
            Array.Clear(Chunks, chunkCount, ChunkCount - Math.Min(chunkCount, ChunkCount));
            ChunkCount = chunkCount;
            if (Shared.Length < world.SharedTypes.Length) Array.Resize(ref Shared, world.SharedTypes.Length);
            for (int type = 0; type < world.SharedTypes.Length; type++) Shared[type] = world.SharedTypes[type]?.Copy(_values, 0, Shared[type]);
            Array.Clear(Shared, world.SharedTypes.Length, Shared.Length - world.SharedTypes.Length);
            if (world.SparseTypes is { } sparse)
            {
                if (Sparse.Length < sparse.Types.Length) Array.Resize(ref Sparse, sparse.Types.Length);
                for (int type = 0; type < sparse.Types.Length; type++) Sparse[type] = sparse.Types[type]?.CopyData(_values, Sparse[type]);
                Array.Clear(Sparse, sparse.Types.Length, Sparse.Length - sparse.Types.Length);
            }
            else Array.Clear(Sparse);
            Resources = world.ResourceValues.Copy(Resources);
            if (Services.Length < world.LifecycleServices.Length) Array.Resize(ref Services, world.LifecycleServices.Length);
            int serviceCount = 0;
            foreach (IWorldLifecycle service in world.LifecycleServices)
            {
                WorldServiceData? previous = Services[serviceCount];
                WorldServiceData data = ((IWorldDataService)service).CopyData(progress == CheckpointProgress.Preserve, previous);
                if (!ReferenceEquals(previous, data)) previous?.Dispose();
                Services[serviceCount] = data;
                serviceCount++;
            }
            for (int i = serviceCount; i < ServiceCount; i++) Services[i].Dispose();
            Array.Clear(Services, serviceCount, Services.Length - serviceCount);
            ServiceCount = serviceCount;
            Membership = progress == CheckpointProgress.Preserve ? world.CopyMembershipData(Membership) : [];
            Events = progress == CheckpointProgress.Preserve ? world.CopyEvents(Events)
                : new(Events.Values ?? [], world.DestroyedSequence, Events.ReaderPositions ?? [], 0, 0);
            if (SystemVersions.Length < systems.Length) Array.Resize(ref SystemVersions, systems.Length);
            if (SystemVersionCounts.Length < systems.Length) Array.Resize(ref SystemVersionCounts, systems.Length);
            for (int i = 0; i < systems.Length; i++)
            {
                int count = systems[i].VersionCount * 2;
                if (SystemVersions[i] is not { } versions || versions.Length < count)
                    SystemVersions[i] = versions = new ulong[count];
                if (progress == CheckpointProgress.Preserve) systems[i].CopyVersions(versions.AsSpan(0, count));
                else versions.AsSpan(0, count).Fill(Version);
                SystemVersionCounts[i] = count;
            }
            Array.Clear(SystemVersions, systems.Length, SystemVersions.Length - systems.Length);
            SystemCount = systems.Length;
            CaptureNumber++;
            return this;
        }
        finally { world.EndRead(); }
    }

    internal static WorldValueAccess[] CaptureAccesses(World world)
    {
        bool[] table = new bool[ComponentTypes.Count];
        var accesses = new List<WorldValueAccess>();
        BuildCaptureAccesses(world, table, accesses);
        return accesses.ToArray();
    }

    private static void BuildCaptureAccesses(World world, bool[] table, List<WorldValueAccess> accesses)
    {
        for (int i = 0; i < world.ArchetypeCount; i++)
            foreach (ComponentType type in world.Archetypes[i].Components) table[type.Id] = true;
        for (int type = 0; type < table.Length; type++)
            if (table[type])
            {
                ComponentType component = ComponentTypes.Get(type);
                accesses.Add(new(type, WorldValueKind.Table, false, false, component.UsesStructureWrites));
                if (component.TracksWrites && !component.UsesStructureWrites) accesses.Add(new(type, WorldValueKind.Changes, false, false));
            }
        if (world.SparseTypes is { } sparse)
            for (int type = 0; type < sparse.Types.Length; type++)
                if (sparse.Types[type] is not null)
                {
                    accesses.Add(new(type, WorldValueKind.Sparse, false, false));
                    if (ComponentTypes.Get(type).TracksWrites) accesses.Add(new(type, WorldValueKind.SparseChanges, false, false));
                }
        for (int type = 0; type < world.ResourceValues.Capacity; type++)
            if (world.ResourceValues.Get(type) is { HasValue: true }) accesses.Add(new(type, WorldValueKind.Resource, false, false));
    }

    public void Restore(World world, params ISystemVersions[] systems)
    {
        foreach (ISystemVersions system in systems) system.Complete();
        world.Execution?.BeginClear();
        try
        {
            using WorldAccess access = world.OpenStructure();
            bool preserve = Progress == CheckpointProgress.Preserve;
            ulong version = preserve ? Version : Math.Max(world.Version, Version) + 1;
            world.ClearValues();
            world.RestoreEntityIds(Records.AsSpan(0, Used + 1), FreeHead, FreeTail, EntityCount, version);
            foreach (SharedValues? values in world.SharedTypes) values?.Release(world);
            var shared = new SharedValues?[Shared.Length];
            for (int type = 0; type < Shared.Length; type++) shared[type] = Shared[type]?.Copy(world, world.Records.Length);
            world.SharedTypes = shared;
            var nextChunks = new Dictionary<Archetype, int>();
            foreach (ChunkData data in Chunks.AsSpan(0, ChunkCount))
            {
                Archetype archetype = world.RestoreArchetype(data.Types, data.Groups);
                int next = nextChunks.GetValueOrDefault(archetype);
                RestoreChunk(world, archetype.RestoreChunk(next, data.Count), data, preserve);
                nextChunks[archetype] = next + 1;
            }
            if (Sparse.Length != 0)
            {
                SparseStores stores = world.SparseTypes ??= new(world, world.Records.Length);
                foreach (SparseStore? values in Sparse) values?.RestoreData(world, stores, preserve);
            }
            world.ResourceValues = Resources.Copy();
            world.RestoreEvents(Events);
            if (preserve) world.RestoreMembershipData(Membership);
            foreach (WorldServiceData service in Services.AsSpan(0, ServiceCount)) service.Restore(world);
            world.RebuildDataIndexes();
            for (int i = 0; i < systems.Length; i++)
            {
                if (preserve) systems[i].RestoreVersions(SystemVersions[i].AsSpan(0, SystemVersionCounts[i]));
                else
                {
                    var versions = new ulong[systems[i].VersionCount * 2];
                    Array.Fill(versions, version);
                    systems[i].RestoreVersions(versions);
                }
            }
        }
        finally { world.Execution?.EndClear(); }
    }

    private static void RestoreChunk(World world, Chunk target, ChunkData data, bool preserve)
    {
        data.Entities.AsSpan(0, data.Count).CopyTo(target.Entities);
        target.Count = data.Count;
        for (int row = 0; row < data.Count; row++)
        {
            Entity entity = data.Entities[row];
            world.Records[entity.Id] = new(target, row, entity.Generation);
        }
        for (int i = 0; i < data.Components.Length; i++)
        {
            ComponentType type = data.Components[i];
            ref ColumnEntry column = ref target.Columns[type.Id];
            ColumnData.Copy(world, type, ref data.Columns[i], ref column, data.Count);
            column.Presence = null;
            if (data.Columns[i].Presence is { } presence)
            {
                target.Archetype.Optional.Add(type.Id); target.Archetype.TransferKinds |= 4;
                column.Presence = new ulong[(target.Capacity + 63) >> 6];
                presence.AsSpan(0, (data.Count + 63) >> 6).CopyTo(column.Presence);
            }
            if (type.TracksWrites)
            {
                ref ComponentChanges changes = ref target.Changes![i];
                if (preserve) ColumnData.RestoreChanges(data.Changes![i], ref changes, data.Count);
                else for (int row = 0; row < data.Count; row++)
                    if (column.Has(row)) changes.Stamp(row, world.Version, true, type.TracksAdded);
            }
        }
        target.Enabled = null;
        target.DisabledCounts = null;
        if (data.Enabled is { } enabled)
        {
            target.Enabled = new ulong[]?[enabled.Length];
            target.DisabledCounts = new int[enabled.Length];
            for (int i = 0; i < enabled.Length; i++)
                if (enabled[i] is { } words)
                {
                    target.Archetype.Optional.Add(target.Archetype.Components[i].Id); target.Archetype.TransferKinds |= 4;
                    target.Enabled[i] = new ulong[(target.Capacity + 63) >> 6];
                    words.AsSpan(0, (data.Count + 63) >> 6).CopyTo(target.Enabled[i]!);
                }
            data.Disabled?.CopyTo(target.DisabledCounts, 0);
        }
    }

    private void ReleaseValues(bool keepChunks = false)
    {
        foreach (ChunkData chunk in Chunks.AsSpan(0, ChunkCount)) chunk.Release(_values, clearArrays: !keepChunks);
        foreach (SharedValues? values in Shared) values?.Release(_values);
        foreach (SparseStore? values in Sparse) values?.ReleaseData(_values, keepChunks);
        Resources.Clear(keepChunks);
        foreach (WorldServiceData service in Services.AsSpan(0, ServiceCount)) service.Clear(keepChunks);
        if (!keepChunks) { Chunks = []; ChunkCount = 0; }
        if (!keepChunks) { Shared = []; Sparse = []; }
        if (!keepChunks) Resources = default;
        if (!keepChunks) { Services = []; ServiceCount = 0; }
    }

    public void Dispose()
    {
        _values.RecyclesBuffers = false;
        ReleaseValues();
        Records = []; Used = FreeHead = FreeTail = FreeCount = 0;
        SystemVersions = []; SystemVersionCounts = []; SystemCount = 0; Events = default; Membership = [];
        _tableTypes = []; _accesses.Clear(); _accesses.TrimExcess();
        _values.Dispose();
    }
}
