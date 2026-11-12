using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Serialization;

public sealed partial class WorldSerializer
{
    public void Save(World world, Entity entity, Stream output, SerializeOptions options = default)
        => Save(world, [entity], output, options);

    public void Save(World world, ReadOnlySpan<Entity> entities, Stream output, SerializeOptions options = default)
        => SaveSelected(world, entities, null, output, options);

    public void Save(World world, Query query, Stream output, SerializeOptions options = default)
        => SaveSelected(world, default, query, output, options);

    private sealed class SelectedTable(Archetype source, SavedColumn[] columns)
    {
        internal readonly Archetype Source = source;
        internal readonly SavedColumn[] Columns = columns;
        internal readonly List<Entity> Entities = [];
    }

    private sealed class SelectedShared(SavedShared column)
    {
        internal readonly SavedShared Column = column;
        internal readonly Dictionary<int, int> Mapping = new() { [0] = 0 };
        internal readonly List<int> Ids = [0];

        internal void Include(int id)
        {
            if (Mapping.TryAdd(id, Ids.Count)) Ids.Add(id);
        }
    }

    private void SaveSelected(World world, ReadOnlySpan<Entity> entities, Query? query, Stream output, SerializeOptions options)
    {
        if (!options.Codecs.IsDefaultOrEmpty)
        {
            new WorldSerializer(SerializationRegistry.Bind(registry, options.Codecs))
                .SaveSelected(world, entities, query, output, options with { Codecs = default });
            return;
        }
        ValidateWriteOptions(options);
        foreach (SavedService service in registry.Services) service.Maintain(world);
        using WorldAccess structure = world.OpenStructure(false);
        Span<WorldValueAccess> accesses = stackalloc WorldValueAccess[registry.Columns.Count + registry.Sparse.Count
            + (query?.Description.Definition.AccessTypes.Length ?? 0)];
        int accessCount = 0;
        foreach (SavedColumn column in registry.Columns.Values)
        {
            ComponentType type = ComponentTypes.Get(column.Type);
            accesses[accessCount++] = new(type.Id, WorldValueKind.Table, false, type.TracksWrites, type.UsesStructureWrites);
        }
        foreach (SavedSparse column in registry.Sparse.Values)
            accesses[accessCount++] = new(column.Type, WorldValueKind.Sparse, false, ComponentTypes.Get(column.Type).TracksWrites);
        if (query is { } filter)
            foreach (int component in filter.Description.Definition.AccessTypes)
            {
                ComponentType type = ComponentTypes.Get(component);
                accesses[accessCount++] = new(type.Id, WorldValueKind.Table, false, type.TracksWrites, type.UsesStructureWrites);
            }
        using WorldAccess values = world.OpenValues(accesses[..accessCount]);
        world.BeginRead();
        try
        {
            if (query is { } selected)
            {
                var members = new List<Entity>();
                using (QueryScope scope = selected.OpenRead())
                    foreach (QueryChunk chunk in scope) members.AddRange(chunk.Entities);
                entities = CollectionsMarshal.AsSpan(members);
            }
            WriteSelected(world, entities, output, options);
        }
        finally { world.EndRead(); }
    }

    private void WriteSelected(World world, ReadOnlySpan<Entity> entities, Stream output, SerializeOptions options)
    {
        var selected = new HashSet<Entity>(entities.Length);
        foreach (Entity entity in entities) { world.Resolve(entity); selected.Add(entity); }
        var tables = new List<SelectedTable>();
        var bySource = new Dictionary<Archetype, Dictionary<TypeSet, SelectedTable>>();
        var bindings = new Dictionary<Archetype, SavedColumn[]>();
        var used = new HashSet<SavedType>();
        var shared = new Dictionary<int, SelectedShared>();
        foreach (Entity entity in entities)
        {
            ref EntityRecord record = ref world.Records[entity.Id];
            Chunk chunk = record.Chunk!;
            if (!bindings.TryGetValue(chunk.Archetype, out SavedColumn[]? bound))
            {
                bound = chunk.Archetype.Components.Where(type => registry.Columns.ContainsKey(type.Id))
                    .Select(type => registry.Columns[type.Id]).ToArray();
                bindings.Add(chunk.Archetype, bound);
            }
            var columns = new List<SavedColumn>();
            foreach (SavedColumn column in bound)
                if (column.IncludesCell(ref chunk.Columns[column.Type], record.Row, selected))
                {
                    columns.Add(column); used.Add(column);
                    if (column is SavedShared sharedColumn)
                    {
                        if (!shared.TryGetValue(column.Type, out SelectedShared? entries)) shared.Add(column.Type, entries = new(sharedColumn));
                        entries.Include(ColumnEntry.At<int>(ref chunk.Columns[column.Type], record.Row));
                    }
                }
            var types = new TypeSet(false, columns.Select(column => column.Type).ToArray());
            if (!bySource.TryGetValue(chunk.Archetype, out var groups)) bySource.Add(chunk.Archetype, groups = []);
            if (!groups.TryGetValue(types, out SelectedTable? table))
            { groups.Add(types, table = new(chunk.Archetype, columns.OrderBy(column => column.Key.StableId).ToArray())); tables.Add(table); }
            table.Entities.Add(entity);
        }
        var sparse = new List<(SavedSparse Column, Entity[] Entities)>();
        int sparseCount = 0;
        if (world.SparseTypes is { } stores)
            foreach (SavedSparse column in registry.Sparse.Values)
            {
                if ((uint)column.Type >= (uint)stores.Types.Length || stores.Types[column.Type] is not { } store) continue;
                var members = new List<Entity>();
                foreach (Entity entity in entities) if (store.Has(entity)) members.Add(entity);
                if (members.Count == 0) continue;
                sparseCount += members.Count; used.Add(column); sparse.Add((column, members.ToArray()));
            }
        if (options.MaximumSparseMemberships != 0 && sparseCount > options.MaximumSparseMemberships)
            throw new InvalidOperationException("Sparse membership count exceeds the configured write limit.");
        SavedService[] services = registry.Services.Where(service => used.Contains(service.Type) && service.Find(world) is not null).ToArray();
        if (options.MaximumTopologyRecords != 0)
        {
            long records = 0;
            foreach (SavedService service in services) records += service.CountSelectedRecords(world, selected);
            if (records > options.MaximumTopologyRecords)
                throw new InvalidOperationException("Topology records exceed the configured write limit.");
        }
        SynchronizeRegistry();
        foreach (SavedType type in _used) _seen[registry.GetOrder(type)] = false;
        _used.Clear();
        if (_seen.Length < registry.Types.Count)
        { Array.Resize(ref _seen, registry.Types.Count); Array.Resize(ref _fileTypes, registry.Types.Count); }
        foreach (SavedType type in registry.Types)
            if (used.Contains(type))
            { int order = registry.GetOrder(type); _seen[order] = true; _fileTypes[order] = _used.Count; _used.Add(type); }
        var writer = new DataWriter(output, options.ExternalReferences);
        WriteHeader(ref writer, options.Purpose);
        writer.WriteByte((byte)CheckpointProgress.Rebuild); writer.WriteUInt64(world.Version);
        writer.WriteInt32(world.Used); writer.WriteInt32(entities.Length); writer.WriteInt32(world.Used - entities.Length);
        for (int id = 1; id <= world.Used; id++) writer.WriteInt32(world.Records[id].Generation);
        for (int id = 1; id <= world.Used; id++)
            if (!selected.Contains(new Entity(id, world.Records[id].Generation))) writer.WriteInt32(id);
        writer.WriteInt32(shared.Count);
        foreach (SelectedShared entries in shared.Values)
        {
            writer.WriteInt32(_fileTypes[registry.GetOrder(entries.Column)]);
            entries.Column.WriteSelectedValues(ref writer, world.SharedTypes[entries.Column.Type]!, CollectionsMarshal.AsSpan(entries.Ids));
        }
        writer.WriteInt32(tables.Count);
        foreach (SelectedTable table in tables) WriteSelectedTable(ref writer, world, table, selected, shared);
        writer.WriteInt32(sparse.Count);
        foreach (var section in sparse)
        {
            writer.WriteInt32(_fileTypes[registry.GetOrder(section.Column)]);
            section.Column.WriteSelected(ref writer, world.SparseTypes!.Types[section.Column.Type]!, section.Entities, world.Version);
        }
        writer.WriteInt32(0); // World resources belong to the whole-World entry.
        int topologyCount = 0;
        foreach (SavedType type in _used)
            if (type.Storage is SavedStorage.Parent or SavedStorage.Children or SavedStorage.Endpoints) topologyCount++;
        writer.WriteInt32(topologyCount);
        long bytesRemaining = options.MaximumTopologyPayloadBytes;
        foreach (SavedType type in _used)
        {
            if (type.Storage is not (SavedStorage.Parent or SavedStorage.Children or SavedStorage.Endpoints)) continue;
            var column = (SavedColumn)type;
            int count = 0;
            foreach (SelectedTable table in tables) if (table.Columns.Contains(column)) count += table.Entities.Count;
            writer.WriteInt32(_fileTypes[registry.GetOrder(type)]); writer.WriteInt32(count);
            long start = writer.Position;
            if (options.MaximumTopologyPayloadBytes != 0) writer.LimitBytes(bytesRemaining);
            try
            {
                foreach (SelectedTable table in tables)
                    if (table.Columns.Contains(column))
                        foreach (Entity entity in table.Entities)
                        {
                            ref EntityRecord record = ref world.Records[entity.Id];
                            writer.WriteEntity(entity);
                            column.WriteSelectedCell(ref writer, world, ref record.Chunk!.Columns[column.Type], record.Row, selected);
                        }
            }
            finally { if (options.MaximumTopologyPayloadBytes != 0) writer.EndByteLimit(); }
            long length = writer.Position - start; bytesRemaining -= length; writer.WriteInt32(checked((int)length));
        }
        WriteEvents(ref writer, new WorldEvents([], 0, []));
        writer.WriteInt32(services.Length);
        foreach (SavedService service in services)
        {
            writer.WriteInt32(_fileTypes[registry.GetOrder(service.Type)]);
            long start = writer.Position;
            if (options.MaximumTopologyPayloadBytes != 0) writer.LimitBytes(bytesRemaining);
            try { service.WriteSelected(ref writer, world, selected); }
            finally { if (options.MaximumTopologyPayloadBytes != 0) writer.EndByteLimit(); }
            bytesRemaining -= writer.Position - start;
        }
        writer.WriteInt32(0); // Selection entry points have no system participants.
        writer.Flush();
    }

    private void WriteSelectedTable(ref DataWriter writer, World world, SelectedTable table,
        IReadOnlySet<Entity> selected, Dictionary<int, SelectedShared> shared)
    {
        writer.WriteInt32(table.Columns.Length);
        foreach (SavedColumn column in table.Columns) writer.WriteInt32(_fileTypes[registry.GetOrder(column)]);
        int groupCount = 0;
        foreach (SavedColumn column in table.Columns) if (table.Source.TryGroup(column.Type, out _)) groupCount++;
        writer.WriteInt32(groupCount);
        foreach (SavedColumn column in table.Columns)
            if (table.Source.TryGroup(column.Type, out int key))
            {
                writer.WriteInt32(_fileTypes[registry.GetOrder(column)]);
                writer.WriteInt32(column is SavedShared ? shared[column.Type].Mapping[key] : key);
            }
        writer.WriteInt32(1); writer.WriteInt32(table.Entities.Count);
        writer.WriteEntities(CollectionsMarshal.AsSpan(table.Entities));
        foreach (SavedColumn column in table.Columns)
        {
            ComponentType type = ComponentTypes.Get(column.Type);
            writer.WriteByte(0); // Rows are grouped by their present projection columns.
            if (column.Storage is not (SavedStorage.Parent or SavedStorage.Children or SavedStorage.Endpoints))
            {
                long start = writer.Position;
                foreach (Entity entity in table.Entities)
                {
                    ref EntityRecord record = ref world.Records[entity.Id];
                    if (column is SavedShared) writer.WriteInt32(shared[column.Type].Mapping[ColumnEntry.At<int>(ref record.Chunk!.Columns[column.Type], record.Row)]);
                    else column.WriteCell(ref writer, world, ref record.Chunk!.Columns[column.Type], record.Row);
                }
                if (column is not SavedShared && !column.Raw && !type.IsTag) writer.WriteInt32(checked((int)(writer.Position - start)));
            }
            bool allEnabled = true;
            foreach (Entity entity in table.Entities)
            {
                ref EntityRecord record = ref world.Records[entity.Id];
                if (!record.Chunk!.IsEnabled(column.Type, record.Row)) { allEnabled = false; break; }
            }
            writer.WriteByte(allEnabled ? (byte)0 : (byte)1);
            if (!allEnabled)
                for (int start = 0; start < table.Entities.Count; start += 64)
                {
                    ulong word = 0;
                    for (int row = 0; row < Math.Min(64, table.Entities.Count - start); row++)
                    {
                        ref EntityRecord record = ref world.Records[table.Entities[start + row].Id];
                        if (record.Chunk!.IsEnabled(column.Type, record.Row)) word |= 1UL << row;
                    }
                    writer.WriteUInt64(word);
                }
            SavedChanges.WriteBaseline(ref writer, table.Entities.Count, type.TracksWrites, type.TracksAdded, world.Version);
        }
    }
}
