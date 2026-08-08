namespace SomeEngine.ECS.Tests;

public sealed class EcsSourceFileSizeGateTests
{
    private const int DefaultMaximumLines = 800;

    private static readonly string[] SourceRoots =
    [
        "src/SomeEngine.ECS",
        "src/SomeEngine.ECS.Serialization",
        "src/SomeEngine.ECS.Systems",
        "src/SomeEngine.ECS.SourceGen",
    ];

    // This compiled table is the reviewed-exception baseline. Its values are non-growth caps,
    // not targets: reducing a file is welcome, while raising a cap requires deliberate review.
    private static readonly ReviewedException[] ReviewedExceptions =
    [
        new("src/SomeEngine.ECS.Serialization/WorldSerializer.cs", 1339),
        new("src/SomeEngine.ECS/Owners.Hierarchy.cs", 2205),
        new("src/SomeEngine.ECS.Serialization/SerializationRegistry.cs", 1291),
        new("src/SomeEngine.ECS/Commands/CommandBuffer.Relations.cs", 1420),
        new("src/SomeEngine.ECS/Owners.RelationGraph.cs", 1244),
        new("src/SomeEngine.ECS.Systems/HierarchyPropagationAdapter.cs", 1342),
        new("src/SomeEngine.ECS/Commands/CommandBuffer.cs", 1246),
        new("src/SomeEngine.ECS/Owners.Bundles.cs", 1192),
        new("src/SomeEngine.ECS/Owners.Copy.cs", 1076),
        new("src/SomeEngine.ECS.SourceGen/SerializationGenerator.cs", 866),
        new("src/SomeEngine.ECS.Systems/JobEntityRuntime.cs", 854),
    ];

    [Fact]
    public void ReviewedExceptionBaselineIsCompleteAndSelfConsistent()
    {
        RepositorySnapshot snapshot = CaptureRepositorySnapshot();
        IReadOnlyList<string> failures = ValidateReviewedExceptionBaseline(snapshot);

        Assert.True(failures.Count == 0, FormatFailures(
            "The ECS source-size reviewed-exception baseline is invalid. Split coherent " +
            "responsibilities into focused types or files; do not mechanically compress source " +
            "text merely to satisfy this gate.",
            failures));
    }

    [Fact]
    public void EcsSourceFilesDoNotExceedReviewedLineBudgets()
    {
        RepositorySnapshot snapshot = CaptureRepositorySnapshot();
        List<string> failures = [.. ValidateReviewedExceptionBaseline(snapshot)];
        Dictionary<string, int> caps = ReviewedExceptions.ToDictionary(
            static exception => exception.RelativePath,
            static exception => exception.MaximumLines,
            StringComparer.Ordinal);

        foreach (SourceFile sourceFile in snapshot.SourceFiles)
        {
            bool reviewed = caps.TryGetValue(sourceFile.RelativePath, out int reviewedCap);
            int maximum = reviewed ? reviewedCap : DefaultMaximumLines;
            if (sourceFile.LineCount <= maximum)
                continue;

            failures.Add(reviewed
                ? $"{sourceFile.RelativePath} has {sourceFile.LineCount} lines and grew beyond its " +
                  $"reviewed non-growth cap of {maximum}."
                : $"{sourceFile.RelativePath} has {sourceFile.LineCount} lines, exceeds the default " +
                  $"limit of {DefaultMaximumLines}, and has no reviewed exception.");
        }

        Assert.True(failures.Count == 0, FormatFailures(
            "ECS source files exceeded their line budgets. Split coherent responsibilities into " +
            "focused types or files; do not mechanically compress formatting, merge statements, " +
            "or remove useful whitespace merely to satisfy this gate.",
            failures));
    }

    [Fact]
    public void CheckpointEnvelopeDoesNotReintroduceASecondWorldCodecOrCapturePlan()
    {
        string repositoryRoot = FindRepositoryRoot();
        string checkpoint = File.ReadAllText(ToFullPath(
            repositoryRoot,
            "src/SomeEngine.ECS.Serialization/WorldCheckpointCodec.cs"));

        Assert.DoesNotContain("WorldCapturePlan", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Manifest.ToArray", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteCanonicalStorage", checkpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteNativeRawStorage", checkpoint, StringComparison.Ordinal);
        Assert.Contains("WorldSerializer.WriteWorldCore", checkpoint, StringComparison.Ordinal);
    }

    private static List<string> ValidateReviewedExceptionBaseline(RepositorySnapshot snapshot)
    {
        var failures = new List<string>();
        string[] duplicates = ReviewedExceptions
            .GroupBy(static exception => exception.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicates.Length > 0)
            failures.Add("Duplicate reviewed-exception paths: " + string.Join(", ", duplicates));

        var reviewedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReviewedException exception in ReviewedExceptions)
        {
            if (!IsCanonicalSourcePath(exception.RelativePath))
            {
                failures.Add($"Reviewed-exception path '{exception.RelativePath}' is not a canonical " +
                    "forward-slash path beneath one of the configured ECS source roots.");
            }

            if (exception.MaximumLines <= DefaultMaximumLines)
            {
                failures.Add($"Reviewed exception '{exception.RelativePath}' has cap " +
                    $"{exception.MaximumLines}; exceptions are only valid above {DefaultMaximumLines}.");
            }

            reviewedPaths.Add(exception.RelativePath);
            if (!snapshot.FilesByPath.TryGetValue(exception.RelativePath, out SourceFile sourceFile))
            {
                failures.Add($"Reviewed-exception baseline file '{exception.RelativePath}' is missing, " +
                    "outside the configured roots, or excluded from the source scan. Remove or replace " +
                    "the entry only after confirming the responsibility split or move.");
                continue;
            }

            if (sourceFile.LineCount <= DefaultMaximumLines)
            {
                failures.Add($"Reviewed exception '{exception.RelativePath}' is now " +
                    $"{sourceFile.LineCount} lines. Remove this stale exception and keep the " +
                    "responsibility split that brought it below the default limit.");
            }
        }

        foreach (SourceFile sourceFile in snapshot.SourceFiles
                     .Where(static file => file.LineCount > DefaultMaximumLines))
        {
            if (!reviewedPaths.Contains(sourceFile.RelativePath))
            {
                failures.Add($"Oversized source file '{sourceFile.RelativePath}' " +
                    $"({sourceFile.LineCount} lines) is absent from the reviewed-exception baseline.");
            }
        }

        return failures;
    }

    private static RepositorySnapshot CaptureRepositorySnapshot()
    {
        string repositoryRoot = FindRepositoryRoot();
        var sourceFiles = new List<SourceFile>();
        foreach (string relativeRoot in SourceRoots)
        {
            string fullRoot = ToFullPath(repositoryRoot, relativeRoot);
            if (!Directory.Exists(fullRoot))
            {
                throw new DirectoryNotFoundException(
                    $"Configured ECS source root '{relativeRoot}' does not exist beneath " +
                    $"repository root '{repositoryRoot}'. The source-size gate cannot run safely.");
            }

            foreach (string fullPath in Directory.EnumerateFiles(
                         fullRoot,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                string relativePath = NormalizeRelativePath(
                    Path.GetRelativePath(repositoryRoot, fullPath));
                if (ContainsBuildOutputSegment(relativePath))
                    continue;

                sourceFiles.Add(new SourceFile(
                    relativePath,
                    File.ReadLines(fullPath).Count()));
            }
        }

        SourceFile[] orderedFiles = sourceFiles
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, SourceFile> filesByPath = orderedFiles.ToDictionary(
            static file => file.RelativePath,
            StringComparer.Ordinal);
        return new RepositorySnapshot(orderedFiles, filesByPath);
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
            $"Could not locate SomeEngine.slnx while walking upward from AppContext.BaseDirectory " +
            $"'{startingPath}'. The ECS source-size gate requires an unambiguous repository root.");
    }

    private static bool IsCanonicalSourcePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            !relativePath.EndsWith(".cs", StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = relativePath.Split('/');
        if (segments.Any(static segment => segment is "" or "." or ".."))
            return false;
        return SourceRoots.Any(root => relativePath.StartsWith(root + "/", StringComparison.Ordinal));
    }

    private static bool ContainsBuildOutputSegment(string relativePath) =>
        relativePath.Split('/').Any(static segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    private static string ToFullPath(string repositoryRoot, string relativePath) =>
        Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static string FormatFailures(string heading, IReadOnlyList<string> failures) =>
        heading + Environment.NewLine + string.Join(
            Environment.NewLine,
            failures.Select(static failure => " - " + failure));

    private readonly record struct ReviewedException(string RelativePath, int MaximumLines);

    private readonly record struct SourceFile(string RelativePath, int LineCount);

    private sealed record RepositorySnapshot(
        IReadOnlyList<SourceFile> SourceFiles,
        IReadOnlyDictionary<string, SourceFile> FilesByPath);
}
