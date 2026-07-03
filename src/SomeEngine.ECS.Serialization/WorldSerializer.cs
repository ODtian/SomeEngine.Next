using System.Runtime.InteropServices;
using System.Text;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Serialization;

public static partial class WorldSerializer
{
    public static byte[] Serialize(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using var stream = new MemoryStream();
        WriteWorld(stream, world, new SerializationRegistry());
        return stream.ToArray();
    }

    public static World Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        return ReadWorld(stream, new SerializationRegistry());
    }

    private const int LinearStableLimit = 8;

    public static void WriteComponent<T>(
        Stream stream,
        in T value,
        SerializationRegistry registry)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);

        SerializationTypeRuntime runtime = registry.GetRegistered<T>();
        T capturedValue = value;
        byte[] payload = WriteData(binaryWriter =>
        {
            var writer = new DataWriter(binaryWriter);
            runtime.Component.Write(ref writer, capturedValue);
        });

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        PayloadFormat.WriteHeader(writer, SnapshotPayloadKind.Component, new[] { runtime });
        writer.Write(0);
        PayloadBytes.Write(writer, payload);
    }

    public static T ReadComponent<T>(
        Stream stream,
        SerializationRegistry registry)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);

        using var binaryReader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var (kind, manifest) = PayloadFormat.ReadHeader(binaryReader);
        if (kind != SnapshotPayloadKind.Component)
            throw new InvalidDataException($"Expected Component payload, found {kind}.");
        if (manifest.Length != 1)
            throw new InvalidDataException("Component payload must contain exactly one manifest entry.");

        SerializationTypeRuntime runtime = registry.ResolveExact(manifest[0]);
        if (runtime.ValueType != typeof(T))
            throw new InvalidDataException(
                $"Component payload contains {runtime.ValueType.FullName}, not requested {typeof(T).FullName}.");

        int manifestIndex = binaryReader.ReadInt32();
        if (manifestIndex != 0)
            throw new InvalidDataException("Invalid component payload manifest index.");

        byte[] data = PayloadBytes.Read(binaryReader);
        return ReadData(data, binaryReader =>
        {
            var reader = new DataReader(binaryReader);
            return (T)runtime.Component.Read(ref reader);
        });
    }

    public static void WriteEntity(
        Stream stream,
        World world,
        Entity entity,
        SerializationRegistry registry,
        SerializeOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);
        IdentityMap.RejectRemap(options.IdentityMode);

        EntityPayload entityPayload = CaptureEntity(world, entity, registry);
        EntityCodec.Write(stream, SnapshotPayloadKind.Entity, entityPayload);
    }

    public static void ApplyEntity(
        Stream stream,
        World world,
        Entity entity,
        SerializationRegistry registry,
        EntityApplyOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        EntityPayload payload = EntityCodec.Read(
            stream,
            registry,
            SnapshotPayloadKind.Entity,
            options.UnknownTypeMode,
            options.SchemaMismatchMode);
        IReferenceRemapper? remapper = IdentityMap.SingleMap(
            payload.Entity,
            entity,
            options.IdentityMode,
            options.MissingReferenceMode);

        using var journalSuppression = world.Journal.Suppress();
        if (options.ApplyMode != EntityApplyMode.MergeIncluded)
            EntityReplacer.RemoveMissing(world, entity, payload, registry);

        EntityApplier.Apply(world, entity, payload, options.ApplyMode, remapper);
    }

    public static Entity CreateEntity(
        Stream stream,
        World world,
        SerializationRegistry registry,
        EntityCreateOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        EntityPayload payload = EntityCodec.Read(
            stream,
            registry,
            SnapshotPayloadKind.Entity,
            options.UnknownTypeMode,
            options.SchemaMismatchMode);
        using var journalSuppression = world.Journal.Suppress();
        Entity entity = world.CreateEntity();
        IReferenceRemapper? remapper = IdentityMap.SingleMap(
            payload.Entity,
            entity,
            options.IdentityMode,
            options.MissingReferenceMode);
        EntityApplier.Apply(world, entity, payload, EntityApplyMode.MergeIncluded, remapper);
        return entity;
    }

    public static void WriteEntities(
        Stream stream,
        World world,
        ReadOnlySpan<Entity> entities,
        SerializationRegistry registry,
        SerializeOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);
        IdentityMap.RejectRemap(options.IdentityMode);

        var payloads = new List<EntityPayload>(entities.Length);
        for (int i = 0; i < entities.Length; i++)
            payloads.Add(CaptureEntity(world, entities[i], registry));

        EntityCodec.WriteSet(stream, SnapshotPayloadKind.EntitySet, payloads);
    }

    public static Entity[] CreateEntities(
        Stream stream,
        World world,
        SerializationRegistry registry,
        EntityCreateOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        EntityPayload[] payloads = EntityCodec.ReadSet(
            stream,
            registry,
            SnapshotPayloadKind.EntitySet,
            options.UnknownTypeMode,
            options.SchemaMismatchMode);

        return ImportPayloads(world, payloads, options.MissingReferenceMode);
    }

    public static void WriteQuery(
        Stream stream,
        World world,
        QueryHandle query,
        SerializationRegistry registry,
        SerializeOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);
        IdentityMap.RejectRemap(options.IdentityMode);

        var payloads = new List<EntityPayload>();
        foreach (var row in world.RunQuery(query).Rows)
            payloads.Add(CaptureEntity(world, row.Entity, registry));

        EntityCodec.WriteSet(stream, SnapshotPayloadKind.QueryResult, payloads);
    }

    public static Entity[] CreateQueryResult(
        Stream stream,
        World world,
        SerializationRegistry registry,
        EntityCreateOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        EntityPayload[] payloads = EntityCodec.ReadSet(
            stream,
            registry,
            SnapshotPayloadKind.QueryResult,
            options.UnknownTypeMode,
            options.SchemaMismatchMode);

        return ImportPayloads(world, payloads, options.MissingReferenceMode);
    }

    public static void WriteWorld(
        Stream stream,
        World world,
        SerializationRegistry registry,
        SerializeOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);
        IdentityMap.RejectRemap(options.IdentityMode);

        Entity[] liveEntities = world.LiveEntities();
        var entityPayloads = new List<EntityPayload>(liveEntities.Length);
        for (int i = 0; i < liveEntities.Length; i++)
            entityPayloads.Add(CaptureEntity(world, liveEntities[i], registry));

        EntitySlotSnapshot[] slots = world.EntitySlots();

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var manifest = BuildManifest(entityPayloads);
        var manifestIndex = BuildManifestIndex(manifest);
        PayloadFormat.WriteHeader(writer, SnapshotPayloadKind.World, manifest);

        writer.Write(world.Clock.Tick);
        writer.Write(slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            writer.Write(slots[i].Index);
            writer.Write(slots[i].Generation);
            writer.Write(slots[i].IsAlive);
        }

        EntityCodec.WriteAll(writer, entityPayloads, manifestIndex, sortByEntityIndex: true);
    }

    public static World ReadWorld(
        Stream stream,
        SerializationRegistry registry,
        WorldLoadOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);

        if (options.IdentityMode != EntityIdentityMode.Remap)
        {
            var loadedWorld = new World();
            WorldRestorer.Restore(
                stream,
                loadedWorld,
                registry,
                options.UnknownTypeMode,
                options.SchemaMismatchMode);
            return loadedWorld;
        }

        WorldPayload payload = ReadWorldPayload(
            stream,
            registry,
            options.UnknownTypeMode,
            options.SchemaMismatchMode);
        var world = new World();
        WorldLoader.Load(world, payload, options);
        return world;
    }

    public static void LoadInto(
        Stream stream,
        World world,
        SerializationRegistry registry,
        WorldLoadOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        if (options.IdentityMode != EntityIdentityMode.Remap)
        {
            WorldRestorer.Restore(
                stream,
                world,
                registry,
                options.UnknownTypeMode,
                options.SchemaMismatchMode);
            return;
        }

        WorldPayload payload = ReadWorldPayload(
            stream,
            registry,
            options.UnknownTypeMode,
            options.SchemaMismatchMode);
        WorldLoader.Load(world, payload, options);
    }

    public static void WriteDelta(
        Stream stream,
        World world,
        SerializationRegistry registry,
        DeltaSerializeOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        var events = world.Journal.Events;
        var affectedPayloads = DeltaCodec.Capture(world, registry, events);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var manifest = BuildManifest(affectedPayloads);
        var manifestIndex = BuildManifestIndex(manifest);
        PayloadFormat.WriteHeader(writer, SnapshotPayloadKind.Delta, manifest);
        DeltaCodec.WriteEvents(writer, events);
        EntityCodec.WriteAll(writer, affectedPayloads, manifestIndex, sortByEntityIndex: false);

        if (options.ClearJournal)
            world.Journal.Clear();
    }

    public static DeltaEvent[] ReadDeltaEvents(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var (kind, manifest) = PayloadFormat.ReadHeader(reader);
        if (kind != SnapshotPayloadKind.Delta)
            throw new InvalidDataException($"Expected Delta payload, found {kind}.");
        _ = manifest;

        return DeltaCodec.ReadEvents(reader);
    }

    public static void ApplyDelta(
        Stream stream,
        World world,
        SerializationRegistry registry,
        EntityApplyOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var (kind, manifestKeys) = PayloadFormat.ReadHeader(reader);
        if (kind != SnapshotPayloadKind.Delta)
            throw new InvalidDataException($"Expected Delta payload, found {kind}.");

        ManifestEntry[] manifest = ResolveManifest(registry, manifestKeys, options.UnknownTypeMode, options.SchemaMismatchMode);
        DeltaEvent[] events = DeltaCodec.ReadEvents(reader);
        EntityPayload[] payloads = EntityCodec.ReadAll(reader, manifest);

        using var journalSuppression = world.Journal.Suppress();
        foreach (var deltaEvent in events)
        {
            if (deltaEvent.Kind == DeltaEventKind.EntityDestroyed && world.IsAlive(deltaEvent.Entity))
                world.DestroyEntity(deltaEvent.Entity);
        }

        foreach (var payload in payloads)
        {
            if (!world.IsAlive(payload.Entity))
            {
                Entity created = world.CreateEntity();
                if (created != payload.Entity)
                {
                    throw new InvalidOperationException(
                        $"Delta baseline identity mismatch: expected to create {payload.Entity}, created {created}.");
                }
            }

            EntityApplier.Apply(
                world,
                payload.Entity,
                payload,
                options.ApplyMode);
        }
    }

    public static string ReadDebugSummary(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long position = stream.CanSeek ? stream.Position : 0;
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var (kind, manifest) = PayloadFormat.ReadHeader(reader);
            var builder = new StringBuilder();
            builder.AppendLine($"PayloadKind: {kind}");
            builder.AppendLine($"ManifestCount: {manifest.Length}");
            Array.Sort(manifest, SerializationRegistry.CompareTypeKeys);
            foreach (var key in manifest)
                builder.AppendLine($"- {key.StableName} {key.StableId} 0x{key.SchemaHash:X8}");
            return builder.ToString();
        }
        finally
        {
            if (stream.CanSeek)
                stream.Position = position;
        }
    }
}

public static partial class WorldSerializer
{
    private static EntityPayload CaptureEntity(World world, Entity entity, SerializationRegistry registry)
    {
        if (!world.IsAlive(entity))
            throw new InvalidOperationException($"Cannot serialize {entity}: entity is not alive.");

        using var journalSuppression = world.Journal.Suppress();
        var runtimes = registry.RuntimeTypes;
        var items = new List<EntityItemPayload>(runtimes.Count);
        foreach (var runtime in runtimes)
        {
            if (!runtime.IsPresent(world, entity))
                continue;

            items.Add(new EntityItemPayload(runtime, runtime.Item.Capture(world, entity)));
        }

        return new EntityPayload(entity, items);
    }

    private static class EntityCodec
    {
        public static void Write(Stream stream, SnapshotPayloadKind kind, EntityPayload payload)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            var manifest = BuildManifest(new[] { payload });
            var manifestIndex = BuildManifestIndex(manifest);
            PayloadFormat.WriteHeader(writer, kind, manifest);
            WriteRecord(writer, payload, manifestIndex);
        }

        public static void WriteSet(Stream stream, SnapshotPayloadKind kind, IReadOnlyList<EntityPayload> payloads)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            var manifest = BuildManifest(payloads);
            var manifestIndex = BuildManifestIndex(manifest);
            PayloadFormat.WriteHeader(writer, kind, manifest);
            WriteAll(writer, payloads, manifestIndex, sortByEntityIndex: false);
        }

        public static void WriteAll(
            BinaryWriter writer,
            IReadOnlyList<EntityPayload> payloads,
            Dictionary<Guid, int> manifestIndex,
            bool sortByEntityIndex)
        {
            writer.Write(payloads.Count);
            if (sortByEntityIndex)
            {
                foreach (var payload in payloads.OrderBy(static payload => payload.Entity.Index))
                    WriteRecord(writer, payload, manifestIndex);

                return;
            }

            for (int i = 0; i < payloads.Count; i++)
                WriteRecord(writer, payloads[i], manifestIndex);
        }

        public static EntityPayload Read(
            Stream stream,
            SerializationRegistry registry,
            SnapshotPayloadKind expectedKind,
            UnknownTypeMode unknownTypeMode,
            SchemaMismatchMode schemaMismatchMode)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var (kind, manifestKeys) = PayloadFormat.ReadHeader(reader);
            if (kind != expectedKind)
                throw new InvalidDataException($"Expected {expectedKind} payload, found {kind}.");

            ManifestEntry[] manifest = ResolveManifest(registry, manifestKeys, unknownTypeMode, schemaMismatchMode);
            return ReadRecord(reader, manifest);
        }

        public static EntityPayload[] ReadSet(
            Stream stream,
            SerializationRegistry registry,
            SnapshotPayloadKind expectedKind,
            UnknownTypeMode unknownTypeMode,
            SchemaMismatchMode schemaMismatchMode)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var (kind, manifestKeys) = PayloadFormat.ReadHeader(reader);
            if (kind != expectedKind)
                throw new InvalidDataException($"Expected {expectedKind} payload, found {kind}.");

            ManifestEntry[] manifest = ResolveManifest(registry, manifestKeys, unknownTypeMode, schemaMismatchMode);
            return ReadAll(reader, manifest);
        }

        public static EntityPayload[] ReadAll(
            BinaryReader reader,
            IReadOnlyList<ManifestEntry> manifest)
        {
            int count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Negative entity payload count.");

            EntityPayload[] payloads = new EntityPayload[count];
            for (int i = 0; i < payloads.Length; i++)
                payloads[i] = ReadRecord(reader, manifest);

            return payloads;
        }

        private static void WriteRecord(
            BinaryWriter writer,
            EntityPayload payload,
            Dictionary<Guid, int> manifestIndex)
        {
            var dataWriter = new DataWriter(writer);
            dataWriter.WriteEntity(payload.Entity);
            writer.Write(payload.Items.Count);

            foreach (var item in payload.Items)
            {
                writer.Write(manifestIndex[item.Runtime.Entry.TypeKey.StableId]);
                PayloadBytes.WriteItem(writer, item.Runtime, item.Value);
            }
        }

        private static EntityPayload ReadRecord(
            BinaryReader reader,
            IReadOnlyList<ManifestEntry> manifest)
        {
            var dataReader = new DataReader(reader);
            Entity entity = dataReader.ReadEntity();
            int itemCount = reader.ReadInt32();
            if (itemCount < 0)
                throw new InvalidDataException("Negative entity item count.");

            var items = new List<EntityItemPayload>(itemCount);
            for (int i = 0; i < itemCount; i++)
            {
                int manifestIndex = reader.ReadInt32();
                if ((uint)manifestIndex >= (uint)manifest.Count)
                    throw new InvalidDataException($"Invalid manifest index {manifestIndex}.");

                byte[] data = PayloadBytes.Read(reader);
                var entry = manifest[manifestIndex];
                if (entry.Runtime is not null)
                {
                    if (entry.Migration is not null)
                        data = MigratePayload(data, entry.Migration);

                    var runtime = entry.Runtime;
                    object value = ReadData(data, binaryReader =>
                    {
                        var itemReader = new DataReader(binaryReader);
                        return runtime.Item.Read(ref itemReader);
                    });
                    items.Add(new EntityItemPayload(runtime, value));
                }
            }

            return new EntityPayload(entity, items);
        }
    }

    private static class DeltaCodec
    {
        public static List<EntityPayload> Capture(
            World world,
            SerializationRegistry registry,
            IReadOnlyList<SerializationChangeEvent> events)
        {
            var affected = new HashSet<Entity>();
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Kind != SerializationChangeKind.EntityDestroyed &&
                    world.IsAlive(events[i].Entity))
                {
                    affected.Add(events[i].Entity);
                }
            }

            var payloads = new List<EntityPayload>(affected.Count);
            foreach (var entity in affected.OrderBy(static entity => entity.Index))
                payloads.Add(CaptureEntity(world, entity, registry));

            return payloads;
        }

        public static void WriteEvents(
            BinaryWriter writer,
            IReadOnlyList<SerializationChangeEvent> events)
        {
            writer.Write(events.Count);
            for (int i = 0; i < events.Count; i++)
            {
                writer.Write((byte)MapKind(events[i].Kind));
                var dataWriter = new DataWriter(writer);
                dataWriter.WriteEntity(events[i].Entity);
                writer.Write(events[i].ComponentId);
                dataWriter.WriteEntity(events[i].Target);
                writer.Write(events[i].Version);
            }
        }

        public static DeltaEvent[] ReadEvents(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Negative delta event count.");

            DeltaEvent[] events = new DeltaEvent[count];
            var dataReader = new DataReader(reader);
            for (int i = 0; i < events.Length; i++)
            {
                var eventKind = (DeltaEventKind)reader.ReadByte();
                Entity entity = dataReader.ReadEntity();
                int componentId = reader.ReadInt32();
                Entity target = dataReader.ReadEntity();
                uint version = reader.ReadUInt32();
                events[i] = new DeltaEvent(eventKind, entity, componentId, target, version);
            }

            return events;
        }

        private static DeltaEventKind MapKind(SerializationChangeKind kind)
        {
            return kind switch
            {
                SerializationChangeKind.EntityCreated => DeltaEventKind.EntityCreated,
                SerializationChangeKind.EntityDestroyed => DeltaEventKind.EntityDestroyed,
                SerializationChangeKind.ComponentAdded => DeltaEventKind.ComponentAdded,
                SerializationChangeKind.ComponentRemoved => DeltaEventKind.ComponentRemoved,
                SerializationChangeKind.ComponentChanged => DeltaEventKind.ComponentChanged,
                SerializationChangeKind.TagAdded => DeltaEventKind.TagAdded,
                SerializationChangeKind.TagRemoved => DeltaEventKind.TagRemoved,
                SerializationChangeKind.EnabledChanged => DeltaEventKind.EnabledChanged,
                SerializationChangeKind.SharedChanged => DeltaEventKind.SharedChanged,
                SerializationChangeKind.SharedAdded => DeltaEventKind.SharedAdded,
                SerializationChangeKind.SharedRemoved => DeltaEventKind.SharedRemoved,
                SerializationChangeKind.BufferChanged => DeltaEventKind.BufferChanged,
                SerializationChangeKind.BufferAdded => DeltaEventKind.BufferAdded,
                SerializationChangeKind.BufferRemoved => DeltaEventKind.BufferRemoved,
                SerializationChangeKind.SparseAdded => DeltaEventKind.SparseAdded,
                SerializationChangeKind.SparseRemoved => DeltaEventKind.SparseRemoved,
                SerializationChangeKind.SparseChanged => DeltaEventKind.SparseChanged,
                SerializationChangeKind.RelationAdded => DeltaEventKind.RelationAdded,
                SerializationChangeKind.RelationRemoved => DeltaEventKind.RelationRemoved,
                SerializationChangeKind.RelationChanged => DeltaEventKind.RelationChanged,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
            };
        }
    }

    private static WorldPayload ReadWorldPayload(
        Stream stream,
        SerializationRegistry registry,
        UnknownTypeMode unknownTypeMode,
        SchemaMismatchMode schemaMismatchMode)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var (kind, manifestKeys) = PayloadFormat.ReadHeader(reader);
        if (kind != SnapshotPayloadKind.World)
            throw new InvalidDataException($"Expected World payload, found {kind}.");

        ManifestEntry[] manifest = ResolveManifest(registry, manifestKeys, unknownTypeMode, schemaMismatchMode);
        uint tick = reader.ReadUInt32();
        int slotCount = reader.ReadInt32();
        if (slotCount < 0)
            throw new InvalidDataException("Negative entity slot count.");

        EntitySlotSnapshot[] slots = new EntitySlotSnapshot[slotCount];
        for (int i = 0; i < slots.Length; i++)
        {
            int index = reader.ReadInt32();
            int generation = reader.ReadInt32();
            bool isAlive = reader.ReadBoolean();
            slots[i] = new EntitySlotSnapshot(index, generation, isAlive);
        }

        EntityPayload[] entities = EntityCodec.ReadAll(reader, manifest);
        return new WorldPayload(tick, slots, entities);
    }

    private static class WorldRestorer
    {
        public static void Restore(
            Stream stream,
            World world,
            SerializationRegistry registry,
            UnknownTypeMode unknownTypeMode,
            SchemaMismatchMode schemaMismatchMode)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var (kind, manifestKeys) = PayloadFormat.ReadHeader(reader);
            if (kind != SnapshotPayloadKind.World)
                throw new InvalidDataException($"Expected World payload, found {kind}.");

            ManifestEntry[] manifest = ResolveManifest(registry, manifestKeys, unknownTypeMode, schemaMismatchMode);
            uint tick = reader.ReadUInt32();
            int slotCount = reader.ReadInt32();
            if (slotCount < 0)
                throw new InvalidDataException("Negative entity slot count.");

            EntitySlotSnapshot[] slots = new EntitySlotSnapshot[slotCount];
            for (int i = 0; i < slots.Length; i++)
            {
                int index = reader.ReadInt32();
                int generation = reader.ReadInt32();
                bool isAlive = reader.ReadBoolean();
                slots[i] = new EntitySlotSnapshot(index, generation, isAlive);
            }

            int maxIndex = ValidateWorldSlots(slots, out int liveSlotCount);
            int payloadCount = reader.ReadInt32();
            if (payloadCount < 0)
                throw new InvalidDataException("Negative entity payload count.");
            if (payloadCount != liveSlotCount)
                throw new InvalidDataException("Serialized entity payload count does not match live slot count.");

            if (CanBulk(stream, manifest))
            {
                LoadBulk(reader, manifest, slots, maxIndex, payloadCount, tick, world);
                return;
            }

            using (world.Journal.Suppress())
            {
                world.PrepareStore(maxIndex, slots);
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i].IsAlive)
                        world.LoadEntity(slots[i].Index, slots[i].Generation);
                }

                var seenPayloads = payloadCount == 0 ? Array.Empty<bool>() : new bool[maxIndex + 1];
                List<DeferredItem>? deferredRelations = null;
                for (int i = 0; i < payloadCount; i++)
                    LoadRecord(reader, manifest, slots, seenPayloads, world, ref deferredRelations);

                long payloadEnd = stream.CanSeek ? stream.Position : 0;
                MergeRelations(reader, world, deferredRelations, payloadEnd);

                world.Clock.Write(tick);
            }

            world.Journal.Clear();
        }

        private static bool CanBulk(Stream stream, IReadOnlyList<ManifestEntry> manifest)
        {
            if (!stream.CanSeek)
                return false;

            for (int i = 0; i < manifest.Count; i++)
            {
                if (manifest[i].Migration is not null)
                    return false;
            }

            return true;
        }

        private static void LoadBulk(
            BinaryReader reader,
            IReadOnlyList<ManifestEntry> manifest,
            EntitySlotSnapshot[] slots,
            int maxIndex,
            int payloadCount,
            uint tick,
            World world)
        {
            using (world.Journal.Suppress())
            {
                world.PrepareStore(maxIndex, slots);

                var seenPayloads = payloadCount == 0 ? Array.Empty<bool>() : new bool[maxIndex + 1];
                var scratch = new Scratch();
                List<DeferredItem>? deferredRelations = null;
                for (int i = 0; i < payloadCount; i++)
                    Restore(reader, manifest, slots, seenPayloads, world, scratch, ref deferredRelations);

                long payloadEnd = reader.BaseStream.Position;
                MergeRelations(reader, world, deferredRelations, payloadEnd);

                world.Clock.Write(tick);
            }

            world.Journal.Clear();
        }

        private static void Restore(
            BinaryReader reader,
            IReadOnlyList<ManifestEntry> manifest,
            IReadOnlyList<EntitySlotSnapshot> slots,
            bool[] seenPayloads,
            World world,
            Scratch scratch,
            ref List<DeferredItem>? deferredRelations)
        {
            var dataReader = new DataReader(reader);
            Entity entity = dataReader.ReadEntity();
            ValidateEntity(entity, slots, seenPayloads);

            int itemCount = reader.ReadInt32();
            if (itemCount < 0)
                throw new InvalidDataException("Negative entity item count.");

            scratch.Reset(itemCount);
            for (int i = 0; i < itemCount; i++)
            {
                int manifestIndex = reader.ReadInt32();
                if ((uint)manifestIndex >= (uint)manifest.Count)
                    throw new InvalidDataException($"Invalid manifest index {manifestIndex}.");

                var entry = manifest[manifestIndex];
                var runtime = entry.Runtime;
                if (runtime is null)
                {
                    new PayloadFrame(reader).Skip();
                    continue;
                }

                if (runtime.Entry.Kind == SerializationValueKind.Shared)
                {
                    runtime.Bundle.AddIds(scratch.ComponentIds);
                    SharedValueSlot sharedValue = new PayloadFrame(reader).ReadShared(runtime, world);
                    scratch.SharedValues.Add(sharedValue);
                    continue;
                }

                if (runtime.Entry.Kind == SerializationValueKind.Relation)
                {
                    long position = reader.BaseStream.Position;
                    if (new PayloadFrame(reader).RelationHasItems())
                    {
                        runtime.Bundle.AddIds(scratch.ComponentIds);
                        var deferredRelation = new DeferredItem(entity, position, entry);
                        (deferredRelations ??= new List<DeferredItem>()).Add(deferredRelation);
                    }
                    continue;
                }

                runtime.Bundle.AddIds(scratch.ComponentIds);
                if (runtime.Entry.Kind == SerializationValueKind.Tag)
                {
                    new PayloadFrame(reader).ExpectEmpty();
                    continue;
                }

                scratch.ImmediateItems.Add(new Item(reader.BaseStream.Position, runtime));
                new PayloadFrame(reader).Skip();
            }

            long payloadEnd = reader.BaseStream.Position;
            BundleWriter writer = CreateLoadWriter(world, entity, scratch.ComponentIds, scratch.SharedValues);
            SpawnItems(reader, world, writer, scratch.ImmediateItems, payloadEnd);
        }

        private static BundleWriter CreateLoadWriter(
            World world,
            Entity entity,
            List<int> componentIds,
            List<SharedValueSlot> sharedValues)
        {
            return world.CreateLoadWriter(
                entity,
                CollectionsMarshal.AsSpan(componentIds),
                CollectionsMarshal.AsSpan(sharedValues));
        }

        private static void SpawnItems(
            BinaryReader reader,
            World world,
            BundleWriter writer,
            IReadOnlyList<Item> items,
            long payloadEnd)
        {
            if (items.Count == 0)
                return;

            var stream = reader.BaseStream;
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                stream.Position = item.PayloadPosition;
                new PayloadFrame(reader).Spawn(item.Runtime, world, writer);
            }

            stream.Position = payloadEnd;
        }

        private static void MergeRelations(
            BinaryReader reader,
            World world,
            IReadOnlyList<DeferredItem>? deferredRelations,
            long payloadEnd)
        {
            if (deferredRelations is null)
                return;

            var stream = reader.BaseStream;
            for (int i = 0; i < deferredRelations.Count; i++)
            {
                var deferred = deferredRelations[i];
                stream.Position = deferred.PayloadPosition;
                new PayloadFrame(reader).Apply(
                    deferred.Entry,
                    world,
                    deferred.Entity,
                    EntityApplyMode.MergeIncluded,
                    remapper: null);
            }

            stream.Position = payloadEnd;
        }

        private static void LoadRecord(
            BinaryReader reader,
            IReadOnlyList<ManifestEntry> manifest,
            IReadOnlyList<EntitySlotSnapshot> slots,
            bool[] seenPayloads,
            World world,
            ref List<DeferredItem>? deferredRelations)
        {
            var dataReader = new DataReader(reader);
            Entity entity = dataReader.ReadEntity();
            ValidateEntity(entity, slots, seenPayloads);

            int itemCount = reader.ReadInt32();
            if (itemCount < 0)
                throw new InvalidDataException("Negative entity item count.");

            for (int i = 0; i < itemCount; i++)
            {
                int manifestIndex = reader.ReadInt32();
                if ((uint)manifestIndex >= (uint)manifest.Count)
                    throw new InvalidDataException($"Invalid manifest index {manifestIndex}.");

                var entry = manifest[manifestIndex];
                if (entry.Runtime?.Entry.Kind == SerializationValueKind.Relation &&
                    reader.BaseStream.CanSeek)
                {
                    long position = reader.BaseStream.Position;
                    if (new PayloadFrame(reader).RelationHasItems())
                    {
                        (deferredRelations ??= new List<DeferredItem>())
                            .Add(new DeferredItem(entity, position, entry));
                    }
                    continue;
                }

                new PayloadFrame(reader).Apply(
                    entry,
                    world,
                    entity,
                    EntityApplyMode.MergeIncluded,
                    remapper: null);
            }
        }

        private static void ValidateEntity(
            Entity entity,
            IReadOnlyList<EntitySlotSnapshot> slots,
            bool[] seenPayloads)
        {
            int maxIndex = slots.Count;
            if (entity.Index <= 0 || entity.Index > maxIndex)
                throw new InvalidDataException($"Serialized entity payload index {entity.Index} has no slot.");
            if (seenPayloads[entity.Index])
                throw new InvalidDataException($"Duplicate serialized entity payload index {entity.Index}.");

            var slot = slots[entity.Index - 1];
            if (!slot.IsAlive)
                throw new InvalidDataException($"Serialized entity payload {entity} targets a dead slot.");
            if (slot.Generation != entity.Generation)
            {
                throw new InvalidDataException(
                    $"Serialized entity payload {entity} does not match slot generation {slot.Generation}.");
            }

            seenPayloads[entity.Index] = true;
        }

        private readonly record struct DeferredItem(Entity Entity, long PayloadPosition, ManifestEntry Entry);

        private sealed class Scratch
        {
            public List<int> ComponentIds { get; } = new();
            public List<SharedValueSlot> SharedValues { get; } = new();
            public List<Item> ImmediateItems { get; } = new();

            public void Reset(int itemCount)
            {
                ComponentIds.Clear();
                SharedValues.Clear();
                ImmediateItems.Clear();
                if (ComponentIds.Capacity < itemCount)
                    ComponentIds.Capacity = itemCount;
                if (ImmediateItems.Capacity < itemCount)
                    ImmediateItems.Capacity = itemCount;
            }
        }

        private readonly record struct Item(long PayloadPosition, SerializationTypeRuntime Runtime);
    }

    private static class WorldLoader
    {
        public static void Load(
            World world,
            WorldPayload payload,
            WorldLoadOptions options)
        {
            using (world.Journal.Suppress())
            {
                if (options.IdentityMode == EntityIdentityMode.Remap)
                    Import(world, payload, options.MissingReferenceMode);
                else
                    LoadPreserved(world, payload);
            }

            world.Journal.Clear();
        }

        private static void LoadPreserved(World world, WorldPayload payload)
        {
            int maxIndex = ValidateIdentity(payload);
            world.PrepareStore(maxIndex, payload.Slots);

            foreach (var entityPayload in payload.Entities)
                world.LoadEntity(entityPayload.Entity.Index, entityPayload.Entity.Generation);

            foreach (var entityPayload in payload.Entities)
                EntityApplier.Apply(
                    world,
                    entityPayload.Entity,
                    entityPayload,
                    EntityApplyMode.MergeIncluded,
                    remapper: null,
                    filter: EntityApplier.Filter.NonRelations);

            foreach (var entityPayload in payload.Entities)
                EntityApplier.Apply(
                    world,
                    entityPayload.Entity,
                    entityPayload,
                    EntityApplyMode.MergeIncluded,
                    remapper: null,
                    filter: EntityApplier.Filter.Relations);

            world.Clock.Write(payload.CurrentTick);
        }

        private static void Import(
            World world,
            WorldPayload payload,
            MissingReferenceMode missingReferenceMode)
        {
            Entity[] created = ImportPayloads(world, payload.Entities, missingReferenceMode);
            if (created.Length != payload.Entities.Length)
                throw new InvalidDataException("Imported world entity count changed during load.");

            world.Clock.Write(payload.CurrentTick);
        }

        private static int ValidateIdentity(WorldPayload payload)
        {
            int maxIndex = ValidateWorldSlots(payload.Slots, out int liveSlotCount);

            if (payload.Entities.Length != liveSlotCount)
                throw new InvalidDataException("Serialized entity payload count does not match live slot count.");

            Array.Sort(payload.Entities, static (left, right) => left.Entity.Index.CompareTo(right.Entity.Index));
            int previousIndex = 0;
            for (int i = 0; i < payload.Entities.Length; i++)
            {
                Entity entity = payload.Entities[i].Entity;
                if (entity.Index <= 0 || entity.Index > maxIndex)
                    throw new InvalidDataException($"Serialized entity payload index {entity.Index} has no slot.");
                if (entity.Index == previousIndex)
                    throw new InvalidDataException($"Duplicate serialized entity payload index {entity.Index}.");

                EntitySlotSnapshot slot = payload.Slots[entity.Index - 1];
                if (!slot.IsAlive)
                    throw new InvalidDataException($"Serialized entity payload {entity} targets a dead slot.");
                if (slot.Generation != entity.Generation)
                {
                    throw new InvalidDataException(
                        $"Serialized entity payload {entity} does not match slot generation {slot.Generation}.");
                }

                previousIndex = entity.Index;
            }

            return maxIndex;
        }
    }

    private static Entity[] ImportPayloads(
        World world,
        IReadOnlyList<EntityPayload> payloads,
        MissingReferenceMode missingReferenceMode)
    {
        using var journalSuppression = world.Journal.Suppress();
        var map = new Dictionary<Entity, Entity>(payloads.Count);
        Entity[] created = new Entity[payloads.Count];
        for (int i = 0; i < payloads.Count; i++)
        {
            created[i] = world.CreateEntity();
            map.Add(payloads[i].Entity, created[i]);
        }

        var remapper = new PolicyReferenceRemapper(map, missingReferenceMode);
        for (int i = 0; i < payloads.Count; i++)
        {
            EntityApplier.Apply(
                world,
                created[i],
                payloads[i],
                mode: EntityApplyMode.MergeIncluded,
                remapper: remapper,
                filter: EntityApplier.Filter.NonRelations);
        }

        for (int i = 0; i < payloads.Count; i++)
        {
            EntityApplier.Apply(
                world,
                created[i],
                payloads[i],
                mode: EntityApplyMode.MergeIncluded,
                remapper: remapper,
                filter: EntityApplier.Filter.Relations);
        }

        return created;
    }

    private static int ValidateWorldSlots(IReadOnlyList<EntitySlotSnapshot> slots, out int liveSlotCount)
    {
        int maxIndex = slots.Count;
        liveSlotCount = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            int expectedIndex = i + 1;
            if (slot.Index != expectedIndex)
                throw new InvalidDataException($"Expected serialized entity slot index {expectedIndex}, found {slot.Index}.");
            if (slot.Generation < 0)
                throw new InvalidDataException($"Invalid serialized entity slot generation {slot.Generation}.");

            if (slot.IsAlive)
                liveSlotCount++;
        }

        return maxIndex;
    }

    private static class EntityApplier
    {
        public static void Apply(
            World world,
            Entity target,
            EntityPayload payload,
            EntityApplyMode mode,
            IReferenceRemapper? remapper = null,
            Filter filter = Filter.All)
        {
            if (!world.IsAlive(target))
                throw new InvalidOperationException($"Cannot apply entity payload to {target}: target is not alive.");

            foreach (var item in payload.Items)
            {
                if (!ShouldApply(item.Runtime.Entry.Kind, filter))
                    continue;

                var value = remapper is null ? item.Value : item.Runtime.Item.Remap(item.Value, remapper);
                item.Runtime.Item.Apply(value, world, target, mode);
            }
        }

        private static bool ShouldApply(SerializationValueKind kind, Filter filter)
        {
            bool isRelation = kind == SerializationValueKind.Relation;
            return filter switch
            {
                Filter.All => true,
                Filter.NonRelations => !isRelation,
                Filter.Relations => isRelation,
                _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, null),
            };
        }

        public enum Filter
        {
            All,
            NonRelations,
            Relations,
        }
    }

    private static class EntityReplacer
    {
        public static void RemoveMissing(
            World world,
            Entity target,
            EntityPayload payload,
            SerializationRegistry registry)
        {
            HashSet<Guid>? payloadStableIds = payload.Items.Count > LinearStableLimit
                ? CreateSet(payload)
                : null;

            foreach (var runtime in registry.RuntimeTypes)
            {
                Guid stableId = runtime.Entry.TypeKey.StableId;
                bool payloadContainsItem = payloadStableIds is not null
                    ? payloadStableIds.Contains(stableId)
                    : Contains(payload, stableId);

                if (!payloadContainsItem &&
                    runtime.IsPresent(world, target))
                {
                    runtime.Remove(world, target);
                }
            }
        }

        private static HashSet<Guid> CreateSet(EntityPayload payload)
        {
            var stableIds = new HashSet<Guid>(payload.Items.Count);
            for (int i = 0; i < payload.Items.Count; i++)
                stableIds.Add(payload.Items[i].Runtime.Entry.TypeKey.StableId);

            return stableIds;
        }

        private static bool Contains(EntityPayload payload, Guid stableId)
        {
            for (int i = 0; i < payload.Items.Count; i++)
            {
                if (payload.Items[i].Runtime.Entry.TypeKey.StableId == stableId)
                    return true;
            }

            return false;
        }
    }

    private static IReadOnlyList<SerializationTypeRuntime> BuildManifest(IReadOnlyList<EntityPayload> payloads)
    {
        var byId = new Dictionary<Guid, SerializationTypeRuntime>();
        foreach (var payload in payloads)
        {
            foreach (var item in payload.Items)
                byId.TryAdd(item.Runtime.Entry.TypeKey.StableId, item.Runtime);
        }

        var manifest = byId.Values.ToList();
        manifest.Sort(static (left, right) =>
            SerializationRegistry.CompareTypeKeys(left.Entry.TypeKey, right.Entry.TypeKey));
        return manifest;
    }

    private static Dictionary<Guid, int> BuildManifestIndex(IReadOnlyList<SerializationTypeRuntime> manifest)
    {
        var index = new Dictionary<Guid, int>(manifest.Count);
        for (int i = 0; i < manifest.Count; i++)
            index.Add(manifest[i].Entry.TypeKey.StableId, i);
        return index;
    }

    private static ManifestEntry[] ResolveManifest(
        SerializationRegistry registry,
        IReadOnlyList<SerializationTypeKey> keys,
        UnknownTypeMode unknownTypeMode,
        SchemaMismatchMode schemaMismatchMode)
    {
        ManifestEntry[] manifest = new ManifestEntry[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            SerializationTypeRuntime? runtime = registry.Resolve(keys[i], unknownTypeMode, schemaMismatchMode, out var migration);
            manifest[i] = new ManifestEntry(runtime, migration);
        }

        return manifest;
    }

    private static byte[] WriteData(Action<BinaryWriter> write)
    {
        using var memory = new MemoryStream();
        using (var binaryWriter = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
            write(binaryWriter);

        return memory.ToArray();
    }

    private static class PayloadBytes
    {
        public static void WriteItem(
            BinaryWriter writer,
            SerializationTypeRuntime runtime,
            object value)
        {
            var stream = writer.BaseStream;
            if (!stream.CanSeek)
            {
                byte[] payload = WriteData(binaryWriter =>
                {
                    var dataWriter = new DataWriter(binaryWriter);
                    runtime.Item.Write(ref dataWriter, value);
                });
                Write(writer, payload);
                return;
            }

            long lengthPosition = stream.Position;
            writer.Write(0);
            long payloadStart = stream.Position;
            var payloadWriter = new DataWriter(writer);
            runtime.Item.Write(ref payloadWriter, value);
            long payloadEnd = stream.Position;
            long payloadLength = payloadEnd - payloadStart;
            if (payloadLength > int.MaxValue)
                throw new InvalidOperationException("Serialized item payload is too large.");

            stream.Position = lengthPosition;
            writer.Write((int)payloadLength);
            stream.Position = payloadEnd;
        }

        public static byte[] Read(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0)
                throw new InvalidDataException("Negative length-prefixed payload length.");

            return ReadExact(reader, length);
        }

        public static byte[] ReadExact(BinaryReader reader, int length)
        {
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new InvalidDataException("Truncated length-prefixed payload.");

            return bytes;
        }

        public static void Write(BinaryWriter writer, ReadOnlySpan<byte> bytes)
        {
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        public static void ExpectEnd(Stream stream)
        {
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Serialized item payload contains trailing bytes.");
        }
    }

    private static byte[] MigratePayload(byte[] data, IMigrationStep migration)
    {
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream();
        using (var binaryReader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true))
        using (var binaryWriter = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            var dataReader = new DataReader(binaryReader);
            var dataWriter = new DataWriter(binaryWriter);
            var migrationReader = new MigrationReader(dataReader);
            var migrationWriter = new MigrationWriter(dataWriter);
            migration.Migrate(ref migrationReader, ref migrationWriter);
            PayloadBytes.ExpectEnd(input);
        }

        return output.ToArray();
    }

    private static TResult ReadData<TResult>(byte[] data, Func<BinaryReader, TResult> read)
    {
        using var memory = new MemoryStream(data, writable: false);
        using var binaryReader = new BinaryReader(memory, Encoding.UTF8, leaveOpen: true);
        var value = read(binaryReader);
        PayloadBytes.ExpectEnd(memory);
        return value;
    }

    private readonly struct PayloadFrame
    {
        private readonly BinaryReader _reader;
        private readonly int _length;
        private readonly long _end;

        public PayloadFrame(BinaryReader reader)
        {
            _reader = reader;
            _length = reader.ReadInt32();
            if (_length < 0)
                throw new InvalidDataException("Negative length-prefixed payload length.");

            var stream = reader.BaseStream;
            long start = stream.Position;
            _end = start + _length;
            if (_end < start)
                throw new InvalidDataException("Length-prefixed payload is too large.");
        }

        public void Skip()
        {
            var stream = _reader.BaseStream;
            long remaining = _end - stream.Position;
            if (remaining < 0)
                throw new InvalidDataException("Serialized item payload read past its length-prefixed boundary.");
            if (remaining == 0)
                return;

            if (stream.CanSeek)
            {
                stream.Position = _end;
                return;
            }

            _ = PayloadBytes.ReadExact(_reader, checked((int)remaining));
        }

        public void ExpectEmpty()
        {
            if (_length != 0)
                throw new InvalidDataException("Serialized item payload contains trailing bytes.");
        }

        public bool RelationHasItems()
        {
            if (_length < sizeof(int))
                throw new InvalidDataException("Truncated relation count.");

            int count = _reader.ReadInt32();
            if (count < 0)
                throw new InvalidDataException("Negative relation count.");

            Skip();
            return count > 0;
        }

        public void Spawn(SerializationTypeRuntime runtime, World world, BundleWriter writer)
        {
            var dataReader = new DataReader(_reader);
            runtime.Bundle.Spawn(ref dataReader, world, writer);
            Finish();
        }

        public SharedValueSlot ReadShared(SerializationTypeRuntime runtime, World world)
        {
            var dataReader = new DataReader(_reader);
            SharedValueSlot value = runtime.Bundle.ReadShared(ref dataReader, world);
            Finish();
            return value;
        }

        public void Apply(
            ManifestEntry entry,
            World world,
            Entity entity,
            EntityApplyMode mode,
            IReferenceRemapper? remapper)
        {
            if (entry.Runtime is null)
            {
                Skip();
                return;
            }

            if (entry.Migration is not null || !_reader.BaseStream.CanSeek)
            {
                byte[] data = PayloadBytes.ReadExact(_reader, _length);
                if (entry.Migration is not null)
                    data = MigratePayload(data, entry.Migration);

                SerializationTypeRuntime? runtime = entry.Runtime;
                object value = ReadData(data, binaryReader =>
                {
                    var itemReader = new DataReader(binaryReader);
                    return runtime.Item.Read(ref itemReader);
                });
                if (remapper is not null)
                    value = runtime.Item.Remap(value, remapper);
                runtime.Item.Apply(value, world, entity, mode);
                return;
            }

            var reader = new DataReader(_reader);
            entry.Runtime.Item.ApplyStream(ref reader, world, entity, mode, remapper);
            Finish();
        }

        private void Finish()
        {
            long position = _reader.BaseStream.Position;
            if (position == _end)
                return;

            if (position < _end)
                throw new InvalidDataException("Serialized item payload contains trailing bytes.");

            throw new InvalidDataException("Serialized item payload read past its length-prefixed boundary.");
        }
    }

    private static class IdentityMap
    {
        public static void RejectRemap(EntityIdentityMode mode)
        {
            if (mode == EntityIdentityMode.Remap)
                throw new InvalidOperationException("EntityIdentityMode.Remap is only valid for read/import operations.");
        }

        public static IReferenceRemapper? SingleMap(
            Entity source,
            Entity target,
            EntityIdentityMode identityMode,
            MissingReferenceMode missingReferenceMode)
        {
            if (identityMode != EntityIdentityMode.Remap)
                return null;

            return new PolicyReferenceRemapper(
                new Dictionary<Entity, Entity> { [source] = target },
                missingReferenceMode);
        }
    }

    private sealed record EntityPayload(Entity Entity, List<EntityItemPayload> Items);

    private sealed record EntityItemPayload(SerializationTypeRuntime Runtime, object Value);

    private sealed record WorldPayload(uint CurrentTick, EntitySlotSnapshot[] Slots, EntityPayload[] Entities);

    private readonly record struct ManifestEntry(SerializationTypeRuntime? Runtime, IMigrationStep? Migration);

    private sealed class PolicyReferenceRemapper : IReferenceRemapper
    {
        private readonly IReadOnlyDictionary<Entity, Entity> _map;
        private readonly MissingReferenceMode _missingReferenceMode;

        public PolicyReferenceRemapper(
            IReadOnlyDictionary<Entity, Entity> map,
            MissingReferenceMode missingReferenceMode)
        {
            _map = map;
            _missingReferenceMode = missingReferenceMode;
        }

        public bool TryMap(Entity source, out Entity mapped)
        {
            if (source == Entity.Null)
            {
                mapped = Entity.Null;
                return true;
            }

            if (_map.TryGetValue(source, out mapped))
                return true;

            switch (_missingReferenceMode)
            {
                case MissingReferenceMode.KeepOriginal:
                    mapped = source;
                    return true;

                case MissingReferenceMode.Clear:
                    mapped = Entity.Null;
                    return true;

                case MissingReferenceMode.Throw:
                    throw new InvalidOperationException($"Missing entity reference remap for {source}.");

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(_missingReferenceMode),
                        _missingReferenceMode,
                        null);
            }
        }
    }

}


