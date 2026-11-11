using System.Buffers;
using System.Numerics;
using SomeEngine.Job;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

public interface IWorldSystemBinding
{
    ISystemVersions[] Create(World world);
}

internal readonly struct EmptyWorldSystemBinding : IWorldSystemBinding
{
    public ISystemVersions[] Create(World world) => [];
}

public sealed partial class WorldSerializer
{
    public static World ReadWorld(Stream input, SerializationRegistry registry, WorldLoadOptions options = default)
        => ReadWorldCore(input, registry, options, new EmptyWorldSystemBinding());

    public static World ReadWorld<TBinding>(Stream input, SerializationRegistry registry, WorldLoadOptions options,
        in TBinding bindSystems) where TBinding : struct, IWorldSystemBinding
        => ReadWorldCore(input, registry, options, bindSystems);

    private static World ReadWorldCore<TBinding>(Stream input, SerializationRegistry registry, WorldLoadOptions options,
        in TBinding bindSystems) where TBinding : struct, IWorldSystemBinding
    {
        registry = SerializationRegistry.Bind(registry, options.Codecs);
        var reader = new DataReader(input, options.ReadLimits);
        try
        {
            var serializer = new WorldSerializer(registry);
            ReadHeaderData header = serializer.ReadHeader(ref reader, options.RequiredPurpose);
            var world = new World(header.Used, execution: options.Execution);
            try
            {
                serializer.ReadValues(world, ref reader, header, [], options, bindSystems, true);
                if (options.RequireEndOfInput) reader.RequireEndOfInput();
                return world;
            }
            catch { world.Dispose(); throw; }
        }
        finally { reader.Dispose(); }
    }

    public void Load(World world, ReadOnlySpan<byte> input, SerializationReadLimits? limits = null, params ISystemVersions[] systems)
    {
        var reader = new DataReader(input, limits);
        Read(world, ref reader, systems);
    }
    public void Load(World world, Stream input, SerializationReadLimits? limits = null, params ISystemVersions[] systems)
    {
        var reader = new DataReader(input, limits);
        try { Read(world, ref reader, systems); }
        finally { reader.Dispose(); }
    }

    private SavedType[] ReadManifest(ref DataReader reader, SavePurpose? requiredPurpose, byte payloadKind = 0)
    {
        if (reader.ReadUInt32() != Magic)
            throw SerializationErrors.CreateDataException(SerializationError.InvalidData, "The ECS container has a different format.");
        if (reader.ReadUInt16() != FormatVersion)
            throw SerializationErrors.CreateDataException(SerializationError.UnsupportedVersion, "The ECS container has a different version.");
        SavePurpose purpose = (SavePurpose)reader.ReadByte();
        if (requiredPurpose is { } required && required != purpose)
            throw SerializationErrors.CreateDataException(SerializationError.SchemaMismatch, "The ECS container has a different persistence purpose.");
        if (reader.ReadByte() != payloadKind)
            throw SerializationErrors.CreateDataException(SerializationError.SchemaMismatch, "The ECS container has a different kind.");
        Guid build = reader.ReadGuid();
        if (purpose == SavePurpose.RawCheckpoint && build != typeof(World).Module.ModuleVersionId)
            throw SerializationErrors.CreateDataException(SerializationError.SchemaMismatch, "The raw checkpoint belongs to a different Core build.");
        int count = reader.Budget.ManifestCount(reader.ReadInt32());
        var types = new SavedType[count];
        Guid previousId = default;
        for (int i = 0; i < count; i++)
        {
            var key = new SerializationTypeKey(reader.ReadGuid(), reader.ReadText(true), reader.ReadUInt64());
            SavedStorage storage = (SavedStorage)reader.ReadByte();
            int width = reader.ReadInt32();
            if (!registry.ById.TryGetValue(key.StableId, out SavedType? binding))
                throw SerializationErrors.CreateDataException(SerializationError.UnknownType, "The saved type requires a registry entry.");
            if (binding.KeyFor(purpose) != key || binding.Storage != storage || binding.Width != width
                || purpose == SavePurpose.DurableSave && !binding.SupportsDurable)
                throw SerializationErrors.CreateDataException(SerializationError.SchemaMismatch, "A serialized type differs from its registered schema.");
            if (i > 0 && key.StableId.CompareTo(previousId) <= 0)
                throw new InvalidDataException("The type manifest must follow format identity order with each type appearing once.");
            previousId = key.StableId;
            types[i] = binding;
        }
        return types;
    }

    private readonly record struct ReadHeaderData(SavedType[] Types, bool Preserve, ulong Version,
        int Used, int Alive, int[] Generations, int[] Free);

    private ReadHeaderData ReadHeader(ref DataReader reader, SavePurpose? requiredPurpose)
    {
        SavedType[] types = ReadManifest(ref reader, requiredPurpose);
        bool preserve = (CheckpointProgress)reader.ReadByte() == CheckpointProgress.Preserve;
        ulong savedVersion = reader.ReadUInt64();
        int used = reader.Budget.EntitySlotCount(reader.ReadInt32());
        int alive = reader.Budget.EntityCount(reader.ReadInt32());
        int freeCount = reader.Budget.Count(reader.ReadInt32(), used, "free entity");
        var generations = new int[used + 1];
        var free = new int[freeCount];
        for (int id = 1; id <= used; id++) generations[id] = reader.ReadInt32();
        for (int i = 0; i < free.Length; i++) free[i] = reader.ReadInt32();
        return new(types, preserve, savedVersion, used, alive, generations, free);
    }

    private void Read(World world, ref DataReader reader, ISystemVersions[] systems)
        => ReadValues(world, ref reader, ReadHeader(ref reader, null), systems, default, new EmptyWorldSystemBinding(), false);

    private void ReadValues<TBinding>(World world, ref DataReader reader, ReadHeaderData header, ISystemVersions[] systems,
        WorldLoadOptions options, in TBinding bindSystems, bool completeWorld) where TBinding : struct, IWorldSystemBinding
    {
        reader.ValuesWorld = world;
        reader.NormalizeShared = completeWorld;
        int[] targetGenerations = options.IdentityMode == EntityIdentityMode.Remap
            ? (int[])header.Generations.Clone() : header.Generations;
        int[] targetFree = options.IdentityMode == EntityIdentityMode.Remap
            ? (int[])header.Free.Clone() : header.Free;
        reader.Identities = options.IdentityMode switch
        {
            EntityIdentityMode.Preserve => null,
            EntityIdentityMode.Remap => new(targetGenerations, targetFree, options.MissingReferenceMode),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
        if (completeWorld) reader.BindReferenceViews();
        foreach (ISystemVersions system in systems) system.Complete();
        SavedType[] types = header.Types;
        bool preserve = header.Preserve;
        ulong version = preserve ? header.Version : Math.Max(header.Version, world.Version) + 1;
        world.Execution?.BeginClear();
        try
        {
            using WorldAccess access = world.OpenStructure();
            world.ClearValues();
            world.RestoreEntityIds(targetGenerations, targetFree, header.Alive, version);
            foreach (SomeEngine.ECS.Shared.SharedValues? values in world.SharedTypes) values?.Release(world);
            Array.Clear(world.SharedTypes);
            int sharedCount = reader.Budget.ManifestCount(reader.ReadInt32());
            for (int i = 0; i < sharedCount; i++) ReadType<SavedShared>(ref reader, types).ReadValues(ref reader, world);
            int tableCount = reader.Budget.TopologyPayloadCount(reader.ReadInt32());
            Dictionary<Archetype, int>? nextChunks = completeWorld ? new() : null;
            var readEntities = new bool[header.Used + 1];
            foreach (int id in header.Free) readEntities[id] = true;
            int entityCount = 0;
            for (int i = 0; i < tableCount; i++) entityCount += ReadTable(ref reader, world, types, preserve, nextChunks, header, readEntities);
            if (entityCount != header.Alive) throw new InvalidDataException("The table row count differs from the saved entity count.");
            int sparseCount = reader.Budget.ManifestCount(reader.ReadInt32());
            for (int i = 0; i < sparseCount; i++) ReadType<SavedSparse>(ref reader, types).Read(ref reader, world, preserve);
            int resourceCount = reader.Budget.ManifestCount(reader.ReadInt32());
            for (int i = 0; i < resourceCount; i++) ReadType<SavedResource>(ref reader, types).Read(ref reader, world);
            ReadTopology(ref reader, world, types);
            foreach (SavedService service in registry.Services) service.ValidateMain(world);
            world.RestoreEvents(ReadEvents(ref reader));
            registry.BindDestroyedReaders(world);
            int serviceCount = reader.Budget.TopologyPayloadCount(reader.ReadInt32());
            if (serviceCount > registry.Services.Count)
                throw new InvalidDataException("The topology service count exceeds its registered types.");
            var services = new HashSet<SavedService>();
            for (int i = 0; i < serviceCount; i++)
            {
                SavedType type = ReadType<SavedType>(ref reader, types);
                if (!registry.ServicesByType.TryGetValue(type.Type, out SavedService? service) || !services.Add(service))
                    throw new InvalidDataException("Each topology service requires one matching type section.");
                service.Read(ref reader, world, preserve);
            }
            if (completeWorld) world.RebuildDataGroups();
            world.RebuildDataIndexes();
            if (completeWorld) systems = bindSystems.Create(world);
            int systemCount = reader.Budget.ManifestCount(reader.ReadInt32());
            if (systemCount != systems.Length) throw new InvalidDataException("System progress participants differ from their saved registration.");
            for (int i = 0; i < systemCount; i++)
            {
                int count = reader.Budget.ManifestCount(reader.ReadInt32());
                if (count != systems[i].VersionCount) throw new InvalidDataException("System progress has a different number of systems.");
                ulong[] buffer = ArrayPool<ulong>.Shared.Rent(count * 2);
                try
                {
                    Span<ulong> values = buffer.AsSpan(0, count * 2);
                    for (int item = 0; item < values.Length; item++)
                    { ulong value = reader.ReadUInt64(); values[item] = preserve ? value : version; }
                    systems[i].RestoreVersions(values);
                }
                finally { ArrayPool<ulong>.Shared.Return(buffer); }
            }
        }
        finally { world.Execution?.EndClear(); }
    }

    private static int ReadTable(ref DataReader reader, World world, SavedType[] types, bool preserve,
        Dictionary<Archetype, int>? nextChunks, ReadHeaderData header, bool[] readEntities)
    {
        int entityCount = 0;
        int count = reader.Budget.EntityItemCount(reader.ReadInt32());
        var columns = new SavedColumn[count];
        var localTypes = new int[count];
        for (int i = 0; i < count; i++)
        {
            columns[i] = ReadType<SavedColumn>(ref reader, types);
            localTypes[i] = columns[i].Type;
            if (i > 0 && columns[i].Key.StableId.CompareTo(columns[i - 1].Key.StableId) <= 0)
                throw new InvalidDataException("Table columns must follow format identity order with each column appearing once.");
        }
        int groupCount = reader.Budget.Count(reader.ReadInt32(), count, "table grouping");
        var groups = new KeyValuePair<int, int>[groupCount];
        for (int i = 0; i < groupCount; i++)
        {
            SavedType type = types[reader.ReadInt32()];
            type.Activate(world);
            groups[i] = new(type.Type, reader.ReadInt32());
        }
        Array.Sort(groups, static (left, right) => left.Key.CompareTo(right.Key));
        var groupKeys = new int[groupCount];
        for (int i = 0; i < groups.Length; i++) groupKeys[i] = groups[i].Value;
        Archetype table = world.RestoreArchetype(new TypeSet(localTypes), groupKeys);
        int chunkCount = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "physical chunk");
        int firstChunk = nextChunks?.GetValueOrDefault(table) ?? 0;
        for (int index = 0; index < chunkCount; index++)
        {
            int rows = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "table row");
            entityCount += rows;
            Chunk chunk = table.RestoreChunk(firstChunk + index, rows);
            chunk.Count = rows;
            for (int row = 0; row < rows; row++)
            {
                Entity saved = reader.ReadEntity();
                if ((uint)(saved.Id - 1) >= (uint)header.Used || saved.Generation != header.Generations[saved.Id]
                    || readEntities[saved.Id])
                    throw new InvalidDataException("Each table row requires one live saved entity identity.");
                readEntities[saved.Id] = true;
                Entity entity = chunk.Entities[row] = reader.MapEntity(saved);
                world.Records[entity.Id] = new(chunk, row, entity.Generation);
            }
            foreach (SavedColumn binding in columns)
            {
                ComponentType type = ComponentTypes.Get(binding.Type);
                ref ColumnEntry column = ref chunk.Columns[type.Id];
                int local = column.ColumnPlusOne - 1;
                column.Presence = ReadMask(ref reader, chunk.Capacity, rows);
                if (column.Presence is not null) { table.Optional.Add(type.Id); table.TransferKinds |= 4; }
                if (binding.Storage is not (SavedStorage.Parent or SavedStorage.Endpoints or SavedStorage.Children))
                    binding.Read(ref reader, ref column, rows);
                ulong[]? enabled = ReadMask(ref reader, chunk.Capacity, rows);
                if (enabled is not null)
                {
                    table.Optional.Add(type.Id); table.TransferKinds |= 4;
                    chunk.Enabled ??= new ulong[]?[table.Components.Length];
                    chunk.DisabledCounts ??= new int[table.Components.Length];
                    chunk.Enabled[local] = enabled;
                    int active = 0;
                    for (int word = 0; word < (rows + 63) >> 6; word++) active += BitOperations.PopCount(enabled[word]);
                    chunk.DisabledCounts[local] = rows - active;
                }
                else if (chunk.Enabled is { } masks) { masks[local] = null; chunk.DisabledCounts![local] = 0; }
                if (type.TracksWrites)
                    SavedChanges.Read(ref reader, ref chunk.Changes![local], rows, true, type.TracksAdded, preserve, world.Version);
                else
                { ComponentChanges unused = default; SavedChanges.Read(ref reader, ref unused, rows, false, false, preserve, world.Version); }
            }
        }
        if (nextChunks is not null) nextChunks[table] = firstChunk + chunkCount;
        return entityCount;
    }

    private static void ReadTopology(ref DataReader reader, World world, SavedType[] types)
    {
        int count = reader.Budget.ManifestCount(reader.ReadInt32());
        var readTypes = new HashSet<int>();
        for (int type = 0; type < count; type++)
        {
            var binding = ReadType<SavedColumn>(ref reader, types);
            if (binding.Storage is not (SavedStorage.Parent or SavedStorage.Endpoints or SavedStorage.Children)
                || !readTypes.Add(binding.Type))
                throw new InvalidDataException("Each topology column requires one matching type section.");
            int rows = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "topology main value");
            var entities = new HashSet<Entity>();
            long start = reader.Position;
            for (int row = 0; row < rows; row++)
            {
                Entity entity = reader.MapEntity(reader.ReadEntity());
                if (!entities.Add(entity)) throw new InvalidDataException("A topology column contains an entity twice.");
                ref EntityRecord record = ref world.Records[entity.Id];
                if (record.Chunk is null || (uint)binding.Type >= (uint)record.Chunk.Columns.Length
                    || !record.Chunk.Columns[binding.Type].Has(record.Row))
                    throw new InvalidDataException("A topology value requires its declared entity column.");
                binding.ReadCell(ref reader, ref record.Chunk!.Columns[binding.Type], record.Row);
            }
            if (reader.Position - start != reader.Budget.PayloadLength(reader.ReadInt32()))
                throw new InvalidDataException("Topology value length differs from its segment footer.");
        }
    }

    private static TBinding ReadType<TBinding>(ref DataReader reader, SavedType[] types) where TBinding : SavedType
    {
        int index = reader.ReadInt32();
        if ((uint)index >= (uint)types.Length || types[index] is not TBinding binding)
            throw new InvalidDataException("The section type index requires a matching manifest entry.");
        return binding;
    }

    private static ulong[]? ReadMask(ref DataReader reader, int capacity, int count)
    {
        if (reader.ReadByte() == 0) return null;
        var words = new ulong[(capacity + 63) >> 6];
        for (int word = 0; word < (count + 63) >> 6; word++) words[word] = reader.ReadUInt64();
        return words;
    }
    private static WorldEvents ReadEvents(ref DataReader reader)
    {
        ulong through = reader.ReadUInt64();
        int count = reader.Budget.TopologyEntryCount(reader.ReadInt32(), "destroyed entity fact");
        var values = new DestroyedEntity[count];
        for (int i = 0; i < count; i++) values[i] = new(reader.MapEntity(reader.ReadEntity(), true), reader.ReadUInt64());
        int readers = reader.Budget.ManifestCount(reader.ReadInt32());
        var positions = new EventReaderPosition[readers];
        for (int i = 0; i < positions.Length; i++) positions[i] = new(reader.ReadGuid(), reader.ReadUInt64());
        return new(values, through, positions);
    }
}
