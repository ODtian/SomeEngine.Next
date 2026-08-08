using System.Reflection;
using SomeEngine.Testing;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class SerializationPublicApiBaselineTests
{
    private const string ExpectedSha256 = "E9C0D7B7F61ECD7F7C235ABA3FE67706C6ECE8AA436C8DAB7474328F92630C6D";

    [Fact]
    public void ExportedApiMatchesReviewedBaseline()
    {
        string surface = PublicApiSurface.Build(typeof(WorldSerializer).Assembly);
        string actual = PublicApiSurface.Sha256(surface);

        Assert.True(
            string.Equals(ExpectedSha256, actual, StringComparison.Ordinal),
            PublicApiSurface.FailureMessage("SomeEngine.ECS.Serialization", ExpectedSha256, actual, surface));
    }

    [Fact]
    public void RemovedCompatibilityEntrypointsRemainAbsent()
    {
        string[] removedWorldMethods =
        [
            "ApplyEntity",
            "CreateEntity",
            "CreateEntities",
            "CreateQueryResult",
            "ApplyDelta",
            "WriteDelta",
            "WriteDurableDelta",
            "WriteCheckpointDelta",
            "ReadDeltaEvents",
            "ReadDurableDeltaEvents",
            "ReadCheckpointDeltaEvents",
        ];
        MethodInfo[] worldMethods = typeof(WorldSerializer).GetMethods(
            BindingFlags.Public | BindingFlags.Static);
        for (int i = 0; i < removedWorldMethods.Length; i++)
        {
            string removedMethod = removedWorldMethods[i];
            Assert.DoesNotContain(worldMethods, method => method.Name == removedMethod);
        }

        Assert.DoesNotContain(
            typeof(WorldCheckpointCodec).GetMethods(BindingFlags.Public | BindingFlags.Static),
            static method => method.Name == "LoadInto");
        Assert.DoesNotContain(
            typeof(SerializationRegistry).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            static method => method.Name is "Capture" or "TryCapture");

        Assert.DoesNotContain(
            typeof(WorldSerializer).Assembly.GetExportedTypes(),
            static type =>
                type.Name.Contains("TopologySnapshot", StringComparison.Ordinal) ||
                type.Name.Contains("TopologyItemPayload", StringComparison.Ordinal) ||
                type.Name is "DeltaEvent" or "DeltaEventKind" or "DeltaSerializeOptions");
        Assert.DoesNotContain(
            typeof(WorldSerializer).GetNestedTypes(
                BindingFlags.Public | BindingFlags.NonPublic),
            static type => type.Name == "ManifestEntry");
    }

    [Fact]
    public void ConcreteDescriptorsOwnMultipleSemanticFacts()
    {
        Assembly[] assemblies =
        [
            typeof(WorldSerializer).Assembly,
            typeof(SomeEngine.Serialization.BinaryContractDescriptor).Assembly,
        ];
        Type[] descriptors = assemblies
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type =>
                !type.IsAbstract &&
                !type.IsInterface &&
                !type.IsEnum &&
                IsDescriptorName(type.Name))
            .ToArray();

        Assert.NotEmpty(descriptors);
        Assert.All(
            descriptors,
            static descriptor =>
            {
                FieldInfo[] fields = descriptor.GetFields(
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly)
                    .Where(static field => !field.IsStatic)
                    .ToArray();
                Assert.True(
                    fields.Length > 1,
                    $"{descriptor.FullName} is a single-field descriptor wrapper. " +
                    "A descriptor must own independent wire facts and invariants.");
            });
    }

    private static bool IsDescriptorName(string name)
    {
        int genericMarker = name.IndexOf('`');
        string simpleName = genericMarker < 0 ? name : name[..genericMarker];
        return simpleName.EndsWith("Descriptor", StringComparison.Ordinal) ||
               simpleName.EndsWith("Desc", StringComparison.Ordinal);
    }
}
