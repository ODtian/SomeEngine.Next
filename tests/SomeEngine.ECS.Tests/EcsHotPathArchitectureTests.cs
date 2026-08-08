namespace SomeEngine.ECS.Tests;

public sealed class EcsHotPathArchitectureTests
{
    private static readonly string[] HotAccessSourceFiles =
    [
        "src/SomeEngine.ECS/World.cs",
        "src/SomeEngine.ECS/World.JobAdmission.cs",
        "src/SomeEngine.ECS/World.Bundle.cs",
        "src/SomeEngine.ECS/World.Components.cs",
        "src/SomeEngine.ECS/World.DynamicBuffer.cs",
        "src/SomeEngine.ECS/World.Entities.cs",
        "src/SomeEngine.ECS/World.Iteration.cs",
        "src/SomeEngine.ECS/World.Queries.cs",
        "src/SomeEngine.ECS/World.SharedComponent.cs",
        "src/SomeEngine.ECS/World.Sparse.cs",
    ];

    private static readonly string[] RemovedCrossCuttingSerializationContexts =
    [
        "SerializationReadRoot",
        "SerializationValidationScope",
        "SerializationOutput",
        "FindSerializationReadRoot",
        "ThrowIfSerializationValidation",
        "EnterSerializationValidation",
        "s_serializationValidationDepth",
    ];

    [Fact]
    public void QueryMutationAndWriteAdmissionContainNoSerializationContext()
    {
        string repositoryRoot = FindRepositoryRoot();
        foreach (string relativePath in HotAccessSourceFiles)
        {
            string source = File.ReadAllText(ToFullPath(repositoryRoot, relativePath));
            Assert.DoesNotContain(
                "serialization",
                source,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CurrentStructureRootResolvesOnlyCandidateOrPublishedOwnership()
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = File.ReadAllText(
            ToFullPath(repositoryRoot, "src/SomeEngine.ECS/World.cs"));
        const string propertyMarker = "private WorldStructureRoot CurrentStructureRoot";
        const string nextMemberMarker = "internal WorldStructureRoot PublishedStructureRoot";
        int start = source.IndexOf(propertyMarker, StringComparison.Ordinal);
        int end = source.IndexOf(nextMemberMarker, start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "Could not isolate World.CurrentStructureRoot.");
        string property = source[start..end];
        Assert.Contains("_publishedStructure", property, StringComparison.Ordinal);
        Assert.Contains("t_candidateContext", property, StringComparison.Ordinal);
        Assert.DoesNotContain("serialization", property, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemovedSerializationContextsCannotReturn()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] sourceRoots =
        [
            "src/SomeEngine.ECS",
            "src/SomeEngine.ECS.Serialization",
        ];
        var violations = new List<string>();

        foreach (string sourceRoot in sourceRoots)
        {
            foreach (string fullPath in Directory.EnumerateFiles(
                         ToFullPath(repositoryRoot, sourceRoot),
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                string relativePath = NormalizeRelativePath(
                    Path.GetRelativePath(repositoryRoot, fullPath));
                if (ContainsBuildOutputSegment(relativePath))
                    continue;

                string source = File.ReadAllText(fullPath);
                foreach (string removedName in RemovedCrossCuttingSerializationContexts)
                {
                    if (source.Contains(removedName, StringComparison.Ordinal))
                        violations.Add($"{relativePath}: {removedName}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Cross-cutting serialization state returned to ECS access paths:" +
            Environment.NewLine +
            string.Join(
                Environment.NewLine,
                violations.Order(StringComparer.Ordinal).Select(static item => " - " + item)));
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
            $"'{startingPath}'. The ECS hot-path architecture gate requires the repository root.");
    }

    private static bool ContainsBuildOutputSegment(string relativePath) =>
        relativePath.Split('/').Any(static segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static string ToFullPath(string repositoryRoot, string relativePath) =>
        Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');
}
