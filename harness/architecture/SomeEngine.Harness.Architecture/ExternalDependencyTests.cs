using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class ExternalDependencyTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void DeclaredExternalSubmodulesAreGitlinksAtPinnedCommits()
    {
        var failures = new List<string>();
        var gitmodulesPath = FullPath(".gitmodules");

        if (!File.Exists(gitmodulesPath))
        {
            failures.Add(".gitmodules must exist because external source dependencies are submodules, not vendored main-repo source.");
        }

        var gitmodules = File.Exists(gitmodulesPath)
            ? ReadGitmodules(gitmodulesPath)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var declaredSubmodulePaths = Config.ExternalDependencies.Submodules
            .Select(submodule => Normalize(submodule.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var extraPath in gitmodules.Keys.Where(path => !declaredSubmodulePaths.Contains(path)).Order(StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($".gitmodules declares undeclared external submodule {extraPath}; excluded/reference external trees must not enter the migrated repository.");
        }

        foreach (var submodule in Config.ExternalDependencies.Submodules)
        {
            var path = Normalize(submodule.Path);
            if (!gitmodules.TryGetValue(path, out var url))
            {
                failures.Add($".gitmodules must declare submodule path {path}");
            }
            else if (!string.Equals(url, submodule.Url, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Submodule {path} url must be {submodule.Url}, not {url}");
            }

            var entry = ReadGitIndexEntry(path);
            if (entry is null)
            {
                failures.Add($"Submodule {path} must be tracked as a gitlink, not as vendored files in the main repository.");
                continue;
            }

            if (!string.Equals(entry.Mode, "160000", StringComparison.Ordinal))
            {
                failures.Add($"Submodule {path} must use gitlink mode 160000, not {entry.Mode}.");
            }

            if (!string.Equals(entry.ObjectId, submodule.Commit, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Submodule {path} must be pinned to {submodule.Commit}, not {entry.ObjectId}.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "External submodule declarations are invalid:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DeclaredExternalSourceDependenciesLiveInsideSubmodules()
    {
        var failures = new List<string>();

        foreach (var package in Config.ExternalDependencies.LocalPackages)
        {
            RequireInsideDeclaredSubmodule(package.ProducerProject, failures);
        }

        foreach (var dependency in Config.ExternalDependencies.SourceProjects)
        {
            RequireInsideDeclaredSubmodule(dependency.Path, failures);
        }

        foreach (var dependency in Config.ExternalDependencies.NativeSources)
        {
            RequireInsideDeclaredSubmodule(dependency.Root, failures);
            foreach (var relativeFile in dependency.RequiredFiles)
            {
                RequireInsideDeclaredSubmodule(Path.Combine(dependency.Root, relativeFile), failures);
            }
        }

        Assert.True(
            failures.Count == 0,
            "External source dependencies must be supplied by declared submodules:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ExcludedExternalSourceRootsAreAbsentFromMigratedWorkspace()
    {
        var failures = new List<string>();

        foreach (var root in Config.ExternalDependencies.ExcludedSourceRoots.Select(Normalize))
        {
            if (Directory.Exists(FullPath(root)) || File.Exists(FullPath(root)))
            {
                failures.Add($"Excluded external source root {root} must not exist in this migrated workspace; keep it only in the legacy reference repository.");
            }

            var trackedEntries = RunGit("ls-files", "--", root)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (trackedEntries.Length > 0)
            {
                failures.Add($"Excluded external source root {root} must not be tracked by the migrated repository, but git tracks: {string.Join(", ", trackedEntries.Take(5))}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Excluded external source roots entered the migrated workspace:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void LocalPackageDependenciesKeepDeclaredProducerConsumerAndContent()
    {
        var failures = new List<string>();

        foreach (var package in Config.ExternalDependencies.LocalPackages)
        {
            RequireFile(package.ProducerProject, failures);
            RequireFile(package.ConsumerProject, failures);
            RequireFile(package.SourcePackage, failures);
            RequireDirectory(package.LocalFeed, failures);

            if (File.Exists(FullPath(package.ProducerProject)))
            {
                var producerVersion = ReadProperty(FullPath(package.ProducerProject), "VersionPrefix");
                if (!string.Equals(package.Version, producerVersion, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{package.PackageId} config version must match producer VersionPrefix: config={package.Version}, producer={producerVersion}");
                }
            }

            if (File.Exists(FullPath(package.ConsumerProject)))
            {
                var packageReference = ReadPackageReference(FullPath(package.ConsumerProject), package.PackageId);
                if (packageReference is null)
                {
                    failures.Add($"{package.ConsumerProject} must consume {package.PackageId} with PackageReference");
                }
                else if (!string.Equals(packageReference.Version, package.Version, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"{package.PackageId} PackageReference version must equal config version: consumer={packageReference.Version}, config={package.Version}");
                }
            }

            if (File.Exists(FullPath(package.SourcePackage)))
            {
                var entries = ReadPackageEntries(FullPath(package.SourcePackage));
                var assemblyName = package.PackageId + ".dll";
                if (!entries.Any(entry => entry.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                                          && entry.EndsWith("/" + assemblyName, StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add($"{package.SourcePackage} must contain managed assembly {assemblyName} under lib/<tfm>/");
                }

                foreach (var requiredAsset in package.RequiredRuntimeAssets)
                {
                    if (!entries.Contains(NormalizePackageEntry(requiredAsset), StringComparer.OrdinalIgnoreCase))
                    {
                        failures.Add($"{package.SourcePackage} must contain {requiredAsset}");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Local package dependency facts are invalid:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void SourceProjectDependenciesKeepDeclaredConsumerReferences()
    {
        var failures = new List<string>();

        foreach (var dependency in Config.ExternalDependencies.SourceProjects)
        {
            RequireFile(dependency.Path, failures);
            RequireFile(dependency.ConsumerProject, failures);

            if (File.Exists(FullPath(dependency.ConsumerProject)))
            {
                var references = ReadProjectReferences(FullPath(dependency.ConsumerProject));
                if (!references.Contains(Normalize(dependency.Path), StringComparer.OrdinalIgnoreCase))
                {
                    failures.Add($"{dependency.ConsumerProject} must ProjectReference {dependency.Path}");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Source project dependency facts are invalid:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void BinaryAndNativeDependenciesKeepDeclaredFiles()
    {
        var failures = new List<string>();

        foreach (var asset in Config.ExternalDependencies.BinaryAssets)
        {
            foreach (var path in asset.Paths)
            {
                RequireFile(path, failures);
            }
        }

        foreach (var dependency in Config.ExternalDependencies.NativeSources)
        {
            RequireDirectory(dependency.Root, failures);
            foreach (var relativeFile in dependency.RequiredFiles)
            {
                RequireFile(Path.Combine(dependency.Root, relativeFile), failures);
            }
        }

        Assert.True(
            failures.Count == 0,
            "Binary/native external dependency facts are invalid:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ExternalConsumerReferenceParsingIncludesProjectLocalPropsTargetsAndRootBuildDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessExternalDependencyTests", Guid.NewGuid().ToString("N"));
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
                    <ProjectReference Include="..\SomeEngine.Direct\SomeEngine.Direct.csproj" />
                    <PackageReference Include="SomeEngine.Local.Direct" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(buildDirectory, "Injected.props"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.HiddenProps\SomeEngine.HiddenProps.csproj" />
                    <PackageReference Include="SomeEngine.Local.Props" Version="2.0.0" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Injected.targets"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.HiddenTargets\SomeEngine.HiddenTargets.csproj" />
                    <PackageReference Include="SomeEngine.Local.Targets" Version="3.0.0" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.props"),
                """
                <Project>
                  <ItemGroup>
                    <ProjectReference Include="..\SomeEngine.RootProps\SomeEngine.RootProps.csproj" />
                    <PackageReference Include="SomeEngine.Local.RootProps" Version="4.0.0" />
                    <ProjectReference Include="$(AnalyzerOnly)" ReferenceOutputAssembly="False" />
                  </ItemGroup>
                </Project>
                """);

            string[] references = ReadProjectReferences(projectPath, tempRoot)
                .Select(path => Path.GetFileNameWithoutExtension(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "SomeEngine.Direct",
                    "SomeEngine.HiddenProps",
                    "SomeEngine.HiddenTargets",
                    "SomeEngine.RootProps",
                ],
                references);

            Assert.Equal("1.0.0", ReadPackageReference(projectPath, "SomeEngine.Local.Direct", tempRoot)?.Version);
            Assert.Equal("2.0.0", ReadPackageReference(projectPath, "SomeEngine.Local.Props", tempRoot)?.Version);
            Assert.Equal("3.0.0", ReadPackageReference(projectPath, "SomeEngine.Local.Targets", tempRoot)?.Version);
            Assert.Equal("4.0.0", ReadPackageReference(projectPath, "SomeEngine.Local.RootProps", tempRoot)?.Version);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void FirstRoundNuGetPackageReferencesMatchAcceptedCatalog()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();
        var productProjects = Config.Projects.ProductProjects.Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
        var buildSupportProjects = Config.Projects.BuildSupportProjects.Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
        var testProjects = Config.Projects.TestProjects.Select(project => project.Name).ToHashSet(StringComparer.Ordinal);

        RequirePackageCatalogTargets("product", Config.NuGetPackages.ProductPackages, productProjects, failures);
        RequirePackageCatalogTargets("build-support", Config.NuGetPackages.BuildSupportPackages, buildSupportProjects, failures);
        RequirePackageCatalogTargets("test", Config.NuGetPackages.TestPackages, testProjects, failures);

        var expected = Config.NuGetPackages.AllPackages()
            .Select(package => NuGetPackageFact(package.Project, package.PackageId, package.Version))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProjectConfig project in Config.Projects.AllProjects())
        {
            string projectPath = FullPath(project.Path);
            if (!File.Exists(projectPath))
            {
                continue;
            }

            foreach (NuGetPackageReference package in ReadNuGetPackageReferences(projectPath, repoRoot))
            {
                string relativeDeclaration = Normalize(Path.GetRelativePath(repoRoot, package.DeclarationFile));
                if (string.IsNullOrWhiteSpace(package.Version))
                {
                    failures.Add($"{project.Name} package {package.PackageId} in {relativeDeclaration} must have a concrete accepted version.");
                    continue;
                }

                string fact = NuGetPackageFact(project.Name, package.PackageId, package.Version);
                if (!actual.Add(fact))
                {
                    failures.Add($"{project.Name} declares duplicate accepted NuGet package {package.PackageId} {package.Version}.");
                }
            }
        }

        foreach (string extra in actual.Except(expected, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"Declared first-round NuGet package is not accepted by harness/config.json: {extra}");
        }

        foreach (string missing in expected.Except(actual, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"Accepted first-round NuGet package is not declared by project files: {missing}");
        }

        Assert.True(
            failures.Count == 0,
            "First-round NuGet package references must match the accepted package catalog:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void NuGetPackageCatalogDoesNotPermitExcludedFirstRoundBoundaryNames()
    {
        var forbiddenReferences = Config.Architecture.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Concat(Config.ProductTests.ForbiddenBoundaryReferences)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (AllowedNuGetPackageConfig package in Config.NuGetPackages.AllPackages())
        {
            string packageFact = NuGetPackageFact(package.Project, package.PackageId, package.Version);
            foreach (string forbidden in forbiddenReferences)
            {
                if (IsAcceptedGraphicsPackageToken(package.Project, forbidden))
                {
                    continue;
                }

                if (ContainsForbiddenDependencyToken(package.Project, forbidden)
                    || ContainsForbiddenDependencyToken(package.PackageId, forbidden))
                {
                    failures.Add($"{packageFact} permits excluded first-round boundary token '{forbidden}'.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Accepted NuGet package catalog permits excluded first-round boundary names:\n" + string.Join("\n", failures));
    }

    private static bool IsAcceptedGraphicsPackageToken(string project, string token) =>
        (project is ("SomeEngine.Graphics.Direct3D12" or "SomeEngine.Graphics.Direct3D12.Tests") &&
         token is ("D3D12" or "Direct3D" or "SharpGen")) ||
        (project == "SomeEngine.RenderGraph.Tests" && token == "RenderGraph");

    [Fact]
    public void NuGetPackageReferenceParsingIncludesProjectLocalPropsTargetsAndCentralVersions()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessNuGetPackages", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectDirectory = Path.Combine(tempRoot, "src", "SomeEngine.Sample");
            string buildDirectory = Path.Combine(projectDirectory, "build");
            Directory.CreateDirectory(buildDirectory);

            string projectPath = Path.Combine(projectDirectory, "SomeEngine.Sample.csproj");
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Some.Direct" Version="1.0.0" />
                    <PackageReference Include="Some.Central" />
                    <PackageReference Include="Some.Override" VersionOverride="6.0.0" />
                    <PackageReference Update="Some.Updated" VersionOverride="6.1.0" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(buildDirectory, "Local.props"),
                """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Some.LocalProps" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(buildDirectory, "Local.targets"),
                """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Some.LocalTargets">
                      <Version>3.0.0</Version>
                    </PackageReference>
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.props"),
                """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Some.Root" Version="4.0.0" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Packages.props"),
                """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="Some.Central" Version="5.0.0" />
                    <PackageVersion Include="Some.LocalProps" Version="2.0.0" />
                    <PackageVersion Update="Some.Updated" Version="1.0.0" />
                    <PackageVersion Include="Some.Override" Version="1.0.0" />
                    <GlobalPackageReference Include="Some.Global" Version="7.0.0" />
                    <PackageDownload Include="Some.Download" Version="[8.0.0]" />
                  </ItemGroup>
                </Project>
                """);

            string[] packages = ReadNuGetPackageReferences(projectPath, tempRoot)
                .Select(package => $"{package.PackageId}:{package.Version}")
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "Some.Central:5.0.0",
                    "Some.Direct:1.0.0",
                    "Some.Download:[8.0.0]",
                    "Some.Global:7.0.0",
                    "Some.LocalProps:2.0.0",
                    "Some.LocalTargets:3.0.0",
                    "Some.Override:6.0.0",
                    "Some.Root:4.0.0",
                    "Some.Updated:6.1.0",
                ],
                packages);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void FirstRoundDirectAssemblyReferencesMatchAcceptedCatalog()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();
        var productProjects = Config.Projects.ProductProjects.Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
        var buildSupportProjects = Config.Projects.BuildSupportProjects.Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
        var testProjects = Config.Projects.TestProjects.Select(project => project.Name).ToHashSet(StringComparer.Ordinal);

        RequireDirectReferenceCatalogTargets("product", Config.DirectAssemblyReferences.ProductReferences, productProjects, failures);
        RequireDirectReferenceCatalogTargets("build-support", Config.DirectAssemblyReferences.BuildSupportReferences, buildSupportProjects, failures);
        RequireDirectReferenceCatalogTargets("test", Config.DirectAssemblyReferences.TestReferences, testProjects, failures);

        var expected = Config.DirectAssemblyReferences.AllReferences()
            .Select(reference => DirectAssemblyReferenceFact(reference.Project, reference.Include, reference.HintPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ProjectConfig project in Config.Projects.AllProjects())
        {
            string projectPath = FullPath(project.Path);
            if (!File.Exists(projectPath))
            {
                continue;
            }

            foreach (DirectAssemblyReference reference in ReadDirectAssemblyReferences(projectPath, repoRoot))
            {
                string fact = DirectAssemblyReferenceFact(project.Name, reference.Include, reference.HintPath);
                if (!actual.Add(fact))
                {
                    failures.Add($"{project.Name} declares duplicate direct assembly reference {reference.Include} at {reference.HintPath}.");
                }
            }
        }

        foreach (string extra in actual.Except(expected, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"Declared first-round direct assembly reference is not accepted by harness/config.json: {extra}");
        }

        foreach (string missing in expected.Except(actual, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            failures.Add($"Accepted first-round direct assembly reference is not declared by project files: {missing}");
        }

        Assert.True(
            failures.Count == 0,
            "First-round direct assembly references must match the accepted reference catalog:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DirectAssemblyReferenceCatalogDoesNotPermitExcludedFirstRoundBoundaryNames()
    {
        var forbiddenReferences = Config.Architecture.ForbiddenBoundaryReferences
            .Concat(Config.Architecture.ExcludedProjectNames)
            .Concat(Config.ProductTests.ForbiddenBoundaryReferences)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var failures = new List<string>();

        foreach (AllowedDirectAssemblyReferenceConfig reference in Config.DirectAssemblyReferences.AllReferences())
        {
            string referenceFact = DirectAssemblyReferenceFact(reference.Project, reference.Include, reference.HintPath);
            foreach (string forbidden in forbiddenReferences)
            {
                if (ContainsForbiddenDependencyToken(reference.Project, forbidden)
                    || ContainsForbiddenDependencyToken(reference.Include, forbidden)
                    || ContainsForbiddenDependencyToken(reference.HintPath, forbidden))
                {
                    failures.Add($"{referenceFact} permits excluded first-round boundary token '{forbidden}'.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Accepted direct assembly reference catalog permits excluded first-round boundary names:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DirectAssemblyReferenceParsingIncludesProjectLocalPropsTargetsAndRootBuildDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessDirectAssemblyReferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectDirectory = Path.Combine(tempRoot, "tests", "SomeEngine.Sample.Tests");
            string buildDirectory = Path.Combine(projectDirectory, "build");
            string repoAssemblyDirectory = Path.Combine(tempRoot, "src", "SomeEngine.Sample", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(buildDirectory);
            Directory.CreateDirectory(repoAssemblyDirectory);

            string projectPath = Path.Combine(projectDirectory, "SomeEngine.Sample.Tests.csproj");
            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <Reference Include="Some.Direct" HintPath="..\..\src\SomeEngine.Sample\bin\Debug\net10.0\Some.Direct.dll" />
                    <Reference Update="Some.Updated" HintPath="..\..\src\SomeEngine.Sample\bin\Debug\net10.0\Some.Updated.dll" />
                    <Reference Update="Some.MetadataOnly" Private="false" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(buildDirectory, "Local.props"),
                """
                <Project>
                  <ItemGroup>
                    <Reference Include="Some.LocalProps">
                      <HintPath>..\..\..\src\SomeEngine.Sample\bin\Debug\net10.0\Some.LocalProps.dll</HintPath>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(projectDirectory, "Local.targets"),
                """
                <Project>
                  <ItemGroup>
                    <Reference Include="Some.LocalTargets" HintPath="C:\Users\sample\.nuget\packages\some.localtargets\1.2.3\lib\netstandard2.0\Some.LocalTargets.dll" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.props"),
                """
                <Project>
                  <ItemGroup>
                    <Reference Include="Some.Root" HintPath="src\SomeEngine.Sample\bin\Debug\net10.0\Some.Root.dll" />
                  </ItemGroup>
                </Project>
                """);

            string[] references = ReadDirectAssemblyReferences(projectPath, tempRoot)
                .Select(reference => $"{reference.Include}:{reference.HintPath}")
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "Some.Direct:src/SomeEngine.Sample/bin/Debug/net10.0/Some.Direct.dll",
                    "Some.LocalProps:src/SomeEngine.Sample/bin/Debug/net10.0/Some.LocalProps.dll",
                    "Some.LocalTargets:$HOME/.nuget/packages/some.localtargets/1.2.3/lib/netstandard2.0/Some.LocalTargets.dll",
                    "Some.Root:src/SomeEngine.Sample/bin/Debug/net10.0/Some.Root.dll",
                    "Some.Updated:src/SomeEngine.Sample/bin/Debug/net10.0/Some.Updated.dll",
                ],
                references);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DeclaredRepoOwnedBinaryDependencyFilesAreNotGitIgnored()
    {
        var failures = new List<string>();

        foreach (var path in Config.ExternalDependencies.BinaryAssets.SelectMany(asset => asset.Paths).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsGitIgnored(path))
            {
                failures.Add($"Repo-owned binary dependency path must be committable, but .gitignore ignores {path}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Declared repo-owned binary dependency paths are hidden by .gitignore:\n" + string.Join("\n", failures));
    }

    private static void RequireInsideDeclaredSubmodule(string relativePath, List<string> failures)
    {
        var path = Normalize(relativePath);
        if (!Config.ExternalDependencies.Submodules.Any(submodule => IsWithin(path, Normalize(submodule.Path))))
        {
            failures.Add($"{path} must live under a declared external submodule, not as main-repo vendored source.");
        }
    }

    private static bool IsWithin(string path, string root)
        => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith(root.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> ReadGitmodules(string gitmodulesPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentPath = null;

        foreach (var rawLine in File.ReadAllLines(gitmodulesPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[submodule ", StringComparison.Ordinal))
            {
                currentPath = null;
                continue;
            }

            if (line.StartsWith("path =", StringComparison.Ordinal))
            {
                currentPath = Normalize(line["path =".Length..].Trim());
                continue;
            }

            if (currentPath is not null && line.StartsWith("url =", StringComparison.Ordinal))
            {
                result[currentPath] = line["url =".Length..].Trim();
            }
        }

        return result;
    }

    private static GitIndexEntry? ReadGitIndexEntry(string relativePath)
    {
        var output = RunGit("ls-files", "--stage", "--", relativePath);
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (line is null)
        {
            return null;
        }

        var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return fields.Length < 2
            ? null
            : new GitIndexEntry(fields[0], fields[1]);
    }

    private static bool IsGitIgnored(string relativePath)
    {
        var exitCode = RunGitExitCode("check-ignore", "--quiet", "--", relativePath);
        return exitCode == 0;
    }

    private static string RunGit(params string[] arguments)
    {
        var process = CreateGitProcess(arguments);
        using var git = Process.Start(process) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = git.StandardOutput.ReadToEnd();
        var stderr = git.StandardError.ReadToEnd();
        git.WaitForExit(5000);
        if (git.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit code {git.ExitCode}: {stderr}");
        }

        return stdout;
    }

    private static int RunGitExitCode(params string[] arguments)
    {
        var process = CreateGitProcess(arguments);
        using var git = Process.Start(process);
        if (git is null)
        {
            return -1;
        }

        git.WaitForExit(5000);
        return git.ExitCode;
    }

    private static ProcessStartInfo CreateGitProcess(params string[] arguments)
    {
        var process = new ProcessStartInfo("git")
        {
            WorkingDirectory = HarnessConfig.ResolveRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
        {
            process.ArgumentList.Add(argument);
        }

        return process;
    }

    private static void RequireFile(string relativePath, List<string> failures)
    {
        if (!File.Exists(FullPath(relativePath)))
        {
            failures.Add($"Required file must exist at {Normalize(relativePath)}");
        }
    }

    private static void RequireDirectory(string relativePath, List<string> failures)
    {
        if (!Directory.Exists(FullPath(relativePath)))
        {
            failures.Add($"Required directory must exist at {Normalize(relativePath)}");
        }
    }

    private static void RequirePackageCatalogTargets(
        string bucket,
        IEnumerable<AllowedNuGetPackageConfig> packages,
        HashSet<string> allowedProjects,
        List<string> failures)
    {
        foreach (AllowedNuGetPackageConfig package in packages)
        {
            if (string.IsNullOrWhiteSpace(package.Project)
                || string.IsNullOrWhiteSpace(package.PackageId)
                || string.IsNullOrWhiteSpace(package.Version))
            {
                failures.Add($"Accepted {bucket} NuGet package catalog entries must declare project, packageId, and version.");
                continue;
            }

            if (!allowedProjects.Contains(package.Project))
            {
                failures.Add($"Accepted {bucket} NuGet package {package.PackageId} targets {package.Project}, which is outside that first-round project bucket.");
            }
        }
    }

    private static void RequireDirectReferenceCatalogTargets(
        string bucket,
        IEnumerable<AllowedDirectAssemblyReferenceConfig> references,
        HashSet<string> allowedProjects,
        List<string> failures)
    {
        foreach (AllowedDirectAssemblyReferenceConfig reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference.Project)
                || string.IsNullOrWhiteSpace(reference.Include))
            {
                failures.Add($"Accepted {bucket} direct assembly reference catalog entries must declare project and include.");
                continue;
            }

            if (!allowedProjects.Contains(reference.Project))
            {
                failures.Add($"Accepted {bucket} direct assembly reference {reference.Include} targets {reference.Project}, which is outside that first-round project bucket.");
            }
        }
    }

    private static HashSet<string> ReadProjectReferences(string projectPath, string? commonDeclarationRoot = null)
    {
        string root = commonDeclarationRoot ?? HarnessConfig.ResolveRepoRoot();
        return ProjectReferenceDeclarations(projectPath, root)
            .Select(reference => Normalize(Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(reference.DeclarationFile)!, reference.Include)))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<NuGetPackageReference> ReadNuGetPackageReferences(string projectPath, string root)
    {
        string[] declarationFiles = ProjectDeclarationFiles(projectPath, root)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var centralVersions = ReadCentralPackageVersions(declarationFiles);

        foreach (string declarationFile in declarationFiles)
        {
            XDocument document = XDocument.Load(declarationFile);
            foreach (XElement reference in document.Descendants().Where(element => element.Name.LocalName == "PackageReference"))
            {
                string packageId = ReadPackageIdentity(reference);
                if (string.IsNullOrWhiteSpace(packageId))
                {
                    continue;
                }

                string version = ReadPackageReferenceVersion(reference, centralVersions)
                    ?? "";
                yield return new NuGetPackageReference(declarationFile, packageId, version);
            }

            foreach (XElement reference in document.Descendants().Where(element => element.Name.LocalName == "GlobalPackageReference"))
            {
                string packageId = ReadPackageIdentity(reference);
                if (string.IsNullOrWhiteSpace(packageId))
                {
                    continue;
                }

                string version = ReadVersion(reference) ?? "";
                yield return new NuGetPackageReference(declarationFile, packageId, version);
            }

            foreach (XElement reference in document.Descendants().Where(element => element.Name.LocalName == "PackageDownload"))
            {
                string packageId = ReadPackageIdentity(reference);
                if (string.IsNullOrWhiteSpace(packageId))
                {
                    continue;
                }

                string version = ReadVersion(reference) ?? "";
                yield return new NuGetPackageReference(declarationFile, packageId, version);
            }
        }
    }

    private static Dictionary<string, string> ReadCentralPackageVersions(IEnumerable<string> declarationFiles)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string declarationFile in declarationFiles)
        {
            XDocument document = XDocument.Load(declarationFile);
            foreach (XElement version in document.Descendants().Where(element => element.Name.LocalName == "PackageVersion"))
            {
                string packageId = ReadPackageIdentity(version);
                string? packageVersion = ReadVersion(version);
                if (!string.IsNullOrWhiteSpace(packageId) && !string.IsNullOrWhiteSpace(packageVersion))
                {
                    versions[packageId] = packageVersion;
                }
            }
        }

        return versions;
    }

    private static string? ReadPackageReferenceVersion(XElement element, Dictionary<string, string> centralVersions)
    {
        string? versionOverride = ReadNamedVersion(element, "VersionOverride");
        if (!string.IsNullOrWhiteSpace(versionOverride))
        {
            return versionOverride;
        }

        string? version = ReadVersion(element);
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        string packageId = ReadPackageIdentity(element);
        return string.IsNullOrWhiteSpace(packageId)
            ? null
            : centralVersions.GetValueOrDefault(packageId);
    }

    private static string ReadPackageIdentity(XElement element)
        => element.Attribute("Include")?.Value?.Trim()
           ?? element.Attribute("Update")?.Value?.Trim()
           ?? "";

    private static string? ReadVersion(XElement element)
        => ReadNamedVersion(element, "Version");

    private static string? ReadNamedVersion(XElement element, string versionElementName)
    {
        string? attributeVersion = element.Attribute(versionElementName)?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(attributeVersion))
        {
            return attributeVersion;
        }

        string? childVersion = element
            .Elements()
            .FirstOrDefault(child => child.Name.LocalName == versionElementName)
            ?.Value
            .Trim();
        return string.IsNullOrWhiteSpace(childVersion)
            ? null
            : childVersion;
    }

    private static string NuGetPackageFact(string project, string packageId, string version)
        => $"{project}:{packageId}:{version}";

    private static IEnumerable<DirectAssemblyReference> ReadDirectAssemblyReferences(string projectPath, string root)
    {
        foreach (string declarationFile in ProjectDeclarationFiles(projectPath, root).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            XDocument document = XDocument.Load(declarationFile);
            foreach (XElement reference in document.Descendants().Where(element => element.Name.LocalName == "Reference"))
            {
                string include = ReadReferenceIdentity(reference);
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                string hintPath = ReadReferenceHintPath(reference);
                if (reference.Attribute("Include") is null && string.IsNullOrWhiteSpace(hintPath))
                {
                    continue;
                }

                yield return new DirectAssemblyReference(
                    declarationFile,
                    include,
                    NormalizeDirectAssemblyHintPath(hintPath, declarationFile, root));
            }
        }
    }

    private static string ReadReferenceIdentity(XElement element)
        => element.Attribute("Include")?.Value?.Trim()
           ?? element.Attribute("Update")?.Value?.Trim()
           ?? "";

    private static string ReadReferenceHintPath(XElement element)
        => element.Attribute("HintPath")?.Value?.Trim()
           ?? element
               .Elements()
               .FirstOrDefault(child => child.Name.LocalName == "HintPath")
               ?.Value
               .Trim()
           ?? "";

    private static string NormalizeDirectAssemblyHintPath(string hintPath, string declarationFile, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(hintPath))
        {
            return "";
        }

        string normalizedHint = Normalize(hintPath);
        int nugetIndex = normalizedHint.IndexOf("/.nuget/packages/", StringComparison.OrdinalIgnoreCase);
        if (nugetIndex >= 0)
        {
            return "$HOME/.nuget/packages/" + normalizedHint[(nugetIndex + "/.nuget/packages/".Length)..];
        }

        nugetIndex = normalizedHint.IndexOf(".nuget/packages/", StringComparison.OrdinalIgnoreCase);
        if (nugetIndex >= 0)
        {
            return "$HOME/.nuget/packages/" + normalizedHint[(nugetIndex + ".nuget/packages/".Length)..];
        }

        string combined = Path.IsPathRooted(hintPath)
            ? hintPath
            : Path.Combine(Path.GetDirectoryName(declarationFile) ?? "", hintPath);
        string fullPath = Path.GetFullPath(combined);
        string relative = Normalize(Path.GetRelativePath(repoRoot, fullPath));

        return relative.StartsWith("../", StringComparison.Ordinal)
            ? normalizedHint
            : relative;
    }

    private static string DirectAssemblyReferenceFact(string project, string include, string hintPath)
        => $"{project}:{include}:{hintPath}";

    private static string? ReadProperty(string projectPath, string propertyName)
        => XDocument.Load(projectPath)
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == propertyName)
            ?.Value.Trim();

    private static PackageReference? ReadPackageReference(string projectPath, string packageId, string? commonDeclarationRoot = null)
    {
        string root = commonDeclarationRoot ?? HarnessConfig.ResolveRepoRoot();
        foreach (NuGetPackageReference reference in ReadNuGetPackageReferences(projectPath, root))
        {
            if (string.Equals(reference.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            {
                return new PackageReference(reference.Version);
            }
        }

        return null;
    }

    private static IEnumerable<ProjectReferenceDeclaration> ProjectReferenceDeclarations(string projectPath, string root)
    {
        foreach (string declarationFile in ProjectDeclarationFiles(projectPath, root))
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
    }

    private static IEnumerable<string> ProjectDeclarationFiles(string projectPath, string root)
    {
        yield return projectPath;

        string projectDirectory = Path.GetDirectoryName(projectPath) ?? "";
        if (!string.IsNullOrEmpty(projectDirectory) && Directory.Exists(projectDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
                         .Where(path => Path.GetExtension(path) is ".props" or ".targets")
                         .Where(path => !IsUnderBuildOutputDirectory(path))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }

        foreach (string fileName in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            string path = Path.Combine(root, fileName);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static bool IsUnderBuildOutputDirectory(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsForbiddenDependencyToken(string text, string token)
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

    private static HashSet<string> ReadPackageEntries(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        return archive.Entries
            .Select(entry => NormalizePackageEntry(entry.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizePackageEntry(string path)
        => path.Replace('\\', '/');

    private static string Normalize(string path)
        => path.Replace('\\', '/');

    private static string FullPath(string relativePath)
        => Path.GetFullPath(Path.Combine(HarnessConfig.ResolveRepoRoot(), relativePath));

    private sealed record GitIndexEntry(string Mode, string ObjectId);

    private sealed record PackageReference(string Version);

    private sealed record NuGetPackageReference(string DeclarationFile, string PackageId, string Version);

    private sealed record DirectAssemblyReference(string DeclarationFile, string Include, string HintPath);

    private sealed record ProjectReferenceDeclaration(string DeclarationFile, string Include);
}
