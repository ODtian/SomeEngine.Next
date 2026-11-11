using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

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
        RepeatedQueryWritesAdvanceTheirObservationVersions();
        ManagedGenericBufferElementsKeepReferencesThroughGrowthAndRemoval();
        TypedRootBatchesWriteFieldsWithOneSharedVersion();
        SparseRootBatchesWriteFieldsWithOneSharedVersion();
#if DEBUG
        QueryFieldWritesReportTheRequiredWritePermission();
        QueryFieldReadsReportTheCurrentChunkRowRange();
#endif
    }


    internal static void TypedRootBatchesWriteFieldsWithOneSharedVersion()
    {
        using var world = new World();
        Entity first = world.Create(new FieldData<Box> { Reference = new Box() });
        Entity second = world.Create(new FieldData<Box> { Reference = new Box() });
        ulong before = world.ComponentDataWriteVersion<FieldData<Box>>(first);
        var roots = world.OpenWrite<FieldData<Box>>();
        try
        {
            ReferenceAccess.Write(ref roots.Get(first), 7f);
            ReferenceAccess.Write(ref roots.Get(second), 7f);
        }
        finally { roots.Dispose(); }
        ulong version = world.ComponentDataWriteVersion<FieldData<Box>>(first);
        Require(version != before);
        RequireEqual((version, version, 7f, 7f), (
            world.ComponentDataWriteVersion<FieldData<Box>>(first), world.ComponentDataWriteVersion<FieldData<Box>>(second),
            world.ReadField<FieldData<Box>, float, ReferenceAccess>(first), world.ReadField<FieldData<Box>, float, ReferenceAccess>(second)));
    }

    internal static void SparseRootBatchesWriteFieldsWithOneSharedVersion()
    {
        using var world = new World();
        Entity first = world.Create();
        Entity second = world.Create();
        world.SetSparse(first, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });
        world.SetSparse(second, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });
        ulong before = world.ComponentDataWriteVersion<SparseFields>(first);
        var roots = world.OpenSparseWrite<SparseFields>();
        try
        {
            SparseAccess<ReferenceAccess>.Write(ref roots.Get(first), 7f);
            SparseAccess<ReferenceAccess>.Write(ref roots.Get(second), 7f);
        }
        finally { roots.Dispose(); }
        ulong version = world.ComponentDataWriteVersion<SparseFields>(first);
        Require(version != before);
        RequireEqual((version, version, 7f, 7f), (
            world.ComponentDataWriteVersion<SparseFields>(first), world.ComponentDataWriteVersion<SparseFields>(second),
            world.ReadField<SparseFields, float, SparseAccess<ReferenceAccess>>(first),
            world.ReadField<SparseFields, float, SparseAccess<ReferenceAccess>>(second)));
    }

    internal static void TableFieldsUseTypedAccessThroughValuesReferencesAndProperties()
    {
        using var world = new World();
        Entity entity = world.Create(new FieldData<Box> { Reference = new Box() });

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
        Entity entity = world.Create();
        world.SetSparse(entity, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });

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
        Entity entity = world.Create(new FieldData<Box> { Number = 9f, Reference = reference });

        world.WriteField<FieldData<Box>, float, ReferenceAccess>(entity, 3f);

        FieldData<Box> value = world.Read<FieldData<Box>>(entity);
        RequireEqual((9f, reference, 3f), (value.Number, value.Reference, value.Reference.Value));
    }

    internal static void ReferenceLeafWritesAdvanceTheRootWriteVersion()
    {
        using var world = new World();
        Entity entity = world.Create(new FieldData<Box> { Reference = new Box() });
        ulong version = world.ComponentDataWriteVersion<FieldData<Box>>(entity);

        world.WriteField<FieldData<Box>, float, ReferenceAccess>(entity, 3f);

        RequireDifferent(version, world.ComponentDataWriteVersion<FieldData<Box>>(entity));
    }

    internal static void SparseReferenceLeafWritesAdvanceTheRootWriteVersion()
    {
        using var world = new World();
        Entity entity = world.Create();
        world.SetSparse(entity, new SparseFields { Data = new FieldData<Box> { Reference = new Box() } });
        ulong version = world.ComponentDataWriteVersion<SparseFields>(entity);

        world.WriteField<SparseFields, float, SparseAccess<ReferenceAccess>>(entity, 3f);

        RequireDifferent(version, world.ComponentDataWriteVersion<SparseFields>(entity));
    }

    internal static void QueryFieldWritesAppearInChangedQueries()
    {
        using var world = new World();
        world.Create(new FieldData<Box> { Reference = new Box() });
        Query query = world.Query(new QueryDescription().WithStored<FieldData<Box>>());
        ulong version = world.Version;

        {
            using QueryScope cursor = (query).Open();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                ReferenceAccess.Write(ref rowChunk.Write<FieldData<Box>>(row), 7f);

        };

        Query changed = world.Query(new QueryDescription().WithStored<FieldData<Box>>().WithChanged<FieldData<Box>>(0));
        var values = new List<float>();
        {
            using QueryScope cursor = (changed).Since(version).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                values.Add(ReferenceAccess.Read(in rowChunk.Read<FieldData<Box>>()[row]));

        };
        Require(values.SequenceEqual([7f]));
    }

    internal static unsafe void QueryFunctionWritesTypedComponentsAndCallerState()
    {
        using var world = new World();
        Entity entity = world.Create(new FieldData<Box> { Reference = new Box() });
        Query query = world.Query(new QueryDescription().WithStored<FieldData<Box>>());
        float value = 9f;

        using (QueryScope scope = query.Open()) WriteReferenceFields(scope, ref value);

        RequireEqual((9f, 10f), (world.ReadField<FieldData<Box>, float, ReferenceAccess>(entity), value));
    }

    internal static unsafe void RepeatedQueryWritesAdvanceTheirObservationVersions()
    {
        using var world = new World();
        Entity entity = world.Create(new FieldData<Box> { Reference = new Box() });
        Query query = world.Query(new QueryDescription().WithStored<FieldData<Box>>());
        ulong before = world.ComponentDataWriteVersion<FieldData<Box>>(entity);
        using (QueryScope scope = query.Open()) WriteNestedFields(scope);
        ulong firstVersion = world.ComponentDataWriteVersion<FieldData<Box>>(entity);

        using (QueryScope scope = query.Open()) WriteNestedFields(scope);

        Require(firstVersion != before && world.ComponentDataWriteVersion<FieldData<Box>>(entity) != firstVersion);
    }

    internal static void ManagedGenericBufferElementsKeepReferencesThroughGrowthAndRemoval()
    {
        using var world = new World();
        Entity entity = world.Create();
        world.AddBuffer<BufferData<Box>>(entity);
        var box = new Box { Value = 5f };
        var buffer = world.GetBuffer<BufferData<Box>>(entity);
        try
        {
            buffer.Add(new BufferData<Box> { Value = new Box() });
            buffer.Add(new BufferData<Box> { Value = box });
            buffer.Add(new BufferData<Box> { Value = new Box() });
            buffer.RemoveAtSwapBack(0);
            buffer[1].Value.Value = 7f;
        }
        finally { buffer.Dispose(); }
        using (ReadOnlyBuffer<BufferData<Box>> read = world.ReadBuffer<BufferData<Box>>(entity))
            RequireEqual((2, box, 7f), (read.Count, read[1].Value, read[1].Value.Value));
        world.RemoveBuffer<BufferData<Box>>(entity);
    }

#if DEBUG
    internal static void QueryFieldWritesReportTheRequiredWritePermission()
    {
        using var world = new World();
        world.Create(new FieldData<Box> { Reference = new Box() });
        Query query = world.Query(new QueryDescription().WithStored<FieldData<Box>>());

        RequireInvalidOperation(() => {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                ReferenceAccess.Write(ref rowChunk.Write<FieldData<Box>>(row), 7f);

        });
    }

    internal static void QueryFieldReadsReportTheCurrentChunkRowRange()
    {
        using var world = new World();
        world.Create(new FieldData<Box> { Reference = new Box() });
        Query query = world.Query(new QueryDescription().WithStored<FieldData<Box>>());

        RequireOutOfRange(() => {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk chunk in cursor)
                ReferenceAccess.Read(in chunk.Read<FieldData<Box>>()[chunk.Count]);

        });
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
    private static void WriteReferenceFields(QueryScope scope, ref float value)
    {
        foreach (QueryChunk chunk in scope)
        {
            Span<FieldData<Box>> fields = chunk.Write<FieldData<Box>>();
            for (int row = 0; row < fields.Length; row++) ReferenceAccess.Write(ref fields[row], in value);
        }
        value++;
    }

    private static void WriteNestedFields(QueryScope scope)
    {
        foreach (QueryChunk chunk in scope)
            for (int row = 0; row < chunk.Count; row++)
                NestedAccess.Write(ref chunk.Write<FieldData<Box>>(row), 4f);
    }

    private static void RequireOutOfRange(Action action)
    {
        try { action(); }
        catch (IndexOutOfRangeException) { return; }
        throw new InvalidOperationException("The typed range must report the invalid row index.");
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

    private struct FieldData<T> : IAddedComponent where T : class
    {
        public float Number;
        public Number Nested;
        public T Reference;
    }

    private struct SparseFields : IAddedComponent, ISparseComponent
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

    private struct BufferData<T> : IBufferElement, IAddedComponent where T : class
    {
        public T Value;
    }
}
