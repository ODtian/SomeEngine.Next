using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class LayerDependencyTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void DeclaredProductProjectsRespectLayerContract()
    {
        var productsByName = Config.Projects.ProductProjects.ToDictionary(project => project.Name, StringComparer.Ordinal);
        var declaredProjectsByName = Config.Projects.ProductProjects
            .Concat(Config.Projects.BuildSupportProjects)
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
        var firstRoundLocalPackageNames = Config.ExternalDependencies.LocalPackages
            .Select(package => package.PackageId)
            .ToHashSet(StringComparer.Ordinal);
        var contract = Config.Architecture.LayerContract.AllowedDependencies;
        var failures = new List<string>();

        foreach (string productName in productsByName.Keys.Order(StringComparer.Ordinal))
        {
            if (!contract.ContainsKey(productName))
            {
                failures.Add($"Product project {productName} has no layer dependency contract");
            }
        }

        foreach (string contractName in contract.Keys.Order(StringComparer.Ordinal))
        {
            if (!declaredProjectsByName.ContainsKey(contractName))
            {
                failures.Add($"Layer contract declares {contractName}, but no product or build-support project fact exists for it");
            }
        }

        foreach (var entry in contract.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!declaredProjectsByName.TryGetValue(entry.Key, out var project))
            {
                continue;
            }

            string fullPath = Path.Combine(HarnessConfig.ResolveRepoRoot(), project.Path);
            if (!File.Exists(fullPath))
            {
                failures.Add($"{project.Name} must exist before layer dependencies can be checked");
                continue;
            }

            var allowedSet = entry.Value.ToHashSet(StringComparer.Ordinal);
            foreach (string reference in ParseAssemblyProjectReferencesFromDeclarationFiles(fullPath)
                         .Concat(ParseDeclaredLocalPackageReferencesFromDeclarationFiles(fullPath, firstRoundLocalPackageNames)))
            {
                if (!allowedSet.Contains(reference))
                {
                    failures.Add($"{project.Name} references {reference}, which is not in its declared layer dependency contract");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared product projects do not satisfy layer contracts:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredExternalDependencyConsumersArePinnedToLayerContract()
    {
        var declaredProjectsByPath = Config.Projects.ProductProjects
            .Concat(Config.Projects.BuildSupportProjects)
            .ToDictionary(project => Normalize(project.Path), project => project.Name, StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (SourceProjectDependencyConfig dependency in Config.ExternalDependencies.SourceProjects)
        {
            RequireLayerDependencyForConsumer(declaredProjectsByPath, dependency.ConsumerProject, dependency.Name, failures);
        }

        foreach (LocalPackageConfig package in Config.ExternalDependencies.LocalPackages)
        {
            RequireLayerDependencyForConsumer(declaredProjectsByPath, package.ConsumerProject, package.PackageId, failures);
        }

        Assert.True(
            failures.Count == 0,
            "Declared first-round external dependency consumers must be pinned in layer contracts:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void LayerContractDoesNotAllowExcludedProjectDependencies()
    {
        var excludedProjectNames = Config.Architecture.ExcludedProjectNames.ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var entry in Config.Architecture.LayerContract.AllowedDependencies.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            foreach (string dependency in entry.Value.Order(StringComparer.Ordinal))
            {
                if (excludedProjectNames.Contains(dependency))
                {
                    failures.Add($"{entry.Key} layer contract allows excluded dependency {dependency}.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Layer contracts must not preserve excluded first-round dependencies:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void LayerContractDoesNotAllowForbiddenBoundaryDependencies()
    {
        var forbiddenReferences = Config.Architecture.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (var entry in Config.Architecture.LayerContract.AllowedDependencies.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            foreach (string dependency in entry.Value.Order(StringComparer.Ordinal))
            {
                foreach (string forbidden in forbiddenReferences)
                {
                    if (ContainsForbiddenBoundaryToken(dependency, forbidden))
                    {
                        failures.Add($"{entry.Key} layer contract allows excluded first-round dependency {dependency} via token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Layer contracts allow excluded first-round dependency names:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void CompiledProductAssembliesRespectLayerContract()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var productsByName = Config.Projects.ProductProjects.ToDictionary(project => project.Name, StringComparer.Ordinal);
        var productNames = productsByName.Keys.ToHashSet(StringComparer.Ordinal);
        var externalNames = Config.ExternalDependencies.SourceProjects
            .Select(dependency => dependency.Name)
            .Concat(Config.ExternalDependencies.LocalPackages.Select(package => package.PackageId))
            .ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach ((string projectName, List<string> allowedDependencies) in Config.Architecture.LayerContract.AllowedDependencies.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!productsByName.TryGetValue(projectName, out ProjectConfig? project))
            {
                continue;
            }

            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{projectName} must be built before compiled layer dependencies can be checked.");
                continue;
            }

            var allowedSet = allowedDependencies.ToHashSet(StringComparer.Ordinal);

            foreach (string reference in ReadAssemblyReferences(assemblyPath))
            {
                if (!productNames.Contains(reference) && !externalNames.Contains(reference))
                {
                    continue;
                }

                if (!allowedSet.Contains(reference))
                {
                    failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} references {reference}, which is not in {projectName}'s accepted layer dependency contract.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Compiled product assemblies do not satisfy layer contracts:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void LayerContractReferenceParsingIncludesProjectLocalPropsAndTargets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessLayerDependencyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectPath = Path.Combine(tempRoot, "SomeEngine.Sample.csproj");
            string importedDirectory = Path.Combine(tempRoot, "build");
            Directory.CreateDirectory(importedDirectory);

            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.Direct\SomeEngine.Direct.csproj" />
                    <PackageReference Include="SomeEngine.Local.Direct" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(importedDirectory, "Injected.props"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.HiddenProps\SomeEngine.HiddenProps.csproj" />
                    <PackageReference Include="SomeEngine.Local.Props" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Injected.targets"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.HiddenTargets\SomeEngine.HiddenTargets.csproj" />
                    <PackageReference Include="SomeEngine.Local.Targets" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.props"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.RootProps\SomeEngine.RootProps.csproj" />
                    <PackageReference Include="SomeEngine.Local.RootProps" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.targets"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.RootTargets\SomeEngine.RootTargets.csproj" ReferenceOutputAssembly="False" />
                  </ItemGroup>
                </Project>
                """);

            var packageIds = new HashSet<string>(StringComparer.Ordinal)
            {
                "SomeEngine.Local.Direct",
                "SomeEngine.Local.Props",
                "SomeEngine.Local.Targets",
                "SomeEngine.Local.RootProps",
            };

            AssertSet(
                [
                    "SomeEngine.Direct",
                    "SomeEngine.HiddenProps",
                    "SomeEngine.HiddenTargets",
                    "SomeEngine.RootProps",
                ],
                ParseAssemblyProjectReferencesFromDeclarationFiles(projectPath, tempRoot));

            AssertSet(
                [
                    "SomeEngine.Local.Direct",
                    "SomeEngine.Local.Props",
                    "SomeEngine.Local.Targets",
                    "SomeEngine.Local.RootProps",
                ],
                ParseDeclaredLocalPackageReferencesFromDeclarationFiles(projectPath, packageIds, tempRoot));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    internal static List<string> ParseAssemblyProjectReferences(string projectPath)
    {
        XDocument document = XDocument.Load(projectPath);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Where(element => !string.Equals(
                element.Attribute("ReferenceOutputAssembly")?.Value,
                "false",
                StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')))
            .ToList();
    }

    internal static List<string> ParseDeclaredLocalPackageReferences(string projectPath, HashSet<string> packageIds)
    {
        XDocument document = XDocument.Load(projectPath);
        return document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .Where(include => packageIds.Contains(include))
            .ToList();
    }

    internal static List<string> ParseAssemblyProjectReferencesFromDeclarationFiles(
        string projectPath,
        string? commonDeclarationRoot = null)
        => ProjectReferenceDeclarationFiles(projectPath, commonDeclarationRoot)
            .SelectMany(ParseAssemblyProjectReferences)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    internal static List<string> ParseDeclaredLocalPackageReferencesFromDeclarationFiles(
        string projectPath,
        HashSet<string> packageIds,
        string? commonDeclarationRoot = null)
        => ProjectReferenceDeclarationFiles(projectPath, commonDeclarationRoot)
            .SelectMany(file => ParseDeclaredLocalPackageReferences(file, packageIds))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static IEnumerable<string> ProjectReferenceDeclarationFiles(string projectPath, string? commonDeclarationRoot = null)
    {
        yield return projectPath;

        string projectDirectory = Path.GetDirectoryName(projectPath) ?? "";
        if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path) is ".props" or ".targets")
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            yield return file;
        }

        string? root = commonDeclarationRoot ?? CommonDeclarationRootForProject(projectPath);
        if (root is null)
        {
            yield break;
        }

        foreach (string file in CommonProjectDeclarationFiles(root))
        {
            yield return file;
        }
    }

    private static string? CommonDeclarationRootForProject(string projectPath)
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        string relative = Path.GetRelativePath(repoRoot, Path.GetFullPath(projectPath));
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return repoRoot;
    }

    private static IEnumerable<string> CommonProjectDeclarationFiles(string root)
    {
        foreach (string fileName in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            string path = Path.Combine(root, fileName);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static void AssertSet(string[] expected, IEnumerable<string> actual)
    {
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            actual.Order(StringComparer.Ordinal));
    }

    private static void RequireLayerDependencyForConsumer(
        IReadOnlyDictionary<string, string> declaredProjectsByPath,
        string consumerProjectPath,
        string dependencyName,
        List<string> failures)
    {
        if (!declaredProjectsByPath.TryGetValue(Normalize(consumerProjectPath), out string? consumerName))
        {
            failures.Add($"{consumerProjectPath} consumes first-round external dependency {dependencyName}, but is not a declared product or build-support project.");
            return;
        }

        if (!Config.Architecture.LayerContract.AllowedDependencies.TryGetValue(consumerName, out List<string>? allowedDependencies))
        {
            failures.Add($"{consumerName} consumes first-round external dependency {dependencyName}, but has no layer dependency contract.");
            return;
        }

        if (!allowedDependencies.Contains(dependencyName, StringComparer.Ordinal))
        {
            failures.Add($"{consumerName} consumes first-round external dependency {dependencyName}, but the layer contract does not allow it.");
        }
    }

    private static string? FindBuiltAssembly(string repoRoot, ProjectConfig project)
    {
        string projectDirectory = Path.Combine(repoRoot, Path.GetDirectoryName(project.Path) ?? "");
        if (!Directory.Exists(projectDirectory))
        {
            return null;
        }

        return Directory.GetFiles(projectDirectory, $"{project.Name}.dll", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IEnumerable<string> ReadAssemblyReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();

        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            AssemblyReference reference = metadata.GetAssemblyReference(handle);
            yield return metadata.GetString(reference.Name);
        }
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/');

    private static bool ContainsForbiddenBoundaryToken(string text, string token)
    {
        if (RequiresExactIdentifierMatch(token))
        {
            return ContainsExactIdentifier(text, token, ExactTokenComparison(token));
        }

        return text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresExactIdentifierMatch(string token)
        => token is "Present" or "Window" or "Windowing" or "Rhi" or "SharpGen";

    private static StringComparison ExactTokenComparison(string token)
        => token is "Present" or "Window"
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    private static bool ContainsExactIdentifier(string text, string token, StringComparison comparison)
    {
        int startIndex = 0;
        while (startIndex < text.Length)
        {
            int index = text.IndexOf(token, startIndex, comparison);
            if (index < 0)
            {
                return false;
            }

            int after = index + token.Length;
            bool startsAtIdentifierBoundary = IsTokenBoundaryBefore(text, index);
            bool endsAtIdentifierBoundary = IsTokenBoundaryAfter(text, after);
            if (startsAtIdentifierBoundary && endsAtIdentifierBoundary)
            {
                return true;
            }

            startIndex = index + token.Length;
        }

        return false;
    }

    private static bool IsIdentifierCharacter(char character)
        => char.IsLetterOrDigit(character) || character == '_';

    private static bool IsTokenBoundaryBefore(string text, int index)
        => index <= 0
           || !IsIdentifierCharacter(text[index - 1])
           || char.IsUpper(text[index]);

    private static bool IsTokenBoundaryAfter(string text, int index)
        => index >= text.Length
           || !IsIdentifierCharacter(text[index])
           || char.IsUpper(text[index]);
}
