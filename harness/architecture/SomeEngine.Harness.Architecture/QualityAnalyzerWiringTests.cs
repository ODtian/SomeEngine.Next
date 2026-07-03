using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class QualityAnalyzerWiringTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();
    private static readonly string[] HardQualityRuleIds =
    [
        "SE001",
        "SE002",
        "SE020",
        "SE021",
        "SE022",
        "SE023",
        "SE024",
        "SE030",
    ];

    private static readonly string[] StyleQualityRuleIds =
    [
        "SE010",
        "SE031",
        "SE052",
    ];

    private static readonly string[] HardQualityCategories =
    [
        "Naming",
        "Complexity",
        "Safety",
    ];

    private static readonly string[] StyleQualityCategories =
    [
        "Style",
        "Safety",
        "Naming",
    ];

    private static readonly HashSet<string> HardQualityRuleIdSet = new(HardQualityRuleIds, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> StyleQualityRuleIdSet = new(StyleQualityRuleIds, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ProductProjectsReceiveQualityAnalyzerDuringBuild()
    {
        var targetsPath = Path.Combine(HarnessConfig.ResolveRepoRoot(), "Directory.Build.targets");
        Assert.True(File.Exists(targetsPath), "Directory.Build.targets must exist at repository root");

        var document = XDocument.Load(targetsPath);
        var analyzerProject = document.Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "SomeEngineQualityAnalyzerProject")
            ?.Value ?? "";

        Assert.EndsWith(
            "harness\\quality\\SomeEngine.Harness.QualityAnalyzer\\SomeEngine.Harness.QualityAnalyzer.csproj",
            analyzerProject);

        var analyzerReference = document.Descendants()
            .SingleOrDefault(element =>
                element.Name.LocalName == "ProjectReference"
                && element.Attribute("Include")?.Value == "$(SomeEngineQualityAnalyzerProject)"
                && element.Attribute("OutputItemType")?.Value == "Analyzer"
                && element.Attribute("ReferenceOutputAssembly")?.Value == "false");

        Assert.NotNull(analyzerReference);

        var additionalFiles = document.Descendants()
            .Where(element => element.Name.LocalName == "AdditionalFiles")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .ToArray();

        Assert.Contains(additionalFiles, include => include.EndsWith("harness\\config.json"));

        var itemGroupCondition = analyzerReference!.Parent?.Attribute("Condition")?.Value ?? "";
        Assert.Contains("SomeEngineQualityAnalyzerEnabled", itemGroupCondition);
    }

    [Fact]
    public void RepositoryBuildKeepsAnalyzerWarningsHard()
    {
        var propsPath = Path.Combine(HarnessConfig.ResolveRepoRoot(), "Directory.Build.props");
        Assert.True(File.Exists(propsPath), "Directory.Build.props must exist at repository root.");

        XDocument document = XDocument.Load(propsPath);
        bool treatsWarningsAsErrors = document.Descendants()
            .Where(element => element.Name.LocalName == "TreatWarningsAsErrors")
            .Select(element => element.Value.Trim())
            .Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

        Assert.True(
            treatsWarningsAsErrors,
            "Repository build props must keep TreatWarningsAsErrors=true so hard quality analyzer diagnostics fail the hard build.");
    }

    [Fact]
    public void DeclaredProductProjectsCannotOptOutOfQualityAnalyzer()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects)
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(projectPath))
            {
                failures.Add($"{project.Name} project file is missing at {project.Path}.");
                continue;
            }

            foreach (string optOutFile in QualityAnalyzerOptOutDeclarations(projectPath, repoRoot))
            {
                failures.Add($"{project.Name} is a first-round product project and must not set SomeEngineQualityAnalyzerOptOut=true in {RelativePath(repoRoot, optOutFile)}.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared product projects must receive the hard quality analyzer when the harness enables it:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredBuildSupportQualityAnalyzerOptOutsArePinned()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();
        var actualOptOuts = new List<string>();

        foreach (ProjectConfig project in Config.Projects.BuildSupportProjects)
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(projectPath))
            {
                failures.Add($"{project.Name} project file is missing at {project.Path}.");
                continue;
            }

            if (QualityAnalyzerOptOutDeclarations(projectPath, repoRoot).Any())
            {
                actualOptOuts.Add(project.Name);
            }
        }

        var expectedOptOuts = new[]
        {
            "SomeEngine.ECS.SourceGen",
            "SomeEngine.Generators",
        };

        var missing = expectedOptOuts
            .Except(actualOptOuts, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var extra = actualOptOuts
            .Except(expectedOptOuts, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        foreach (string item in missing)
        {
            failures.Add($"{item} build-support quality analyzer opt-out is no longer explicit.");
        }

        foreach (string item in extra)
        {
            failures.Add($"{item} is an unaccepted build-support quality analyzer opt-out.");
        }

        Assert.True(
            failures.Count == 0,
            "Build-support quality analyzer opt-outs must stay explicit and limited to the accepted build-support generator projects:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void QualityAnalyzerOptOutScanIncludesProjectLocalPropsTargetsAndRootBuildDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessQualityAnalyzerWiring", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectPath = Path.Combine(tempRoot, "SomeEngine.Sample.csproj");
            string buildDirectory = Path.Combine(tempRoot, "build");
            Directory.CreateDirectory(buildDirectory);

            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                Path.Combine(buildDirectory, "OptOut.props"),
                """
                <Project>
                  <PropertyGroup>
                    <SomeEngineQualityAnalyzerOptOut>true</SomeEngineQualityAnalyzerOptOut>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "OptOut.targets"),
                """
                <Project>
                  <PropertyGroup>
                    <SomeEngineQualityAnalyzerOptOut>true</SomeEngineQualityAnalyzerOptOut>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.props"),
                """
                <Project>
                  <PropertyGroup>
                    <SomeEngineQualityAnalyzerOptOut>true</SomeEngineQualityAnalyzerOptOut>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Packages.props"),
                """
                <Project>
                  <PropertyGroup>
                    <SomeEngineQualityAnalyzerOptOut>true</SomeEngineQualityAnalyzerOptOut>
                  </PropertyGroup>
                </Project>
                """);

            string[] files = QualityAnalyzerOptOutDeclarations(projectPath, tempRoot)
                .Select(file => Path.GetFileName(file)!)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "Directory.Build.props",
                    "Directory.Packages.props",
                    "OptOut.props",
                    "OptOut.targets",
                ],
                files);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DeclaredProductProjectsDoNotDisableQualityAnalyzerExecution()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (string filePath in EnumerateProductQualityConfigurationFiles(repoRoot))
        {
            InspectMsBuildAnalyzerExecutionConfiguration(filePath, repoRoot, failures);
        }

        Assert.True(
            failures.Count == 0,
            "First-round product projects must not disable analyzer execution:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredProductProjectsDoNotSuppressHardQualityRules()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (string filePath in EnumerateProductQualityConfigurationFiles(repoRoot))
        {
            InspectMsBuildQualityConfiguration(filePath, repoRoot, failures);
        }

        foreach (string filePath in EnumerateProductEditorConfigFiles(repoRoot))
        {
            InspectEditorConfigQualityConfiguration(filePath, repoRoot, failures);
        }

        Assert.True(
            failures.Count == 0,
            "First-round product hard quality rules must stay visible and hard:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredProductProjectsDoNotSuppressWarningQualityRules()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (string filePath in EnumerateProductQualityConfigurationFiles(repoRoot))
        {
            InspectMsBuildWarningQualityConfiguration(filePath, repoRoot, failures);
        }

        foreach (string filePath in EnumerateProductEditorConfigFiles(repoRoot))
        {
            InspectEditorConfigWarningQualityConfiguration(filePath, repoRoot, failures);
        }

        Assert.True(
            failures.Count == 0,
            "First-round product style quality rules must stay visible in the warning bucket:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredProductProjectsDoNotRemoveQualityAnalyzerInputs()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects)
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(projectPath))
            {
                failures.Add($"{project.Name} project file is missing at {project.Path}.");
                continue;
            }

            foreach ((string filePath, string itemName, string removedValue) in QualityAnalyzerInputRemovalDeclarations(projectPath, repoRoot))
            {
                failures.Add($"{project.Name} removes quality analyzer input {itemName} Remove=\"{removedValue}\" in {RelativePath(repoRoot, filePath)}.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared product projects must not remove the analyzer or harness config inputs supplied by the quality harness:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void QualityAnalyzerInputRemovalScanIncludesProjectLocalPropsTargetsAndRootBuildDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessQualityAnalyzerInputRemoval", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectPath = Path.Combine(tempRoot, "SomeEngine.Sample.csproj");
            string buildDirectory = Path.Combine(tempRoot, "build");
            Directory.CreateDirectory(buildDirectory);

            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Analyzer Remove="$(SomeEngineQualityAnalyzerProject)" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(buildDirectory, "Remove.props"),
                """
                <Project>
                  <ItemGroup>
                    <AdditionalFiles Remove="$(SomeEngineRepositoryRoot)harness\config.json" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.targets"),
                """
                <Project>
                  <ItemGroup>
                    <Analyzer Remove="@(Analyzer)" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Packages.props"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Remove="$(SomeEngineQualityAnalyzerProject)" />
                  </ItemGroup>
                </Project>
                """);

            string[] files = QualityAnalyzerInputRemovalDeclarations(projectPath, tempRoot)
                .Select(removal => Path.GetFileName(removal.FilePath)!)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "Directory.Build.targets",
                    "Directory.Packages.props",
                    "Remove.props",
                    "SomeEngine.Sample.csproj",
                ],
                files);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static IEnumerable<string> QualityAnalyzerOptOutDeclarations(string projectPath, string repoRoot)
        => EnumerateQualityConfigurationFilesForProject(projectPath, repoRoot)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(FileSetsQualityAnalyzerOptOut);

    private static IEnumerable<QualityAnalyzerInputRemoval> QualityAnalyzerInputRemovalDeclarations(string projectPath, string repoRoot)
    {
        foreach (string filePath in EnumerateQualityConfigurationFilesForProject(projectPath, repoRoot)
                     .Where(File.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            XDocument document = XDocument.Load(filePath);
            foreach (XElement element in document.Descendants())
            {
                string localName = element.Name.LocalName;
                if (!string.Equals(localName, "Analyzer", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(localName, "AdditionalFiles", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(localName, "ProjectReference", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? remove = element.Attribute("Remove")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(remove))
                {
                    continue;
                }

                if (string.Equals(localName, "ProjectReference", StringComparison.OrdinalIgnoreCase)
                    && !remove.Contains("SomeEngineQualityAnalyzer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new QualityAnalyzerInputRemoval(filePath, localName, remove);
            }
        }
    }

    private static bool FileSetsQualityAnalyzerOptOut(string filePath)
    {
        XDocument document = XDocument.Load(filePath);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "SomeEngineQualityAnalyzerOptOut")
            .Select(element => element.Value.Trim())
            .Any(value => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateProductQualityConfigurationFiles(string repoRoot)
    {
        var files = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects)
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            files.AddRange(EnumerateQualityConfigurationFilesForProject(projectPath, repoRoot));
        }

        return files
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
    }

    private static IEnumerable<string> EnumerateQualityConfigurationFilesForProject(string projectPath, string repoRoot)
    {
        yield return Path.Combine(repoRoot, "Directory.Build.props");
        yield return Path.Combine(repoRoot, "Directory.Build.targets");
        yield return Path.Combine(repoRoot, "Directory.Packages.props");
        yield return projectPath;

        string? projectDirectory = Path.GetDirectoryName(projectPath);
        if (projectDirectory is null || !Directory.Exists(projectDirectory))
        {
            yield break;
        }

        foreach (string path in Directory
                     .EnumerateFiles(projectDirectory, "*.*", SearchOption.AllDirectories)
                     .Where(path =>
                         !IsUnderBuildOutputDirectory(path)
                         && (path.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                             || path.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            yield return path;
        }
    }

    private static IEnumerable<string> EnumerateProductEditorConfigFiles(string repoRoot)
    {
        var files = new List<string>
        {
            Path.Combine(repoRoot, ".editorconfig"),
            Path.Combine(repoRoot, ".globalconfig"),
        };

        foreach (ProjectConfig project in Config.Projects.ProductProjects)
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            string? projectDirectory = Path.GetDirectoryName(projectPath);
            if (projectDirectory is null || !Directory.Exists(projectDirectory))
            {
                continue;
            }

            files.AddRange(Directory
                .EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
                .Where(path =>
                    !IsUnderBuildOutputDirectory(path)
                    && (string.Equals(Path.GetFileName(path), ".editorconfig", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(path), ".globalconfig", StringComparison.OrdinalIgnoreCase))));
        }

        return files
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
    }

    private static void InspectMsBuildQualityConfiguration(string filePath, string repoRoot, List<string> failures)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(filePath);
        }
        catch (Exception ex)
        {
            failures.Add($"{RelativePath(repoRoot, filePath)} is not valid MSBuild XML: {ex.Message}");
            return;
        }

        foreach (XElement element in document.Descendants())
        {
            string localName = element.Name.LocalName;
            string value = element.Value.Trim();

            if (string.Equals(localName, "NoWarn", StringComparison.OrdinalIgnoreCase)
                || string.Equals(localName, "WarningsNotAsErrors", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string ruleId in ExplicitHardRuleIds(value))
                {
                    failures.Add($"{RelativePath(repoRoot, filePath)} {localName} suppresses hard quality rule {ruleId}.");
                }
            }

            if (string.Equals(localName, "TreatWarningsAsErrors", StringComparison.OrdinalIgnoreCase)
                && string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{RelativePath(repoRoot, filePath)} sets TreatWarningsAsErrors=false for first-round product quality.");
            }
        }
    }

    private static void InspectMsBuildAnalyzerExecutionConfiguration(string filePath, string repoRoot, List<string> failures)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(filePath);
        }
        catch (Exception ex)
        {
            failures.Add($"{RelativePath(repoRoot, filePath)} is not valid MSBuild XML: {ex.Message}");
            return;
        }

        foreach (XElement element in document.Descendants())
        {
            string localName = element.Name.LocalName;
            string value = element.Value.Trim();

            if ((string.Equals(localName, "RunAnalyzers", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(localName, "RunAnalyzersDuringBuild", StringComparison.OrdinalIgnoreCase))
                && string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{RelativePath(repoRoot, filePath)} sets {localName}=false for first-round product quality.");
            }

            if (string.Equals(localName, "SkipAnalyzers", StringComparison.OrdinalIgnoreCase)
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{RelativePath(repoRoot, filePath)} sets SkipAnalyzers=true for first-round product quality.");
            }
        }
    }

    private static void InspectMsBuildWarningQualityConfiguration(string filePath, string repoRoot, List<string> failures)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(filePath);
        }
        catch (Exception ex)
        {
            failures.Add($"{RelativePath(repoRoot, filePath)} is not valid MSBuild XML: {ex.Message}");
            return;
        }

        foreach (XElement element in document.Descendants())
        {
            string localName = element.Name.LocalName;
            if (!string.Equals(localName, "NoWarn", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(localName, "WarningsNotAsErrors", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string ruleId in ExplicitStyleRuleIds(element.Value.Trim()))
            {
                failures.Add($"{RelativePath(repoRoot, filePath)} {localName} suppresses warning quality rule {ruleId}.");
            }
        }
    }

    private static void InspectEditorConfigQualityConfiguration(string filePath, string repoRoot, List<string> failures)
    {
        int lineNumber = 0;
        foreach (string line in File.ReadLines(filePath))
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            int equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            string key = trimmed[..equalsIndex].Trim();
            string value = TrimEditorConfigComment(trimmed[(equalsIndex + 1)..]).Trim();
            if (!IsNonHardAnalyzerSeverity(value))
            {
                continue;
            }

            if (IsBroadAnalyzerSeverityKeyForCategories(key, HardQualityCategories))
            {
                failures.Add($"{RelativePath(repoRoot, filePath)}:{lineNumber} lowers a hard quality analyzer category to {value}.");
            }

            foreach (string ruleId in HardQualityRuleIds)
            {
                if (string.Equals(key, $"dotnet_diagnostic.{ruleId}.severity", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{RelativePath(repoRoot, filePath)}:{lineNumber} lowers hard quality rule {ruleId} to {value}.");
                }
            }
        }
    }

    private static void InspectEditorConfigWarningQualityConfiguration(string filePath, string repoRoot, List<string> failures)
    {
        int lineNumber = 0;
        foreach (string line in File.ReadLines(filePath))
        {
            lineNumber++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            int equalsIndex = trimmed.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            string key = trimmed[..equalsIndex].Trim();
            string value = TrimEditorConfigComment(trimmed[(equalsIndex + 1)..]).Trim();
            if (!IsNonHardAnalyzerSeverity(value))
            {
                continue;
            }

            if (IsBroadAnalyzerSeverityKeyForCategories(key, StyleQualityCategories))
            {
                failures.Add($"{RelativePath(repoRoot, filePath)}:{lineNumber} lowers a warning quality analyzer category to {value}.");
            }

            foreach (string ruleId in StyleQualityRuleIds)
            {
                if (string.Equals(key, $"dotnet_diagnostic.{ruleId}.severity", StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{RelativePath(repoRoot, filePath)}:{lineNumber} lowers warning quality rule {ruleId} to {value}.");
                }
            }
        }
    }

    private static IEnumerable<string> ExplicitHardRuleIds(string value)
    {
        string[] tokens = value.Split(
            [';', ',', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);

        return tokens
            .Select(token => token.Trim())
            .Where(token => !token.StartsWith("$(", StringComparison.Ordinal))
            .Where(token => HardQualityRuleIdSet.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
    }

    private static IEnumerable<string> ExplicitStyleRuleIds(string value)
    {
        string[] tokens = value.Split(
            [';', ',', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);

        return tokens
            .Select(token => token.Trim())
            .Where(token => !token.StartsWith("$(", StringComparison.Ordinal))
            .Where(token => StyleQualityRuleIdSet.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
    }

    private static bool IsNonHardAnalyzerSeverity(string value)
    {
        return string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "silent", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "suggestion", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "refactoring", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBroadAnalyzerSeverityKeyForCategories(string key, IReadOnlyCollection<string> categories)
    {
        if (string.Equals(key, "dotnet_analyzer_diagnostic.severity", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string category in categories)
        {
            if (string.Equals(
                    key,
                    $"dotnet_analyzer_diagnostic.category-{category}.severity",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimEditorConfigComment(string value)
    {
        int hashIndex = value.IndexOf('#');
        int semicolonIndex = value.IndexOf(';');
        int commentIndex = hashIndex < 0
            ? semicolonIndex
            : semicolonIndex < 0
                ? hashIndex
                : Math.Min(hashIndex, semicolonIndex);

        return commentIndex < 0 ? value : value[..commentIndex];
    }

    private static bool IsUnderBuildOutputDirectory(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string RelativePath(string repoRoot, string filePath)
    {
        return Path.GetRelativePath(repoRoot, filePath).Replace(Path.DirectorySeparatorChar, '/');
    }

    private readonly record struct QualityAnalyzerInputRemoval(string FilePath, string ItemName, string RemovedValue);
}
