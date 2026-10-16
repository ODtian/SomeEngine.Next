using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Tests;

internal static class FieldAccessChecks
{
    internal static void VerifyAll()
    {
        TableFieldsUseTypedAccessThroughValuesReferencesAndProperties();
        SparseFieldsUseTypedAccessThroughValuesReferencesAndProperties();
        ReferenceLeafWritesKeepTheCurrentObjectAndOtherFields();
        ReferenceLeafWritesAdvanceTheRootWriteVersion();
        SparseReferenceLeafWritesAdvanceTheRootWriteVersion();
        QueryFieldWritesAppearInChangedQueries();
        QueryFunctionWritesTypedComponentsAndCallerState();
        QueryFunctionAndDelegatePublishTheSameVersionRules();
        ManagedGenericBufferElementsKeepReferencesThroughGrowthAndRemoval();
        TypedRootBatchesWriteFieldsWithOneSharedVersion();
        SparseRootBatchesWriteFieldsWithOneSharedVersion();
#if DEBUG
        QueryFieldWritesReportTheRequiredWritePermission();
        QueryFieldReadsReportTheCurrentChunkRowRange();
#endif
    }


    internal static unsafe void TypedRootBatchesWriteFieldsWithOneSharedVersion()
    {
        using var world = new World();
        Entity first = world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        Entity second = world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        uint before = world.WriteVersion<FieldData<Box>>(first);
        var batch = new FieldBatch([first, second], 7f);

        world.ExecuteComponentWrite<FieldData<Box>, FieldBatch>(ref batch, &WriteRootBatch);

        Require(batch.Version != before);
        RequireEqual((batch.Version, batch.Version, 7f, 7f), (
            world.WriteVersion<FieldData<Box>>(first), world.WriteVersion<FieldData<Box>>(second),
            world.ReadField<FieldData<Box>, float, ReferenceAccess>(first),
            world.ReadField<FieldData<Box>, float, ReferenceAccess>(second)));
    }

    internal static unsafe void SparseRootBatchesWriteFieldsWithOneSharedVersion()
    {
        using var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        world.AddSparse(first, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });
        world.AddSparse(second, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });
        uint before = world.SparseWriteVersion<SparseFields>(first);
        var batch = new FieldBatch([first, second], 7f);

        world.ExecuteComponentWrite<SparseFields, FieldBatch>(ref batch, &WriteSparseBatch);

        Require(batch.Version != before);
        RequireEqual((batch.Version, batch.Version, 7f, 7f), (
            world.SparseWriteVersion<SparseFields>(first), world.SparseWriteVersion<SparseFields>(second),
            world.ReadField<SparseFields, float, SparseAccess<ReferenceAccess>>(first),
            world.ReadField<SparseFields, float, SparseAccess<ReferenceAccess>>(second)));
    }

    private static void WriteRootBatch(ComponentWriteBatch<FieldData<Box>> roots, ref FieldBatch batch)
    {
        foreach (Entity entity in batch.Entities) ReferenceAccess.Write(ref roots.Write(entity), in batch.Value);
        batch.Version = roots.CurrentSystemVersion;
    }

    private static void WriteSparseBatch(ComponentWriteBatch<SparseFields> roots, ref FieldBatch batch)
    {
        foreach (Entity entity in batch.Entities) SparseAccess<ReferenceAccess>.Write(ref roots.Write(entity), in batch.Value);
        batch.Version = roots.CurrentSystemVersion;
    }

    private ref struct FieldBatch(ReadOnlySpan<Entity> entities, float value)
    {
        public readonly ReadOnlySpan<Entity> Entities = entities;
        public readonly float Value = value;
        public uint Version;
    }

    internal static void TableFieldsUseTypedAccessThroughValuesReferencesAndProperties()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new FieldData<Box> { Reference = new Box() });

        world.WriteField<FieldData<Box>, float, NumberAccess>(entity, 1f);
        world.WriteField<FieldData<Box>, float, NestedAccess>(entity, 2f);
        world.WriteField<FieldData<Box>, float, ReferenceAccess>(entity, 3f);
        world.WriteField<FieldData<Box>, float, PropertyAccess>(entity, 4f);

        RequireEqual((1f, 2f, 3f, 4f), (
            world.ReadField<FieldData<Box>, float, NumberAccess>(entity),
            world.ReadField<FieldData<Box>, float, NestedAccess>(entity),
            world.ReadField<FieldData<Box>, float, ReferenceAccess>(entity),
            world.ReadField<FieldData<Box>, float, PropertyAccess>(entity)));
    }

    internal static void SparseFieldsUseTypedAccessThroughValuesReferencesAndProperties()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        world.AddSparse(entity, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });

        world.WriteField<SparseFields, float, SparseAccess<NumberAccess>>(entity, 1f);
        world.WriteField<SparseFields, float, SparseAccess<NestedAccess>>(entity, 2f);
        world.WriteField<SparseFields, float, SparseAccess<ReferenceAccess>>(entity, 3f);
        world.WriteField<SparseFields, float, SparseAccess<PropertyAccess>>(entity, 4f);

        RequireEqual((1f, 2f, 3f, 4f), (
            world.ReadField<SparseFields, float, SparseAccess<NumberAccess>>(entity),
            world.ReadField<SparseFields, float, SparseAccess<NestedAccess>>(entity),
            world.ReadField<SparseFields, float, SparseAccess<ReferenceAccess>>(entity),
            world.ReadField<SparseFields, float, SparseAccess<PropertyAccess>>(entity)));
    }

    internal static void ReferenceLeafWritesKeepTheCurrentObjectAndOtherFields()
    {
        using var world = new World();
        var reference = new Box();
        Entity entity = world.CreateEntity(new FieldData<Box> { Number = 9f, Reference = reference });

        world.WriteField<FieldData<Box>, float, ReferenceAccess>(entity, 3f);

        FieldData<Box> value = world.Read<FieldData<Box>>(entity);
        RequireEqual((9f, reference, 3f), (value.Number, value.Reference, value.Reference.Value));
    }

    internal static void ReferenceLeafWritesAdvanceTheRootWriteVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        uint version = world.WriteVersion<FieldData<Box>>(entity);

        world.WriteField<FieldData<Box>, float, ReferenceAccess>(entity, 3f);

        RequireDifferent(version, world.WriteVersion<FieldData<Box>>(entity));
    }

    internal static void SparseReferenceLeafWritesAdvanceTheRootWriteVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        world.AddSparse(entity, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });
        uint version = world.SparseWriteVersion<SparseFields>(entity);

        world.WriteField<SparseFields, float, SparseAccess<ReferenceAccess>>(entity, 3f);

        RequireDifferent(version, world.SparseWriteVersion<SparseFields>(entity));
    }

    internal static void QueryFieldWritesAppearInChangedQueries()
    {
        using var world = new World();
        world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        QueryHandle query = world.Query(world.QueryDefinition().Write<FieldData<Box>>());
        uint version = world.CurrentTick;

        world.ExecuteQuery(query, static cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
                row.WriteField<FieldData<Box>, float, ReferenceAccess>(7f);
        });

        QueryHandle changed = world.Query(world.QueryDefinition().Read<FieldData<Box>>().Changed<FieldData<Box>>());
        var values = new List<float>();
        world.ExecuteQuery(changed, version, cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
                values.Add(row.ReadField<FieldData<Box>, float, ReferenceAccess>());
        });
        Require(values.SequenceEqual([7f]));
    }

    internal static unsafe void QueryFunctionWritesTypedComponentsAndCallerState()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        QueryHandle query = world.Query(world.QueryDefinition().Write<FieldData<Box>>());
        float value = 9f;

        world.ExecuteQuery(query, ref value, &WriteReferenceFields);

        RequireEqual((9f, 10f), (world.ReadField<FieldData<Box>, float, ReferenceAccess>(entity), value));
    }

    internal static unsafe void QueryFunctionAndDelegatePublishTheSameVersionRules()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        QueryHandle query = world.Query(world.QueryDefinition().Write<FieldData<Box>>());
        uint before = world.WriteVersion<FieldData<Box>>(entity);
        world.ExecuteQuery(query, WriteNestedFields);
        uint delegateVersion = world.WriteVersion<FieldData<Box>>(entity);

        world.ExecuteQuery(query, &WriteNestedFields);

        Require(delegateVersion != before && world.WriteVersion<FieldData<Box>>(entity) != delegateVersion);
    }

    internal static void ManagedGenericBufferElementsKeepReferencesThroughGrowthAndRemoval()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        world.AddBuffer<BufferData<Box>>(entity);
        var box = new Box { Value = 5f };
        world.ExecuteBufferWrite<BufferData<Box>>(entity, buffer =>
        {
            buffer.Add(new BufferData<Box> { Value = new Box() });
            buffer.Add(new BufferData<Box> { Value = box });
            buffer.Add(new BufferData<Box> { Value = new Box() });
            buffer.SwapRemoveAt(0);
            buffer[1].Value.Value = 7f;
        });

        world.ExecuteBufferRead<BufferData<Box>>(entity, buffer =>
            RequireEqual((2, box, 7f), (buffer.Count, buffer.Read(1).Value, buffer.Read(1).Value.Value)));
        world.RemoveBuffer<BufferData<Box>>(entity);
    }

#if DEBUG
    internal static void QueryFieldWritesReportTheRequiredWritePermission()
    {
        using var world = new World();
        world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        QueryHandle query = world.Query(world.QueryDefinition().Read<FieldData<Box>>());

        RequireInvalidOperation(() => world.ExecuteQuery(query, static cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
                row.WriteField<FieldData<Box>, float, ReferenceAccess>(7f);
        }));
    }

    internal static void QueryFieldReadsReportTheCurrentChunkRowRange()
    {
        using var world = new World();
        world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        QueryHandle query = world.Query(world.QueryDefinition().Read<FieldData<Box>>());

        RequireInvalidOperation(() => world.ExecuteQuery(query, static cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
                chunk.ReadField<FieldData<Box>, float, ReferenceAccess>(chunk.Count);
        }));
    }
#endif

    private static void RequireEqual<T>(T expected, T actual) => Require(EqualityComparer<T>.Default.Equals(expected, actual));

    private static void RequireDifferent<T>(T expected, T actual) => Require(!EqualityComparer<T>.Default.Equals(expected, actual));

    private static void Require(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Typed ECS field behavior check failed.");
    }

    private static void RequireInvalidOperation(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Typed ECS field development check must report the invalid access.");
    }
    private static void WriteReferenceFields(QueryCursor cursor, ref float value)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
        {
            FieldWriteView<FieldData<Box>, float, ReferenceAccess> fields =
                chunk.WriteFields<FieldData<Box>, float, ReferenceAccess>();
            for (int i = 0; i < fields.Count; i++)
                fields.Write(i, in value);
        }
        value++;
    }

    private static void WriteNestedFields(QueryCursor cursor)
    {
        foreach (QueryChunkView chunk in cursor.Chunks)
            foreach (int row in chunk.RowIndices)
                chunk.WriteField<FieldData<Box>, float, NestedAccess>(row, 4f);
    }

    private sealed class Box
    {
        public float Value;
        public float Property { get; set; }
    }

    private struct Number
    {
        public float Value;
    }

    private struct FieldData<T> : IComponent where T : class
    {
        public float Number;
        public Number Nested;
        public T Reference;
    }

    private struct SparseFields : IComponent, ISparseComponent
    {
        public FieldData<Box> Data;
    }

    private struct NumberAccess : IFieldAccess<FieldData<Box>, float>
    {
        public static float Read(in FieldData<Box> component) => component.Number;
        public static void Write(ref FieldData<Box> component, in float value) => component.Number = value;
    }

    private struct NestedAccess : IFieldAccess<FieldData<Box>, float>
    {
        public static float Read(in FieldData<Box> component) => component.Nested.Value;
        public static void Write(ref FieldData<Box> component, in float value) => component.Nested.Value = value;
    }

    private struct ReferenceAccess : IFieldAccess<FieldData<Box>, float>
    {
        public static float Read(in FieldData<Box> component) => component.Reference.Value;
        public static void Write(ref FieldData<Box> component, in float value) => component.Reference.Value = value;
    }

    private struct PropertyAccess : IFieldAccess<FieldData<Box>, float>
    {
        public static float Read(in FieldData<Box> component) => component.Reference.Property;
        public static void Write(ref FieldData<Box> component, in float value) => component.Reference.Property = value;
    }

    private struct SparseAccess<TAccess> : IFieldAccess<SparseFields, float>
        where TAccess : struct, IFieldAccess<FieldData<Box>, float>
    {
        public static float Read(in SparseFields component) => TAccess.Read(in component.Data);
        public static void Write(ref SparseFields component, in float value) => TAccess.Write(ref component.Data, in value);
    }

    [BufferCapacity(2)]
    private struct BufferData<T> : IBufferElement where T : class
    {
        public T Value;
    }
}

