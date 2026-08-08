using System.Collections.Concurrent;
using System.Reflection;
using SomeEngine.ECS.Owners;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class RelationStorageContractTests
{
    [Fact]
    public void PublishedGeneration_UsesSlotPagedStorage_AndHasNoCardinalityIndex()
    {
        Type generation = typeof(RelationGeneration<>);

        AssertPagedMap(RequiredProperty(generation, "Edges").PropertyType);
        AssertPagedMap(RequiredProperty(generation, "Outgoing").PropertyType);
        AssertPagedMap(RequiredProperty(generation, "Incoming").PropertyType);
        AssertPagedMap(RequiredProperty(generation, "Incident").PropertyType);

        Assert.Null(generation.GetProperty("PairIndex", AllMembers));
        Assert.Null(generation.GetProperty("FirstIndex", AllMembers));
        Assert.Null(generation.GetProperty("SecondIndex", AllMembers));
        Assert.Null(generation.GetProperty("IncidentIndex", AllMembers));

        foreach (FieldInfo field in generation.GetFields(AllMembers))
        {
            if (IsHashContainer(field.FieldType))
            {
                Assert.StartsWith(
                    "_mutable",
                    field.Name,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RelationState_HashContainersAreStrictlyCommandBatchLocal()
    {
        foreach (FieldInfo field in typeof(RelationTypeState<>).GetFields(AllMembers))
        {
            if (IsHashContainer(field.FieldType))
            {
                Assert.StartsWith(
                    "_commandBatch",
                    field.Name,
                    StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RelationGraph_TypeAndEndpointMetadataUsePublishedSlotTables()
    {
        Type graph = typeof(RelationGraph);
        Assert.Equal(
            typeof(RelationTypeSlotTable),
            RequiredField(graph, "_states").FieldType);
        Type endpointTable = RequiredField(graph, "_endpointTrackers").FieldType;
        Assert.True(endpointTable.IsGenericType);
        Assert.Equal(typeof(RelationComponentSlotTable<>), endpointTable.GetGenericTypeDefinition());

        foreach (FieldInfo field in graph.GetFields(AllMembers))
        {
            Assert.False(
                IsDictionary(field.FieldType),
                $"RelationGraph retains long-lived dictionary field {field.Name}.");
        }
    }

    [Fact]
    public void RelationTopologyDiagnostics_UseComponentSlotTable()
    {
        Type fieldType = RequiredField(
            typeof(World),
            "_relationTopologyWriteCounters").FieldType;

        Assert.True(fieldType.IsGenericType);
        Assert.Equal(typeof(RelationComponentSlotTable<>), fieldType.GetGenericTypeDefinition());
    }

    private static void AssertPagedMap(Type type)
    {
        Assert.True(type.IsGenericType);
        Assert.Equal(typeof(RelationEntityMap<>), type.GetGenericTypeDefinition());
    }

    private static bool IsHashContainer(Type type) =>
        IsDictionary(type) ||
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>);

    private static bool IsDictionary(Type type) =>
        type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
         type.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>));

    private static FieldInfo RequiredField(Type type, string name) =>
        type.GetField(name, AllMembers) ??
        throw new InvalidOperationException($"Required field {type.FullName}.{name} is missing.");

    private static PropertyInfo RequiredProperty(Type type, string name) =>
        type.GetProperty(name, AllMembers) ??
        throw new InvalidOperationException($"Required property {type.FullName}.{name} is missing.");

    private const BindingFlags AllMembers =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
}
