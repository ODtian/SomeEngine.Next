using System.Text.RegularExpressions;

namespace SomeEngine.ECS.Tests;

public sealed partial class EcsMaterializationBoundaryTests
{
    private static readonly string[] SourceRoots =
    [
        "src/SomeEngine.ECS",
        "src/SomeEngine.ECS.Serialization",
        "src/SomeEngine.ECS.Systems",
        "src/SomeEngine.ECS.SourceGen",
    ];

    private static readonly ReviewedMaterialization[] ReviewedMaterializations =
    [
        new(
            "src/SomeEngine.ECS.Serialization/AdmittedWorldWrite.cs",
            2,
            MaterializationBoundary.SerializationPlan,
            "Freezes the retained-root manifest and its archetype dispatch plan before external output."),
        new(
            "src/SomeEngine.ECS.Serialization/WorldSerializer.ManifestValidation.cs",
            1,
            MaterializationBoundary.SerializationPlan,
            "Freezes the validated present-type manifest at the serialization boundary."),
        new(
            "src/SomeEngine.ECS.SourceGen/BundleGenerator.cs",
            4,
            MaterializationBoundary.SourceGeneration,
            "Materializes Roslyn symbol models while generating source, outside runtime ECS access."),
        new(
            "src/SomeEngine.ECS.SourceGen/JobEntityGenerator.cs",
            2,
            MaterializationBoundary.SourceGeneration,
            "Materializes Roslyn method and parameter models while generating source."),
        new(
            "src/SomeEngine.ECS.SourceGen/SerializationGenerator.cs",
            1,
            MaterializationBoundary.SourceGeneration,
            "Materializes Roslyn field models while generating codecs."),
        new(
            "src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs",
            9,
            MaterializationBoundary.SchedulerHandoff,
            "Freezes validated hierarchy partitions, capabilities, and packets before Job handoff."),
        new(
            "src/SomeEngine.ECS.Systems/JobEntity.cs",
            1,
            MaterializationBoundary.ImmutableDescriptor,
            "Takes ownership of a normalized generated-access descriptor at construction."),
        new(
            "src/SomeEngine.ECS.Systems/JobEntityRuntime.cs",
            3,
            MaterializationBoundary.SchedulerHandoff,
            "Freezes stable packet ranges, packets, and declared accesses before scheduling."),
        new(
            "src/SomeEngine.ECS.Systems/TopologyPacketFinalizer.cs",
            1,
            MaterializationBoundary.SchedulerHandoff,
            "Publishes the final validated topology packet set to the scheduler."),
        new(
            "src/SomeEngine.ECS/Archetypes/Archetype.cs",
            7,
            MaterializationBoundary.ImmutableOwner,
            "Builds the immutable component, column, tag, enableable, cleanup, and shared layouts."),
        new(
            "src/SomeEngine.ECS/Archetypes/SharedComponentTuple.cs",
            1,
            MaterializationBoundary.ImmutableOwner,
            "Takes ownership of one immutable shared-component tuple used as a stable key."),
        new(
            "src/SomeEngine.ECS/BundleSpawnMap.cs",
            1,
            MaterializationBoundary.ImmutableOwner,
            "Takes ownership of a normalized bundle component layout."),
        new(
            "src/SomeEngine.ECS/Indexing/ComponentIndex.cs",
            1,
            MaterializationBoundary.SnapshotPublication,
            "Publishes one immutable index generation that can back retained read-only spans."),
        new(
            "src/SomeEngine.ECS/Owners.Copy.cs",
            1,
            MaterializationBoundary.ImmutableOwner,
            "Builds the owned destination archetype shape for an entity-copy operation."),
        new(
            "src/SomeEngine.ECS/Owners.Hierarchy.cs",
            5,
            MaterializationBoundary.TopologyTransaction,
            "Freezes ordered permutations, maintenance plans, destroy plans, and rollback worklists."),
        new(
            "src/SomeEngine.ECS/Owners.Hierarchy.Storage.cs",
            5,
            MaterializationBoundary.SnapshotPublication,
            "Detaches writable hierarchy shards and publishes immutable child generations."),
        new(
            "src/SomeEngine.ECS/Owners.RelationGraph.EndpointTracking.cs",
            1,
            MaterializationBoundary.TopologyTransaction,
            "Freezes and orders relation endpoint preimages for deterministic rollback."),
        new(
            "src/SomeEngine.ECS/Queries/QueryDefinition.cs",
            3,
            MaterializationBoundary.ImmutableDescriptor,
            "Takes ownership of normalized query terms and compiled storage capabilities."),
        new(
            "src/SomeEngine.ECS/Queries/QueryState.cs",
            5,
            MaterializationBoundary.ImmutableDescriptor,
            "Compiles an archetype match once; row and chunk enumeration only borrow its arrays."),
        new(
            "src/SomeEngine.ECS/Relations/RelationGeneration.Mutation.cs",
            2,
            MaterializationBoundary.TopologyTransaction,
            "Creates replacement adjacency shards at topology copy-on-write publication points."),
        new(
            "src/SomeEngine.ECS/Relations/RelationTypeState.Queries.cs",
            1,
            MaterializationBoundary.TopologyTransaction,
            "Freezes the small deterministic affected-shard set used by a relation mutation."),
        new(
            "src/SomeEngine.ECS/Relations/RelationTypeState.Support.cs",
            1,
            MaterializationBoundary.TopologyTransaction,
            "Freezes a mutable relation shard into its immutable published generation."),
        new(
            "src/SomeEngine.ECS/Relations/RelationTypeState.Tracking.cs",
            1,
            MaterializationBoundary.TopologyTransaction,
            "Freezes and orders the live-edge worklist used to reconcile a topology transaction."),
    ];

    [Fact]
    public void ToArrayCallsExistOnlyAtReviewedMaterializationBoundaries()
    {
        string repositoryRoot = FindRepositoryRoot();
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string sourceRoot in SourceRoots)
        {
            string fullRoot = ToFullPath(repositoryRoot, sourceRoot);
            foreach (string fullPath in Directory.EnumerateFiles(
                         fullRoot,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                string relativePath = NormalizeRelativePath(
                    Path.GetRelativePath(repositoryRoot, fullPath));
                if (ContainsBuildOutputSegment(relativePath))
                    continue;

                int count = ToArrayCall().Matches(File.ReadAllText(fullPath)).Count;
                if (count != 0)
                    actual.Add(relativePath, count);
            }
        }

        Dictionary<string, ReviewedMaterialization> reviewed =
            ReviewedMaterializations.ToDictionary(
                static item => item.RelativePath,
                StringComparer.Ordinal);
        Assert.All(
            ReviewedMaterializations,
            static item =>
            {
                Assert.True(item.ExpectedCalls > 0);
                Assert.False(string.IsNullOrWhiteSpace(item.Reason));
            });

        string[] unreviewed = actual.Keys
            .Except(reviewed.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            unreviewed.Length == 0,
            "Unreviewed runtime ToArray materialization(s):" +
            Environment.NewLine +
            string.Join(Environment.NewLine, unreviewed.Select(static path => " - " + path)));

        string[] stale = reviewed.Keys
            .Except(actual.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            stale.Length == 0,
            "Reviewed ToArray boundary no longer exists; remove or update its review entry:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, stale.Select(static path => " - " + path)));

        foreach ((string relativePath, int count) in actual)
        {
            ReviewedMaterialization expected = reviewed[relativePath];
            Assert.True(
                count == expected.ExpectedCalls,
                $"{relativePath} contains {count} ToArray call(s), but its reviewed " +
                $"{expected.Boundary} boundary permits exactly {expected.ExpectedCalls}. " +
                $"Review every changed materialization before updating the count. " +
                $"Boundary reason: {expected.Reason}");
        }
    }

    private static string FindRepositoryRoot()
    {
        string startingPath = Path.GetFullPath(AppContext.BaseDirectory);
        for (DirectoryInfo? directory = new(startingPath);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate SomeEngine.slnx while walking upward from " +
            $"'{startingPath}'. The materialization-boundary gate requires the repository root.");
    }

    private static bool ContainsBuildOutputSegment(string relativePath) =>
        relativePath.Split('/').Any(static segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static string ToFullPath(string repositoryRoot, string relativePath) =>
        Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    [GeneratedRegex(@"\.ToArray\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ToArrayCall();

    private enum MaterializationBoundary
    {
        ImmutableDescriptor,
        ImmutableOwner,
        SchedulerHandoff,
        SerializationPlan,
        SnapshotPublication,
        SourceGeneration,
        TopologyTransaction,
    }

    private readonly record struct ReviewedMaterialization(
        string RelativePath,
        int ExpectedCalls,
        MaterializationBoundary Boundary,
        string Reason);
}
