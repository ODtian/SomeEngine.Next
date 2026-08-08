using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SomeEngine.Harness.Core;

/// <summary>
/// Loads the small fact table used by executable harness checks.
/// Rules live in tests/analyzers; this file only supplies paths and thresholds.
/// </summary>
public sealed class HarnessConfig
{
    public RepositoryConfig Repository { get; set; } = new();
    public ProjectCatalogConfig Projects { get; set; } = new();
    public ProductTestsConfig ProductTests { get; set; } = new();
    public NuGetPackagesConfig NuGetPackages { get; set; } = new();
    public DirectAssemblyReferencesConfig DirectAssemblyReferences { get; set; } = new();
    public List<ApiContractConfig> ApiContracts { get; set; } = [];
    public ExternalDependenciesConfig ExternalDependencies { get; set; } = new();
    public NamingConfig Naming { get; set; } = new();
    public StyleConfig Style { get; set; } = new();
    public ComplexityConfig Complexity { get; set; } = new();
    public ArchitectureConfig Architecture { get; set; } = new();
    public DiffIntentConfig DiffIntent { get; set; } = new();
    public RuntimeAllocationConfig RuntimeAllocation { get; set; } = new();
    public ProfilerConfig Profiler { get; set; } = new();
    public CoverageConfig Coverage { get; set; } = new();

    public static HarnessConfig Load()
    {
        var configPath = ResolveConfigPath();
        var json = File.ReadAllText(configPath);
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        return JsonSerializer.Deserialize<HarnessConfig>(json, opts)
            ?? throw new InvalidDataException("harness/config.json deserialized to null.");
    }

    public static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            var configPath = Path.Combine(dir.FullName, "harness", "config.json");
            if (Directory.Exists(gitPath) || File.Exists(configPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Cannot locate repository root (no .git or harness/config.json).");
    }

    private static string ResolveConfigPath()
        => Path.Combine(ResolveRepoRoot(), "harness", "config.json");

    public string WikiRoot => Path.Combine(ResolveRepoRoot(), Repository.WikiRoot);
    public string SrcRoot => Path.Combine(ResolveRepoRoot(), Repository.SrcRoot);
    public string SolutionPath => Path.Combine(ResolveRepoRoot(), "SomeEngine.slnx");
}

public sealed class RepositoryConfig
{
    public string RootHint { get; set; } = ".git";
    public string WikiRoot { get; set; } = "wiki";
    public string SrcRoot { get; set; } = "src";
}

public sealed class ProjectCatalogConfig
{
    public List<ProjectConfig> ProductProjects { get; set; } = [];
    public List<ProjectConfig> BuildSupportProjects { get; set; } = [];
    public List<ProjectConfig> TestProjects { get; set; } = [];

    public IEnumerable<ProjectConfig> AllProjects()
    {
        foreach (var project in ProductProjects) yield return project;
        foreach (var project in BuildSupportProjects) yield return project;
        foreach (var project in TestProjects) yield return project;
    }
}

public sealed class ProductTestsConfig
{
    public List<TestTraitConfig> WarningTraits { get; set; } = [];
    public List<string> ForbiddenBoundaryReferences { get; set; } = [];
}

public sealed class NuGetPackagesConfig
{
    public List<AllowedNuGetPackageConfig> ProductPackages { get; set; } = [];
    public List<AllowedNuGetPackageConfig> BuildSupportPackages { get; set; } = [];
    public List<AllowedNuGetPackageConfig> TestPackages { get; set; } = [];

    public IEnumerable<AllowedNuGetPackageConfig> AllPackages()
    {
        foreach (var package in ProductPackages) yield return package;
        foreach (var package in BuildSupportPackages) yield return package;
        foreach (var package in TestPackages) yield return package;
    }
}

public sealed class AllowedNuGetPackageConfig
{
    public string Project { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class DirectAssemblyReferencesConfig
{
    public List<AllowedDirectAssemblyReferenceConfig> ProductReferences { get; set; } = [];
    public List<AllowedDirectAssemblyReferenceConfig> BuildSupportReferences { get; set; } = [];
    public List<AllowedDirectAssemblyReferenceConfig> TestReferences { get; set; } = [];

    public IEnumerable<AllowedDirectAssemblyReferenceConfig> AllReferences()
    {
        foreach (var reference in ProductReferences) yield return reference;
        foreach (var reference in BuildSupportReferences) yield return reference;
        foreach (var reference in TestReferences) yield return reference;
    }
}

public sealed class AllowedDirectAssemblyReferenceConfig
{
    public string Project { get; set; } = "";
    public string Include { get; set; } = "";
    public string HintPath { get; set; } = "";
}

public sealed class ProjectConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class ApiContractConfig
{
    public string Assembly { get; set; } = "";
    public string Type { get; set; } = "";
    public List<ApiMemberContractConfig> Members { get; set; } = [];
}

public sealed class ApiMemberContractConfig
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
}

public sealed class ExternalDependenciesConfig
{
    public List<ExternalSubmoduleConfig> Submodules { get; set; } = [];
    public List<string> ExcludedSourceRoots { get; set; } = [];
    public List<LocalPackageConfig> LocalPackages { get; set; } = [];
    public List<SourceProjectDependencyConfig> SourceProjects { get; set; } = [];
    public List<BinaryAssetDependencyConfig> BinaryAssets { get; set; } = [];
    public List<NativeSourceDependencyConfig> NativeSources { get; set; } = [];
}

public sealed class ExternalSubmoduleConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    public string Commit { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class LocalPackageConfig
{
    public string Id { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";
    public string ProducerProject { get; set; } = "";
    public string SourcePackage { get; set; } = "";
    public string ConsumerProject { get; set; } = "";
    public string LocalFeed { get; set; } = "";
    public string PackageSourceKey { get; set; } = "";
    public List<string> RequiredRuntimeAssets { get; set; } = [];
}

public sealed class SourceProjectDependencyConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string ConsumerProject { get; set; } = "";
}

public sealed class BinaryAssetDependencyConfig
{
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = [];
}

public sealed class NativeSourceDependencyConfig
{
    public string Name { get; set; } = "";
    public string Root { get; set; } = "";
    public List<string> RequiredFiles { get; set; } = [];
}

public sealed class NamingConfig
{
    public List<string> ForbiddenClassSuffixes { get; set; } = [];
    public List<string> ForbiddenMethodSuffixes { get; set; } = [];
    public List<string> ClassWhitelist { get; set; } = [];
}

public sealed class StyleConfig
{
    public List<string> AllowedVarContexts { get; set; } = [];
    public bool WarnOnImplicitCast { get; set; } = true;
}

public sealed class ComplexityConfig
{
    public int MaxCyclomaticComplexity { get; set; } = 12;
    public int MaxMethodLines { get; set; } = 60;
    public int MaxMethodsPerClass { get; set; } = 25;
    public int MaxFieldsPerClass { get; set; } = 20;
    public int MaxCoupledTypes { get; set; } = 8;
}

public sealed class ArchitectureConfig
{
    public LayerContract LayerContract { get; set; } = new();
    public List<string> ForbiddenBoundaryReferences { get; set; } = [];
    public List<string> ExcludedProjectNames { get; set; } = [];
    public List<string> ExcludedWorkspaceRoots { get; set; } = [];
    public List<ProductTypeContractConfig> RequiredProductTypes { get; set; } = [];
    public List<ForbiddenProductTypeConfig> ForbiddenProductTypes { get; set; } = [];
    public List<DomainBoundaryConfig> DomainBoundaries { get; set; } = [];
}

public sealed class LayerContract
{
    public Dictionary<string, List<string>> AllowedDependencies { get; set; } = [];
}

public sealed class DomainBoundaryConfig
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> AllowedReferences { get; set; } = [];
    public List<string> AllowedTestReferences { get; set; } = [];
    public List<string> ForbiddenReferences { get; set; } = [];
    public List<string> ForbiddenPathSegments { get; set; } = [];
}

public sealed class ForbiddenProductTypeConfig
{
    public string Assembly { get; set; } = "";
    public string Type { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class ProductTypeContractConfig
{
    public string Assembly { get; set; } = "";
    public string Type { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<ApiMemberContractConfig> Members { get; set; } = [];
}

public sealed class DiffIntentConfig
{
    public List<string> Labels { get; set; } = [];
    public int BugfixMaxFiles { get; set; } = 5;
    public int RefactorNewFunctionTolerance { get; set; } = 2;
}

public sealed class RuntimeAllocationConfig
{
    public string TracePath { get; set; } = "";
    public List<string> AllowedTraceSources { get; set; } = [];
    public int MaxGcGen0PerFrame { get; set; } = 0;
    public int MaxAllocBytesPerFrame { get; set; } = 1024;
}

public sealed class ProfilerConfig
{
    public string ExternalToolRoot { get; set; } = "";
    public List<string> RequiredTools { get; set; } = [];
    public List<string> BridgeFiles { get; set; } = [];
    public List<string> ForbiddenProfilerCenters { get; set; } = [];
}

public sealed class CoverageConfig
{
    public string ReportPath { get; set; } = "";
    public double MinLineRate { get; set; } = 0.0;
    public double MinBranchRate { get; set; } = 0.0;
    public List<string> RequiredAssemblies { get; set; } = [];
    public List<TestTraitConfig> ExcludedTestTraits { get; set; } = [];
}

public sealed class TestTraitConfig
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Reason { get; set; } = "";
}

