using System.Buffers;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Serialization;

public sealed partial class WorldSerializer
{
    private const byte EntityPayload = 2;

    /// <summary>Writes the selected members and their component columns as an entity group.</summary>
    public static void WriteEntities(Stream output, World source, ReadOnlySpan<Entity> entities,
        SerializeOptions options = default)
        => new WorldSerializer(SerializationRegistry.Bind(null, options.Codecs))
            .WriteEntityGroup(output, source, entities, options);

    /// <summary>Appends a complete entity group and returns its new member identities.</summary>
    public static Entity[] ReadEntities(ReadOnlyMemory<byte> input, World target,
        SerializationReadOptions options = default)
    {
        var reader = new DataReader(input.Span, options.ReadLimits);
        return new WorldSerializer(SerializationRegistry.Bind(null, options.Codecs))
            .ReadEntityGroup(ref reader, input, target, options);
    }

    /// <summary>Appends a complete entity group from the current stream position.</summary>
    public static Entity[] ReadEntities(Stream input, World target, SerializationReadOptions options = default)
    {
        var reader = new DataReader(input, options.ReadLimits);
        try
        {
            return new WorldSerializer(SerializationRegistry.Bind(null, options.Codecs))
                .ReadEntityGroup(ref reader, default, target, options);
        }
        finally { reader.Dispose(); }
    }

    private sealed class EntityWriteColumn(SavedType type)
    {
        internal readonly SavedType Type = type;
        internal readonly List<int> Members = [];
        internal ReadOnlyMemory<byte> Bytes;
    }

    private sealed class EntityWriteGroup(SavedColumn[] columns)
    {
        internal readonly SavedColumn[] Columns = columns;
        internal readonly List<int> Members = [];
    }

    private void WriteEntityGroup(Stream output, World source, ReadOnlySpan<Entity> entities,
        SerializeOptions options)
    {
        ValidateWriteOptions(options);
        using WorldAccess structure = source.OpenStructure(false);
        using WorldAccess values = source.OpenValues(WorldCheckpoint.CaptureAccesses(source));
        source.BeginRead();
        try
        {
            var selected = new Dictionary<SavedType, EntityWriteColumn>();
            var bindings = new Dictionary<Archetype, SavedColumn[]>();
            var completeGroups = new Dictionary<Archetype, EntityWriteGroup>();
            var groups = new Dictionary<TypeSet, EntityWriteGroup>();
            for (int index = 0; index < entities.Length; index++)
            {
                ref EntityRecord record = ref source.Resolve(entities[index]);
                Chunk chunk = record.Chunk!;
                int sourceRow = record.Row;
                if (!bindings.TryGetValue(chunk.Archetype, out SavedColumn[]? bound))
                {
                    bound = chunk.Archetype.Components.Where(component => !component.IsComputed && registry.Columns.ContainsKey(component.Id))
                        .Select(component => registry.Columns[component.Id]).ToArray();
                    bindings.Add(chunk.Archetype, bound);
                }
                bool optional = false;
                foreach (SavedColumn column in bound)
                    if (chunk.Columns[column.Type].Presence is not null) { optional = true; break; }
                completeGroups.TryGetValue(chunk.Archetype, out EntityWriteGroup? group);
                if (optional || group is null)
                {
                    SavedColumn[] present = optional ? bound.Where(column => chunk.Columns[column.Type].Has(sourceRow)).ToArray() : bound;
                    var types = new TypeSet(false, present.Select(column => column.Type).ToArray());
                    if (!groups.TryGetValue(types, out group)) groups.Add(types, group = new(present));
                    if (!optional) completeGroups.Add(chunk.Archetype, group);
                }
                group.Members.Add(index);
                foreach (SavedColumn column in group.Columns) AddSelected(selected, column, index);
                if (source.SparseTypes is { } sparse)
                    foreach (SavedSparse column in registry.Sparse.Values)
                        if ((uint)column.Type < (uint)sparse.Types.Length
                            && sparse.Types[column.Type] is { } store && store.Has(entities[index]))
                            AddSelected(selected, column, index);
            }
            EntityWriteColumn[] columns = selected.Values.OrderBy(column => column.Type.Key.StableId).ToArray();
            _used.Clear();
            foreach (EntityWriteColumn column in columns) _used.Add(column.Type);
            var writer = new DataWriter(output, options.ExternalReferences);
            WriteHeader(ref writer, options.Purpose, EntityPayload);
            writer.WriteInt32(entities.Length);
            writer.WriteEntities(entities);
            var fileTypes = new Dictionary<SavedType, int>();
            for (int index = 0; index < columns.Length; index++) fileTypes.Add(columns[index].Type, index);
            writer.WriteInt32(groups.Count);
            foreach (EntityWriteGroup group in groups.Values)
            {
                writer.WriteInt32(group.Columns.Length);
                foreach (SavedColumn column in group.Columns) writer.WriteInt32(fileTypes[column]);
                writer.WriteInt32(group.Members.Count);
                foreach (int member in group.Members) writer.WriteInt32(member);
            }
            writer.WriteInt32(columns.Length);
            int sparseCount = 0;
            long topologyCount = 0, topologyBytes = 0;
            for (int index = 0; index < columns.Length; index++)
            {
                EntityWriteColumn column = columns[index];
                var bytes = new ArrayBufferWriter<byte>();
                var fields = new DataWriter(bytes, options.ExternalReferences);
                foreach (int member in column.Members)
                {
                    Entity entity = entities[member];
                    if (column.Type is SavedColumn tableColumn)
                    {
                        ref EntityRecord record = ref source.Records[entity.Id];
                        tableColumn.WriteCell(ref fields, source, ref record.Chunk!.Columns[column.Type.Type], record.Row);
                    }
                    else ((SavedSparse)column.Type).WriteCell(ref fields, source.SparseTypes!.Types[column.Type.Type]!, entity);
                }
                fields.Flush();
                column.Bytes = bytes.WrittenMemory;
                writer.WriteInt32(index);
                writer.WriteInt32(column.Members.Count);
                foreach (int member in column.Members) writer.WriteInt32(member);
                if (column.Type is SavedColumn)
                    foreach (int member in column.Members)
                    {
                        ref EntityRecord record = ref source.Records[entities[member].Id];
                        writer.WriteBoolean(record.Chunk!.IsEnabled(column.Type.Type, record.Row));
                    }
                writer.WriteInt32(column.Bytes.Length);
                if (column.Type is SavedSparse) sparseCount += column.Members.Count;
                if (column.Type.Storage is SavedStorage.Parent or SavedStorage.Endpoints)
                { topologyCount += column.Members.Count; topologyBytes += column.Bytes.Length; }
            }
            if (options.MaximumSparseMemberships != 0 && sparseCount > options.MaximumSparseMemberships)
                throw new InvalidOperationException("Sparse membership count exceeds the configured write limit.");
            if (options.MaximumTopologyRecords != 0 && topologyCount > options.MaximumTopologyRecords)
                throw new InvalidOperationException("Topology records exceed the configured write limit.");
            if (options.MaximumTopologyPayloadBytes != 0 && topologyBytes > options.MaximumTopologyPayloadBytes)
                throw new InvalidOperationException("Topology payload bytes exceed the configured write limit.");
            foreach (EntityWriteColumn column in columns) writer.WriteRawBytes(column.Bytes.Span);
            writer.Flush();
        }
        finally { source.EndRead(); }
    }

    private static void AddSelected(Dictionary<SavedType, EntityWriteColumn> columns, SavedType type, int member)
    {
        if (!columns.TryGetValue(type, out EntityWriteColumn? column)) columns.Add(type, column = new(type));
        column.Members.Add(member);
    }

    private sealed class EntityReadColumn(SavedType type, int[] members, bool[]? enabled, int length)
    {
        internal readonly SavedType Type = type;
        internal readonly int[] Members = members;
        internal readonly bool[]? Enabled = enabled;
        internal readonly int Length = length;
        internal ReadOnlyMemory<byte> Bytes;
    }

    private readonly record struct EntityReadGroup(TypeSet Types, int[] Members);

    private Entity[] ReadEntityGroup(ref DataReader reader, ReadOnlyMemory<byte> input, World target,
        SerializationReadOptions options)
    {
        SavedType[] types = ReadManifest(ref reader, options.RequiredPurpose, EntityPayload);
        int count = reader.Budget.EntityCount(reader.ReadInt32());
        var sources = new Entity[count];
        for (int index = 0; index < count; index++) sources[index] = reader.ReadEntity();
        int groupCount = reader.Budget.Count(reader.ReadInt32(), count, "entity group");
        var groups = new EntityReadGroup[groupCount];
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            int typeCount = reader.Budget.EntityItemCount(reader.ReadInt32());
            int[] components = new int[typeCount];
            for (int index = 0; index < components.Length; index++)
                components[index] = ((SavedColumn)types[reader.Budget.Count(reader.ReadInt32(), types.Length - 1, "entity type")]).Type;
            int memberCount = reader.Budget.Count(reader.ReadInt32(), count, "group member");
            int[] members = new int[memberCount];
            for (int index = 0; index < members.Length; index++)
                members[index] = reader.Budget.Count(reader.ReadInt32(), count - 1, "group entity");
            groups[groupIndex] = new(new TypeSet(false, components), members);
        }
        int columnCount = reader.Budget.Count(reader.ReadInt32(), types.Length, "entity column");
        var columns = new EntityReadColumn[columnCount];
        for (int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
        {
            int typeIndex = reader.Budget.Count(reader.ReadInt32(), types.Length - 1, "entity column type");
            SavedType type = types[typeIndex];
            int membersCount = reader.Budget.Count(reader.ReadInt32(), count, "column member");
            var members = new int[membersCount];
            for (int index = 0; index < members.Length; index++)
            {
                int member = members[index] = reader.Budget.Count(reader.ReadInt32(), count - 1, "column entity");
            }
            bool[]? enabled = null;
            if (type is SavedColumn)
            {
                enabled = new bool[members.Length];
                for (int index = 0; index < enabled.Length; index++) enabled[index] = reader.ReadBoolean();
            }
            int length = reader.Budget.PayloadLength(reader.ReadInt32());
            columns[columnIndex] = new(type, members, enabled, length);
        }
        int offset = checked((int)reader.Position);
        foreach (EntityReadColumn column in columns)
        {
            if (!input.IsEmpty) column.Bytes = input.Slice(offset, column.Length);
            else
            {
                reader.Budget.ReserveAllocation(column.Length, "stream column bytes");
                byte[] bytes = new byte[column.Length];
                reader.ReadRawBytes(bytes);
                column.Bytes = bytes;
            }
            offset = checked(offset + column.Length);
        }
        using WorldAccess structure = target.OpenStructure();
        using World.BatchVersionScope version = target.BeginBatchVersion(0);
        foreach (SavedType type in types) type.Activate(target);
        var bound = new Archetype[count];
        foreach (EntityReadGroup group in groups)
        {
            Archetype table = target.Find(group.Types);
            foreach (int member in group.Members) bound[member] = table;
        }
        var membersCreated = new Entity[count];
        target.CreateCopies(bound, 1, membersCreated);
        foreach (EntityReadColumn column in columns)
        {
            if (column.Type is SavedColumn tableColumn) tableColumn.PrepareRead(target);
            else ((SavedSparse)column.Type).AllocateAppend(target, membersCreated, column.Members);
        }
        var references = new EntityGroupReferences(sources, membersCreated, options.MissingReferenceMode);
        var work = new ReadEntityColumns(target, columns, membersCreated, references, reader.Budget);
        if (target.Execution is { } execution) execution.ExecuteParallel(work, columns.Length);
        else JobSystem.Default.ExecuteParallel(work, columns.Length);
        foreach (EntityReadColumn column in columns)
            if (column.Enabled is { } enabled)
                for (int index = 0; index < enabled.Length; index++)
                    if (!enabled[index])
                    {
                        ref EntityRecord record = ref target.Records[membersCreated[column.Members[index]].Id];
                        target.SetEnabled(record.Chunk!, column.Type.Type, record.Row, false);
                    }
        target.CompleteCopies(membersCreated);
        return membersCreated;
    }

    private readonly struct ReadEntityColumns(World world, EntityReadColumn[] columns, Entity[] entities,
        EntityGroupReferences references, SerializationReadBudget budget) : IJobParallelFor
    {
        public void Execute(int item)
        {
            EntityReadColumn column = columns[item];
            var reader = new DataReader(column.Bytes.Span, budget) { ValuesWorld = world };
            reader.BindEntityReferences(references);
            foreach (int index in column.Members)
            {
                Entity entity = entities[index];
                if (column.Type is SavedColumn tableColumn)
                {
                    ref EntityRecord record = ref world.Records[entity.Id];
                    tableColumn.ReadCell(ref reader, ref record.Chunk!.Columns[column.Type.Type], record.Row);
                }
                else ((SavedSparse)column.Type).ReadCell(ref reader, world, entity);
            }
            reader.RequireEndOfInput();
        }
    }

    private sealed class EntityGroupReferences : ReadReferenceRemapper
    {
        private readonly Dictionary<Entity, Entity> _entities;
        private readonly MissingReferenceMode _missing;

        internal EntityGroupReferences(Entity[] source, Entity[] target, MissingReferenceMode missing)
        {
            _entities = new(source.Length);
            for (int index = 0; index < source.Length; index++) _entities.Add(source[index], target[index]);
            _missing = missing;
        }

        public override bool TryMap(Entity source, out Entity mapped)
        {
            if (source == Entity.Null) { mapped = source; return true; }
            if (_entities.TryGetValue(source, out mapped)) return true;
            mapped = _missing switch
            {
                MissingReferenceMode.KeepOriginal => source,
                MissingReferenceMode.Clear => Entity.Null,
                MissingReferenceMode.Throw => throw new InvalidOperationException($"Missing entity reference remap for {source}."),
                _ => throw new ArgumentOutOfRangeException(nameof(_missing)),
            };
            return true;
        }
    }
}
