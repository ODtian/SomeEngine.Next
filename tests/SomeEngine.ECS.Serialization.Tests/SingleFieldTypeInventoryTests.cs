using System.Reflection;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class SingleFieldTypeInventoryTests
{
    private static readonly IReadOnlyDictionary<string, string> ReviewedTypes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SomeEngine.ECS.Serialization.DataWriter"] =
                "Ref-struct wire capability that restricts codecs to canonical typed primitives.",
            ["SomeEngine.ECS.Serialization.ExternalReferenceKey"] =
                "Strong external-reference identity with explicit empty-key validation.",
            ["SomeEngine.ECS.Serialization.RelationTopologySerializationRuntime`1"] =
                "Owns typed relation topology validation, encoding, decoding, and payload binding.",
            ["SomeEngine.ECS.Serialization.SerializedFieldAttribute"] =
                "Declares the stable field identity consumed by generated canonical codecs.",
        };

    [Fact]
    public void EverySingleFieldConcreteTypeHasAReviewedIndependentResponsibility()
    {
        Assert.All(
            ReviewedTypes,
            static entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value)));

        string[] actual = SingleFieldConcreteTypes(typeof(WorldSerializer).Assembly);
        string[] expected = ReviewedTypes.Keys.Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            actual.SequenceEqual(expected, StringComparer.Ordinal),
            InventoryFailure("SomeEngine.ECS.Serialization", expected, actual));
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
