using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class ProductBoundaryTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void SolutionContainsOnlyAcceptedBoundaryProjects()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        XDocument solution = XDocument.Load(Config.SolutionPath);
        var failures = new List<string>();
        var acceptedPaths = AcceptedSolutionProjectPaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedNames = Config.Architecture.ExcludedProjectNames.ToHashSet(StringComparer.Ordinal);
        var solutionPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (XElement project in solution.Descendants("Project"))
        {
            string path = Normalize(project.Attribute("Path")?.Value ?? "");
            if (string.IsNullOrWhiteSpace(path))
            {
                failures.Add("Solution contains a Project element without a Path attribute");
                continue;
            }

            solutionPaths.Add(path);

            string name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
            if (excludedNames.Contains(name))
            {
                failures.Add($"Excluded legacy project {name} must not appear in SomeEngine.slnx");
            }

            if (!acceptedPaths.Contains(path) && !path.StartsWith("harness/", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Solution project {path} is not part of the accepted product, test, build-support, harness, or declared external boundary");
            }

            string fullPath = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                failures.Add($"Solution project {path} does not exist on disk");
            }
        }

        foreach (string path in acceptedPaths.Order(StringComparer.OrdinalIgnoreCase))
        {
            string fullPath = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (path.StartsWith("harness/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(fullPath))
            {
                failures.Add($"Accepted boundary project {path} does not exist on disk");
            }

            if (!solutionPaths.Contains(path))
            {
                failures.Add($"Accepted boundary project {path} must appear in SomeEngine.slnx");
            }
        }

        Assert.True(
            failures.Count == 0,
            "SomeEngine.slnx does not match the accepted product boundary:\n" + string.Join("\n", failures));
    }



    [Fact]
    public void ExcludedWorkspaceRootsAreAbsentFromMigratedWorkspace()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (string root in Config.Architecture.ExcludedWorkspaceRoots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string fullPath = Path.Combine(repoRoot, root.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(fullPath) || File.Exists(fullPath))
            {
                failures.Add($"Excluded workspace root {root} must not exist in SomeEngine.Next; keep it only in the legacy/reference repository until it is accepted by harness.");
            }

            var tracked = Repo.Git($"ls-files -- {root}")
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Take(5)
                .ToArray();
            if (tracked.Length > 0)
            {
                failures.Add($"Excluded workspace root {root} must not be tracked, but git tracks: {string.Join(", ", tracked)}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Excluded legacy or not-yet-accepted workspace roots are present in the migrated workspace:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ExcludedProjectsAreNotDeclaredAsFirstRoundBoundary()
    {
        var failures = new List<string>();
        var excludedNames = Config.Architecture.ExcludedProjectNames.ToHashSet(StringComparer.Ordinal);

        foreach (ProjectConfig project in Config.Projects.ProductProjects)
        {
            if (excludedNames.Contains(project.Name))
            {
                failures.Add($"Excluded project {project.Name} must not be declared as a first-round product project");
            }
        }

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            if (excludedNames.Contains(project.Name))
            {
                failures.Add($"Excluded project {project.Name} must not be declared as a first-round test project");
            }
        }

        foreach (ProjectConfig project in Config.Projects.BuildSupportProjects)
        {
            if (excludedNames.Contains(project.Name))
            {
                failures.Add($"Excluded project {project.Name} must not be declared as a first-round build-support project");
            }
        }

        foreach (ApiContractConfig contract in Config.ApiContracts)
        {
            if (excludedNames.Contains(contract.Assembly))
            {
                failures.Add($"Excluded project {contract.Assembly} must not provide first-round API contract {contract.Type}");
            }
        }

        foreach (string assembly in Config.Coverage.RequiredAssemblies)
        {
            if (excludedNames.Contains(assembly))
            {
                failures.Add($"Excluded project {assembly} must not be required for first-round coverage");
            }
        }

        foreach (string projectName in Config.Architecture.LayerContract.AllowedDependencies.Keys)
        {
            if (excludedNames.Contains(projectName))
            {
                failures.Add($"Excluded project {projectName} must not have a first-round layer dependency contract");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Excluded projects are still declared as first-round boundary:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredApiContractsDoNotRequireExcludedFirstRoundBoundaryNames()
    {
        var sourceProjectsByName = Config.Projects.ProductProjects
            .Concat(Config.Projects.BuildSupportProjects)
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
        var forbiddenReferences = Config.Architecture.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (ApiContractConfig contract in Config.ApiContracts)
        {
            if (!sourceProjectsByName.TryGetValue(contract.Assembly, out ProjectConfig? project))
            {
                failures.Add($"API contract {contract.Assembly}:{contract.Type} must belong to an accepted first-round source project.");
                continue;
            }

            foreach (string forbidden in SourceForbiddenReferencesForProject(project, forbiddenReferences))
            {
                if (ContainsForbiddenSourceToken(contract.Assembly, forbidden)
                    || ContainsForbiddenSourceToken(contract.Type, forbidden))
                {
                    failures.Add($"API contract {contract.Assembly}:{contract.Type} requires excluded first-round boundary token '{forbidden}'.");
                }

                foreach (ApiMemberContractConfig member in contract.Members)
                {
                    string memberFact = $"{member.Kind}:{member.Name}";
                    if (ContainsForbiddenSourceToken(memberFact, forbidden))
                    {
                        failures.Add($"API contract {contract.Assembly}:{contract.Type}:{memberFact} requires excluded first-round boundary token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared API contracts require excluded first-round boundary names:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AcceptedSourceProjectsDoNotHideSourceWithCompileRemove()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();
        var declarationFiles = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects.Concat(Config.Projects.BuildSupportProjects))
        {
            string projectFullPath = Path.Combine(repoRoot, project.Path);
            if (!File.Exists(projectFullPath))
            {
                continue;
            }

            declarationFiles.AddRange(ProjectReferenceDeclarationFiles(projectFullPath));
        }

        declarationFiles.AddRange(CommonProjectDeclarationFiles(repoRoot));

        foreach (string declarationFile in declarationFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string removedSource in ReadRemovedCompileItems(declarationFile))
            {
                string relativeDeclaration = Normalize(Path.GetRelativePath(repoRoot, declarationFile));
                failures.Add($"{relativeDeclaration} hides source with <Compile Remove=\"{removedSource}\">. Excluded legacy code must be physically absent from the migrated workspace, not hidden from compilation.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Accepted source projects hide source files from the first-round boundary:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredFirstRoundProjectFilesDoNotDeclareExcludedBoundaryReferences()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var forbiddenReferences = Config.Architecture.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects.Concat(Config.Projects.BuildSupportProjects))
        {
            string projectFullPath = Path.Combine(repoRoot, project.Path);
            string projectDirectory = Path.GetDirectoryName(projectFullPath) ?? repoRoot;
            var projectFiles = new List<string>();

            if (File.Exists(projectFullPath))
            {
                projectFiles.Add(projectFullPath);
            }

            if (Directory.Exists(projectDirectory))
            {
                projectFiles.AddRange(ProjectDeclarationFiles(projectDirectory));
            }

            foreach (string file in projectFiles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                string text = File.ReadAllText(file);
                foreach (string forbidden in SourceForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (ContainsForbiddenBoundaryToken(relative, forbidden)
                        || ContainsForbiddenBoundaryToken(text, forbidden))
                    {
                        failures.Add($"{relative} declares excluded first-round boundary token '{forbidden}'.");
                    }
                }
            }
        }

        var commonFiles = new List<string>();
        AddFileIfExists(commonFiles, Path.Combine(repoRoot, "Directory.Build.props"));
        AddFileIfExists(commonFiles, Path.Combine(repoRoot, "Directory.Build.targets"));
        AddFileIfExists(commonFiles, Path.Combine(repoRoot, "Directory.Packages.props"));

        foreach (string file in commonFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            string text = File.ReadAllText(file);

            foreach (string forbidden in forbiddenReferences)
            {
                if (ContainsForbiddenBoundaryToken(relative, forbidden)
                    || ContainsForbiddenBoundaryToken(text, forbidden))
                {
                    failures.Add($"{relative} declares excluded first-round boundary token '{forbidden}'.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared first-round project files preserve excluded boundary references:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredFirstRoundProjectFilesDoNotUseUnscannedExplicitImports()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var declarationFiles = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects.Concat(Config.Projects.BuildSupportProjects))
        {
            string projectFullPath = Path.Combine(repoRoot, project.Path);
            if (File.Exists(projectFullPath))
            {
                declarationFiles.AddRange(ProjectReferenceDeclarationFiles(projectFullPath));
            }
        }

        declarationFiles.AddRange(CommonProjectDeclarationFiles(repoRoot));

        var failures = FindUnscannedExplicitImports(repoRoot, declarationFiles);

        Assert.True(
            failures.Count == 0,
            "Declared first-round project files import build declarations outside the scanned boundary surface:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ExactSourceBoundaryTokensKeepOrdinaryLowercaseWordsAllowed()
    {
        Assert.False(ContainsForbiddenSourceToken("bool present = archetype.HasComponent(componentId);", "Present"));
        Assert.False(ContainsForbiddenSourceToken("throw new InvalidOperationException(\"Component was not present.\");", "Present"));
        Assert.False(ContainsForbiddenSourceToken("string message = \"window size is not configured\";", "Window"));

        Assert.True(ContainsForbiddenSourceToken("public sealed class SwapchainPresent { }", "Present"));
        Assert.True(ContainsForbiddenSourceToken("public void CreateWindow() { }", "Window"));
        Assert.True(ContainsForbiddenSourceToken("PackageReference Include=\"Some.Windowing\"", "Windowing"));
        Assert.True(ContainsForbiddenSourceToken("PackageReference Include=\"some.windowing\"", "Windowing"));
        Assert.True(ContainsForbiddenSourceToken("private IRhiDevice? device;", "Rhi"));
    }

    [Fact]
    public void CommonIntegrationSourceWordsAreScopedToRenderAndAssetBoundaryProjects()
    {
        string[] references = ["Present", "Window", "SomeEngine.Rhi"];

        var ecsReferences = SourceForbiddenReferencesForProject(
                new ProjectConfig { Name = "SomeEngine.ECS", Path = "src/SomeEngine.ECS/SomeEngine.ECS.csproj" },
                references)
            .ToArray();

        Assert.DoesNotContain("Present", ecsReferences);
        Assert.DoesNotContain("Window", ecsReferences);
        Assert.Contains("SomeEngine.Rhi", ecsReferences);

        var renderReferences = SourceForbiddenReferencesForProject(
                new ProjectConfig { Name = "SomeEngine.Render", Path = "src/SomeEngine.Render/SomeEngine.Render.csproj" },
                references)
            .ToArray();

        Assert.Contains("Present", renderReferences);
        Assert.Contains("Window", renderReferences);
    }

    [Fact]
    public void ProductSourceBoundaryFilesIncludeTextContractAssets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessProductSourceFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            foreach (string fileName in new[]
            {
                "Contract.cs",
                "Contract.json",
                "Contract.gltf",
                "Contract.asset",
                "Contract.slang",
                "Contract.hlsl",
                "Contract.hlsli",
                "Contract.material",
                "Contract.yaml",
                "Upper.Contract.FBS",
                "Upper.Contract.SLANG",
                "Upper.Contract.YML",
            })
            {
                File.WriteAllText(Path.Combine(tempRoot, fileName), "");
            }

            Directory.CreateDirectory(Path.Combine(tempRoot, "obj"));
            File.WriteAllText(Path.Combine(tempRoot, "obj", "Ignored.json"), "");

            string[] files = SourceFiles(tempRoot)
                .Select(file => Path.GetFileName(file)!)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "Contract.asset",
                    "Contract.cs",
                    "Contract.gltf",
                    "Contract.hlsl",
                    "Contract.hlsli",
                    "Contract.json",
                    "Contract.material",
                    "Contract.slang",
                    "Contract.yaml",
                    "Upper.Contract.FBS",
                    "Upper.Contract.SLANG",
                    "Upper.Contract.YML",
                ],
                files);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DeclaredFirstRoundSourceFilesDoNotUseExcludedBoundaryReferences()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var bridgeFiles = Config.Profiler.BridgeFiles
            .Select(path => Path.GetFullPath(Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forbiddenReferences = Config.Architecture.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects.Concat(Config.Projects.BuildSupportProjects))
        {
            string projectFullPath = Path.Combine(repoRoot, project.Path);
            string projectDirectory = Path.GetDirectoryName(projectFullPath) ?? repoRoot;
            if (!Directory.Exists(projectDirectory))
            {
                continue;
            }

            foreach (string file in SourceFiles(projectDirectory))
            {
                if (bridgeFiles.Contains(Path.GetFullPath(file)))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                string text = File.ReadAllText(file);
                foreach (string forbidden in SourceForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (IsAcceptedRenderGraphGeneratorSource(project.Name, forbidden, relative))
                    {
                        continue;
                    }

                    if (ContainsForbiddenSourceToken(relative, forbidden)
                        || ContainsForbiddenSourceToken(text, forbidden))
                    {
                        failures.Add($"{relative} uses excluded first-round boundary token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared first-round source files preserve excluded boundary references:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AcceptedProductAssembliesDoNotReferenceExcludedBoundaryAssemblies()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var forbiddenReferences = Config.Architecture.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects.Concat(Config.Projects.BuildSupportProjects))
        {
            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{project.Name} must be built before compiled assembly references can be checked.");
                continue;
            }

            foreach (string reference in ReadAssemblyReferences(assemblyPath))
            {
                foreach (string forbidden in SourceForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (ContainsForbiddenBoundaryToken(reference, forbidden))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} references excluded first-round assembly {reference} via token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Accepted first-round assemblies reference excluded boundary assemblies:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AcceptedProductAssembliesDoNotDeclareForbiddenFirstRoundNames()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var forbiddenReferences = Config.Architecture.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var profilerBridgeSymbols = ReadProfilerBridgeSymbols(repoRoot);
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects.Concat(Config.Projects.BuildSupportProjects))
        {
            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{project.Name} must be built before compiled product names can be checked.");
                continue;
            }

            foreach (string declaredName in ReadDeclaredSymbolNames(assemblyPath))
            {
                if (IsProfilerBridgeSymbol(declaredName, profilerBridgeSymbols))
                {
                    continue;
                }

                foreach (string forbidden in SourceForbiddenReferencesForProject(project, forbiddenReferences))
                {
                    if (IsAcceptedRenderGraphGeneratorSymbol(project.Name, forbidden, declaredName))
                    {
                        continue;
                    }

                    if (ContainsForbiddenSourceToken(declaredName, forbidden))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} declares first-round symbol {declaredName} via forbidden token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Accepted first-round assemblies declare excluded boundary names:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredFirstRoundProjectsGrantInternalsOnlyInsideFirstRoundBoundary()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var allowedFriendAssemblies = Config.Projects.ProductProjects
            .Concat(Config.Projects.BuildSupportProjects)
            .Concat(Config.Projects.TestProjects)
            .Select(project => project.Name)
            .ToHashSet(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects.Concat(Config.Projects.BuildSupportProjects))
        {
            string projectDirectory = Path.Combine(repoRoot, Path.GetDirectoryName(project.Path) ?? "");
            string projectPath = Path.Combine(repoRoot, project.Path);
            if (!Directory.Exists(projectDirectory) || !File.Exists(projectPath))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                         .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                         .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                string source = File.ReadAllText(file);
                foreach (string friendAssembly in ReadSourceInternalsVisibleTo(source))
                {
                    if (!allowedFriendAssemblies.Contains(friendAssembly))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, file)} grants InternalsVisibleTo to {friendAssembly}, which is outside the first-round boundary");
                    }
                }
            }

            foreach (string declarationFile in ProjectReferenceDeclarationFiles(projectPath))
            {
                foreach (string friendAssembly in ReadProjectInternalsVisibleTo(declarationFile))
                {
                    if (!allowedFriendAssemblies.Contains(friendAssembly))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, declarationFile)} grants InternalsVisibleTo to {friendAssembly}, which is outside the first-round boundary");
                    }
                }
            }

            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{project.Name} must be built before compiled InternalsVisibleTo attributes can be checked.");
                continue;
            }

            foreach (string friendAssembly in ReadCompiledInternalsVisibleTo(assemblyPath))
            {
                if (!allowedFriendAssemblies.Contains(friendAssembly))
                {
                    failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} grants compiled InternalsVisibleTo to {friendAssembly}, which is outside the first-round boundary");
                }
            }
        }

        foreach (string declarationFile in CommonProjectDeclarationFiles(repoRoot))
        {
            foreach (string friendAssembly in ReadProjectInternalsVisibleTo(declarationFile))
            {
                if (!allowedFriendAssemblies.Contains(friendAssembly))
                {
                    failures.Add($"{Path.GetRelativePath(repoRoot, declarationFile)} grants InternalsVisibleTo to {friendAssembly}, which is outside the first-round boundary");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared first-round projects expose internals outside the first-round boundary:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AcceptedProductAssembliesDoNotContainForbiddenFirstRoundTypes()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var declaredProjects = Config.Projects.ProductProjects
            .Concat(Config.Projects.BuildSupportProjects)
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (ForbiddenProductTypeConfig forbiddenType in Config.Architecture.ForbiddenProductTypes)
        {
            if (!declaredProjects.TryGetValue(forbiddenType.Assembly, out ProjectConfig? project))
            {
                failures.Add($"Forbidden type {forbiddenType.Type} targets undeclared assembly {forbiddenType.Assembly}.");
                continue;
            }

            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{forbiddenType.Assembly} must be built before forbidden product types can be checked.");
                continue;
            }

            if (AssemblyContainsType(assemblyPath, forbiddenType.Type))
            {
                failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} contains forbidden first-round product type {forbiddenType.Type}.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Accepted product assemblies contain execution-shaped concepts outside the first-round boundary:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void RequiredProductTypeContractsDoNotRequireExcludedFirstRoundBoundaryNames()
    {
        var sourceProjectsByName = Config.Projects.ProductProjects
            .Concat(Config.Projects.BuildSupportProjects)
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
        var forbiddenReferences = Config.Architecture.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (ProductTypeContractConfig requiredType in Config.Architecture.RequiredProductTypes)
        {
            if (!sourceProjectsByName.TryGetValue(requiredType.Assembly, out ProjectConfig? project))
            {
                failures.Add($"Required product type {requiredType.Assembly}:{requiredType.Type} must belong to an accepted first-round source project.");
                continue;
            }

            foreach (string forbidden in SourceForbiddenReferencesForProject(project, forbiddenReferences))
            {
                if (ContainsForbiddenSourceToken(requiredType.Assembly, forbidden)
                    || ContainsForbiddenSourceToken(requiredType.Type, forbidden))
                {
                    failures.Add($"Required product type {requiredType.Assembly}:{requiredType.Type} requires excluded first-round boundary token '{forbidden}'.");
                }

                foreach (ApiMemberContractConfig member in requiredType.Members)
                {
                    string memberFact = $"{member.Kind}:{member.Name}";
                    if (ContainsForbiddenSourceToken(memberFact, forbidden))
                    {
                        failures.Add($"Required product type {requiredType.Assembly}:{requiredType.Type}:{memberFact} requires excluded first-round boundary token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Required product type contracts require excluded first-round boundary names:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AcceptedProductAssembliesContainRequiredFirstRoundTypeContracts()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var declaredProjects = Config.Projects.ProductProjects
            .Concat(Config.Projects.BuildSupportProjects)
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (ProductTypeContractConfig requiredType in Config.Architecture.RequiredProductTypes)
        {
            if (!declaredProjects.TryGetValue(requiredType.Assembly, out ProjectConfig? project))
            {
                failures.Add($"Required type {requiredType.Type} targets undeclared assembly {requiredType.Assembly}.");
                continue;
            }

            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{requiredType.Assembly} must be built before required product types can be checked.");
                continue;
            }

            var typeFact = ReadTypeFact(assemblyPath, requiredType.Type);
            if (typeFact is null)
            {
                failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} must contain accepted first-round product type {requiredType.Type}.");
                continue;
            }

            foreach (ApiMemberContractConfig member in requiredType.Members)
            {
                if (!typeFact.Value.ContainsMember(member))
                {
                    failures.Add($"{requiredType.Type} must contain accepted first-round {member.Kind} member {member.Name}.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Accepted product assemblies are missing first-round Cluster model contracts:\n" + string.Join("\n", failures));
    }

    private static IEnumerable<string> ReadSourceInternalsVisibleTo(string source)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            source,
            @"InternalsVisibleTo\s*\(\s*""(?<name>[^""]+)""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            string assemblyName = match.Groups["name"].Value.Split(',')[0].Trim();
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                yield return assemblyName;
            }
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

    private static IEnumerable<string> ReadCompiledInternalsVisibleTo(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();

        foreach (CustomAttributeHandle handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = metadata.GetCustomAttribute(handle);
            if (!string.Equals(GetAttributeTypeName(metadata, attribute.Constructor),
                    "System.Runtime.CompilerServices.InternalsVisibleToAttribute",
                    StringComparison.Ordinal))
            {
                continue;
            }

            CustomAttributeValue<string> value = attribute.DecodeValue(StringAttributeTypeProvider.Instance);
            foreach (CustomAttributeTypedArgument<string> argument in value.FixedArguments)
            {
                if (argument.Value is string friendAssembly && !string.IsNullOrWhiteSpace(friendAssembly))
                {
                    yield return friendAssembly.Split(',')[0].Trim();
                }
            }
        }
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

    private static HashSet<string> ReadProfilerBridgeSymbols(string repoRoot)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal);

        foreach (string relativeFile in Config.Profiler.BridgeFiles)
        {
            if (!relativeFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fullPath = Path.Combine(repoRoot, relativeFile.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue;
            }

            string source = File.ReadAllText(fullPath);
            string namespaceName = ReadFileScopedNamespace(source);
            foreach (string typeName in ReadTopLevelTypeNames(source))
            {
                symbols.Add(FullName(namespaceName, typeName));
            }
        }

        return symbols;
    }

    private static bool IsProfilerBridgeSymbol(string declaredName, HashSet<string> profilerBridgeSymbols)
    {
        foreach (string symbol in profilerBridgeSymbols)
        {
            if (string.Equals(declaredName, symbol, StringComparison.Ordinal)
                || declaredName.StartsWith(symbol + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadFileScopedNamespace(string source)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            source,
            @"^\s*namespace\s+(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*;",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["namespace"].Value : string.Empty;
    }

    private static IEnumerable<string> ReadTopLevelTypeNames(string source)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            source,
            @"^\s*(?:public|internal|private|protected)?\s*(?:sealed\s+|static\s+|abstract\s+|readonly\s+|partial\s+)*" +
            @"(?:class|struct|interface|enum|record(?:\s+struct|\s+class)?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            yield return match.Groups["name"].Value;
        }
    }

    private static bool AssemblyContainsType(string assemblyPath, string fullName)
        => ReadTypeFact(assemblyPath, fullName) is not null;

    private static ProductTypeFact? ReadTypeFact(string assemblyPath, string fullName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        MetadataReader metadata = peReader.GetMetadataReader();

        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition definition = metadata.GetTypeDefinition(handle);
            string candidate = FullName(metadata.GetString(definition.Namespace), metadata.GetString(definition.Name));
            if (string.Equals(candidate, fullName, StringComparison.Ordinal))
            {
                return new ProductTypeFact(ReadMembers(metadata, definition));
            }
        }

        return null;
    }

    private static IReadOnlySet<string> ReadMembers(MetadataReader metadata, TypeDefinition definition)
    {
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (PropertyDefinitionHandle handle in definition.GetProperties())
        {
            PropertyDefinition property = metadata.GetPropertyDefinition(handle);
            members.Add("Property:" + metadata.GetString(property.Name));
        }

        foreach (MethodDefinitionHandle handle in definition.GetMethods())
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if ((method.Attributes & MethodAttributes.SpecialName) == MethodAttributes.SpecialName)
            {
                continue;
            }

            members.Add("Method:" + metadata.GetString(method.Name));
        }

        foreach (FieldDefinitionHandle handle in definition.GetFields())
        {
            FieldDefinition field = metadata.GetFieldDefinition(handle);
            members.Add("Field:" + metadata.GetString(field.Name));
        }

        return members;
    }

    private static string GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        if (constructor.Kind == HandleKind.MemberReference)
        {
            MemberReference reference = metadata.GetMemberReference((MemberReferenceHandle)constructor);
            return GetTypeName(metadata, reference.Parent);
        }

        if (constructor.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinition method = metadata.GetMethodDefinition((MethodDefinitionHandle)constructor);
            TypeDefinition declaringType = metadata.GetTypeDefinition(method.GetDeclaringType());
            return FullName(metadata.GetString(declaringType.Namespace), metadata.GetString(declaringType.Name));
        }

        return string.Empty;
    }

    private static string GetTypeName(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeReference)
        {
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)handle);
            return FullName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
        }

        if (handle.Kind == HandleKind.TypeDefinition)
        {
            TypeDefinition type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
            return FullName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));
        }

        return string.Empty;
    }

    private static string FullName(string namespaceName, string name)
        => string.IsNullOrEmpty(namespaceName) ? name : namespaceName + "." + name;

    private readonly record struct ProductTypeFact(IReadOnlySet<string> Members)
    {
        public bool ContainsMember(ApiMemberContractConfig member)
            => Members.Contains($"{member.Kind}:{member.Name}");
    }

    private sealed class StringAttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly StringAttributeTypeProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            return FullName(reader.GetString(definition.Namespace), reader.GetString(definition.Name));
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference reference = reader.GetTypeReference(handle);
            return FullName(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
        }

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(string type) => string.Equals(type, "System.Type", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ReadProjectInternalsVisibleTo(string projectPath)
    {
        XDocument document = XDocument.Load(projectPath);

        foreach (XElement element in document.Descendants().Where(element => element.Name.LocalName == "InternalsVisibleTo"))
        {
            string? include = element.Attribute("Include")?.Value;
            if (!string.IsNullOrWhiteSpace(include))
            {
                yield return include.Trim();
            }
        }

        foreach (XElement element in document.Descendants().Where(element => element.Name.LocalName == "AssemblyAttribute"))
        {
            string include = element.Attribute("Include")?.Value ?? "";
            if (!string.Equals(include, "System.Runtime.CompilerServices.InternalsVisibleToAttribute", StringComparison.Ordinal))
            {
                continue;
            }

            string? parameter = element
                .Elements()
                .FirstOrDefault(child => child.Name.LocalName == "_Parameter1")
                ?.Value;
            if (!string.IsNullOrWhiteSpace(parameter))
            {
                yield return parameter.Trim();
            }
        }
    }

    [Fact]
    public void DeclaredProjectsDoNotReferenceUndeclaredRepoExternalProjects()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var declaredProjectPaths = AcceptedSolutionProjectPaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.AllProjects())
        {
            string projectFullPath = Path.Combine(repoRoot, project.Path);
            if (!File.Exists(projectFullPath))
            {
                continue;
            }

            foreach (ProjectReferenceDeclaration reference in ProjectReferenceDeclarations(projectFullPath)
                         .Concat(CommonProjectReferenceDeclarations(repoRoot)))
            {
                string declarationRelativePath = Normalize(Path.GetRelativePath(repoRoot, reference.DeclarationFile));
                string referenceFullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(reference.DeclarationFile)!, reference.Include));
                string referenceRelativePath = Normalize(Path.GetRelativePath(repoRoot, referenceFullPath));
                if (referenceRelativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(referenceRelativePath))
                {
                    failures.Add($"{project.Name} declaration {declarationRelativePath} references repo-external project {reference.Include}");
                    continue;
                }

                if (!declaredProjectPaths.Contains(referenceRelativePath))
                {
                    failures.Add($"{project.Name} declaration {declarationRelativePath} references undeclared project {referenceRelativePath}");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared projects reference undeclared or repo-external projects:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredProjectReferenceParsingIncludesProjectLocalPropsAndTargets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessProductBoundaryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectPath = Path.Combine(tempRoot, "SomeEngine.Sample.csproj");
            string importedDirectory = Path.Combine(tempRoot, "build");
            string ignoredDirectory = Path.Combine(tempRoot, "obj");
            Directory.CreateDirectory(importedDirectory);
            Directory.CreateDirectory(ignoredDirectory);

            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.Direct\SomeEngine.Direct.csproj" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(importedDirectory, "Injected.props"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.HiddenProps\SomeEngine.HiddenProps.csproj" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Injected.targets"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.HiddenTargets\SomeEngine.HiddenTargets.csproj" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "AnalyzerOnly.targets"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.AnalyzerOnly\SomeEngine.AnalyzerOnly.csproj" ReferenceOutputAssembly="False" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(ignoredDirectory, "Ignored.props"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.Ignored\SomeEngine.Ignored.csproj" />
                  </ItemGroup>
                </Project>
                """);

            string[] references = ProjectReferenceDeclarations(projectPath)
                .Select(reference => Path.GetFileNameWithoutExtension(Normalize(reference.Include)))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "SomeEngine.Direct",
                    "SomeEngine.HiddenProps",
                    "SomeEngine.HiddenTargets",
                ],
                references);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ExplicitProjectImportScanRejectsUnscannedDeclarationFiles()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessExplicitImports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectDirectory = Path.Combine(tempRoot, "src", "SomeEngine.Sample");
            string buildDirectory = Path.Combine(tempRoot, "build");
            Directory.CreateDirectory(projectDirectory);
            Directory.CreateDirectory(buildDirectory);

            string projectPath = Path.Combine(projectDirectory, "SomeEngine.Sample.csproj");
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <Import Project="..\..\build\Hidden.props" />
                </Project>
                """);

            File.WriteAllText(Path.Combine(buildDirectory, "Hidden.props"), "<Project />");

            var failures = FindUnscannedExplicitImports(tempRoot, ProjectReferenceDeclarationFiles(projectPath));

            Assert.Single(failures);
            Assert.Contains("Hidden.props", failures[0]);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CommonProjectDeclarationFilesIncludeRootBuildDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessCommonDeclarations", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            File.WriteAllText(Path.Combine(tempRoot, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(tempRoot, "Directory.Build.targets"), "<Project />");
            File.WriteAllText(Path.Combine(tempRoot, "Directory.Packages.props"), "<Project />");

            string[] files = CommonProjectDeclarationFiles(tempRoot)
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

    private static IEnumerable<string> AcceptedSolutionProjectPaths()
    {
        foreach (ProjectConfig project in Config.Projects.AllProjects())
        {
            yield return Normalize(project.Path);
        }

        foreach (SourceProjectDependencyConfig source in Config.ExternalDependencies.SourceProjects)
        {
            yield return Normalize(source.Path);
        }

        foreach (LocalPackageConfig package in Config.ExternalDependencies.LocalPackages)
        {
            yield return Normalize(package.ProducerProject);
        }
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/');

    private static IEnumerable<string> ProjectDeclarationFiles(string projectDirectory)
        => Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets")
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SourceFiles(string projectDirectory)
        => Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
            .Where(path => IsProductSourceBoundaryExtension(Path.GetExtension(path)))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static bool IsProductSourceBoundaryExtension(string extension)
        => NormalizeExtension(extension) is ".cs"
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

    private static IEnumerable<ProjectReferenceDeclaration> ProjectReferenceDeclarations(string projectPath)
    {
        foreach (string declarationFile in ProjectReferenceDeclarationFiles(projectPath))
        {
            foreach (ProjectReferenceDeclaration reference in ProjectReferenceDeclarationsFromFile(declarationFile))
            {
                yield return reference;
            }
        }
    }

    private static IEnumerable<ProjectReferenceDeclaration> CommonProjectReferenceDeclarations(string repoRoot)
    {
        foreach (string declarationFile in CommonProjectDeclarationFiles(repoRoot))
        {
            foreach (ProjectReferenceDeclaration reference in ProjectReferenceDeclarationsFromFile(declarationFile))
            {
                yield return reference;
            }
        }
    }

    private static IEnumerable<ProjectReferenceDeclaration> ProjectReferenceDeclarationsFromFile(string declarationFile)
    {
            XDocument document = XDocument.Load(declarationFile);
            foreach (XElement reference in document.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                if (string.Equals(
                        reference.Attribute("ReferenceOutputAssembly")?.Value,
                        "false",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string include = reference.Attribute("Include")?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(include))
                {
                    yield return new ProjectReferenceDeclaration(declarationFile, include);
                }
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
                string relativeDeclaration = Normalize(Path.GetRelativePath(repoRoot, declarationFile));
                if (!TryResolveImportPath(declarationFile, importProject, out string? importFullPath))
                {
                    failures.Add($"{relativeDeclaration} imports '{importProject}', which cannot be resolved as a stable first-round declaration file.");
                    continue;
                }

                if (!scannedFiles.Contains(importFullPath!))
                {
                    string relativeImport = Normalize(Path.GetRelativePath(repoRoot, importFullPath!));
                    failures.Add($"{relativeDeclaration} imports unscanned first-round declaration file {relativeImport}.");
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

    private static IEnumerable<string> ProjectReferenceDeclarationFiles(string projectPath)
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
    }

    private readonly record struct ProjectReferenceDeclaration(string DeclarationFile, string Include);

    private static IEnumerable<string> CommonProjectDeclarationFiles(string repoRoot)
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

    private static IEnumerable<string> ReadRemovedCompileItems(string declarationFile)
    {
        XDocument document = XDocument.Load(declarationFile);
        return document
            .Descendants()
            .Where(element => element.Name.LocalName == "Compile")
            .Select(element => element.Attribute("Remove")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Replace('\\', '/'))
            .ToList();
    }

    private static void AddFileIfExists(List<string> files, string path)
    {
        if (File.Exists(path))
        {
            files.Add(path);
        }
    }

    private static bool ContainsForbiddenBoundaryToken(string text, string token)
    {
        if (RequiresExactIdentifierMatch(token))
        {
            return ContainsExactIdentifier(text, token);
        }

        return text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsForbiddenSourceToken(string text, string token)
    {
        if (RequiresExactIdentifierMatch(token))
        {
            return ContainsExactIdentifier(text, token, ExactSourceTokenComparison(token));
        }

        return text.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SourceForbiddenReferencesForProject(ProjectConfig project, IEnumerable<string> forbiddenReferences)
    {
        foreach (string forbidden in forbiddenReferences)
        {
            if (IsAllowedDomainBoundaryReference(project.Name, forbidden))
            {
                continue;
            }

            if (IsAcceptedGraphicsBoundaryToken(project.Name, forbidden))
            {
                continue;
            }

            if (IsCommonIntegrationWord(forbidden) && !IsRenderOrAssetBoundaryProject(project.Name))
            {
                continue;
            }

            yield return forbidden;
        }
    }

    private static bool IsAllowedDomainBoundaryReference(string projectName, string token)
        => Config.Architecture.DomainBoundaries.Any(boundary =>
            string.Equals(boundary.Name, projectName, StringComparison.Ordinal) &&
            boundary.AllowedReferences.Contains(token, StringComparer.OrdinalIgnoreCase));

    private static bool IsCommonIntegrationWord(string token)
        => token is "Present" or "Window";

    private static bool IsRenderOrAssetBoundaryProject(string projectName)
        => projectName is "SomeEngine.Assets" or "SomeEngine.Render" or "SomeEngine.Render.Cluster";

    private static bool IsAcceptedGraphicsBoundaryToken(string projectName, string token)
    {
        if ((projectName is "SomeEngine.Assets.Importers" or "SomeEngine.AssetCook") && token == "D3D12")
        {
            return true;
        }

        bool portableGraphicsTerm = token is
            "Swapchain" or "SwapChain" or "Device" or "IQueue" or "ISwapchain" or
            "Present" or "BufferHandle" or "TextureHandle" or "RenderContext" or
            "PipelineCache" or "GpuResource" or "GpuResourceHandle" or "GpuBuffer" or
            "GpuBufferHandle" or "GpuTexture" or "GpuTextureHandle" or "RenderPass" or
            "RenderPipeline" or "ComputePipeline" or "GraphicsPipeline" or "PipelineState" or
            "DescriptorSet" or "RenderEncoder" or "ComputeEncoder";

        if (projectName == "SomeEngine.Graphics")
        {
            return portableGraphicsTerm || token is "Rhi" or "D3D12" or "Direct3D" or "RenderGraph";
        }

        if (projectName == "SomeEngine.Graphics.Null")
        {
            return portableGraphicsTerm || token is "Rhi" or "D3D12" or "Direct3D";
        }

        if (projectName == "SomeEngine.Graphics.Direct3D12")
        {
            return portableGraphicsTerm || token is
                "Rhi" or "D3D12" or "Direct3D" or "DXGI" or "SharpGen" or "DeviceContext" or
                "ShaderResourceBinding" or "RootSignature";
        }

        if (projectName == "SomeEngine.RenderGraph.Sample")
        {
            return portableGraphicsTerm || token is
                "Rhi" or "D3D12" or "Direct3D" or "RenderGraph" or "RenderGraphHandle";
        }

        if (projectName == "SomeEngine.Graphics.Benchmarks")
        {
            return portableGraphicsTerm || token is
                "Rhi" or "D3D12" or "Direct3D" or "RenderGraph" or "RenderGraphHandle";
        }

        return projectName == "SomeEngine.RenderGraph" &&
               (portableGraphicsTerm || token is "Rhi" or "RenderGraph" or "RenderGraphHandle");
    }

    private static bool IsAcceptedRenderGraphGeneratorSource(
        string projectName,
        string token,
        string relativePath) =>
        projectName == "SomeEngine.Generators" &&
        token == "RenderGraph" &&
        relativePath.Equals(
            "src/SomeEngine.Generators/RenderGraphParameterGenerator.cs",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsAcceptedRenderGraphGeneratorSymbol(
        string projectName,
        string token,
        string declaredName)
    {
        const string prefix = "SomeEngine.Generators.RenderGraphParameterGenerator";
        return projectName == "SomeEngine.Generators" &&
               token == "RenderGraph" &&
               (declaredName.Equals(prefix, StringComparison.Ordinal) ||
                declaredName.StartsWith(prefix + ".", StringComparison.Ordinal) ||
                declaredName.StartsWith(prefix + "+", StringComparison.Ordinal));
    }

    private static bool RequiresExactIdentifierMatch(string token)
        => token is "SharpGen" or "Present" or "Window" or "Windowing" or "Rhi";

    private static StringComparison ExactSourceTokenComparison(string token)
        => token is "Present" or "Window"
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

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
}


