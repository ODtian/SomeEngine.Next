using System.Reflection;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class SingleFieldTypeInventoryTests
{
    private static readonly IReadOnlyDictionary<string, string> ReviewedTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SomeEngine.ECS.Systems.HierarchyMaintenanceEvidence"] =
                "Owns publication and validation of the completed inverse revision.",
            ["SomeEngine.ECS.Systems.HierarchyPropagationState"] =
                "Owns one-time publication and required retrieval of a partition proof.",
            ["SomeEngine.ECS.Systems.ImmediateSystemDriver"] =
                "Implements system version, checkpoint, lifetime, and World binding behavior.",
            ["SomeEngine.ECS.Systems.JobCommandBuffer+PublicationAdapter"] =
                "Executable scheduler adapter that publishes completed producer segments.",
            ["SomeEngine.ECS.Systems.JobCommandWriter"] =
                "Callback-scoped ref-struct capability exposing only record-safe operations.",
            ["SomeEngine.ECS.Systems.RelationMaintenanceSystem`1+MaintenanceJob"] =
                "Executable typed maintenance job with relation-finalization behavior.",
            ["SomeEngine.ECS.Systems.SystemNode`2"] =
                "Owns one system value and dispatches its complete lifecycle.",
            ["SomeEngine.ECS.Systems.TopologyPacketFinalizer`1+ParentFinalizerJob"] =
                "Executable finalizer that validates and atomically publishes staged Parent edits.",
        };

    [Fact]
    public void EverySingleFieldConcreteTypeHasAReviewedIndependentResponsibility()
    {
        Assert.All(
            ReviewedTypes,
            static entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value)));

        string[] actual = SingleFieldConcreteTypes(typeof(ISystemDriver<>).Assembly);
        string[] expected = ReviewedTypes.Keys.Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            actual.SequenceEqual(expected, StringComparer.Ordinal),
            InventoryFailure("SomeEngine.ECS.Systems", expected, actual));
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
