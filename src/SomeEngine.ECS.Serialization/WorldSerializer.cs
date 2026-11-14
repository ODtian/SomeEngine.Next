using System.Buffers;
using System.Numerics;
using SomeEngine.Job;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Shared;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Serialization;

public sealed partial class WorldSerializer(SerializationRegistry registry)
{
    private const uint Magic = 0x53434553;
    private const ushort FormatVersion = 5;
    private readonly List<TableBinding> _tables = [];
    private readonly List<SavedType> _used = [];
    private readonly List<SavedService> _services = [];
    private World? _boundWorld;
    private TableBinding?[] _bindings = [];
    private bool[] _seen = [];
    private int[] _fileTypes = [];
    private int _registryVersion = -1;

    private void SynchronizeRegistry()
    {
        if (_registryVersion == registry.Version) return;
        _bindings = [];
        _capturedChunks = null;
        _registryVersion = registry.Version;
    }

    private sealed class TableBinding(Archetype table, SavedColumn[] columns)
    {
        internal readonly Archetype Table = table;
        internal readonly SavedColumn[] Columns = columns;
    }

    public void Save(World world, IBufferWriter<byte> output, SavePurpose purpose = SavePurpose.RawCheckpoint,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
        => Save(world, output, new SerializeOptions(Purpose: purpose), progress, systems);

    public void Save(World world, IBufferWriter<byte> output, SerializeOptions options,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
    {
        if (!options.Codecs.IsDefaultOrEmpty)
        {
            new WorldSerializer(SerializationRegistry.Bind(registry, options.Codecs))
                .Save(world, output, options with { Codecs = default }, progress, systems);
            return;
        }
        ValidateWriteOptions(options);
        var writer = new DataWriter(output, options.ExternalReferences);
        Write(world, ref writer, options, progress, systems);
        writer.Flush();
    }

    public void Save(World world, Stream output, SavePurpose purpose = SavePurpose.RawCheckpoint,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
        => Save(world, output, new SerializeOptions(Purpose: purpose), progress, systems);

    public void Save(World world, Stream output, SerializeOptions options,
        CheckpointProgress progress = CheckpointProgress.Rebuild, params ISystemVersions[] systems)
    {
        if (!options.Codecs.IsDefaultOrEmpty)
        {
            new WorldSerializer(SerializationRegistry.Bind(registry, options.Codecs))
                .Save(world, output, options with { Codecs = default }, progress, systems);
            return;
        }
        ValidateWriteOptions(options);
        Write(world, output, options, progress, systems);
    }

    private void Write(World world, Stream output, SerializeOptions options, CheckpointProgress progress,
        ISystemVersions[] systems)
    {
        var writer = new DataWriter(output, options.ExternalReferences);
        Write(world, ref writer, options, progress, systems);
        writer.Flush();
    }

    private static void ValidateWriteOptions(in SerializeOptions options)
    {
        if (options.MaximumSparseMemberships < 0 || options.MaximumTopologyRecords < 0 || options.MaximumTopologyPayloadBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static void CheckSparseMemberships(SparseStore?[] types, SerializationRegistry registry, int maximum)
    {
        if (maximum == 0) return;
        int remaining = maximum;
        for (int type = 0; type < types.Length; type++)
            if (types[type] is { } values && registry.Sparse.ContainsKey(type))
            {
                if (values.Count > remaining) throw new InvalidOperationException("Sparse membership count exceeds the configured write limit.");
                remaining -= values.Count;
            }
    }

    internal static void CheckCaptureLimits(World world, SerializationRegistry registry, in SerializeOptions options,
        CheckpointProgress progress)
    {
        if (world.SparseTypes is { } sparse) CheckSparseMemberships(sparse.Types, registry, options.MaximumSparseMemberships);
        if (options.MaximumTopologyRecords == 0) return;
        long remaining = options.MaximumTopologyRecords;
        foreach (SavedService service in registry.Services)
        {
            long count = service.CountRecords(world, progress == CheckpointProgress.Preserve);
            if (count > remaining) throw new InvalidOperationException("Topology records exceed the configured write limit.");
            remaining -= count;
        }
    }

    private void Select(World world)
    {
        SynchronizeRegistry();
        foreach (SavedType type in _used) _seen[registry.GetOrder(type)] = false;
        _used.Clear(); _tables.Clear(); _services.Clear();
        if (_seen.Length < registry.Types.Count)
        { Array.Resize(ref _seen, registry.Types.Count); Array.Resize(ref _fileTypes, registry.Types.Count); }
        if (!ReferenceEquals(_boundWorld, world)) { _bindings = []; _boundWorld = world; }
        if (_bindings.Length < world.ArchetypeCount) Array.Resize(ref _bindings, Math.Max(16, world.ArchetypeCount * 2));
        for (int i = 0; i < world.ArchetypeCount; i++)
        {
            Archetype table = world.Archetypes[i];
            bool nonempty = false;
            foreach (Chunk chunk in table.AllChunks) if (chunk.Count != 0) { nonempty = true; break; }
            if (!nonempty) continue;
            TableBinding? binding = _bindings[table.Id];
            if (binding is null)
            {
                var columns = new List<SavedColumn>();
                foreach (ComponentType component in table.Components)
                    if (registry.Columns.TryGetValue(component.Id, out SavedColumn? value)) columns.Add(value);
                columns.Sort(static (left, right) => left.Key.StableId.CompareTo(right.Key.StableId));
                _bindings[table.Id] = binding = new(table, columns.ToArray());
            }
            _tables.Add(binding);
            foreach (SavedColumn column in binding.Columns) _seen[registry.GetOrder(column)] = true;
        }
        for (int type = 0; type < world.SharedTypes.Length; type++)
            if (world.SharedTypes[type] is not null && registry.Columns.TryGetValue(type, out SavedColumn? shared)) _seen[registry.GetOrder(shared)] = true;
        if (world.SparseTypes is { } sparse)
            for (int type = 0; type < sparse.Types.Length; type++)
                if (sparse.Types[type] is not null && registry.Sparse.TryGetValue(type, out SavedSparse? values)) _seen[registry.GetOrder(values)] = true;
        for (int type = 0; type < world.ResourceValues.Capacity; type++)
            if (world.ResourceValues.Get(type) is { HasValue: true } && registry.Resources.TryGetValue(type, out SavedResource? resource)) _seen[registry.GetOrder(resource)] = true;
        foreach (SavedService service in registry.Services)
            if (service.Find(world) is not null) { _services.Add(service); _seen[registry.GetOrder(service.Type)] = true; }
        foreach (SavedType type in registry.Types)
            if (_seen[registry.GetOrder(type)]) { _fileTypes[registry.GetOrder(type)] = _used.Count; _used.Add(type); }
    }

    private void Write(World world, ref DataWriter writer, SerializeOptions options, CheckpointProgress progress,
        ISystemVersions[] systems)
    {
        foreach (ISystemVersions system in systems) system.Complete();
        if (progress == CheckpointProgress.Rebuild)
            foreach (SavedService service in registry.Services) service.Maintain(world);
        using WorldAccess structure = world.OpenStructure(false);
        using WorldAccess access = world.OpenValues(WorldCheckpoint.CaptureAccesses(world));
        world.BeginRead();
        try
        {
            Select(world);
            CheckCaptureLimits(world, registry, options, progress);
            WriteHeader(ref writer, options.Purpose);
            writer.WriteByte((byte)progress); writer.WriteUInt64(world.Version);
            writer.WriteInt32(world.Used); writer.WriteInt32(world.EntityCount);
            int freeCount = 0;
            for (int id = world.FirstFreeEntity; id != 0; id = world.Records[id].Row) freeCount++;
            writer.WriteInt32(freeCount);
            for (int id = 1; id <= world.Used; id++) writer.WriteInt32(world.Records[id].Generation);
            for (int id = world.FirstFreeEntity; id != 0; id = world.Records[id].Row) writer.WriteInt32(id);

            int sharedCount = 0, sparseCount = 0, resourceCount = 0;
            foreach (SavedType type in _used)
                if (type is SavedShared) sharedCount++;
                else if (type is SavedSparse) sparseCount++;
                else if (type is SavedResource) resourceCount++;
            writer.WriteInt32(sharedCount);
            foreach (SavedType type in _used)
                if (type is SavedShared shared)
                { writer.WriteInt32(_fileTypes[registry.GetOrder(type)]); shared.WriteValues(ref writer, world.SharedTypes[type.Type]!); }

            writer.WriteInt32(_tables.Count);
            foreach (TableBinding binding in _tables) WriteTable(ref writer, binding);
            writer.WriteInt32(sparseCount);
            foreach (SavedType type in _used)
                if (type is SavedSparse sparse)
                { writer.WriteInt32(_fileTypes[registry.GetOrder(type)]); sparse.Write(ref writer, world.SparseTypes!.Types[type.Type]!); }
            writer.WriteInt32(resourceCount);
            foreach (SavedType type in _used)
                if (type is SavedResource resource)
                { writer.WriteInt32(_fileTypes[registry.GetOrder(type)]); resource.Write(ref writer, world.ResourceValues.Get(type.Type)!); }
            long topologyBytesRemaining = options.MaximumTopologyPayloadBytes;
            WriteTopology(ref writer, world, options.MaximumTopologyPayloadBytes != 0, ref topologyBytesRemaining);
            WriteEvents(ref writer, progress == CheckpointProgress.Preserve ? world.CopyEvents() : new([], world.DestroyedSequence, []));
            writer.WriteInt32(_services.Count);
            foreach (SavedService service in _services)
            {
                writer.WriteInt32(_fileTypes[registry.GetOrder(service.Type)]);
                bool bounded = options.MaximumTopologyPayloadBytes != 0 && service.Type.Storage is SavedStorage.Parent or SavedStorage.Endpoints;
                long start = writer.Position;
                if (bounded) writer.LimitBytes(topologyBytesRemaining);
                try { service.Write(ref writer, world, progress == CheckpointProgress.Preserve); }
                finally { if (bounded) writer.EndByteLimit(); }
                if (bounded) topologyBytesRemaining -= writer.Position - start;
            }
            writer.WriteInt32(systems.Length);
            foreach (ISystemVersions system in systems)
            {
                writer.WriteInt32(system.VersionCount);
                ulong[] versions = ArrayPool<ulong>.Shared.Rent(system.VersionCount * 2);
                try
                {
                    Span<ulong> values = versions.AsSpan(0, system.VersionCount * 2);
                    if (progress == CheckpointProgress.Preserve) system.CopyVersions(values);
                    else values.Fill(world.Version);
                    foreach (ulong value in values) writer.WriteUInt64(value);
                }
                finally { ArrayPool<ulong>.Shared.Return(versions); }
            }
        }
        finally { world.EndRead(); }
    }

    private void WriteHeader(ref DataWriter writer, SavePurpose purpose, byte payloadKind = 0)
    {
        _used.Sort(static (left, right) => left.Key.StableId.CompareTo(right.Key.StableId));
        if (_fileTypes.Length < registry.Types.Count) Array.Resize(ref _fileTypes, registry.Types.Count);
        for (int index = 0; index < _used.Count; index++) _fileTypes[registry.GetOrder(_used[index])] = index;
        foreach (SavedType type in _used)
            if (purpose == SavePurpose.DurableSave && !type.SupportsDurable)
                throw new InvalidOperationException("Durable save requires an explicit field codec and schema for every value type.");
        writer.WriteUInt32(Magic); writer.WriteUInt16(FormatVersion);
        writer.WriteByte((byte)purpose); writer.WriteByte(payloadKind);
        writer.WriteGuid(purpose == SavePurpose.RawCheckpoint ? typeof(World).Module.ModuleVersionId : Guid.Empty);
        writer.WriteInt32(_used.Count);
        foreach (SavedType type in _used)
        {
            SerializationTypeKey key = type.KeyFor(purpose);
            writer.WriteGuid(key.StableId); writer.WriteText(key.StableName); writer.WriteUInt64(key.SchemaFingerprint);
            writer.WriteByte((byte)type.Storage); writer.WriteInt32(type.Width);
        }
    }

    private void WriteTable(ref DataWriter writer, TableBinding binding)
    {
        Archetype table = binding.Table;
        writer.WriteInt32(binding.Columns.Length);
        foreach (SavedColumn column in binding.Columns) writer.WriteInt32(_fileTypes[registry.GetOrder(column)]);
        int groupCount = 0;
        for (int group = 0; group < table.GroupKeys.Length; group++)
            if (registry.Columns.ContainsKey(table.Groups!.Groupings[group].Type)) groupCount++;
        writer.WriteInt32(groupCount);
        for (int group = 0; group < table.GroupKeys.Length; group++)
            if (registry.Columns.TryGetValue(table.Groups!.Groupings[group].Type, out SavedColumn? grouped))
            {
                writer.WriteInt32(_fileTypes[registry.GetOrder(grouped)]);
                writer.WriteInt32(table.GroupKeys[group]);
            }
        int count = 0;
        foreach (Chunk chunk in table.AllChunks) if (chunk.Count != 0) count++;
        writer.WriteInt32(count);
        foreach (Chunk chunk in table.AllChunks)
        {
            if (chunk.Count == 0) continue;
            writer.WriteInt32(chunk.Count);
            writer.WriteEntities(chunk.Entities.AsSpan(0, chunk.Count));
            foreach (SavedColumn bindingColumn in binding.Columns)
            {
                ComponentType type = ComponentTypes.Get(bindingColumn.Type);
                ref ColumnEntry column = ref chunk.Columns[type.Id];
                WriteMask(ref writer, column.Presence, chunk.Count);
                if (bindingColumn.Storage is not (SavedStorage.Parent or SavedStorage.Endpoints or SavedStorage.Children))
                    bindingColumn.Write(ref writer, ref column, chunk.Count);
                ulong[]? enabled = chunk.Enabled is { } entries ? entries[column.ColumnPlusOne - 1] : null;
                WriteMask(ref writer, enabled, chunk.Count);
                SavedChanges.Write(ref writer, chunk.Changes is { } changes ? changes[column.ColumnPlusOne - 1] : default,
                    chunk.Count, type.TracksWrites, type.TracksAdded);
            }
        }
    }

    private void WriteTopology(ref DataWriter writer, World world, bool bounded, ref long bytesRemaining)
    {
        int count = 0;
        foreach (SavedType type in _used) if (type.Storage is SavedStorage.Parent or SavedStorage.Endpoints or SavedStorage.Children) count++;
        writer.WriteInt32(count);
        foreach (SavedType type in _used)
        {
            if (type.Storage is not (SavedStorage.Parent or SavedStorage.Endpoints or SavedStorage.Children)) continue;
            var column = (SavedColumn)type;
            writer.WriteInt32(_fileTypes[registry.GetOrder(type)]);
            int rows = 0;
            foreach (TableBinding table in _tables)
                if (table.Table.Types.Bits.Has(type.Type))
                    foreach (Chunk chunk in table.Table.AllChunks)
                        if (chunk.Columns[type.Type].Presence is null) rows += chunk.Count;
                        else for (int row = 0; row < chunk.Count; row++) if (chunk.Columns[type.Type].Has(row)) rows++;
            writer.WriteInt32(rows);
            long start = writer.Position;
            if (bounded) writer.LimitBytes(bytesRemaining);
            try
            {
                foreach (TableBinding table in _tables)
                    if (table.Table.Types.Bits.Has(type.Type))
                        foreach (Chunk chunk in table.Table.AllChunks)
                            column.WriteEntityRows(ref writer, ref chunk.Columns[type.Type], chunk.Entities.AsSpan(0, chunk.Count));
            }
            finally { if (bounded) writer.EndByteLimit(); }
            if (bounded) bytesRemaining -= writer.Position - start;
            writer.WriteInt32(checked((int)(writer.Position - start)));
        }
    }

    private static void WriteMask(ref DataWriter writer, ulong[]? words, int count)
    {
        writer.WriteByte(words is null ? (byte)0 : (byte)1);
        if (words is null) return;
        int length = (count + 63) >> 6;
        for (int word = 0; word < length; word++)
            writer.WriteUInt64(words[word] & (word == length - 1 && (count & 63) != 0 ? (1UL << (count & 63)) - 1 : ulong.MaxValue));
    }

    private static void WriteEvents(ref DataWriter writer, in WorldEvents events)
    {
        writer.WriteUInt64(events.Through); writer.WriteInt32(events.ValueCount);
        foreach (DestroyedEntity value in events.Values.AsSpan(0, events.ValueCount)) { writer.WriteEntity(value.Entity); writer.WriteUInt64(value.Sequence); }
        int count = 0;
        foreach (EventReaderPosition position in events.ReaderPositions.AsSpan(0, events.ReaderCount))
            if (position.Id != Guid.Empty) count++;
        writer.WriteInt32(count);
        foreach (EventReaderPosition position in events.ReaderPositions.AsSpan(0, events.ReaderCount))
            if (position.Id != Guid.Empty) { writer.WriteGuid(position.Id); writer.WriteUInt64(position.Position); }
    }
}
