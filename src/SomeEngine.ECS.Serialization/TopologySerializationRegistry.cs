using System.Runtime.InteropServices;
using System.Text;
using SomeEngine.ECS.Components;
using IComponent = global::SomeEngine.ECS.IComponent;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using static SomeEngine.ECS.Serialization.TopologySerializationHelpers;

namespace SomeEngine.ECS.Serialization;

internal enum TopologySerializationKind : byte
{
    Hierarchy,
    Relation,
}

public sealed partial class SerializationRegistry
{
    private const uint TopologyWireVersion = 2;
    private readonly List<TopologySerializationRuntime> _topologyRuntimes = new();
    private readonly Dictionary<Guid, TopologySerializationRuntime> _topologyByStableId = new();
    private readonly Dictionary<(TopologySerializationKind Kind, Type Type), TopologySerializationRuntime>
        _topologyByType = new();

    public SerializationRegistry RegisterHierarchyDomain<TDomain>()
        where TDomain : IHierarchyDomain =>
        RegisterHierarchyDomain<TDomain>(CreateHierarchyTopologyKey<TDomain>());

    public SerializationRegistry RegisterHierarchyDomain<TDomain>(SerializationTypeKey typeKey)
        where TDomain : IHierarchyDomain =>
        RegisterTopology(new HierarchyTopologySerializationRuntime<TDomain>(typeKey));

    /// <summary>Registers canonical relation topology for an ordinary component payload.</summary>
    /// <remarks>
    /// Whole-World v4 streaming invokes each topology encoder exactly once and writes its observed
    /// byte-count footer directly after the payload, including on non-seekable destinations.
    /// </remarks>
    public SerializationRegistry RegisterRelationTopology<T>()
        where T : struct, IComponent
    {
        SerializationTypeRuntime payload = RequireOrdinaryRelationPayload<T>();
        return RegisterTopology(new RelationTopologySerializationRuntime<T>(
            CreateRelationTopologyKey<T>(payload.Entry.TypeKey),
            payload.Entry));
    }

    public SerializationRegistry RegisterRelationTopology<T>(SerializationTypeKey typeKey)
        where T : struct, IComponent
    {
        SerializationTypeRuntime payload = RequireOrdinaryRelationPayload<T>();
        return RegisterTopology(new RelationTopologySerializationRuntime<T>(typeKey, payload.Entry));
    }

    private SerializationTypeRuntime RequireOrdinaryRelationPayload<T>()
        where T : struct, IComponent
    {
        SerializationTypeRuntime payload = GetRegistered<T>();
        if (payload.Entry.Kind != SerializationValueKind.Component ||
            payload.Entry.Storage != SomeEngine.ECS.Registry.StoragePath.Table)
        {
            throw new InvalidOperationException(
                $"Relation payload {typeof(T).FullName} must be registered as an ordinary table component before its topology.");
        }

        return payload;
    }

    internal ReadOnlySpan<TopologySerializationRuntime> TopologyRuntimes =>
        CollectionsMarshal.AsSpan(_topologyRuntimes);

    internal TopologySerializationRuntime ResolveTopology(
        TopologySerializationKind kind,
        SerializationTypeKey fileKey)
    {
        if (!_topologyByStableId.TryGetValue(fileKey.StableId, out var runtime))
        {
            throw new InvalidDataException(
                $"Unknown serialized {kind} topology '{fileKey.StableName}' ({fileKey.StableId}).");
        }
        if (runtime.Kind != kind)
        {
            throw new InvalidDataException(
                $"Serialized topology '{fileKey.StableName}' is {kind}, but the registered stable id is {runtime.Kind}.");
        }
        if (fileKey.SchemaFingerprint == 0 ||
            runtime.TypeKey.SchemaFingerprint != fileKey.SchemaFingerprint)
        {
            throw new InvalidDataException(
                $"Topology schema mismatch for '{fileKey.StableName}': " +
                $"file=0x{fileKey.SchemaFingerprint:X16}, " +
                $"local=0x{runtime.TypeKey.SchemaFingerprint:X16}.");
        }
        if (!string.Equals(
                runtime.TypeKey.StableName,
                fileKey.StableName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Serialized topology key name '{fileKey.StableName}' does not exactly match " +
                $"registered name '{runtime.TypeKey.StableName}'.");
        }
        return runtime;
    }

    private SerializationRegistry RegisterTopology(TopologySerializationRuntime runtime)
    {
        if (runtime.TypeKey.SchemaFingerprint == 0)
        {
            throw new ArgumentException(
                $"Topology type '{runtime.TypeKey.StableName}' must declare a non-zero 64-bit schema fingerprint.",
                nameof(runtime));
        }
        if (_topologyByStableId.ContainsKey(runtime.TypeKey.StableId))
            throw new InvalidOperationException($"Topology serialization stable id {runtime.TypeKey.StableId} is already registered.");
        var typeKey = (runtime.Kind, runtime.ValueType);
        if (_topologyByType.ContainsKey(typeKey))
        {
            throw new InvalidOperationException(
                $"{runtime.Kind} topology {runtime.ValueType.FullName} is already registered for serialization.");
        }
        _topologyRuntimes.Add(runtime);
        _topologyRuntimes.Sort(static (left, right) =>
        {
            int kind = left.Kind.CompareTo(right.Kind);
            return kind != 0 ? kind : CompareTypeKeys(left.TypeKey, right.TypeKey);
        });
        _topologyByStableId.Add(runtime.TypeKey.StableId, runtime);
        _topologyByType.Add(typeKey, runtime);
        return this;
    }

    private static SerializationTypeKey CreateHierarchyTopologyKey<TDomain>()
        where TDomain : IHierarchyDomain
    {
        string stableName = (typeof(TDomain).FullName ?? typeof(TDomain).Name) + "#hierarchy-topology-v2";
        ulong fingerprint = ComputeTopologySchemaFingerprint(stableName, 0x48494552ul);
        return new SerializationTypeKey(
            CreateDeterministicGuid(stableName),
            stableName,
            fingerprint);
    }

    private static SerializationTypeKey CreateRelationTopologyKey<T>(SerializationTypeKey payloadKey)
        where T : struct, IComponent
    {
        RelationSchema schema = RelationSchema.For<T>();
        string stableName = payloadKey.StableName + "#relation-topology-v2";
        ulong fingerprint = ComputeTopologySchemaFingerprint(
            stableName,
            payloadKey.SchemaFingerprint);
        fingerprint = (fingerprint ^ (uint)schema.Direction) * 1099511628211ul;
        fingerprint = (fingerprint ^ (uint)schema.Cardinality) * 1099511628211ul;
        fingerprint = (fingerprint ^ (schema.AllowSelfEdge ? 1u : 0u)) * 1099511628211ul;
        fingerprint = fingerprint == 0 ? 1ul : fingerprint;
        return new SerializationTypeKey(
            CreateDeterministicGuid(stableName),
            stableName,
            fingerprint);
    }

    private static ulong ComputeTopologySchemaFingerprint(string stableName, ulong seed)
    {
        ulong hash = 14695981039346656037ul ^ seed;
        foreach (byte value in Encoding.UTF8.GetBytes(stableName))
            hash = (hash ^ value) * 1099511628211ul;
        hash = (hash ^ TopologyWireVersion) * 1099511628211ul;
        return hash == 0 ? 1ul : hash;
    }
}

internal abstract class TopologySerializationRuntime
{
    protected TopologySerializationRuntime(
        TopologySerializationKind kind,
        Type valueType,
        SerializationTypeKey typeKey)
    {
        Kind = kind;
        ValueType = valueType;
        TypeKey = typeKey;
    }

    internal TopologySerializationKind Kind { get; }

    internal Type ValueType { get; }

    internal SerializationTypeKey TypeKey { get; }

    internal abstract void ValidateWriteState(AdmittedWorldWrite admitted);

    internal abstract void WriteAdmitted(
        BinaryWriter writer,
        AdmittedWorldWrite admitted,
        TopologyCaptureBudget budget);

    internal abstract void ReadApply(
        BinaryReader reader,
        SerializationReadBudget budget,
        World world,
        IReferenceRemapper? remapper);

    internal virtual void ValidateContract(SerializationContract contract)
    {
        if (contract == SerializationContract.DurableSave && TypeKey.SchemaFingerprint == 0)
        {
            throw new InvalidOperationException(
                $"Topology '{TypeKey.StableName}' does not declare an explicit 64-bit schema fingerprint " +
                "and cannot be written as DurableSave.");
        }
    }

}

internal sealed class HierarchyTopologySerializationRuntime<TDomain> : TopologySerializationRuntime
    where TDomain : IHierarchyDomain
{
    internal HierarchyTopologySerializationRuntime(SerializationTypeKey typeKey)
        : base(TopologySerializationKind.Hierarchy, typeof(TDomain), typeKey)
    {
    }

    internal override void ValidateWriteState(AdmittedWorldWrite admitted)
    {
        _ = admitted.OpenHierarchyTopology<TDomain>();
    }

    internal override void WriteAdmitted(
        BinaryWriter writer,
        AdmittedWorldWrite admitted,
        TopologyCaptureBudget budget)
    {
        HierarchyTopologyWriteAccess<TDomain> access =
            admitted.OpenHierarchyTopology<TDomain>();
        budget.ReserveRecords(access.RecordCount, TypeKey.StableName);
        var data = new DataWriter(writer);
        writer.Write(access.ParentCount);
        int writtenParents = 0;
        for (int slot = 0; slot < access.SlotCount; slot++)
        {
            if (!access.TryGetParentAt(slot, out Entity child, out Entity parent))
                continue;
            data.WriteEntity(child);
            data.WriteEntity(parent);
            writtenParents++;
        }
        if (writtenParents != access.ParentCount)
            throw new InvalidOperationException("Hierarchy Parent count changed during admitted serialization.");

        writer.Write(access.OrderedSequenceCount);
        int writtenSequences = 0;
        for (int slot = 0; slot < access.SlotCount; slot++)
        {
            if (!access.TryGetOrderedChildrenAt(
                    slot,
                    out Entity parent,
                    out ReadOnlyMemory<Entity> childrenMemory))
                continue;
            ReadOnlySpan<Entity> children = childrenMemory.Span;
            data.WriteEntity(parent);
            writer.Write(children.Length);
            for (int i = 0; i < children.Length; i++)
                data.WriteEntity(children[i]);
            writtenSequences++;
        }
        if (writtenSequences != access.OrderedSequenceCount)
            throw new InvalidOperationException("Hierarchy ordered sequence count changed during admitted serialization.");
    }

    internal override void ReadApply(
        BinaryReader reader,
        SerializationReadBudget budget,
        World world,
        IReferenceRemapper? remapper)
    {
        var data = new DataReader(reader, budget);
        int parentCount = ReadCount(reader, budget, "hierarchy Parent");
        var import =
            world.BeginHierarchyTopologyImport<TDomain>(parentCount);
        Entity previousChild = default;
        for (int i = 0; i < parentCount; i++)
        {
            Entity wireChild = data.ReadEntity();
            RequireCanonicalEntityOrder(previousChild, wireChild, i, "hierarchy Parent child");
            previousChild = wireChild;
            Entity child = MapOptional(remapper, wireChild);
            Entity parent = MapOptional(remapper, data.ReadEntity());
            import.AddParent(child, parent);
        }
        import.SealParents();

        int sequenceCount = ReadCount(reader, budget, "ordered hierarchy sequence");
        import.SetOrderedSequenceCount(sequenceCount);
        Entity previousSequenceParent = default;
        for (int i = 0; i < sequenceCount; i++)
        {
            Entity wireParent = data.ReadEntity();
            RequireCanonicalEntityOrder(
                previousSequenceParent,
                wireParent,
                i,
                "ordered hierarchy sequence parent");
            previousSequenceParent = wireParent;
            Entity parent = MapOptional(remapper, wireParent);
            int childCount = ReadCount(reader, budget, "ordered hierarchy child");
            var children = new Entity[childCount];
            for (int childIndex = 0; childIndex < children.Length; childIndex++)
                children[childIndex] = MapOptional(remapper, data.ReadEntity());
            import.AddOrderedSequence(parent, children);
        }
        import.Complete();
    }
}

internal sealed class RelationTopologySerializationRuntime<T> : TopologySerializationRuntime
    where T : struct, IComponent
{
    private readonly SerializationTypeEntry _payloadEntry;

    internal RelationTopologySerializationRuntime(
        SerializationTypeKey typeKey,
        SerializationTypeEntry payloadEntry)
        : base(TopologySerializationKind.Relation, typeof(T), typeKey)
    {
        _payloadEntry = payloadEntry;
    }

    internal override void ValidateContract(SerializationContract contract)
    {
        base.ValidateContract(contract);
        if (contract == SerializationContract.DurableSave &&
            _payloadEntry.CodecKind == ComponentCodecKind.Raw)
        {
            throw new InvalidOperationException(
                $"Relation payload '{_payloadEntry.TypeKey.StableName}' uses an ABI-dependent implicit raw " +
                "codec and cannot be written as DurableSave.");
        }


        if (contract == SerializationContract.DurableSave &&
            _payloadEntry.TypeKey.SchemaFingerprint == 0)
        {
            throw new InvalidOperationException(
                $"Relation payload '{_payloadEntry.TypeKey.StableName}' does not declare an explicit 64-bit " +
                "schema fingerprint and cannot be written as DurableSave.");
        }


        if (contract == SerializationContract.DurableSave &&
            _payloadEntry.SchemaSource != SerializationSchemaSource.Explicit)
        {
            throw new InvalidOperationException(
                $"Relation payload '{_payloadEntry.TypeKey.StableName}' uses a build-derived runtime schema " +
                "identity and cannot be written as DurableSave.");
        }
    }

    internal override void ValidateWriteState(AdmittedWorldWrite admitted)
    {
        _ = admitted.OpenRelationTopology<T>(validate: true);
    }

    internal override void WriteAdmitted(
        BinaryWriter writer,
        AdmittedWorldWrite admitted,
        TopologyCaptureBudget budget)
    {
        RelationTopologyWriteAccess<T> access =
            admitted.OpenRelationTopology<T>(validate: false);
        budget.ReserveRecords(access.RecordCount, TypeKey.StableName);
        writer.Write((byte)access.Schema.Direction);
        writer.Write((byte)access.Schema.Cardinality);
        writer.Write(access.Schema.AllowSelfEdge);
        var data = new DataWriter(writer);

        writer.Write(access.EdgeCount);
        int writtenEdges = 0;
        for (int slot = 0; slot < access.SlotCount; slot++)
        {
            if (!access.TryGetEdgeAt(
                    slot,
                    out Entity edge,
                    out Entity first,
                    out Entity second))
            {
                continue;
            }

            data.WriteEntity(edge);
            data.WriteEntity(first);
            data.WriteEntity(second);
            writtenEdges++;
        }
        if (writtenEdges != access.EdgeCount)
            throw new InvalidOperationException("Relation edge count changed during admitted serialization.");

        writer.Write(access.OrderedSequenceCount);
        int writtenSequences = 0;
        for (int slot = 0; slot < access.SlotCount; slot++)
        {
            if (access.Schema.Direction == RelationDirection.Directed)
            {
                if (TryWriteOrderedAt(
                        writer,
                        ref data,
                        access,
                        slot,
                        RelationAdjacencyRole.Outgoing))
                {
                    writtenSequences++;
                }
                if (TryWriteOrderedAt(
                        writer,
                        ref data,
                        access,
                        slot,
                        RelationAdjacencyRole.Incoming))
                {
                    writtenSequences++;
                }
            }
            else
            {
                if (TryWriteOrderedAt(
                        writer,
                        ref data,
                        access,
                        slot,
                        RelationAdjacencyRole.Incident))
                {
                    writtenSequences++;
                }
            }
        }
        if (writtenSequences != access.OrderedSequenceCount)
            throw new InvalidOperationException("Relation ordered sequence count changed during admitted serialization.");

        admitted.RecordRelationTopologyWrite<T>(writtenEdges, writtenSequences);
    }

    private static bool TryWriteOrderedAt(
        BinaryWriter writer,
        ref DataWriter data,
        RelationTopologyWriteAccess<T> access,
        int slot,
        RelationAdjacencyRole role)
    {
        if (!access.TryGetOrderedAt(
                slot,
                role,
                out Entity endpoint,
                out ReadOnlySpan<RelationAdjacencyEntry<T>> entries))
        {
            return false;
        }

        data.WriteEntity(endpoint);
        writer.Write((byte)role);
        writer.Write(entries.Length);
        for (int i = 0; i < entries.Length; i++)
            data.WriteEntity(entries[i].Edge.Entity);
        return true;
    }

    internal override void ReadApply(
        BinaryReader reader,
        SerializationReadBudget budget,
        World world,
        IReferenceRemapper? remapper)
    {
        var direction = (RelationDirection)reader.ReadByte();
        var cardinality = (RelationCardinality)reader.ReadByte();
        byte allowSelfByte = reader.ReadByte();
        if (allowSelfByte > 1)
            throw new InvalidDataException($"Invalid relation allow-self flag {allowSelfByte}.");

        var data = new DataReader(reader, budget);
        int edgeCount = ReadCount(reader, budget, "relation edge");
        RelationTopologyImport<T> import = world.BeginRelationTopologyImport<T>(
            direction,
            cardinality,
            allowSelfByte != 0,
            edgeCount);
        Entity previousEdge = default;
        for (int i = 0; i < edgeCount; i++)
        {
            Entity wireEdge = data.ReadEntity();
            RequireCanonicalEntityOrder(previousEdge, wireEdge, i, "relation edge");
            previousEdge = wireEdge;
            Entity edge = MapOptional(remapper, wireEdge);
            Entity first = MapOptional(remapper, data.ReadEntity());
            Entity second = MapOptional(remapper, data.ReadEntity());
            import.AddEdge(edge, first, second);
        }

        int sequenceCount = ReadCount(reader, budget, "ordered relation sequence");
        import.SetOrderedSequenceCount(sequenceCount);
        Entity previousEndpoint = default;
        RelationAdjacencyRole previousRole = default;
        for (int i = 0; i < sequenceCount; i++)
        {
            Entity wireEndpoint = data.ReadEntity();
            var role = (RelationAdjacencyRole)reader.ReadByte();
            bool validRole = direction == RelationDirection.Directed
                ? role == RelationAdjacencyRole.Outgoing || role == RelationAdjacencyRole.Incoming
                : role == RelationAdjacencyRole.Incident;
            if (!validRole)
                throw new InvalidDataException($"Invalid ordered relation adjacency role {(byte)role}.");
            if (i > 0)
            {
                int endpointOrder = CompareEntities(previousEndpoint, wireEndpoint);
                if (endpointOrder > 0 ||
                    (endpointOrder == 0 && previousRole.CompareTo(role) >= 0))
                {
                    throw new InvalidDataException(
                        "Ordered relation sequences are not in canonical endpoint/role order.");
                }
            }
            previousEndpoint = wireEndpoint;
            previousRole = role;
            Entity endpoint = MapOptional(remapper, wireEndpoint);
            int memberCount = ReadCount(reader, budget, "ordered relation edge");
            RelationTopologyImport<T>.OrderedSequence sequence =
                import.BeginOrderedSequence(endpoint, role, memberCount);
            for (int member = 0; member < memberCount; member++)
                sequence.AddEdge(MapOptional(remapper, data.ReadEntity()));
            sequence.Complete();
        }
        import.Complete();
    }

}

internal static class TopologySerializationHelpers
{
    internal static void RequireCanonicalEntityOrder(
        Entity previous,
        Entity current,
        int ordinal,
        string description)
    {
        if (ordinal > 0 && CompareEntities(previous, current) >= 0)
        {
            throw new InvalidDataException(
                $"Serialized {description} records are duplicate or not in canonical entity order.");
        }
    }

    internal static int CompareEntities(Entity left, Entity right)
    {
        int index = left.Index.CompareTo(right.Index);
        return index != 0 ? index : left.Generation.CompareTo(right.Generation);
    }

    internal static int ReadCount(
        BinaryReader reader,
        SerializationReadBudget budget,
        string description)
    {
        return budget.TopologyEntryCount(reader.ReadInt32(), description);
    }

    internal static Entity Map(IReferenceRemapper remapper, Entity entity)
    {
        if (!remapper.TryMap(entity, out Entity mapped))
            throw new InvalidOperationException($"Missing entity remap for topology entity {entity}.");
        return mapped;
    }

    internal static Entity MapOptional(IReferenceRemapper? remapper, Entity entity) =>
        remapper is null ? entity : Map(remapper, entity);

}
