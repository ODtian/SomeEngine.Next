using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

// Explicit checkpoint-holder census, not a heap/object-graph walker.
// The returned dictionaries contain only numbers; all object identity sets are local.
internal static class CheckpointHolderCounts
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Dictionary<Type, int> Widths = new();
    private static readonly Dictionary<(Type, string), MemberInfo?> Members = new();

    private static MemberInfo? Member(Type type, string name)
    {
        var key = (type, name);
        if (Members.TryGetValue(key, out MemberInfo? cached)) return cached;
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            MemberInfo? found = current.GetField(name, Instance | BindingFlags.DeclaredOnly)
                ?? (MemberInfo?)current.GetProperty(name, Instance | BindingFlags.DeclaredOnly);
            if (found is not null) return Members[key] = found;
        }
        Members[key] = null;
        return null;
    }

    private static object? Read(object? value, string name)
    {
        if (value is null) return null;
        return Member(value.GetType(), name) switch
        {
            FieldInfo field => field.GetValue(value),
            PropertyInfo property => property.GetValue(value),
            _ => null,
        };
    }

    private static long Number(object? value, string name)
        => Read(value, name) is { } number ? Convert.ToInt64(number) : 0;

    private static int Width(Type type)
    {
        if (!Widths.TryGetValue(type, out int size))
            Widths[type] = size = (int)typeof(CheckpointHolderCounts)
                .GetMethod(nameof(Size), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(type).Invoke(null, null)!;
        return size;
    }
    private static int Size<T>() => Unsafe.SizeOf<T>();

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Dictionary<string, long> ReadCurrent(WorldCheckpoint? checkpoint, SourceFullArchiveWorld? full)
    {
        var count = new Census();
        if (checkpoint is not null) count.Checkpoint(checkpoint);
        if (full is not null)
        {
            count.WorldRoot(full.World, "World");
            count.Serializer(Read(full, "_serializer"));
        }
        // This already-registered process list is read, never a new ComponentType<T>.Value.
        Type? types = typeof(World).Assembly.GetType("SomeEngine.ECS.Storage.ComponentTypes", false);
        object? entries = types?.GetField("Types", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        count.List("SharedTypeCache.RegisteredTypes", entries);
        count.Add("Observer.WidthCacheEntries", Widths.Count);
        count.Add("Observer.MemberCacheEntries", Members.Count);
        count.Add("Scope.GlobalBclArrayPoolInternalsEnumerated", 0);
        count.Add("Scope.ArbitraryObjectGraphEnumerated", 0);
        return count.Numbers;
    }

    private sealed class Census
    {
        internal readonly Dictionary<string, long> Numbers = new(StringComparer.Ordinal);
        private readonly HashSet<object> _arrays = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _chunks = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _services = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> _stores = new(ReferenceEqualityComparer.Instance);

        internal void Add(string key, long value)
            => Numbers[key] = Numbers.GetValueOrDefault(key) + value;

        private void Array(string key, Array? values, bool borrowed = false)
        {
            if (values is null) { Add(key + ".NullReferences", 1); return; }
            Add(key + ".ArrayReferences", 1);
            Add(key + ".ReferencedCapacity", values.LongLength);
            if (values.Length == 0) { Add(key + ".EmptyArrayReferences", 1); return; }
            if (!_arrays.Add(values)) { Add(key + ".AliasedArrayReferences", 1); return; }
            int width = Width(values.GetType().GetElementType()!);
            Add(key + ".DistinctArrays", 1);
            Add(key + ".DistinctPayloadBytes", values.LongLength * width);
            Add("KnownArrays.DistinctNonemptyCount", 1);
            Add("KnownArrays.PayloadBytes", values.LongLength * width);
            Add("KnownArrays.ElementWidth" + width + ".Count", 1);
            Add(borrowed ? "KnownArrays.BorrowedMetadataPayloadBytes" : "KnownArrays.OtherReferencedPayloadBytes",
                values.LongLength * width);
        }

        internal void List(string key, object? list)
        {
            if (list is null) return;
            Add(key + ".Objects", 1);
            Add(key + ".Count", Number(list, "Count"));
            Add(key + ".Capacity", Number(list, "Capacity"));
            Array(key + ".Backing", Read(list, "_items") as Array);
        }

        private void NestedArrays(string key, Array? directory)
        {
            Array(key + ".Directory", directory);
            if (directory is null) return;
            foreach (object? value in directory)
                if (value is Array inner) Array(key + ".Inner", inner);
        }

        private void Changes(string key, Array? changes)
        {
            Array(key + ".Directory", changes);
            if (changes is null) return;
            foreach (object value in changes)
            {
                Array(key + ".Rows", Read(value, "Rows") as Array);
                Array(key + ".Words", Read(value, "Words") as Array);
                Array(key + ".Blocks", Read(value, "Blocks") as Array);
            }
        }

        // Buffer<T>, Children<TDomain>, AdjacencyHead and SharedGroup only:
        // Items is a declared array, Count is a field OR the CountFlags-backed property.
        private void Owned(string key, Array? slots, long activeSlots, bool pairValue = false)
        {
            if (slots is null) return;
            Type element = slots.GetType().GetElementType()!;
            if (!pairValue && Member(element, "Items") is not FieldInfo) return;
            if (!pairValue && !typeof(Array).IsAssignableFrom(((FieldInfo)Member(element, "Items")!).FieldType)) return;
            for (int i = 0; i < slots.Length; i++)
            {
                object? entry = slots.GetValue(i);
                if (pairValue) entry = Read(entry, "Value");
                if (Read(entry, "Items") is not Array items) continue;
                long members = Number(entry, "Count");
                Add(key + ".RetainedBufferReferences", 1);
                Add(key + ".RetainedSlotMemberCounts", members);
                if (i < activeSlots) Add(key + ".ActiveMembers", members);
                else Add(key + ".InactiveSlotStoredMemberCounts", members);
                if (i >= activeSlots || members == 0) Add(key + ".InactiveBufferCapacity", items.Length);
                Array(key + ".Items", items);
            }
        }

        private void Chunk(object chunk, string owner)
        {
            Add(owner + ".ChunkReferences", 1);
            if (!_chunks.Add(chunk)) return;
            Add("CheckpointChunks.Objects", 1);
            long rows = Number(chunk, "Count");
            Add("CheckpointChunks.ActiveRows", rows);
            Array("CheckpointChunks.Entities", Read(chunk, "Entities") as Array);
            Array("CheckpointChunks.ComponentsBorrowed", Read(chunk, "Components") as Array, true);
            Array("CheckpointChunks.GroupsBorrowed", Read(chunk, "Groups") as Array, true);
            var columns = Read(chunk, "Columns") as Array;
            Array("CheckpointChunks.Columns", columns);
            if (columns is not null)
                foreach (object column in columns)
                {
                    var values = Read(column, "Values") as Array;
                    Array("CheckpointChunks.Values", values);
                    Owned("CheckpointChunks.OwnedValues", values, rows);
                    Array("CheckpointChunks.ColumnPresence", Read(column, "Presence") as Array);
                }
            NestedArrays("CheckpointChunks.Presence", Read(chunk, "_presence") as Array);
            NestedArrays("CheckpointChunks.EnabledWords", Read(chunk, "_enabledWords") as Array);
            NestedArrays("CheckpointChunks.EnabledView", Read(chunk, "_enabled") as Array);
            Array("CheckpointChunks.Disabled", Read(chunk, "_disabled") as Array);
            Changes("CheckpointChunks.Changes", Read(chunk, "Changes") as Array);
        }

        private void Stores(string key, Array? stores, bool shared)
        {
            Array(key + ".Directory", stores);
            if (stores is null) return;
            foreach (object? store in stores)
            {
                if (store is null) continue;
                Add(key + ".NonNullSlots", 1);
                if (!_stores.Add(store)) continue;
                Add(key + ".Objects", 1);
                long active = Number(store, "Count");
                Add(key + ".ActiveValues", active);
                var values = Read(store, "Values") as Array;
                Array(key + ".Values", values);
                Owned(key + ".OwnedValues", values, active);
                Array(key + ".Entities", Read(store, "Entities") as Array);
                NestedArrays(key + ".Pages", Read(store, "Pages") as Array);
                Changes(key + ".Changes", Read(store, "Changes") as Array);
                if (shared)
                {
                    Array(key + ".Links", Read(store, "_links") as Array);
                    Array(key + ".Buckets", Read(store, "_buckets") as Array);
                    object? groups = Read(store, "Groups");
                    var slots = Read(groups, "_groups") as Array;
                    Array(key + ".Groups", slots);
                    Array(key + ".GroupPlaces", Read(groups, "_places") as Array);
                    Owned(key + ".GroupMembers", slots, slots?.Length ?? 0);
                }
            }
        }

        private void Resources(object? resources)
        {
            if (resources is null) return;
            long capacity = Number(resources, "Capacity");
            Add("CheckpointAux.Resources.Capacity", capacity);
            Array("CheckpointAux.Resources.Tail", Read(resources, "_tail") as Array);
            MethodInfo? get = resources.GetType().GetMethod("Get", Instance);
            for (int i = 0; i < capacity; i++)
            {
                object? box = get?.Invoke(resources, [i]);
                if (box is null) continue;
                Add("CheckpointAux.Resources.Boxes", 1);
                if (Read(box, "HasValue") is true) Add("CheckpointAux.Resources.HasValue", 1);
                object? value = Read(box, "Value");
                if (value is SaveResource known)
                {
                    Add("CheckpointAux.Resources.SaveResourceObjects", 1);
                    Array("CheckpointAux.Resources.SaveResourceItems", known.Items);
                }
                else if (value is not null) Add("CheckpointAux.Resources.UnmappedPayloads", 1);
            }
        }

        private void Service(object service, string owner)
        {
            Add(owner + ".ServiceReferences", 1);
            if (!_services.Add(service)) return;
            Add("CheckpointServices.Objects", 1);
            bool known = Read(service, "Entries") is Array || Read(service, "Outgoing") is Array
                || Read(service, "RetiredChildren") is Array;
            if (!known) { Add("CheckpointServices.UnmappedObjects", 1); return; }
            foreach (string name in new[] { "Applied", "Retired", "RetiredChildren", "Future", "EmptyOrdered", "Outgoing", "Incoming", "RetiredHeads", "Entries" })
            {
                if (Read(service, name) is not Array slots) continue;
                string countName = name switch
                {
                    "RetiredChildren" => "RetiredChildCount",
                    "RetiredHeads" => "RetiredHeadCount",
                    "Entries" => "Count",
                    _ => name + "Count",
                };
                long active = Number(service, countName);
                Add("CheckpointServices." + name + ".Active", active);
                Array("CheckpointServices." + name, slots);
                if (name is "Outgoing" or "Incoming") Owned("CheckpointServices." + name, slots, active);
                if (name is "RetiredChildren" or "RetiredHeads") Owned("CheckpointServices." + name, slots, active, true);
                if (name == "Future")
                    for (int i = 0; i < slots.Length; i++)
                    {
                        object? list = Read(slots.GetValue(i), "Value");
                        if (list is null) continue;
                        List("CheckpointServices.FutureMembers", list);
                        if (i >= active) Add("CheckpointServices.FutureMembers.InactiveCapacity", Number(list, "Capacity"));
                    }
            }
        }

        internal void WorldRoot(World world, string key)
        {
            Add(key + ".Objects", 1);
            Add(key + ".Used", Number(world, "Used"));
            Add(key + ".EntityCount", world.EntityCount);
            Array(key + ".Records", Read(world, "Records") as Array);
            Array(key + ".ArchetypeDirectory", Read(world, "Archetypes") as Array);
            Add(key + ".ArchetypeCount", Number(world, "ArchetypeCount"));
            var pools = Read(world, "_bufferPools") as Array;
            Array(key + ".BufferPools.Directory", pools);
            if (pools is null) return;
            foreach (object? pool in pools)
            {
                if (pool is null) continue;
                Add(key + ".BufferPools.Objects", 1);
                long active = Number(pool, "_count");
                Add(key + ".BufferPools.FreeCount", active);
                var slots = Read(pool, "_free") as Array;
                Array(key + ".BufferPools.FreeDirectory", slots);
                Owned(key + ".BufferPools.FreeBuffers", slots, active);
            }
        }

        internal void Checkpoint(WorldCheckpoint checkpoint)
        {
            Add("CheckpointAux.Objects", 1);
            Array("CheckpointAux.Records", Read(checkpoint, "Records") as Array);
            Add("CheckpointAux.Used", Number(checkpoint, "Used"));
            Add("CheckpointAux.FreeCount", Number(checkpoint, "FreeCount"));
            var chunks = Read(checkpoint, "Chunks") as Array;
            Array("CheckpointAux.ChunkDirectory", chunks);
            Add("CheckpointAux.ChunkCount", Number(checkpoint, "ChunkCount"));
            if (chunks is not null) foreach (object? chunk in chunks) if (chunk is not null) Chunk(chunk, "Checkpoint");
            Stores("CheckpointShared", Read(checkpoint, "Shared") as Array, true);
            Stores("CheckpointSparse", Read(checkpoint, "Sparse") as Array, false);
            Resources(Read(checkpoint, "Resources"));
            var services = Read(checkpoint, "Services") as Array;
            Array("CheckpointAux.Services", services);
            Add("CheckpointAux.ServiceCount", Number(checkpoint, "ServiceCount"));
            if (services is not null) foreach (object? service in services) if (service is not null) Service(service, "Checkpoint");
            object? events = Read(checkpoint, "Events");
            Array("CheckpointAux.Events.Values", Read(events, "Values") as Array);
            Array("CheckpointAux.Events.Readers", Read(events, "ReaderPositions") as Array);
            Add("CheckpointAux.Events.ValueCount", Number(events, "ValueCount"));
            Add("CheckpointAux.Events.ReaderCount", Number(events, "ReaderCount"));
            NestedArrays("CheckpointAux.SystemVersions", Read(checkpoint, "SystemVersions") as Array);
            Array("CheckpointAux.SystemVersionCounts", Read(checkpoint, "SystemVersionCounts") as Array);
            Add("CheckpointAux.SystemCount", Number(checkpoint, "SystemCount"));
            if (Read(checkpoint, "SystemVersionCounts") is int[] counts)
                Add("CheckpointAux.ActiveSystemVersionElements", counts.Take((int)Number(checkpoint, "SystemCount")).Sum());
            Array("CheckpointAux.TableTypes", Read(checkpoint, "_tableTypes") as Array);
            List("CheckpointAux.Accesses", Read(checkpoint, "_accesses"));
            if (Read(checkpoint, "_values") is World values) WorldRoot(values, "CheckpointValueWorld");
        }

        internal void Serializer(object? serializer)
        {
            if (serializer is null) return;
            Add("Serializer.Objects", 1);
            Add("Serializer.ActiveTables", Number(serializer, "_checkpointTableCount"));
            var chunks = Read(serializer, "_snapshotChunks") as Array;
            Array("Serializer.SnapshotChunkDirectory", chunks);
            if (chunks is not null) foreach (object? chunk in chunks) if (chunk is not null) Chunk(chunk, "Serializer");
            object? tables = Read(serializer, "_snapshotTables");
            List("Serializer.Tables", tables);
            if (tables is IList entries)
                foreach (object table in entries)
                {
                    Add("Serializer.TableObjects", 1);
                    Array("Serializer.TableColumns", Read(table, "Columns") as Array);
                    object? members = Read(table, "Chunks");
                    List("Serializer.TableChunks", members);
                    if (Read(table, "First") is { } first) Chunk(first, "Serializer.First");
                    if (members is IList tableChunks)
                        foreach (object chunk in tableChunks) Chunk(chunk, "Serializer.Table");
                }
            object? services = Read(serializer, "_snapshotServices");
            List("Serializer.Services", services);
            if (services is IList saved)
                foreach (object pair in saved)
                    if (Read(pair, "Item2") is { } data) Service(data, "Serializer");
        }
    }
}
