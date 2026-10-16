using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Tests;

public sealed class FieldAccessTests
{
    [Fact]
    public void TableFieldsUseTypedAccessThroughValuesReferencesAndProperties()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new FieldData<Box> { Reference = new Box() });

        world.WriteField<FieldData<Box>, float, NumberAccess>(entity, 1f);
        world.WriteField<FieldData<Box>, float, NestedAccess>(entity, 2f);
        world.WriteField<FieldData<Box>, float, ReferenceAccess>(entity, 3f);
        world.WriteField<FieldData<Box>, float, PropertyAccess>(entity, 4f);

        Assert.Equal((1f, 2f, 3f, 4f), (
            world.ReadField<FieldData<Box>, float, NumberAccess>(entity),
            world.ReadField<FieldData<Box>, float, NestedAccess>(entity),
            world.ReadField<FieldData<Box>, float, ReferenceAccess>(entity),
            world.ReadField<FieldData<Box>, float, PropertyAccess>(entity)));
    }

    [Fact]
    public void SparseFieldsUseTypedAccessThroughValuesReferencesAndProperties()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        world.AddSparse(entity, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });

        world.WriteField<SparseFields, float, SparseAccess<NumberAccess>>(entity, 1f);
        world.WriteField<SparseFields, float, SparseAccess<NestedAccess>>(entity, 2f);
        world.WriteField<SparseFields, float, SparseAccess<ReferenceAccess>>(entity, 3f);
        world.WriteField<SparseFields, float, SparseAccess<PropertyAccess>>(entity, 4f);

        Assert.Equal((1f, 2f, 3f, 4f), (
            world.ReadField<SparseFields, float, SparseAccess<NumberAccess>>(entity),
            world.ReadField<SparseFields, float, SparseAccess<NestedAccess>>(entity),
            world.ReadField<SparseFields, float, SparseAccess<ReferenceAccess>>(entity),
            world.ReadField<SparseFields, float, SparseAccess<PropertyAccess>>(entity)));
    }

    [Fact]
    public void ReferenceLeafWritesKeepTheCurrentObjectAndOtherFields()
    {
        using var world = new World();
        var reference = new Box();
        Entity entity = world.CreateEntity(new FieldData<Box> { Number = 9f, Reference = reference });

        world.WriteField<FieldData<Box>, float, ReferenceAccess>(entity, 3f);

        FieldData<Box> value = world.Read<FieldData<Box>>(entity);
        Assert.Equal((9f, reference, 3f), (value.Number, value.Reference, value.Reference.Value));
    }

    [Fact]
    public void ReferenceLeafWritesAdvanceTheRootWriteVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        uint version = world.WriteVersion<FieldData<Box>>(entity);

        world.WriteField<FieldData<Box>, float, ReferenceAccess>(entity, 3f);

        Assert.NotEqual(version, world.WriteVersion<FieldData<Box>>(entity));
    }

    [Fact]
    public void SparseReferenceLeafWritesAdvanceTheRootWriteVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        world.AddSparse(entity, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });
        uint version = world.SparseWriteVersion<SparseFields>(entity);

        world.WriteField<SparseFields, float, SparseAccess<ReferenceAccess>>(entity, 3f);

        Assert.NotEqual(version, world.SparseWriteVersion<SparseFields>(entity));
    }

    [Fact]
    public void QueryFieldWritesAppearInChangedQueries()
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
        Assert.Equal([7f], values);
    }

    [Fact]
    public unsafe void QueryFunctionWritesTypedComponentsAndCallerState()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        QueryHandle query = world.Query(world.QueryDefinition().Write<FieldData<Box>>());
        float value = 9f;

        world.ExecuteQuery(query, ref value, &WriteReferenceFields);

        Assert.Equal((9f, 10f), (world.ReadField<FieldData<Box>, float, ReferenceAccess>(entity), value));
    }

    [Fact]
    public unsafe void QueryFunctionAndDelegatePublishTheSameVersionRules()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        QueryHandle query = world.Query(world.QueryDefinition().Write<FieldData<Box>>());
        world.ExecuteQuery(query, WriteNestedFields);
        uint delegateVersion = world.WriteVersion<FieldData<Box>>(entity);

        world.ExecuteQuery(query, &WriteNestedFields);

        Assert.True(VersionClock.IsNewer(world.WriteVersion<FieldData<Box>>(entity), delegateVersion));
    }

    [Fact]
    public void ManagedGenericBufferElementsKeepReferencesThroughGrowthAndRemoval()
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
            Assert.Equal((2, box, 7f), (buffer.Count, buffer.Read(1).Value, buffer.Read(1).Value.Value)));
        world.RemoveBuffer<BufferData<Box>>(entity);
    }

#if DEBUG
    [Fact]
    public void QueryFieldWritesReportTheRequiredWritePermission()
    {
        using var world = new World();
        world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        QueryHandle query = world.Query(world.QueryDefinition().Read<FieldData<Box>>());

        Assert.Throws<InvalidOperationException>(() => world.ExecuteQuery(query, static cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
                row.WriteField<FieldData<Box>, float, ReferenceAccess>(7f);
        }));
    }

    [Fact]
    public void QueryFieldReadsReportTheCurrentChunkRowRange()
    {
        using var world = new World();
        world.CreateEntity(new FieldData<Box> { Reference = new Box() });
        QueryHandle query = world.Query(world.QueryDefinition().Read<FieldData<Box>>());

        Assert.Throws<InvalidOperationException>(() => world.ExecuteQuery(query, static cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
                chunk.ReadField<FieldData<Box>, float, ReferenceAccess>(chunk.Count);
        }));
    }
#endif

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
