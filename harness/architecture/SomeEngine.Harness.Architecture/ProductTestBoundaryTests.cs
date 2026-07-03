using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed partial class ProductTestBoundaryTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void ExactProductTestBoundaryTokensCatchPrefixedBackendNamesWithoutCatchingLowercaseWords()
    {
        Assert.True(ContainsForbiddenBoundaryToken("private IRhiDevice? device;", "Rhi"));
        Assert.True(ContainsForbiddenBoundaryToken("DiligentSharpGenBinding", "SharpGen"));
        Assert.True(ContainsForbiddenBoundaryToken("CreateWindow", "Window"));
        Assert.True(ContainsForbiddenBoundaryToken("PackageReference Include=\"Some.Windowing\"", "Windowing"));
        Assert.True(ContainsForbiddenBoundaryToken("PackageReference Include=\"some.windowing\"", "Windowing"));
        Assert.True(ContainsForbiddenBoundaryToken("SwapchainPresent", "Present"));

        Assert.False(ContainsForbiddenBoundaryToken("bool present = false;", "Present"));
        Assert.False(ContainsForbiddenBoundaryToken("string message = \"window not configured\";", "Window"));
    }

    [Fact]
    public void CommonIntegrationTestWordsAreScopedToRenderAndAssetBoundaryProjects()
    {
        string[] references = ["Present", "Window", "SomeEngine.Rhi"];

        var ecsReferences = TestForbiddenReferencesForProject(
                new ProjectConfig { Name = "SomeEngine.ECS.Tests", Path = "tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj" },
                references)
            .ToArray();

        Assert.DoesNotContain("Present", ecsReferences);
        Assert.DoesNotContain("Window", ecsReferences);
        Assert.Contains("SomeEngine.Rhi", ecsReferences);

        var renderReferences = TestForbiddenReferencesForProject(
                new ProjectConfig { Name = "SomeEngine.Render.Tests", Path = "tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj" },
                references)
            .ToArray();

        Assert.Contains("Present", renderReferences);
        Assert.Contains("Window", renderReferences);
    }

    [Fact]
    public void DeclaredProductTestsDoNotRequireExcludedBackendOrUiContracts()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var forbiddenReferences = Config.ProductTests.ForbiddenBoundaryReferences
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            string projectDirectory = Path.GetDirectoryName(projectPath) ?? repoRoot;
            if (!Directory.Exists(projectDirectory))
            {
                failures.Add($"{project.Name} directory does not exist at {projectDirectory}.");
                continue;
            }

            foreach (string file in TestContractFiles(projectDirectory))
            {
                string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                string text = File.ReadAllText(file);
                foreach (string forbidden in TestForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (ContainsForbiddenBoundaryToken(relative, forbidden)
                        || ContainsForbiddenBoundaryToken(text, forbidden))
                    {
                        failures.Add($"{relative} requires excluded first-round boundary token '{forbidden}'.");
                    }
                }
            }
        }

        foreach (string file in CommonTestBoundaryFiles(repoRoot))
        {
            string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            string text = File.ReadAllText(file);
            foreach (string forbidden in forbiddenReferences)
            {
                if (ContainsForbiddenBoundaryToken(relative, forbidden)
                    || ContainsForbiddenBoundaryToken(text, forbidden))
                {
                    failures.Add($"{relative} requires excluded first-round boundary token '{forbidden}'.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared first-round product tests require excluded backend/UI contracts:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredDomainProductTestsDoNotRequireDomainExcludedContracts()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var boundariesByName = Config.Architecture.DomainBoundaries
            .ToDictionary(boundary => boundary.Name, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            string domainName = RemoveTestsSuffix(project.Name);
            if (!boundariesByName.TryGetValue(domainName, out DomainBoundaryConfig? boundary))
            {
                continue;
            }

            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            string projectDirectory = Path.GetDirectoryName(projectPath) ?? repoRoot;
            if (!Directory.Exists(projectDirectory))
            {
                failures.Add($"{project.Name} directory does not exist at {projectDirectory}.");
                continue;
            }

            var forbiddenReferences = boundary.ForbiddenReferences
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var forbiddenPathSegments = boundary.ForbiddenPathSegments
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string file in TestContractFiles(projectDirectory))
            {
                string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                string relativeToProject = Path.GetRelativePath(projectDirectory, file);
                string text = File.ReadAllText(file);

                foreach (string segment in forbiddenPathSegments)
                {
                    if (ContainsForbiddenPathSegment(relativeToProject, segment))
                    {
                        failures.Add($"{relative} is under domain-excluded first-round test path segment '{segment}'.");
                    }
                }

                foreach (string forbidden in TestForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (ContainsForbiddenBoundaryToken(relative, forbidden)
                        || ContainsForbiddenBoundaryToken(text, forbidden))
                    {
                        failures.Add($"{relative} requires domain-excluded first-round token '{forbidden}'.");
                    }
                }
            }

            foreach (string file in CommonTestBoundaryFiles(repoRoot))
            {
                string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                string text = File.ReadAllText(file);

                foreach (string forbidden in TestForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (ContainsForbiddenBoundaryToken(relative, forbidden)
                        || ContainsForbiddenBoundaryToken(text, forbidden))
                    {
                        failures.Add($"{relative} configures domain-excluded first-round test token '{forbidden}' for {project.Name}.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared first-round domain product tests require excluded domain contracts:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredProductTestProjectFilesDoNotUseUnscannedExplicitImports()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var declarationFiles = new List<string>();

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(projectPath))
            {
                declarationFiles.AddRange(TestProjectDeclarationFiles(projectPath));
            }
        }

        declarationFiles.AddRange(CommonTestBoundaryFiles(repoRoot));

        var failures = FindUnscannedExplicitImports(repoRoot, declarationFiles);

        Assert.True(
            failures.Count == 0,
            "Declared first-round product-test project files import build declarations outside the scanned boundary surface:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void CompiledProductTestAssembliesDoNotReferenceExcludedBoundaryAssemblies()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var forbiddenReferences = Config.ProductTests.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{project.Name} must be built before compiled product-test references can be checked.");
                continue;
            }

            foreach (string reference in ReadAssemblyReferences(assemblyPath))
            {
                foreach (string forbidden in TestForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (ContainsForbiddenBoundaryToken(reference, forbidden))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} references excluded first-round test assembly {reference} via token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared first-round product-test assemblies reference excluded backend/UI contracts:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void CompiledProductTestAssembliesDoNotDeclareExcludedBoundaryNames()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var forbiddenReferences = Config.ProductTests.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{project.Name} must be built before compiled product-test names can be checked.");
                continue;
            }

            foreach (string declaredName in ReadDeclaredSymbolNames(assemblyPath))
            {
                foreach (string forbidden in TestForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (ContainsForbiddenBoundaryToken(declaredName, forbidden))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} declares excluded first-round test symbol {declaredName} via token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Compiled first-round product-test assemblies declare excluded boundary names:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void CompiledDomainProductTestAssembliesDoNotReferenceDomainExcludedAssemblies()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var boundariesByName = Config.Architecture.DomainBoundaries
            .ToDictionary(boundary => boundary.Name, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            string domainName = RemoveTestsSuffix(project.Name);
            if (!boundariesByName.TryGetValue(domainName, out DomainBoundaryConfig? boundary))
            {
                continue;
            }

            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{project.Name} must be built before compiled domain product-test references can be checked.");
                continue;
            }

            var forbiddenReferences = boundary.ForbiddenReferences
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string reference in ReadAssemblyReferences(assemblyPath))
            {
                foreach (string forbidden in TestForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (ContainsForbiddenBoundaryToken(reference, forbidden))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} references domain-excluded first-round test assembly {reference} via token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared first-round domain product-test assemblies reference excluded domain contracts:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void CompiledDomainProductTestAssembliesDoNotDeclareDomainExcludedNames()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var boundariesByName = Config.Architecture.DomainBoundaries
            .ToDictionary(boundary => boundary.Name, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            string domainName = RemoveTestsSuffix(project.Name);
            if (!boundariesByName.TryGetValue(domainName, out DomainBoundaryConfig? boundary))
            {
                continue;
            }

            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{project.Name} must be built before compiled domain product-test names can be checked.");
                continue;
            }

            var forbiddenReferences = boundary.ForbiddenReferences
                .Where(reference => !string.IsNullOrWhiteSpace(reference))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (string declaredName in ReadDeclaredSymbolNames(assemblyPath))
            {
                foreach (string forbidden in TestForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (ContainsForbiddenBoundaryToken(declaredName, forbidden))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} declares domain-excluded first-round test symbol {declaredName} via token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Compiled first-round domain product-test assemblies declare excluded domain names:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void CommonProductTestBoundaryFilesIncludeRootBuildDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessCommonTestBoundaryFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(tempRoot, "Directory.Build.targets"), "<Project />");
            File.WriteAllText(Path.Combine(tempRoot, "Directory.Packages.props"), "<Project />");

            string[] files = CommonTestBoundaryFiles(tempRoot)
                .Select(file => Path.GetFileName(file)!)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "Directory.Build.props",
                    "Directory.Build.targets",
                    "Directory.Packages.props",
                ],
                files);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DomainProductTestBoundaryScanIncludesRootBuildDeclarations()
    {
        string text = """
            <Project>
              <ItemGroup>
                <ProjectReference Include="src\SomeEngine.Render.Cluster\SomeEngine.Render.Cluster.csproj" />
              </ItemGroup>
            </Project>
            """;

        Assert.True(ContainsForbiddenBoundaryToken(text, "SomeEngine.Render.Cluster"));
    }

    [Fact]
    public void ProductTestExplicitImportScanRejectsUnscannedDeclarationFiles()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessProductTestExplicitImports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectDirectory = Path.Combine(tempRoot, "tests", "SomeEngine.Sample.Tests");
            string buildDirectory = Path.Combine(tempRoot, "build");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(buildDirectory);

            string projectPath = Path.Combine(projectDirectory, "SomeEngine.Sample.Tests.csproj");
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="..\..\build\HiddenTest.props" />
                </Project>
                """);

            File.WriteAllText(Path.Combine(buildDirectory, "HiddenTest.props"), "<Project />");

            var failures = FindUnscannedExplicitImports(tempRoot, TestProjectDeclarationFiles(projectPath));

            Assert.Single(failures);
            Assert.Contains("HiddenTest.props", failures[0]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void PerformanceSensitiveProductTestsRunOnlyInWarningBucket()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            string projectDirectory = Path.GetDirectoryName(projectPath) ?? repoRoot;
            if (!Directory.Exists(projectDirectory))
            {
                failures.Add($"{project.Name} directory does not exist at {projectDirectory}.");
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGeneratedOutputPath(file))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                string source = File.ReadAllText(file);
                foreach (TestMethod method in FindTestMethods(source))
                {
                    if (IsPerformanceSensitive(method.Name, method.Body)
                        && !HasPerformanceTrait(method.Attributes))
                    {
                        failures.Add($"{relative}:{method.Name} is performance-sensitive and must use Category=Performance.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Performance-sensitive product tests must stay out of the hard product-test bucket:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void PerformanceSensitiveTestFinderIncludesInternalFullyQualifiedAsyncTests()
    {
        string source = """
            using Xunit;

            public sealed class SamplePerformanceTests
            {
                [Fact]
                internal async System.Threading.Tasks.Task InternalTimingTest()
                {
                    _ = Stopwatch.StartNew();
                    await System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        TestMethod method = Assert.Single(FindTestMethods(source));

        Assert.Equal("InternalTimingTest", method.Name);
        Assert.True(IsPerformanceSensitive(method.Name, method.Body));
    }

    [Fact]
    public void ProductTestBoundaryFilesIncludeUppercaseTextContractExtensions()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessProductTestBoundaryFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            foreach (string fileName in new[]
            {
                "Contract.CSPROJ",
                "Contract.PROPS",
                "Contract.TARGETS",
                "Contract.FBS",
                "Contract.SLANG",
                "Contract.YML",
            })
            {
                File.WriteAllText(Path.Combine(tempRoot, fileName), "");
            }

            Directory.CreateDirectory(Path.Combine(tempRoot, "bin"));
            File.WriteAllText(Path.Combine(tempRoot, "bin", "Ignored.CS"), "");

            string[] files = TestContractFiles(tempRoot)
                .Select(file => Path.GetFileName(file)!)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "Contract.CSPROJ",
                    "Contract.FBS",
                    "Contract.PROPS",
                    "Contract.SLANG",
                    "Contract.TARGETS",
                    "Contract.YML",
                ],
                files);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static IEnumerable<string> TestContractFiles(string projectDirectory)
        => Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
            .Where(path => IsCheckedExtension(Path.GetExtension(path)))
            .Where(path => !IsGeneratedOutputPath(path));

    private static IEnumerable<string> CommonTestBoundaryFiles(string repoRoot)
    {
        foreach (string fileName in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            string path = Path.Combine(repoRoot, fileName);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> TestProjectDeclarationFiles(string projectPath)
    {
        yield return projectPath;

        string projectDirectory = Path.GetDirectoryName(projectPath) ?? "";
        if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path) is ".props" or ".targets")
                     .Where(path => !IsGeneratedOutputPath(path))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            yield return file;
        }
    }

    private static List<string> FindUnscannedExplicitImports(string repoRoot, IEnumerable<string> declarationFiles)
    {
        var scannedFiles = declarationFiles
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (string declarationFile in scannedFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string importProject in ReadExplicitImports(declarationFile))
            {
                string relativeDeclaration = Path.GetRelativePath(repoRoot, declarationFile).Replace('\\', '/');
                if (!TryResolveImportPath(declarationFile, importProject, out string? importFullPath))
                {
                    failures.Add($"{relativeDeclaration} imports '{importProject}', which cannot be resolved as a stable first-round test declaration file.");
                    continue;
                }

                if (!scannedFiles.Contains(importFullPath!))
                {
                    string relativeImport = Path.GetRelativePath(repoRoot, importFullPath!).Replace('\\', '/');
                    failures.Add($"{relativeDeclaration} imports unscanned first-round test declaration file {relativeImport}.");
                }
            }
        }

        return failures;
    }

    private static IEnumerable<string> ReadExplicitImports(string declarationFile)
    {
        XDocument document = XDocument.Load(declarationFile);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "Import")
            .Select(element => element.Attribute("Project")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
    }

    private static bool TryResolveImportPath(string declarationFile, string importProject, out string? importFullPath)
    {
        importFullPath = null;
        if (importProject.Contains("$(", StringComparison.Ordinal)
            || importProject.Contains('*')
            || importProject.Contains('?'))
        {
            return false;
        }

        string baseDirectory = Path.GetDirectoryName(declarationFile) ?? "";
        string combined = Path.IsPathRooted(importProject)
            ? importProject
            : Path.Combine(baseDirectory, importProject);
        importFullPath = Path.GetFullPath(combined);
        return true;
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

    private static IEnumerable<string> ReadDeclaredSymbolNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();

        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition definition = metadata.GetTypeDefinition(handle);
            string typeName = FullName(metadata.GetString(definition.Namespace), metadata.GetString(definition.Name));
            yield return typeName;

            foreach (PropertyDefinitionHandle propertyHandle in definition.GetProperties())
            {
                PropertyDefinition property = metadata.GetPropertyDefinition(propertyHandle);
                yield return typeName + "." + metadata.GetString(property.Name);
            }

            foreach (MethodDefinitionHandle methodHandle in definition.GetMethods())
            {
                MethodDefinition method = metadata.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.SpecialName) == MethodAttributes.SpecialName)
                {
                    continue;
                }

                yield return typeName + "." + metadata.GetString(method.Name);
            }

            foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
            {
                FieldDefinition field = metadata.GetFieldDefinition(fieldHandle);
                yield return typeName + "." + metadata.GetString(field.Name);
            }
        }
    }

    private static string FullName(string namespaceName, string typeName)
        => string.IsNullOrWhiteSpace(namespaceName)
            ? typeName
            : namespaceName + "." + typeName;

    private static bool IsGeneratedOutputPath(string path)
        => path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
           || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsCheckedExtension(string extension)
        => NormalizeExtension(extension) is ".cs"
            or ".csproj"
            or ".props"
            or ".targets"
            or ".json"
            or ".gltf"
            or ".asset"
            or ".meta"
            or ".slang"
            or ".fbs"
            or ".hlsl"
            or ".hlsli"
            or ".glsl"
            or ".vert"
            or ".frag"
            or ".comp"
            or ".geom"
            or ".tesc"
            or ".tese"
            or ".mesh"
            or ".shader"
            or ".material"
            or ".yaml"
            or ".yml";

    private static string NormalizeExtension(string extension)
        => extension.ToLowerInvariant();

    private static string RemoveTestsSuffix(string testProjectName)
        => testProjectName.EndsWith(".Tests", StringComparison.Ordinal)
            ? testProjectName[..^".Tests".Length]
            : testProjectName;

    private static IEnumerable<TestMethod> FindTestMethods(string source)
    {
        foreach (Match match in TestMethodPattern().Matches(source))
        {
            string attributes = match.Groups["attributes"].Value;
            if (!attributes.Contains("[Fact", StringComparison.Ordinal)
                && !attributes.Contains("[Theory", StringComparison.Ordinal))
            {
                continue;
            }

            int bodyStart = match.Index + match.Length - 1;
            int bodyEnd = FindBodyEnd(source, bodyStart);
            if (bodyEnd <= bodyStart)
            {
                continue;
            }

            yield return new TestMethod(
                match.Groups["name"].Value,
                attributes,
                source[bodyStart..bodyEnd]);
        }
    }

    private static int FindBodyEnd(string source, int bodyStart)
    {
        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
                continue;
            }

            if (source[index] != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return index + 1;
            }
        }

        return -1;
    }

    private static bool IsPerformanceSensitive(string methodName, string body)
        => ContainsIgnoreCase(methodName, "Benchmark")
           || ContainsIgnoreCase(methodName, "BoundedAllocation")
           || ContainsIgnoreCase(methodName, "DoesNotAllocate")
           || ContainsIgnoreCase(methodName, "AllocationSmoke")
           || ContainsIgnoreCase(methodName, "StressAndLiveness")
           || ContainsIgnoreCase(methodName, "LocalQueue")
           || ContainsIgnoreCase(methodName, "Stolen")
           || body.Contains("GC.GetAllocatedBytesForCurrentThread", StringComparison.Ordinal)
           || body.Contains("Stopwatch.StartNew", StringComparison.Ordinal)
           || body.Contains("LocalQueuedWorkItems", StringComparison.Ordinal)
           || body.Contains("StolenWorkItems", StringComparison.Ordinal)
           || body.Contains("RunWithTimeout(", StringComparison.Ordinal);

    private static bool HasPerformanceTrait(string attributes)
        => PerformanceTraitPattern().IsMatch(attributes);

    private static bool ContainsIgnoreCase(string text, string value)
        => text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsForbiddenBoundaryToken(string text, string token)
    {
        if (RequiresExactIdentifierMatch(token))
        {
            return ContainsExactIdentifier(text, token, ExactTokenComparison(token));
        }

        return text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresExactIdentifierMatch(string token)
        => token is "Rhi" or "SharpGen" or "Present" or "Window" or "Windowing";

    private static StringComparison ExactTokenComparison(string token)
        => token is "Present" or "Window"
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    private static IEnumerable<string> TestForbiddenReferencesForProject(ProjectConfig project, IEnumerable<string> forbiddenReferences)
    {
        foreach (string forbidden in forbiddenReferences)
        {
            if (IsCommonIntegrationWord(forbidden) && !IsRenderOrAssetBoundaryTestProject(project.Name))
            {
                continue;
            }

            yield return forbidden;
        }
    }

    private static bool IsCommonIntegrationWord(string token)
        => token is "Present" or "Window";

    private static bool IsRenderOrAssetBoundaryTestProject(string projectName)
        => projectName is "SomeEngine.Assets.Tests" or "SomeEngine.Render.Tests" or "SomeEngine.Render.Cluster.Tests";

    private static bool ContainsForbiddenPathSegment(string relativePath, string segment)
    {
        string[] pathParts = relativePath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] forbiddenParts = segment.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (forbiddenParts.Length == 0 || forbiddenParts.Length > pathParts.Length)
        {
            return false;
        }

        for (int start = 0; start <= pathParts.Length - forbiddenParts.Length; start++)
        {
            bool matches = true;
            for (int offset = 0; offset < forbiddenParts.Length; offset++)
            {
                if (!PathPartMatches(pathParts[start + offset], forbiddenParts[offset]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    private static bool PathPartMatches(string pathPart, string forbiddenPart)
        => string.Equals(pathPart, forbiddenPart, StringComparison.OrdinalIgnoreCase)
           || string.Equals(Path.GetFileNameWithoutExtension(pathPart), forbiddenPart, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsExactIdentifier(string text, string token)
        => ContainsExactIdentifier(text, token, StringComparison.OrdinalIgnoreCase);

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

    [GeneratedRegex(@"(?<attributes>(?:\s*\[[^\]]+\]\s*)+)\s*(?:public|internal)\s+(?:async\s+)?(?:void|Task|ValueTask|System\.Threading\.Tasks\.Task|System\.Threading\.Tasks\.ValueTask)\s+(?<name>\w+)\s*\([^)]*\)\s*\{")]
    private static partial Regex TestMethodPattern();

    [GeneratedRegex(@"\[Trait\s*\(\s*""Category""\s*,\s*""Performance""\s*\)\s*\]")]
    private static partial Regex PerformanceTraitPattern();

    private readonly record struct TestMethod(string Name, string Attributes, string Body);
}



