using System.Runtime.InteropServices;
using System.Text;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Serialization;
using SomeEngine.Serialization.IO;

namespace SomeEngine.ECS.Serialization;

public static partial class WorldSerializer
{
    public static void WriteComponent<T>(
        Stream stream,
        in T value,
        SerializationRegistry registry,
        SerializeOptions options = default)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);

        SerializationTypeRuntime runtime = registry.GetRegistered<T>();
        if (runtime is not ValueSerializationRuntime<T> valueRuntime)
        {
            throw new InvalidOperationException(
                $"Serialized type {typeof(T).FullName} does not have a standalone value codec.");
        }

        using var writer = new BinaryWriter(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        PayloadFormat.WriteHeader(writer, SnapshotPayloadKind.Component, new[] { runtime }, options.Contract);
        writer.Write(0);
        PayloadBytes.WriteComponent(writer, valueRuntime, in value);
    }

    public static T ReadComponent<T>(
        Stream stream,
        SerializationRegistry registry,
        SerializationReadLimits? readLimits = null)
        where T : struct =>
        ReadComponent<T>(stream, registry, new SerializationReadOptions(readLimits));

    public static T ReadComponent<T>(
        Stream stream,
        SerializationRegistry registry,
        SerializationReadOptions options)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);

        var budget = new SerializationReadBudget(options.ReadLimits);
        using var binaryReader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var (kind, manifest, contract) = PayloadFormat.ReadHeader(
            binaryReader,
            budget,
            options.RequiredContract);
        if (kind != SnapshotPayloadKind.Component)
            throw new InvalidDataException($"Expected Component payload, found {kind}.");
        if (manifest.Length != 1)
            throw new InvalidDataException("Component payload must contain exactly one manifest entry.");

        SerializationTypeRuntime runtime = registry.ResolveExact(manifest[0]);
        PayloadFormat.ValidateReadContract(contract, runtime);
        if (runtime.ValueType != typeof(T))
            throw new InvalidDataException(
                $"Component payload contains {runtime.ValueType.FullName}, not requested {typeof(T).FullName}.");
        if (runtime is not ValueSerializationRuntime<T> valueRuntime)
        {
            throw new InvalidDataException(
                $"Serialized type {typeof(T).FullName} does not have a standalone value codec.");
        }

        int manifestIndex = binaryReader.ReadInt32();
        if (manifestIndex != 0)
            throw new InvalidDataException("Invalid component payload manifest index.");

        return new PayloadFrame(binaryReader, budget).ReadComponent(valueRuntime);
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
        SerializationTypeRuntime[]? capturedManifest = null;
        using AdmittedWorldWrite admitted = AdmittedWorldWrite.Enter(
            world,
            captured =>
            {
                if (!captured.IsAlive(entity))
                {
                    throw new InvalidOperationException(
                        $"Cannot serialize {entity}: entity is not alive.");
                }
                capturedManifest = BuildEntityManifest(captured, entity, registry);
            });
        SerializationTypeRuntime[] manifest = capturedManifest ??
            throw new InvalidOperationException("Entity capture manifest was not created.");
        var manifestIndex = BuildManifestIndex(manifest);
        using var writer = new BinaryWriter(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        PayloadFormat.WriteHeader(writer, SnapshotPayloadKind.Entity, manifest, options.Contract);
        EntityCodec.WriteRecord(writer, admitted, entity, manifest, manifestIndex);
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
        SerializationTypeRuntime[]? capturedManifest = null;
        using AdmittedWorldWrite admitted = AdmittedWorldWrite.BeginCapture(world);
        for (int i = 0; i < entities.Length; i++)
        {
            if (!admitted.IsAlive(entities[i]))
            {
                throw new InvalidOperationException(
                    $"Cannot serialize {entities[i]}: entity is not alive.");
            }
        }
        capturedManifest = BuildEntityManifest(admitted, entities, registry);
        admitted.CompleteCapture();
        SerializationTypeRuntime[] manifest = capturedManifest ??
            throw new InvalidOperationException("Entity-set capture manifest was not created.");
        var manifestIndex = BuildManifestIndex(manifest);
        using var writer = new BinaryWriter(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        PayloadFormat.WriteHeader(writer, SnapshotPayloadKind.EntitySet, manifest, options.Contract);
        writer.Write(entities.Length);
        for (int i = 0; i < entities.Length; i++)
            EntityCodec.WriteRecord(writer, admitted, entities[i], manifest, manifestIndex);
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
        int entityCount = 0;
        SerializationTypeRuntime[]? capturedManifest = null;
        using AdmittedWorldWrite admitted = AdmittedWorldWrite.Enter(
            world,
            captured =>
            {
                var present = new HashSet<SerializationTypeRuntime>();
                captured.ExecuteQuery(query, cursor =>
                {
                    foreach (var row in cursor.Rows)
                    {
                        entityCount = checked(entityCount + 1);
                        AddPresentRuntimes(captured, row.Entity, registry.RuntimeTypes, present);
                    }
                });
                capturedManifest = SortManifest(present);
            });
        SerializationTypeRuntime[] manifest = capturedManifest ??
            throw new InvalidOperationException("Query capture manifest was not created.");
        var manifestIndex = BuildManifestIndex(manifest);

        using var writer = new BinaryWriter(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        PayloadFormat.WriteHeader(writer, SnapshotPayloadKind.QueryResult, manifest, options.Contract);
        writer.Write(entityCount);
        int written = 0;
        admitted.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                EntityCodec.WriteRecord(writer, admitted, row.Entity, manifest, manifestIndex);
                written = checked(written + 1);
            }
        });
        if (written != entityCount)
            throw new InvalidOperationException("Captured query membership changed during serialization.");
    }

    public static void WriteWorld(
        Stream stream,
        World world,
        SerializationRegistry registry,
        SerializeOptions options = default) =>
        WriteWorldCore(stream, world, registry, options, beforeOutput: null, afterOutput: null);

    internal static void WriteWorldCore(
        Stream stream,
        World world,
        SerializationRegistry registry,
        SerializeOptions options,
        Action? beforeOutput,
        Action? afterOutput)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        TopologyCodec.ValidateWriteContract(registry, options.Contract);
        if (options.MaximumSparseMemberships < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"{nameof(SerializeOptions.MaximumSparseMemberships)} cannot be negative.");
        }
        if (options.MaximumTopologyRecords < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"{nameof(SerializeOptions.MaximumTopologyRecords)} cannot be negative.");
        }
        if (options.MaximumTopologyPayloadBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"{nameof(SerializeOptions.MaximumTopologyPayloadBytes)} cannot be negative.");
        }
        int maximumSparseMemberships = options.MaximumSparseMemberships == 0
            ? int.MaxValue
            : options.MaximumSparseMemberships;

        // Validate and fork under one short topology control-plane admission. Encoding then reads
        // the retained source root directly while mutations proceed against the published COW
        // successor. World disposal alone waits for the explicit encoder lifetime.
        WorldWritePlan? capturedPlan = null;
        using AdmittedWorldWrite admitted = AdmittedWorldWrite.Enter(
            world,
            captured =>
            {
                TopologyCodec.ValidateWriteState(captured, registry);
                capturedPlan = WorldWritePlan.Build(
                    captured,
                    registry,
                    maximumSparseMemberships);
                registry.ValidateWorldSnapshotCapture(capturedPlan.Manifest);
            });
        WorldWritePlan capturePlan = capturedPlan ??
            throw new InvalidOperationException("World capture plan was not created.");

        beforeOutput?.Invoke();
        using var writer = new BinaryWriter(stream, SerializationBinary.StrictUtf8, leaveOpen: true);
        ReadOnlySpan<SerializationTypeRuntime> manifest = capturePlan.Manifest;
        var manifestIndex = BuildManifestIndex(manifest);
        PayloadFormat.WriteHeader(writer, SnapshotPayloadKind.World, manifest, options.Contract);

        writer.Write(admitted.CurrentTick);
        writer.Write(admitted.SlotCount);
        for (int i = 0; i < admitted.SlotCount; i++)
        {
            EntitySlotSnapshot slot = admitted.GetSlot(i);
            writer.Write(slot.Index);
            writer.Write(slot.Generation);
            writer.Write(slot.IsAlive);
        }

        WriteWorldEntities(writer, admitted, capturePlan, manifestIndex);
        TopologyCodec.WriteAll(
            writer,
            admitted,
            registry,
            options.Contract,
            options.MaximumTopologyRecords,
            options.MaximumTopologyPayloadBytes);
        writer.Flush();
        afterOutput?.Invoke();
    }

    public static World ReadWorld(
        Stream stream,
        SerializationRegistry registry,
        WorldLoadOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(registry);
        var budget = new SerializationReadBudget(options.ReadLimits);

        if (options.IdentityMode is not EntityIdentityMode.Preserve and not EntityIdentityMode.Remap)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.IdentityMode,
                "World identity mode must be Preserve or Remap.");
        }

        if (options.IdentityMode == EntityIdentityMode.Preserve)
        {
            var loadedWorld = new World();
            try
            {
                WorldRestorer.Restore(
                    stream,
                    loadedWorld,
                    registry,
                    budget,
                    options.RequiredContract);
                return loadedWorld;
            }
            catch (Exception restoreFailure)
            {
                RethrowAfterTemporaryWorldFailure(restoreFailure, loadedWorld.Dispose);
                throw;
            }
        }

        var importedWorld = new World();
        try
        {
            WorldImporter.Restore(
                stream,
                importedWorld,
                registry,
                budget,
                options.RequiredContract,
                options.MissingReferenceMode);
            return importedWorld;
        }
        catch (Exception restoreFailure)
        {
            RethrowAfterTemporaryWorldFailure(restoreFailure, importedWorld.Dispose);
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void RethrowAfterTemporaryWorldFailure(
        Exception restoreFailure,
        Action disposeWorld)
    {
        ArgumentNullException.ThrowIfNull(restoreFailure);
        ArgumentNullException.ThrowIfNull(disposeWorld);
        try
        {
            disposeWorld();
        }
        catch (Exception disposeFailure)
        {
            throw new AggregateException(
                "World restoration and temporary World disposal both failed.",
                restoreFailure,
                disposeFailure);
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo
            .Capture(restoreFailure)
            .Throw();
        throw new InvalidOperationException("Unreachable exception rethrow path.");
    }

    public static string ReadDebugSummary(
        Stream stream,
        SerializationReadLimits? readLimits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long position = stream.CanSeek ? stream.Position : 0;
        try
        {
            var budget = new SerializationReadBudget(readLimits);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var (kind, manifest, contract) = PayloadFormat.ReadHeader(reader, budget);
            var builder = new StringBuilder();
            builder.AppendLine($"PayloadKind: {kind}");
            builder.AppendLine($"Contract: {contract}");
            builder.AppendLine($"ManifestCount: {manifest.Length}");
            Array.Sort(manifest, SerializationRegistry.CompareTypeKeys);
            foreach (var key in manifest)
                builder.AppendLine($"- {key.StableName} {key.StableId} 0x{key.SchemaFingerprint:X16}");
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
    private static void WriteWorldEntities(
        BinaryWriter writer,
        AdmittedWorldWrite admitted,
        WorldWritePlan capturePlan,
        IReadOnlyDictionary<Guid, int> manifestIndex)
    {
        writer.Write(admitted.LiveEntityCount);
        for (int i = 0; i < admitted.SlotCount; i++)
        {
            EntitySlotSnapshot slot = admitted.GetSlot(i);
            if (!slot.IsAlive)
                continue;

            var entity = new Entity(slot.Index, slot.Generation);
            EntityRecord record = admitted.ReadRecord(entity);
            if (record.Archetype is null || record.Chunk is null)
            {
                throw new InvalidOperationException(
                    $"Cannot serialize {entity}: its retained structural record is not placed.");
            }

            ReadOnlySpan<SerializationTypeRuntime> tableItems =
                capturePlan.TableItems(record.Archetype);
            ReadOnlySpan<SerializationTypeRuntime> sparseItems =
                capturePlan.SparseItems(entity);

            var dataWriter = new DataWriter(writer);
            dataWriter.WriteEntity(entity);
            writer.Write(checked(tableItems.Length + sparseItems.Length));

            // Both inputs are ordered by stable type identity. Merge them to retain the exact
            // canonical per-entity item ordering without materializing a second payload graph.
            int tableIndex = 0;
            int sparseIndex = 0;
            while (tableIndex < tableItems.Length || sparseIndex < sparseItems.Length)
            {
                SerializationTypeRuntime runtime;
                if (sparseIndex >= sparseItems.Length ||
                    (tableIndex < tableItems.Length &&
                     SerializationRegistry.CompareTypeKeys(
                         tableItems[tableIndex].Entry.TypeKey,
                         sparseItems[sparseIndex].Entry.TypeKey) < 0))
                {
                    runtime = tableItems[tableIndex++];
                }
                else
                {
                    runtime = sparseItems[sparseIndex++];
                }

                writer.Write(manifestIndex[runtime.Entry.TypeKey.StableId]);

                PayloadBytes.WriteItemAdmitted(writer, runtime, admitted, entity);
            }
        }
    }

    private static class EntityCodec
    {
        internal static void WriteRecord(
            BinaryWriter writer,
            AdmittedWorldWrite admitted,
            Entity entity,
            ReadOnlySpan<SerializationTypeRuntime> manifest,
            IReadOnlyDictionary<Guid, int> manifestIndex)
        {
            var dataWriter = new DataWriter(writer);
            dataWriter.WriteEntity(entity);
            int itemCount = 0;
            for (int i = 0; i < manifest.Length; i++)
            {
                if (manifest[i].IsPresent(admitted, entity))
                    itemCount++;
            }
            writer.Write(itemCount);

            for (int i = 0; i < manifest.Length; i++)
            {
                SerializationTypeRuntime runtime = manifest[i];
                if (!runtime.IsPresent(admitted, entity))
                    continue;

                writer.Write(manifestIndex[runtime.Entry.TypeKey.StableId]);
                PayloadBytes.WriteItemAdmitted(writer, runtime, admitted, entity);
            }
        }
    }

    private static class WorldRestorer
    {
        public static void Restore(
            Stream stream,
            World world,
            SerializationRegistry registry,
            SerializationReadBudget budget,
            SerializationContract? requiredContract)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var (kind, manifestKeys, contract) = PayloadFormat.ReadHeader(
                reader,
                budget,
                requiredContract);
            if (kind != SnapshotPayloadKind.World)
                throw new InvalidDataException($"Expected World payload, found {kind}.");

            SerializationTypeRuntime[] manifest =
                ResolveManifest(registry, manifestKeys, contract);
            uint tick = reader.ReadUInt32();
            int slotCount = budget.EntitySlotCount(reader.ReadInt32());

            {
                world.BeginSerializationStore(slotCount);
                int liveSlotCount = 0;
                for (int i = 0; i < slotCount; i++)
                {
                    int expectedIndex = i + 1;
                    int index = reader.ReadInt32();
                    int generation = reader.ReadInt32();
                    byte aliveByte = reader.ReadByte();
                    if (index != expectedIndex)
                    {
                        throw new InvalidDataException(
                            $"Expected serialized entity slot index {expectedIndex}, found {index}.");
                    }
                    if (generation < 0)
                        throw new InvalidDataException($"Invalid serialized entity slot generation {generation}.");
                    if (aliveByte > 1)
                        throw new InvalidDataException($"Invalid serialized entity slot alive flag {aliveByte}.");
                    bool isAlive = aliveByte != 0;
                    world.AppendSerializationSlot(index, generation, isAlive);
                    if (isAlive)
                        liveSlotCount = checked(liveSlotCount + 1);
                }
                world.CompleteSerializationStore();

                int payloadCount = budget.EntityCount(reader.ReadInt32());
                if (payloadCount != liveSlotCount)
                {
                    throw new InvalidDataException(
                        "Serialized entity payload count does not match live slot count.");
                }

                int previousEntityIndex = 0;
                for (int i = 0; i < payloadCount; i++)
                    LoadRecord(reader, manifest, world, budget, ref previousEntityIndex);

                TopologyCodec.ReadApply(
                    reader,
                    registry,
                    budget,
                    contract,
                    world,
                    remapper: null);

                world.Clock.Write(tick);
            }

        }

        private static void LoadRecord(
            BinaryReader reader,
            ReadOnlySpan<SerializationTypeRuntime> manifest,
            World world,
            SerializationReadBudget budget,
            ref int previousEntityIndex)
        {
            var dataReader = new DataReader(reader, budget);
            Entity entity = dataReader.ReadEntity();
            ValidateEntity(entity, world, ref previousEntityIndex);

            int itemCount = budget.EntityItemCount(reader.ReadInt32());
            int previousManifestIndex = -1;
            for (int i = 0; i < itemCount; i++)
            {
                int manifestIndex = ReadCanonicalManifestIndex(
                    reader, itemCount, manifest.Length, i, ref previousManifestIndex);

                SerializationTypeRuntime runtime = manifest[manifestIndex];
                new PayloadFrame(reader, budget).Apply(
                    runtime,
                    world,
                    entity,
                    remapper: null);
            }
        }

        private static void ValidateEntity(
            Entity entity,
            World world,
            ref int previousEntityIndex)
        {
            if (entity.Index <= previousEntityIndex)
            {
                throw new InvalidDataException(
                    $"Serialized entity payload index {entity.Index} is duplicate or not canonical.");
            }
            if (!world.SerializationSlotMatches(entity))
                throw new InvalidDataException($"Serialized entity payload {entity} has no matching live slot.");

            previousEntityIndex = entity.Index;
        }

    }

    /// <summary>
    /// Streaming remap loader. Source identities are known from the slot table before entity
    /// records begin, so destination entities and the complete remap can be created up front.
    /// Each following item frame is then applied immediately; forward entity references do not
    /// require retaining a decoded per-entity object graph.
    /// </summary>
    private static class WorldImporter
    {
        public static void Restore(
            Stream stream,
            World world,
            SerializationRegistry registry,
            SerializationReadBudget budget,
            SerializationContract? requiredContract,
            MissingReferenceMode missingReferenceMode)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var (kind, manifestKeys, contract) = PayloadFormat.ReadHeader(
                reader,
                budget,
                requiredContract);
            if (kind != SnapshotPayloadKind.World)
                throw new InvalidDataException($"Expected World payload, found {kind}.");

            SerializationTypeRuntime[] manifest =
                ResolveManifest(registry, manifestKeys, contract);
            uint tick = reader.ReadUInt32();
            int slotCount = budget.EntitySlotCount(reader.ReadInt32());
            var map = new Dictionary<Entity, Entity>();

            {
                // Source identity creation order is canonical slot order, so destination identity
                // assignment is deterministic and the map is complete before any codec patches a
                // forward reference.
                int liveSlotCount = 0;
                for (int i = 0; i < slotCount; i++)
                {
                    int expectedIndex = i + 1;
                    int index = reader.ReadInt32();
                    int generation = reader.ReadInt32();
                    byte aliveByte = reader.ReadByte();
                    if (index != expectedIndex)
                    {
                        throw new InvalidDataException(
                            $"Expected serialized entity slot index {expectedIndex}, found {index}.");
                    }
                    if (generation < 0)
                        throw new InvalidDataException($"Invalid serialized entity slot generation {generation}.");
                    if (aliveByte > 1)
                        throw new InvalidDataException($"Invalid serialized entity slot alive flag {aliveByte}.");
                    if (aliveByte == 0)
                        continue;

                    var source = new Entity(index, generation);
                    map.Add(source, world.CreateEntity());
                    liveSlotCount = checked(liveSlotCount + 1);
                }

                int payloadCount = budget.EntityCount(reader.ReadInt32());
                if (payloadCount != liveSlotCount)
                {
                    throw new InvalidDataException(
                        "Serialized entity payload count does not match live slot count.");
                }

                var remapper = new PolicyReferenceRemapper(map, missingReferenceMode);
                int previousEntityIndex = 0;
                for (int i = 0; i < payloadCount; i++)
                {
                    LoadRecord(
                        reader,
                        manifest,
                        map,
                        remapper,
                        world,
                        budget,
                        ref previousEntityIndex);
                }

                TopologyCodec.ReadApply(
                    reader,
                    registry,
                    budget,
                    contract,
                    world,
                    remapper);
                world.Clock.Write(tick);
            }

        }

        private static void LoadRecord(
            BinaryReader reader,
            ReadOnlySpan<SerializationTypeRuntime> manifest,
            IReadOnlyDictionary<Entity, Entity> map,
            IReferenceRemapper remapper,
            World world,
            SerializationReadBudget budget,
            ref int previousEntityIndex)
        {
            var data = new DataReader(reader, budget);
            Entity source = data.ReadEntity();
            if (source.Index <= previousEntityIndex)
            {
                throw new InvalidDataException(
                    $"Serialized entity payload index {source.Index} is duplicate or not canonical.");
            }
            if (!map.TryGetValue(source, out Entity target))
            {
                throw new InvalidDataException(
                    $"Serialized entity payload {source} has no imported destination.");
            }
            previousEntityIndex = source.Index;

            int itemCount = budget.EntityItemCount(reader.ReadInt32());
            int previousManifestIndex = -1;
            for (int i = 0; i < itemCount; i++)
            {
                int manifestIndex = ReadCanonicalManifestIndex(
                    reader, itemCount, manifest.Length, i, ref previousManifestIndex);

                new PayloadFrame(reader, budget).Apply(
                    manifest[manifestIndex],
                    world,
                    target,
                    remapper);
            }
        }

    }

    private static class PayloadBytes
    {
        public static void WriteComponent<T>(
            BinaryWriter writer,
            ValueSerializationRuntime<T> runtime,
            in T value)
            where T : struct
        {
            writer.Flush();
            using var counter = new BoundedCountingWriteStream(
                writer.BaseStream,
                int.MaxValue,
                limitExceeded: static (_, _, _) =>
                    new InvalidOperationException("Serialized item payload is too large."));
            using (var payloadBinaryWriter = new BinaryWriter(
                       counter,
                       SerializationBinary.StrictUtf8,
                       leaveOpen: true))
            {
                var payloadWriter = new DataWriter(payloadBinaryWriter);
                runtime.WriteStandalone(ref payloadWriter, in value);
                payloadBinaryWriter.Flush();
            }
            writer.Write(checked((int)counter.BytesWritten));
        }

        public static void WriteItemAdmitted(
            BinaryWriter writer,
            SerializationTypeRuntime runtime,
            AdmittedWorldWrite admitted,
            Entity entity)
        {
            writer.Flush();
            using var counter = new BoundedCountingWriteStream(
                writer.BaseStream,
                int.MaxValue,
                limitExceeded: static (_, _, _) =>
                    new InvalidOperationException("Serialized item payload is too large."));
            using (var payloadBinaryWriter = new BinaryWriter(
                       counter,
                       SerializationBinary.StrictUtf8,
                       leaveOpen: true))
            {
                var payloadWriter = new DataWriter(payloadBinaryWriter);
                runtime.WriteAdmitted(ref payloadWriter, admitted, entity);
                payloadBinaryWriter.Flush();
            }
            writer.Write(checked((int)counter.BytesWritten));
        }

    }

    private readonly struct PayloadFrame
    {
        private readonly BinaryReader _reader;
        private readonly SerializationReadBudget _budget;

        public PayloadFrame(BinaryReader reader, SerializationReadBudget budget)
        {
            _reader = reader;
            _budget = budget;
        }

        public T ReadComponent<T>(ValueSerializationRuntime<T> runtime)
            where T : struct
        {
            using var payload = OpenPayload();
            T value;
            using (var binaryReader = OpenReader(payload))
            {
                var data = new DataReader(binaryReader, _budget);
                value = runtime.ReadStandalone(ref data);
            }
            Finish(payload);
            return value;
        }

        public void Apply(
            SerializationTypeRuntime runtime,
            World world,
            Entity entity,
            IReferenceRemapper? remapper)
        {
            using var payload = OpenPayload();
            using (var binaryReader = OpenReader(payload))
            {
                var reader = new DataReader(binaryReader, _budget);
                runtime.Apply(ref reader, world, entity, remapper);
            }
            Finish(payload);
        }

        private BoundedCountingReadStream OpenPayload() =>
            new(
                _reader.BaseStream,
                _budget.Limits.MaxPayloadBytes,
                limitExceeded: static (_, _, _) => new InvalidDataException(
                    "Serialized item payload exceeds the configured byte limit."));

        private static BinaryReader OpenReader(Stream payload) =>
            new(payload, SerializationBinary.StrictUtf8, leaveOpen: true);

        private void Finish(BoundedCountingReadStream payload)
        {
            int declaredLength;
            try
            {
                declaredLength = _reader.ReadInt32();
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("Serialized item payload is missing its byte-count footer.", exception);
            }
            if (payload.BytesRead != declaredLength)
            {
                throw new InvalidDataException(
                    "Serialized item payload footer does not match the bytes consumed by its codec.");
            }
            _budget.PayloadLength(declaredLength);
        }

    }

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


