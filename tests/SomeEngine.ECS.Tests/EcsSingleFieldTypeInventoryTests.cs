using System.Reflection;

namespace SomeEngine.ECS.Tests;

public sealed class EcsSingleFieldTypeInventoryTests
{
    private static readonly IReadOnlyDictionary<string, string> ReviewedTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SomeEngine.ECS.Collections.SmallInlineStorage`1"] =
                "Inline-array physical layout; the field is the first in-place storage element.",
            ["SomeEngine.ECS.Commands.CommandBuffer+JobProducerPlaybackBatch"] =
                "Owns completion and disposal of one admitted producer-playback batch.",
            ["SomeEngine.ECS.Commands.CommandBuffer+RecordAccessScope"] =
                "Owns monitor release for one command-recording critical section.",
            ["SomeEngine.ECS.Commands.DeferredEntity"] =
                "Typed deferred identity whose cell carries pending/resolved/invalid lifecycle.",
            ["SomeEngine.ECS.Commands.DeferredRelationEdge`1"] =
                "Relation-typed deferred edge identity with resolution lifecycle.",
            ["SomeEngine.ECS.Commands.DestroyRelationCommand`1"] =
                "Executable command behavior for resolving and destroying one deferred edge.",
            ["SomeEngine.ECS.Commands.DestroySubtreeCommand`1"] =
                "Executable hierarchy command with domain-specific subtree semantics.",
            ["SomeEngine.ECS.Components.BufferCapacityAttribute"] =
                "Declarative capacity invariant consumed by component registration.",
            ["SomeEngine.ECS.Components.DynamicBufferInline`1"] =
                "Inline-array ABI for the component's in-chunk buffer payload.",
            ["SomeEngine.ECS.Hierarchy.Parent`1"] =
                "Canonical domain-qualified hierarchy fact, not an Entity forwarding facade.",
            ["SomeEngine.ECS.Indexing.ComponentIndex`2+Builder"] =
                "Owns the mutable construction algorithm for one immutable index generation.",
            ["SomeEngine.ECS.Owners.Clock"] =
                "Owns monotonic ECS version acquisition and wrap semantics.",
            ["SomeEngine.ECS.Owners.ExceptionAccumulator"] =
                "Owns deterministic aggregation and terminal throw behavior.",
            ["SomeEngine.ECS.Owners.HierarchyDomainStore`1+HierarchyDomainGeneration"] =
                "Owns copy-on-write sharing state for a hierarchy generation.",
            ["SomeEngine.ECS.Queries.ChunkRowEnumerator"] =
                "Callback-scoped ref-struct enumeration state over one chunk row cursor.",
            ["SomeEngine.ECS.Queries.QueryDefinitionBuilder"] =
                "Owns query-term accumulation and normalized definition construction.",
            ["SomeEngine.ECS.Relations.RelationDirtyEdgeBucket"] =
                "Owns an immutable endpoint-local dirty-edge generation.",
            ["SomeEngine.ECS.Relations.RelationEdge`1"] =
                "Relation-typed edge identity; the type parameter prevents cross-relation use.",
            ["SomeEngine.ECS.Relations.RelationEntityMap`1"] =
                "Owns a persistent entity-page table and its copy-on-write generation.",
            ["SomeEngine.ECS.SharedStores"] =
                "Owns registration, growth, cloning, and disposal of shared-component stores.",
        };

    [Fact]
    public void EverySingleFieldConcreteTypeHasAReviewedIndependentResponsibility()
    {
        Assert.All(
            ReviewedTypes,
            static entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value)));

        string[] actual = SingleFieldConcreteTypes(typeof(World).Assembly);
        string[] expected = ReviewedTypes.Keys.Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            actual.SequenceEqual(expected, StringComparer.Ordinal),
            InventoryFailure("SomeEngine.ECS", expected, actual));
    }

    private static string[] SingleFieldConcreteTypes(Assembly assembly) =>
        assembly.GetTypes()
            .Where(static type =>
                !type.IsInterface &&
                !type.IsEnum &&
                !type.IsAbstract &&
                !type.Name.StartsWith('<') &&
                type.GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly)
                    .Count(static field => !field.IsStatic) == 1)
            .Select(static type => type.FullName!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string InventoryFailure(
        string assemblyName,
        IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> actual)
    {
        string[] added = actual.Except(expected, StringComparer.Ordinal).ToArray();
        string[] removed = expected.Except(actual, StringComparer.Ordinal).ToArray();
        return $"{assemblyName} single-field type inventory changed. Every candidate must be " +
               "reviewed for independent identity, invariant, lifecycle, storage, or algorithm " +
               "responsibility before updating this list." +
               Environment.NewLine +
               "Unreviewed:" + Environment.NewLine +
               string.Join(Environment.NewLine, added.Select(static item => " + " + item)) +
               Environment.NewLine +
               "Stale:" + Environment.NewLine +
               string.Join(Environment.NewLine, removed.Select(static item => " - " + item));
    }
}
