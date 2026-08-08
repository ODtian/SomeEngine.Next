using System.Reflection;
using SomeEngine.Testing;

namespace SomeEngine.ECS.Tests;

public sealed class EcsPublicApiBaselineTests
{
    private const string ExpectedSha256 = "FA4BE728D2ECE9AFD895E996A064CA00EEBAC92B76E3BFAF9ABA17BEFE1A02EE";

    [Fact]
    public void ExportedApiMatchesReviewedBaseline()
    {
        string surface = PublicApiSurface.Build(typeof(World).Assembly);
        string actual = PublicApiSurface.Sha256(surface);

        Assert.True(
            string.Equals(ExpectedSha256, actual, StringComparison.Ordinal),
            PublicApiSurface.FailureMessage("SomeEngine.ECS", ExpectedSha256, actual, surface));
    }

    [Fact]
    public void RemovedOwnershipAndBorrowWrappersRemainAbsent()
    {
        Assembly assembly = typeof(World).Assembly;
        string[] removedTypes =
        [
            "SomeEngine.ECS.ResourceOwnership",
            "SomeEngine.ECS.Archetypes.ArchetypeEdge",
            "SomeEngine.ECS.Components.BufferValues`1",
            "SomeEngine.ECS.Components.SharedComponentValue`1",
            "SomeEngine.ECS.Components.IComponent",
            "SomeEngine.ECS.Components.IEnableableComponent",
            "SomeEngine.ECS.Components.ICleanupComponent",
            "SomeEngine.ECS.Hierarchy.HierarchyChildrenView`1",
            "SomeEngine.ECS.Queries.ReadWriteMatches",
            "SomeEngine.ECS.Queries.QueryHandleBox",
            "SomeEngine.ECS.Serialization.SerializationChangeJournal",
        ];
        for (int i = 0; i < removedTypes.Length; i++)
            Assert.Null(assembly.GetType(removedTypes[i], throwOnError: false));

        Assert.DoesNotContain(
            typeof(World).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            static method => method.Name == "SuppressSerializationJournal");
    }
}
