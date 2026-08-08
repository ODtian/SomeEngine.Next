using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class AcceptedBoundaryHarnessCoverageTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void FirstRoundProjectCatalogIsPinnedToAcceptedBoundary()
    {
        AssertSet(
            "first-round product projects",
            RequiredProductProjects,
            Config.Projects.ProductProjects.Select(project => project.Name));

        AssertSet(
            "first-round build-support projects",
            RequiredBuildSupportProjects,
            Config.Projects.BuildSupportProjects.Select(project => project.Name));

        AssertSet(
            "first-round test projects",
            RequiredTestProjects,
            Config.Projects.TestProjects.Select(project => project.Name));

        AssertSet(
            "first-round coverage assemblies",
            RequiredCoverageAssemblies,
            Config.Coverage.RequiredAssemblies);

        AssertSet(
            "first-round product project paths",
            RequiredProductProjectFacts,
            Config.Projects.ProductProjects.Select(project => $"{project.Name}:{Normalize(project.Path)}"));

        AssertSet(
            "first-round build-support project paths",
            RequiredBuildSupportProjectFacts,
            Config.Projects.BuildSupportProjects.Select(project => $"{project.Name}:{Normalize(project.Path)}"));

        AssertSet(
            "first-round test project paths",
            RequiredTestProjectFacts,
            Config.Projects.TestProjects.Select(project => $"{project.Name}:{Normalize(project.Path)}"));
    }

    [Fact]
    public void FirstRoundLayerDependencyContractIsPinnedToAcceptedBoundary()
    {
        AssertSet(
            "layer contract project keys",
            RequiredLayerDependencies.Keys.ToArray(),
            Config.Architecture.LayerContract.AllowedDependencies.Keys);

        foreach ((string projectName, string[] expectedDependencies) in RequiredLayerDependencies)
        {
            Assert.True(
                Config.Architecture.LayerContract.AllowedDependencies.TryGetValue(projectName, out List<string>? actualDependencies),
                $"Layer contract must declare {projectName}.");

            AssertSet(
                $"{projectName} layer dependencies",
                expectedDependencies,
                actualDependencies);
        }
    }

    [Fact]
    public void FirstRoundNuGetPackageCatalogIsPinnedToAcceptedBoundary()
    {
        AssertSet(
            "first-round product NuGet packages",
            RequiredProductNuGetPackages,
            Config.NuGetPackages.ProductPackages.Select(NuGetPackageFact));

        AssertSet(
            "first-round build-support NuGet packages",
            RequiredBuildSupportNuGetPackages,
            Config.NuGetPackages.BuildSupportPackages.Select(NuGetPackageFact));

        AssertSet(
            "first-round test NuGet packages",
            RequiredTestNuGetPackages,
            Config.NuGetPackages.TestPackages.Select(NuGetPackageFact));
    }

    [Fact]
    public void FirstRoundDirectAssemblyReferenceCatalogIsPinnedToAcceptedBoundary()
    {
        AssertSet(
            "first-round product direct assembly references",
            RequiredProductDirectAssemblyReferences,
            Config.DirectAssemblyReferences.ProductReferences.Select(DirectAssemblyReferenceFact));

        AssertSet(
            "first-round build-support direct assembly references",
            RequiredBuildSupportDirectAssemblyReferences,
            Config.DirectAssemblyReferences.BuildSupportReferences.Select(DirectAssemblyReferenceFact));

        AssertSet(
            "first-round test direct assembly references",
            RequiredTestDirectAssemblyReferences,
            Config.DirectAssemblyReferences.TestReferences.Select(DirectAssemblyReferenceFact));
    }

    [Fact]
    public void RuntimeAndLegacyProjectsRemainPinnedOutsideFirstRoundBoundary()
    {
        RequireContains(
            "excluded project names",
            RequiredExcludedProjectNames,
            Config.Architecture.ExcludedProjectNames);

        RequireContains(
            "excluded workspace roots",
            RequiredExcludedWorkspaceRoots,
            Config.Architecture.ExcludedWorkspaceRoots);

        Assert.False(
            Config.Architecture.ExcludedWorkspaceRoots
                .Select(Normalize)
                .Contains("src/SomeEngine.Runtime", StringComparer.OrdinalIgnoreCase),
            "SomeEngine.Runtime source may remain as future/reference source and must not be treated as an excluded workspace root.");

        RequireContains(
            "excluded external source roots",
            RequiredExcludedExternalRoots,
            Config.ExternalDependencies.ExcludedSourceRoots);
    }

    [Fact]
    public void DomainBoundaryConfigsCoverAcceptedBackendExclusions()
    {
        var boundariesByName = Config.Architecture.DomainBoundaries
            .ToDictionary(boundary => boundary.Name, StringComparer.Ordinal);

        foreach (string boundaryName in RequiredDomainBoundaries)
        {
            Assert.True(
                boundariesByName.TryGetValue(boundaryName, out DomainBoundaryConfig? boundary),
                $"Domain boundary {boundaryName} must be declared in harness/config.json.");

            IEnumerable<string> requiredBackendReferences = boundaryName == "SomeEngine.Assets"
                ? RequiredBackendForbiddenReferences.Where(static value => value != "D3D12")
                : RequiredBackendForbiddenReferences;
            requiredBackendReferences = requiredBackendReferences.Except(
                boundary.AllowedReferences,
                StringComparer.OrdinalIgnoreCase);
            RequireContains(
                $"{boundaryName} forbidden references",
                requiredBackendReferences,
                boundary.ForbiddenReferences);

            Assert.Empty(boundary.AllowedReferences.Intersect(
                boundary.ForbiddenReferences,
                StringComparer.OrdinalIgnoreCase));
        }

        RequireContains(
            "SomeEngine.Render allowed references",
            new[] { "Device" },
            boundariesByName["SomeEngine.Render"].AllowedReferences);

        RequireContains(
            "SomeEngine.Render test-only allowed references",
            new[] { "BufferHandle", "TextureHandle" },
            boundariesByName["SomeEngine.Render"].AllowedTestReferences);

        RequireContains(
            "SomeEngine.Render test-only references remain forbidden in product source",
            boundariesByName["SomeEngine.Render"].AllowedTestReferences,
            boundariesByName["SomeEngine.Render"].ForbiddenReferences);

        Assert.Empty(boundariesByName["SomeEngine.Render"].AllowedReferences.Intersect(
            boundariesByName["SomeEngine.Render"].AllowedTestReferences,
            StringComparer.OrdinalIgnoreCase));

        RequireContains(
            "SomeEngine.Render.Cluster allowed references",
            new[] { "Device", "BufferHandle" },
            boundariesByName["SomeEngine.Render.Cluster"].AllowedReferences);

        Assert.DoesNotContain(
            "BufferHandle",
            boundariesByName["SomeEngine.Render"].AllowedReferences);

        RequireContains(
            "SomeEngine.Render forbidden references",
            new[] { "SomeEngine.Render.Cluster" },
            boundariesByName["SomeEngine.Render"].ForbiddenReferences);

        RequireContains(
            "SomeEngine.Render.Cluster forbidden references",
            new[] { "ClusterPipeline", "ClusterPass" },
            boundariesByName["SomeEngine.Render.Cluster"].ForbiddenReferences);

        RequireContains(
            "SomeEngine.Assets forbidden path segments",
            RequiredAssetsForbiddenPathSegments,
            boundariesByName["SomeEngine.Assets"].ForbiddenPathSegments);

        RequireContains(
            "SomeEngine.Render forbidden path segments",
            RequiredRenderForbiddenPathSegments,
            boundariesByName["SomeEngine.Render"].ForbiddenPathSegments);

        RequireContains(
            "SomeEngine.Render.Cluster forbidden path segments",
            RequiredClusterForbiddenPathSegments,
            boundariesByName["SomeEngine.Render.Cluster"].ForbiddenPathSegments);

        Assert.DoesNotContain(
            "Pass",
            boundariesByName["SomeEngine.Render"].ForbiddenReferences);
    }

    [Fact]
    public void MaterialPassSemanticNamesRemainAllowed()
    {
        string[] materialSemanticNames =
        [
            "MaterialPass",
            "PassEntry",
            "PassVersion",
        ];

        foreach (DomainBoundaryConfig boundary in Config.Architecture.DomainBoundaries)
        {
            foreach (string semanticName in materialSemanticNames)
            {
                Assert.DoesNotContain(semanticName, boundary.ForbiddenReferences);
            }
        }

        foreach (string semanticName in materialSemanticNames)
        {
            Assert.DoesNotContain(semanticName, Config.ProductTests.ForbiddenBoundaryReferences);
            Assert.DoesNotContain(semanticName, Config.Architecture.ForbiddenBoundaryReferences);
        }

        var forbiddenTypeFacts = Config.Architecture.ForbiddenProductTypes
            .Select(type => type.Type)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("SomeEngine.Render.Materials.MaterialPass", forbiddenTypeFacts);
        Assert.DoesNotContain("SomeEngine.Assets.Schema.PassEntry", forbiddenTypeFacts);
    }

    [Fact]
    public void ProductTestCatalogDoesNotRequireExcludedBackendOrUiContracts()
    {
        RequireContains(
            "product-test forbidden boundary references",
            RequiredProductTestForbiddenReferences,
            Config.ProductTests.ForbiddenBoundaryReferences);

        RequireContains(
            "product-boundary forbidden references",
            RequiredProductBoundaryForbiddenReferences,
            Config.Architecture.ForbiddenBoundaryReferences);
    }

    [Fact]
    public void ProductTestWarningBucketCoversPerformanceAndSchedulingSignals()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string productTestBoundaryTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "ProductTestBoundaryTests.cs"));

        foreach (string requiredSignal in new[]
        {
            "GC.GetAllocatedBytesForCurrentThread",
            "Stopwatch.StartNew",
            "RunWithTimeout(",
            "LocalQueuedWorkItems",
            "StolenWorkItems",
            "PerformanceSensitiveTestFinderIncludesInternalFullyQualifiedAsyncTests",
            "System\\.Threading\\.Tasks\\.Task",
            "(?:public|internal)",
            "Category=Performance",
        })
        {
            Assert.Contains(requiredSignal, productTestBoundaryTests);
        }
    }

    [Fact]
    public void ProductTestBoundaryCoversDomainSpecificBackendSignals()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string productTestBoundaryTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "ProductTestBoundaryTests.cs"));

        Assert.Contains("DeclaredDomainProductTestsDoNotRequireDomainExcludedContracts", productTestBoundaryTests);
        Assert.Contains("Config.Architecture.DomainBoundaries", productTestBoundaryTests);
        Assert.Contains("ForbiddenPathSegments", productTestBoundaryTests);
        Assert.Contains("ContainsForbiddenPathSegment", productTestBoundaryTests);
    }

    [Fact]
    public void QualityAnalyzerTestsCoverAcceptedHardAndWarningRuleDiagnostics()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string qualityAnalyzerTestsRoot = Path.Combine(
            root,
            "harness",
            "quality",
            "SomeEngine.Harness.QualityAnalyzer.Tests");
        Assert.True(Directory.Exists(qualityAnalyzerTestsRoot), "Quality analyzer test project must exist.");

        string testSources = string.Join(
            "\n",
            Directory.EnumerateFiles(qualityAnalyzerTestsRoot, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(File.ReadAllText));

        foreach (string ruleId in new[]
        {
            "SE001",
            "SE002",
            "SE010",
            "SE020",
            "SE021",
            "SE022",
            "SE023",
            "SE024",
            "SE030",
            "SE031",
            "SE052",
        })
        {
            Assert.Contains(ruleId, testSources);
        }

        Assert.Contains("ComplexityAnalyzerTests", testSources);
        Assert.Contains("DuplicateEnumTests", testSources);
        Assert.Contains("ClassEndingInPlan_Diagnostic", testSources);
        Assert.Contains("ClassEndingInRun_Diagnostic", testSources);
        Assert.Contains("ClassEndingInProgram_Diagnostic", testSources);
        Assert.Contains("MethodEndingInPlan_Diagnostic", testSources);
        Assert.Contains("MethodEndingInRun_Diagnostic", testSources);
        Assert.Contains("MethodEndingInProgram_Diagnostic", testSources);
        Assert.Contains("ElementAccessVar_NoDiagnostic", testSources);
        Assert.Contains("ExceptionMessage_NoDiagnostic", testSources);
        Assert.Contains("SmallStructuralNumber_NoDiagnostic", testSources);
        Assert.Contains("SharedEnumMemberAcrossFiles_ReportsLaterDeclarationDeterministically", testSources);
    }

    [Fact]
    public void FirstRoundBoundaryHarnessIncludesSourceAssemblyAndAssetContentChecks()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string productBoundaryTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "ProductBoundaryTests.cs"));
        string domainBoundaryTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "DomainBoundaryTests.cs"));
        string assetContentBoundaryTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "AssetContentBoundaryTests.cs"));
        string productTestBoundaryTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "ProductTestBoundaryTests.cs"));
        string layerDependencyTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "LayerDependencyTests.cs"));
        string profilerBridgeTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "ProfilerBridgeTests.cs"));
        string projectInventoryTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "ProjectInventoryTests.cs"));
        string projectDeclarationPlatformTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "ProjectDeclarationPlatformBoundaryTests.cs"));
        string projectDeclarationSurfaceTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "ProjectDeclarationSurfaceBoundaryTests.cs"));
        string qualityAnalyzerWiringTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "QualityAnalyzerWiringTests.cs"));

        Assert.Contains("DeclaredFirstRoundSourceFilesDoNotUseExcludedBoundaryReferences", productBoundaryTests);
        Assert.Contains("AcceptedSourceProjectsDoNotHideSourceWithCompileRemove", productBoundaryTests);
        Assert.Contains("DeclaredFirstRoundProjectFilesDoNotDeclareExcludedBoundaryReferences", productBoundaryTests);
        Assert.Contains("DeclaredFirstRoundProjectFilesDoNotUseUnscannedExplicitImports", productBoundaryTests);
        Assert.Contains("ExplicitProjectImportScanRejectsUnscannedDeclarationFiles", productBoundaryTests);
        Assert.Contains("DeclaredApiContractsDoNotRequireExcludedFirstRoundBoundaryNames", productBoundaryTests);
        Assert.Contains("AcceptedProductAssembliesDoNotReferenceExcludedBoundaryAssemblies", productBoundaryTests);
        Assert.Contains("AcceptedProductAssembliesDoNotDeclareForbiddenFirstRoundNames", productBoundaryTests);
        Assert.Contains("RequiredProductTypeContractsDoNotRequireExcludedFirstRoundBoundaryNames", productBoundaryTests);
        Assert.Contains("Config.Architecture.ForbiddenBoundaryReferences", productBoundaryTests);
        Assert.DoesNotContain("Config.ProductTests.ForbiddenBoundaryReferences", productBoundaryTests);
        Assert.Contains("Config.Architecture.ExcludedProjectNames", productBoundaryTests);
        Assert.Contains("Config.Profiler.BridgeFiles", productBoundaryTests);
        Assert.Contains("AddFileIfExists", productBoundaryTests);
        Assert.Contains("ReadAssemblyReferences", productBoundaryTests);
        Assert.Contains("ReadDeclaredSymbolNames", productBoundaryTests);
        Assert.Contains("ReadProfilerBridgeSymbols", productBoundaryTests);
        Assert.Contains("IsProfilerBridgeSymbol", productBoundaryTests);
        Assert.Contains("DeclaredProjectReferenceParsingIncludesProjectLocalPropsAndTargets", productBoundaryTests);
        Assert.Contains("ProjectReferenceDeclarations", productBoundaryTests);
        Assert.Contains("CommonProjectReferenceDeclarations", productBoundaryTests);
        Assert.Contains("ProjectReferenceDeclarationsFromFile", productBoundaryTests);
        Assert.Contains("ReferenceOutputAssembly", productBoundaryTests);
        Assert.Contains("ProjectReferenceDeclarationFiles", productBoundaryTests);
        Assert.Contains("CommonProjectDeclarationFilesIncludeRootBuildDeclarations", productBoundaryTests);
        Assert.Contains("CommonProjectDeclarationFiles", productBoundaryTests);
        Assert.Contains("FindUnscannedExplicitImports", productBoundaryTests);
        Assert.Contains("ReadExplicitImports", productBoundaryTests);
        Assert.Contains("TryResolveImportPath", productBoundaryTests);
        Assert.Contains("ReadRemovedCompileItems", productBoundaryTests);
        Assert.Contains("ExactSourceBoundaryTokensKeepOrdinaryLowercaseWordsAllowed", productBoundaryTests);
        Assert.Contains("CommonIntegrationSourceWordsAreScopedToRenderAndAssetBoundaryProjects", productBoundaryTests);
        Assert.Contains("ProductSourceBoundaryFilesIncludeTextContractAssets", productBoundaryTests);
        Assert.Contains("IsProductSourceBoundaryExtension", productBoundaryTests);
        Assert.Contains("NormalizeExtension", productBoundaryTests);
        Assert.Contains("Upper.Contract.SLANG", productBoundaryTests);
        Assert.Contains("\".hlsl\"", productBoundaryTests);
        Assert.Contains("\".hlsli\"", productBoundaryTests);
        Assert.Contains("\".json\"", productBoundaryTests);
        Assert.Contains("SourceForbiddenReferencesForProject", productBoundaryTests);
        Assert.Contains("ExactSourceTokenComparison", productBoundaryTests);
        Assert.Contains("IsTokenBoundaryBefore", productBoundaryTests);

        Assert.Contains("BoundaryPaths", domainBoundaryTests);
        Assert.Contains("ProjectDeclarationFiles", domainBoundaryTests);
        Assert.Contains("DomainProjectDeclarationFilesIncludeRootBuildDeclarations", domainBoundaryTests);
        Assert.Contains("Directory.Build.props", domainBoundaryTests);
        Assert.Contains("Directory.Packages.props", domainBoundaryTests);
        Assert.Contains("ForbiddenPathSegments", domainBoundaryTests);
        Assert.Contains("ContainsForbiddenPathSegment", domainBoundaryTests);
        Assert.Contains("DomainAssembliesDoNotReferenceBackendExecutionAssemblies", domainBoundaryTests);
        Assert.Contains("DomainAssembliesDoNotDeclareBackendExecutionNames", domainBoundaryTests);
        Assert.Contains("ReadAssemblyReferences", domainBoundaryTests);
        Assert.Contains("ReadDeclaredSymbolNames", domainBoundaryTests);
        Assert.Contains("ExactDomainBoundaryTokensCatchPrefixedBackendNamesWithoutCatchingLowercaseWords", domainBoundaryTests);
        Assert.Contains("DomainSourceFilesIncludeTextContractAssets", domainBoundaryTests);
        Assert.Contains("IsDomainSourceBoundaryExtension", domainBoundaryTests);
        Assert.Contains("NormalizeExtension", domainBoundaryTests);
        Assert.Contains("Upper.Contract.SLANG", domainBoundaryTests);
        Assert.Contains("\".hlsl\"", domainBoundaryTests);
        Assert.Contains("\".hlsli\"", domainBoundaryTests);
        Assert.Contains("\".json\"", domainBoundaryTests);

        Assert.Contains("BoundaryAssetPaths", assetContentBoundaryTests);
        Assert.Contains("forbiddenPathSegments", assetContentBoundaryTests);
        Assert.Contains("BoundaryAssetFiles", assetContentBoundaryTests);
        Assert.Contains("AssetBoundaryFilesIncludeUppercaseTextContractExtensions", assetContentBoundaryTests);
        Assert.Contains("NormalizeExtension", assetContentBoundaryTests);
        Assert.Contains("Contract.SLANG", assetContentBoundaryTests);
        Assert.Contains("\".json\"", assetContentBoundaryTests);
        Assert.Contains("\".gltf\"", assetContentBoundaryTests);
        Assert.Contains("\".hlsl\"", assetContentBoundaryTests);
        Assert.Contains("\".hlsli\"", assetContentBoundaryTests);
        Assert.Contains("ExactAssetBoundaryTokensCatchPrefixedBackendNamesWithoutCatchingLowercaseWords", assetContentBoundaryTests);

        Assert.Contains("ExactProductTestBoundaryTokensCatchPrefixedBackendNamesWithoutCatchingLowercaseWords", productTestBoundaryTests);
        Assert.Contains("CommonIntegrationTestWordsAreScopedToRenderAndAssetBoundaryProjects", productTestBoundaryTests);
        Assert.Contains("TestForbiddenReferencesForProject", productTestBoundaryTests);
        Assert.Contains("CompiledProductTestAssembliesDoNotReferenceExcludedBoundaryAssemblies", productTestBoundaryTests);
        Assert.Contains("CompiledProductTestAssembliesDoNotDeclareExcludedBoundaryNames", productTestBoundaryTests);
        Assert.Contains("CompiledDomainProductTestAssembliesDoNotReferenceDomainExcludedAssemblies", productTestBoundaryTests);
        Assert.Contains("CompiledDomainProductTestAssembliesDoNotDeclareDomainExcludedNames", productTestBoundaryTests);
        Assert.Contains("DeclaredProductTestProjectFilesDoNotUseUnscannedExplicitImports", productTestBoundaryTests);
        Assert.Contains("ProductTestExplicitImportScanRejectsUnscannedDeclarationFiles", productTestBoundaryTests);
        Assert.Contains("CommonProductTestBoundaryFilesIncludeRootBuildDeclarations", productTestBoundaryTests);
        Assert.Contains("DomainProductTestBoundaryScanIncludesRootBuildDeclarations", productTestBoundaryTests);
        Assert.Contains("configures domain-excluded first-round test token", productTestBoundaryTests);
        Assert.Contains("CommonTestBoundaryFiles", productTestBoundaryTests);
        Assert.Contains("TestProjectDeclarationFiles", productTestBoundaryTests);
        Assert.Contains("FindUnscannedExplicitImports", productTestBoundaryTests);
        Assert.Contains("ReadExplicitImports", productTestBoundaryTests);
        Assert.Contains("TryResolveImportPath", productTestBoundaryTests);
        Assert.Contains("ReadAssemblyReferences", productTestBoundaryTests);
        Assert.Contains("ReadDeclaredSymbolNames", productTestBoundaryTests);
        Assert.Contains("ProductTestBoundaryFilesIncludeUppercaseTextContractExtensions", productTestBoundaryTests);
        Assert.Contains("NormalizeExtension", productTestBoundaryTests);
        Assert.Contains("Contract.SLANG", productTestBoundaryTests);
        Assert.Contains("\".json\"", productTestBoundaryTests);
        Assert.Contains("\".gltf\"", productTestBoundaryTests);
        Assert.Contains("\".asset\"", productTestBoundaryTests);
        Assert.Contains("\".hlsli\"", productTestBoundaryTests);

        foreach ((string label, string source) in new[]
        {
            ("product boundary", productBoundaryTests),
            ("domain boundary", domainBoundaryTests),
            ("asset content boundary", assetContentBoundaryTests),
            ("product test boundary", productTestBoundaryTests),
        })
        {
            Assert.Contains("RequiresExactIdentifierMatch", source);
            Assert.Contains("IsTokenBoundaryBefore", source);
            foreach (string token in new[] { "\"Rhi\"", "\"SharpGen\"", "\"Present\"", "\"Window\"", "\"Windowing\"" })
            {
                Assert.Contains(token, source);
            }
        }

        Assert.Contains("CompiledProductAssembliesRespectLayerContract", layerDependencyTests);
        Assert.Contains("DeclaredExternalDependencyConsumersArePinnedToLayerContract", layerDependencyTests);
        Assert.Contains("LayerContractDoesNotAllowForbiddenBoundaryDependencies", layerDependencyTests);
        Assert.Contains("LayerContractReferenceParsingIncludesProjectLocalPropsAndTargets", layerDependencyTests);
        Assert.Contains("ProjectReferenceDeclarationFiles", layerDependencyTests);
        Assert.Contains("CommonDeclarationRootForProject", layerDependencyTests);
        Assert.Contains("CommonProjectDeclarationFiles", layerDependencyTests);
        Assert.Contains("ReferenceOutputAssembly", layerDependencyTests);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", layerDependencyTests);
        Assert.Contains("ParseAssemblyProjectReferencesFromDeclarationFiles", layerDependencyTests);
        Assert.Contains("ParseDeclaredLocalPackageReferencesFromDeclarationFiles", layerDependencyTests);
        Assert.Contains("ParseDeclaredLocalPackageReferences", layerDependencyTests);
        Assert.Contains("ReadAssemblyReferences", layerDependencyTests);

        Assert.Contains("Config.Projects.ProductProjects", profilerBridgeTests);
        Assert.Contains("Config.Projects.BuildSupportProjects", profilerBridgeTests);
        Assert.DoesNotContain("Config.Repository.SrcRoot", profilerBridgeTests);

        Assert.Contains("AllSourceAndTestProjectsAreClassifiedByFirstRoundBoundary", projectInventoryTests);

        Assert.Contains("FirstRoundSourceProjectDeclarationsDoNotUseExcludedUiPlatformDeclarations", projectDeclarationPlatformTests);
        Assert.Contains("FirstRoundTestProjectDeclarationsDoNotUseExcludedUiPlatformDeclarations", projectDeclarationPlatformTests);
        Assert.Contains("UiPlatformDeclarationScanCoversProjectLocalPropsTargetsAndRootDeclarations", projectDeclarationPlatformTests);
        Assert.Contains("Microsoft.NET.Sdk.WindowsDesktop", projectDeclarationPlatformTests);
        Assert.Contains("SDK element", projectDeclarationPlatformTests);
        Assert.Contains("net10.0-windows", projectDeclarationPlatformTests);
        Assert.Contains("UseWPF", projectDeclarationPlatformTests);
        Assert.Contains("UseWindowsForms", projectDeclarationPlatformTests);
        Assert.Contains("EnableWindowsTargeting", projectDeclarationPlatformTests);
        Assert.Contains("UseWinUI", projectDeclarationPlatformTests);
        Assert.Contains("WinExe", projectDeclarationPlatformTests);
        Assert.Contains("TargetPlatformIdentifier", projectDeclarationPlatformTests);
        Assert.Contains("FrameworkReference", projectDeclarationPlatformTests);
        Assert.Contains("Microsoft.WindowsDesktop.App", projectDeclarationPlatformTests);
        Assert.Contains("ReadItemIdentity", projectDeclarationPlatformTests);
        Assert.Contains("IsExcludedUiFrameworkReference", projectDeclarationPlatformTests);
        Assert.Contains("Directory.Build.props", projectDeclarationPlatformTests);
        Assert.Contains("Directory.Build.targets", projectDeclarationPlatformTests);
        Assert.Contains("Directory.Packages.props", projectDeclarationPlatformTests);

        Assert.Contains("FirstRoundProjectsDoNotUseIntermediateAutomaticBuildDeclarations", projectDeclarationSurfaceTests);
        Assert.Contains("IntermediateAutomaticDeclarationScanCoversPropsTargetsAndCentralPackages", projectDeclarationSurfaceTests);
        Assert.Contains("Directory.Build.props", projectDeclarationSurfaceTests);
        Assert.Contains("Directory.Build.targets", projectDeclarationSurfaceTests);
        Assert.Contains("Directory.Packages.props", projectDeclarationSurfaceTests);
        Assert.Contains("outside the root/project-local declaration surface", projectDeclarationSurfaceTests);

        Assert.Contains("DeclaredProductProjectsCannotOptOutOfQualityAnalyzer", qualityAnalyzerWiringTests);
        Assert.Contains("DeclaredBuildSupportQualityAnalyzerOptOutsArePinned", qualityAnalyzerWiringTests);
        Assert.Contains("QualityAnalyzerOptOutScanIncludesProjectLocalPropsTargetsAndRootBuildDeclarations", qualityAnalyzerWiringTests);
        Assert.Contains("QualityAnalyzerOptOutDeclarations", qualityAnalyzerWiringTests);
        Assert.Contains("EnumerateQualityConfigurationFilesForProject", qualityAnalyzerWiringTests);
        Assert.Contains("Directory.Packages.props", qualityAnalyzerWiringTests);
        Assert.Contains("DeclaredProductProjectsDoNotDisableQualityAnalyzerExecution", qualityAnalyzerWiringTests);
        Assert.Contains("DeclaredProductProjectsDoNotSuppressHardQualityRules", qualityAnalyzerWiringTests);
        Assert.Contains("DeclaredProductProjectsDoNotSuppressWarningQualityRules", qualityAnalyzerWiringTests);
        Assert.Contains("DeclaredProductProjectsDoNotRemoveQualityAnalyzerInputs", qualityAnalyzerWiringTests);
        Assert.Contains("QualityAnalyzerInputRemovalScanIncludesProjectLocalPropsTargetsAndRootBuildDeclarations", qualityAnalyzerWiringTests);
        Assert.Contains("QualityAnalyzerInputRemovalDeclarations", qualityAnalyzerWiringTests);
        Assert.Contains("IsBroadAnalyzerSeverityKeyForCategories", qualityAnalyzerWiringTests);
        Assert.Contains("RepositoryBuildKeepsAnalyzerWarningsHard", qualityAnalyzerWiringTests);
    }

    [Fact]
    public void FirstRoundBoundaryHarnessIncludesDependencySchemaAndReviewGates()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string externalDependencyTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "ExternalDependencyTests.cs"));
        string assetSchemaContractTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "AssetSchemaContractTests.cs"));
        string packageSourceMappingTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "architecture",
            "SomeEngine.Harness.Architecture",
            "PackageSourceMappingTests.cs"));
        string reviewTargetAuthoringTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "behaviour",
            "SomeEngine.Harness.Behaviour",
            "ReviewTargetAuthoringTests.cs"));
        string reviewTargetGateTests = File.ReadAllText(Path.Combine(
            root,
            "harness",
            "behaviour",
            "SomeEngine.Harness.Behaviour",
            "ReviewTargetGateTests.cs"));
        foreach (string required in new[]
        {
            "DeclaredExternalSubmodulesAreGitlinksAtPinnedCommits",
            "DeclaredExternalSourceDependenciesLiveInsideSubmodules",
            "ExcludedExternalSourceRootsAreAbsentFromMigratedWorkspace",
            "LocalPackageDependenciesKeepDeclaredProducerConsumerAndContent",
            "SourceProjectDependenciesKeepDeclaredConsumerReferences",
            "BinaryAndNativeDependenciesKeepDeclaredFiles",
            "DeclaredRepoOwnedBinaryDependencyFilesAreNotGitIgnored",
            "ExternalConsumerReferenceParsingIncludesProjectLocalPropsTargetsAndRootBuildDeclarations",
            "FirstRoundNuGetPackageReferencesMatchAcceptedCatalog",
            "NuGetPackageCatalogDoesNotPermitExcludedFirstRoundBoundaryNames",
            "NuGetPackageReferenceParsingIncludesProjectLocalPropsTargetsAndCentralVersions",
            "Config.NuGetPackages",
            "ReadNuGetPackageReferences",
            "ReadCentralPackageVersions",
            "ReadPackageReferenceVersion",
            "ReadPackageIdentity",
            "PackageVersion",
            "GlobalPackageReference",
            "PackageDownload",
            "VersionOverride",
            "FirstRoundDirectAssemblyReferencesMatchAcceptedCatalog",
            "DirectAssemblyReferenceCatalogDoesNotPermitExcludedFirstRoundBoundaryNames",
            "DirectAssemblyReferenceParsingIncludesProjectLocalPropsTargetsAndRootBuildDeclarations",
            "Config.DirectAssemblyReferences",
            "ReadDirectAssemblyReferences",
            "ReadReferenceIdentity",
            "ReadReferenceHintPath",
            "NormalizeDirectAssemblyHintPath",
            "Reference Update",
            "ProjectDeclarationFiles",
            "ProjectReferenceDeclarations",
            "ReferenceOutputAssembly",
        })
        {
            Assert.Contains(required, externalDependencyTests);
        }

        foreach (string required in new[]
        {
            "AssetContracts.cs",
            "AssetsProjectConsumesCurrentCSharpSchemaContracts",
            "LegacyFlatBufferSchemasAreAbsentFromProductAndBuildGraph",
            "BinaryCompatibility.ExactSchema",
            "BinaryCompatibility.Additive",
            "BinaryCompatibility.Migration",
            "PreviousSchema",
            "CurrentRootContracts",
            "FlatSharpSchema",
            "FlatBuffers",
            "*.fbs",
        })
        {
            Assert.Contains(required, assetSchemaContractTests);
        }

        Assert.Contains("RootNuGetConfigMapsLocalPackagesToRepositoryFeed", packageSourceMappingTests);
        Assert.Contains("packageSourceMapping", packageSourceMappingTests);
        foreach (string required in new[]
        {
            "ActiveAgentRunReviewTargetsHaveRequiredSections",
            "ActiveAgentRunReviewTargetsMatchDeclaredObjectives",
            "ActiveAgentRunBatchInstructionsUseAcceptedAuthoringShape",
            "harness-change-does-not-weaken-contract",
            "migration-has-no-temporary-exceptions",
            "run-classification-uses-accepted-terms",
        })
        {
            Assert.Contains(required, reviewTargetAuthoringTests);
        }

        foreach (string required in new[]
        {
            "MissingReviewResultNeedsFix",
            "ResultWithoutMatchingTargetBreaksHarness",
            "NamingResearchTargetRequiresPassingComment",
            "NeedsGrillCommentReturnsNeedsGrill",
            "InvalidResultJsonBreaksHarness",
            "MissingPassFieldBreaksHarness",
        })
        {
            Assert.Contains(required, reviewTargetGateTests);
        }

    }

    [Fact]
    public void AcceptedAssetAndRenderApiContractsRemainPinned()
    {
        var apiContracts = Config.ApiContracts
            .Select(contract => $"{contract.Assembly}:{contract.Type}")
            .ToHashSet(StringComparer.Ordinal);

        RequireContains("API contracts", RequiredApiContracts, apiContracts);
        Assert.DoesNotContain(
            "SomeEngine.Render:SomeEngine.Render.Frame.RenderTimeline",
            apiContracts);
        Assert.DoesNotContain(
            "SomeEngine.Render:SomeEngine.Render.Frame.RenderTimelineLease",
            apiContracts);
        Assert.DoesNotContain(
            "SomeEngine.Render:SomeEngine.Render.Frame.RenderFrameUseLease",
            apiContracts);
        Assert.DoesNotContain(
            "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding",
            apiContracts);
        Assert.DoesNotContain(
            "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterInstanceBinding",
            apiContracts);
        Assert.DoesNotContain(
            "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterResidencyBinding",
            apiContracts);

        var apiMemberContracts = Config.ApiContracts
            .SelectMany(contract => contract.Members.Select(member => $"{contract.Assembly}:{contract.Type}:{member.Kind}:{member.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        RequireContains("API member contracts", RequiredApiMemberContracts, apiMemberContracts);
        Assert.DoesNotContain(
            "SomeEngine.Render:SomeEngine.Render.Frame.RenderFrame:Method:AcquireUse",
            apiMemberContracts);
        Assert.DoesNotContain(
            "SomeEngine.Render:SomeEngine.Render.Frame.RenderFrame:Method:RegisterTimeline",
            apiMemberContracts);
    }

    [Fact]
    public void AcceptedInternalRenderAndClusterTypeContractsRemainPinned()
    {
        var productTypeContracts = Config.Architecture.RequiredProductTypes
            .Select(contract => $"{contract.Assembly}:{contract.Type}")
            .ToHashSet(StringComparer.Ordinal);

        RequireContains("compiled product type contracts", RequiredProductTypeContracts, productTypeContracts);

        var memberContracts = Config.Architecture.RequiredProductTypes
            .SelectMany(contract => contract.Members.Select(member => $"{contract.Assembly}:{contract.Type}:{member.Kind}:{member.Name}"))
            .ToHashSet(StringComparer.Ordinal);

        RequireContains("compiled product type member contracts", RequiredProductTypeMemberContracts, memberContracts);
        Assert.DoesNotContain(
            "SomeEngine.Render:SomeEngine.Render.Frame.RenderFrame:Method:RegisterTimeline",
            memberContracts);
    }

    [Fact]
    public void ForbiddenExecutionShapedProductTypesRemainPinned()
    {
        var forbiddenProductTypes = Config.Architecture.ForbiddenProductTypes
            .Select(type => $"{type.Assembly}:{type.Type}")
            .ToHashSet(StringComparer.Ordinal);

        RequireContains("forbidden first-round product types", RequiredForbiddenProductTypes, forbiddenProductTypes);
    }

    private static void AssertSet(string label, IReadOnlyCollection<string> expected, IEnumerable<string> actual)
    {
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(actualSet, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var extra = actualSet.Except(expectedSet, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0 && extra.Length == 0,
            $"{label} do not match the accepted first-round boundary."
            + FormatSetDifference("Missing", missing)
            + FormatSetDifference("Extra", extra));
    }

    private static string NuGetPackageFact(AllowedNuGetPackageConfig package)
        => $"{package.Project}:{package.PackageId}:{package.Version}";

    private static string DirectAssemblyReferenceFact(AllowedDirectAssemblyReferenceConfig reference)
        => $"{reference.Project}:{reference.Include}:{reference.HintPath}";

    private static void RequireContains(string label, IEnumerable<string> required, IEnumerable<string> actual)
    {
        var actualSet = actual.ToHashSet(StringComparer.Ordinal);
        var missing = required
            .Where(item => !actualSet.Contains(item))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"{label} must contain accepted boundary facts."
            + FormatSetDifference("Missing", missing));
    }

    private static string FormatSetDifference(string label, IReadOnlyCollection<string> items)
        => items.Count == 0
            ? string.Empty
            : $"\n{label}: {string.Join(", ", items)}";

    private static string Normalize(string path)
        => path.Replace('\\', '/');

    private static readonly string[] RequiredProductProjects =
    [
        "SomeEngine.Assets",
        "SomeEngine.Core",
        "SomeEngine.ECS",
        "SomeEngine.ECS.Serialization",
        "SomeEngine.ECS.Systems",
        "SomeEngine.Graphics",
        "SomeEngine.Graphics.Direct3D12",
        "SomeEngine.Graphics.Null",
        "SomeEngine.Job",
        "SomeEngine.Job.Dots",
        "SomeEngine.Render",
        "SomeEngine.Render.Cluster",
        "SomeEngine.RenderGraph",
    ];

    private static readonly string[] RequiredProductProjectFacts =
    [
        "SomeEngine.Assets:src/SomeEngine.Assets/SomeEngine.Assets.csproj",
        "SomeEngine.Core:src/SomeEngine.Core/SomeEngine.Core.csproj",
        "SomeEngine.ECS:src/SomeEngine.ECS/SomeEngine.ECS.csproj",
        "SomeEngine.ECS.Serialization:src/SomeEngine.ECS.Serialization/SomeEngine.ECS.Serialization.csproj",
        "SomeEngine.ECS.Systems:src/SomeEngine.ECS.Systems/SomeEngine.ECS.Systems.csproj",
        "SomeEngine.Graphics:src/SomeEngine.Graphics/SomeEngine.Graphics.csproj",
        "SomeEngine.Graphics.Direct3D12:src/SomeEngine.Graphics.Direct3D12/SomeEngine.Graphics.Direct3D12.csproj",
        "SomeEngine.Graphics.Null:src/SomeEngine.Graphics.Null/SomeEngine.Graphics.Null.csproj",
        "SomeEngine.Job:src/SomeEngine.Job/SomeEngine.Job.csproj",
        "SomeEngine.Job.Dots:src/SomeEngine.Job.Dots/SomeEngine.Job.Dots.csproj",
        "SomeEngine.Render:src/SomeEngine.Render/SomeEngine.Render.csproj",
        "SomeEngine.Render.Cluster:src/SomeEngine.Render.Cluster/SomeEngine.Render.Cluster.csproj",
        "SomeEngine.RenderGraph:src/SomeEngine.RenderGraph/SomeEngine.RenderGraph.csproj",
    ];

    private static readonly string[] RequiredBuildSupportProjects =
    [
        "SomeEngine.Graphics.Benchmarks",
        "SomeEngine.AssetCook",
        "SomeEngine.Assets.Importers",
        "SomeEngine.ECS.SourceGen",
        "SomeEngine.Generators",
        "SomeEngine.RenderGraph.Sample",
    ];

    private static readonly string[] RequiredBuildSupportProjectFacts =
    [
        "SomeEngine.Graphics.Benchmarks:benchmarks/SomeEngine.Graphics.Benchmarks/SomeEngine.Graphics.Benchmarks.csproj",
        "SomeEngine.AssetCook:tools/SomeEngine.AssetCook/SomeEngine.AssetCook.csproj",
        "SomeEngine.Assets.Importers:src/SomeEngine.Assets.Importers/SomeEngine.Assets.Importers.csproj",
        "SomeEngine.ECS.SourceGen:src/SomeEngine.ECS.SourceGen/SomeEngine.ECS.SourceGen.csproj",
        "SomeEngine.Generators:src/SomeEngine.Generators/SomeEngine.Generators.csproj",
        "SomeEngine.RenderGraph.Sample:samples/SomeEngine.RenderGraph.Sample/SomeEngine.RenderGraph.Sample.csproj",
    ];

    private static readonly string[] RequiredTestProjects =
    [
        "SomeEngine.Assets.Tests",
        "SomeEngine.Core.Tests",
        "SomeEngine.ECS.Serialization.Tests",
        "SomeEngine.ECS.SourceGen.Tests",
        "SomeEngine.ECS.Systems.Tests",
        "SomeEngine.ECS.Tests",
        "SomeEngine.Graphics.Direct3D12.Tests",
        "SomeEngine.Graphics.Tests",
        "SomeEngine.Job.Dots.Tests",
        "SomeEngine.Job.Tests",
        "SomeEngine.Render.Cluster.Tests",
        "SomeEngine.Render.Tests",
        "SomeEngine.RenderGraph.Tests",
    ];

    private static readonly string[] RequiredTestProjectFacts =
    [
        "SomeEngine.Assets.Tests:tests/SomeEngine.Assets.Tests/SomeEngine.Assets.Tests.csproj",
        "SomeEngine.Core.Tests:tests/SomeEngine.Core.Tests/SomeEngine.Core.Tests.csproj",
        "SomeEngine.ECS.Serialization.Tests:tests/SomeEngine.ECS.Serialization.Tests/SomeEngine.ECS.Serialization.Tests.csproj",
        "SomeEngine.ECS.SourceGen.Tests:tests/SomeEngine.ECS.SourceGen.Tests/SomeEngine.ECS.SourceGen.Tests.csproj",
        "SomeEngine.ECS.Systems.Tests:tests/SomeEngine.ECS.Systems.Tests/SomeEngine.ECS.Systems.Tests.csproj",
        "SomeEngine.ECS.Tests:tests/SomeEngine.ECS.Tests/SomeEngine.ECS.Tests.csproj",
        "SomeEngine.Graphics.Direct3D12.Tests:tests/SomeEngine.Graphics.Direct3D12.Tests/SomeEngine.Graphics.Direct3D12.Tests.csproj",
        "SomeEngine.Graphics.Tests:tests/SomeEngine.Graphics.Tests/SomeEngine.Graphics.Tests.csproj",
        "SomeEngine.Job.Dots.Tests:tests/SomeEngine.Job.Dots.Tests/SomeEngine.Job.Dots.Tests.csproj",
        "SomeEngine.Job.Tests:tests/SomeEngine.Job.Tests/SomeEngine.Job.Tests.csproj",
        "SomeEngine.Render.Cluster.Tests:tests/SomeEngine.Render.Cluster.Tests/SomeEngine.Render.Cluster.Tests.csproj",
        "SomeEngine.Render.Tests:tests/SomeEngine.Render.Tests/SomeEngine.Render.Tests.csproj",
        "SomeEngine.RenderGraph.Tests:tests/SomeEngine.RenderGraph.Tests/SomeEngine.RenderGraph.Tests.csproj",
    ];

    private static readonly string[] RequiredProductNuGetPackages =
    [
        "SomeEngine.Graphics.Direct3D12:Vortice.Direct3D12:3.8.3",
        "SomeEngine.Graphics.Direct3D12:Vortice.Mathematics:2.1.0",
    ];

    private static readonly string[] RequiredBuildSupportNuGetPackages =
    [
        "SomeEngine.Assets.Importers:SharpGLTF.Core:1.0.6",
        "SomeEngine.ECS.SourceGen:Microsoft.CodeAnalysis.Analyzers:3.11.0",
        "SomeEngine.ECS.SourceGen:Microsoft.CodeAnalysis.CSharp:4.13.0",
        "SomeEngine.Generators:Microsoft.CodeAnalysis.Analyzers:3.11.0",
        "SomeEngine.Generators:Microsoft.CodeAnalysis.CSharp:4.12.0",
    ];

    private static readonly string[] RequiredTestNuGetPackages =
    [
        "SomeEngine.Assets.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.Assets.Tests:SharpGLTF.Toolkit:1.0.6",
        "SomeEngine.Assets.Tests:coverlet.collector:6.0.4",
        "SomeEngine.Assets.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.Assets.Tests:xunit:2.9.3",
        "SomeEngine.Core.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.Core.Tests:coverlet.collector:6.0.4",
        "SomeEngine.Core.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.Core.Tests:xunit:2.9.3",
        "SomeEngine.ECS.Serialization.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.ECS.Serialization.Tests:coverlet.collector:6.0.4",
        "SomeEngine.ECS.Serialization.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.ECS.Serialization.Tests:xunit:2.9.3",
        "SomeEngine.ECS.SourceGen.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.ECS.SourceGen.Tests:coverlet.collector:6.0.4",
        "SomeEngine.ECS.SourceGen.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.ECS.SourceGen.Tests:xunit:2.9.3",
        "SomeEngine.ECS.Systems.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.ECS.Systems.Tests:coverlet.collector:6.0.4",
        "SomeEngine.ECS.Systems.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.ECS.Systems.Tests:xunit:2.9.3",
        "SomeEngine.ECS.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.ECS.Tests:coverlet.collector:6.0.4",
        "SomeEngine.ECS.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.ECS.Tests:xunit:2.9.3",
        "SomeEngine.Graphics.Direct3D12.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.Graphics.Direct3D12.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.Graphics.Direct3D12.Tests:xunit:2.9.3",
        "SomeEngine.Graphics.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.Graphics.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.Graphics.Tests:xunit:2.9.3",
        "SomeEngine.Job.Dots.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.Job.Dots.Tests:coverlet.collector:6.0.4",
        "SomeEngine.Job.Dots.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.Job.Dots.Tests:xunit:2.9.3",
        "SomeEngine.Job.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.Job.Tests:coverlet.collector:6.0.4",
        "SomeEngine.Job.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.Job.Tests:xunit:2.9.3",
        "SomeEngine.Render.Cluster.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.Render.Cluster.Tests:coverlet.collector:6.0.4",
        "SomeEngine.Render.Cluster.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.Render.Cluster.Tests:xunit:2.9.3",
        "SomeEngine.Render.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.Render.Tests:coverlet.collector:6.0.4",
        "SomeEngine.Render.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.Render.Tests:xunit:2.9.3",
        "SomeEngine.RenderGraph.Tests:Microsoft.NET.Test.Sdk:17.14.1",
        "SomeEngine.RenderGraph.Tests:xunit.runner.visualstudio:3.1.4",
        "SomeEngine.RenderGraph.Tests:xunit:2.9.3",
    ];

    private static readonly string[] RequiredProductDirectAssemblyReferences =
    [
    ];

    private static readonly string[] RequiredBuildSupportDirectAssemblyReferences =
    [
    ];

    private static readonly string[] RequiredTestDirectAssemblyReferences =
    [
        "SomeEngine.ECS.SourceGen.Tests:Microsoft.CodeAnalysis.CSharp:$HOME/.nuget/packages/microsoft.codeanalysis.csharp/4.13.0/lib/netstandard2.0/Microsoft.CodeAnalysis.CSharp.dll",
        "SomeEngine.ECS.SourceGen.Tests:Microsoft.CodeAnalysis:$HOME/.nuget/packages/microsoft.codeanalysis.common/4.13.0/lib/netstandard2.0/Microsoft.CodeAnalysis.dll",
        "SomeEngine.ECS.SourceGen.Tests:SomeEngine.ECS.SourceGen:src/SomeEngine.ECS.SourceGen/bin/Debug/netstandard2.0/SomeEngine.ECS.SourceGen.dll",
    ];

    private static readonly IReadOnlyDictionary<string, string[]> RequiredLayerDependencies =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["SomeEngine.Assets"] = ["SomeEngine.Serialization"],
            ["SomeEngine.Assets.Importers"] = ["SomeEngine.Assets", "SlangShaderSharp", "Alimer.Bindings.MeshOptimizer"],
            ["SomeEngine.Core"] = ["SomeEngine.Job", "SomeEngine.ECS", "SomeEngine.ECS.Systems"],
            ["SomeEngine.ECS"] = ["SomeEngine.Job", "SomeEngine.Job.Contracts"],
            ["SomeEngine.ECS.Serialization"] = ["SomeEngine.ECS", "SomeEngine.Serialization"],
            ["SomeEngine.ECS.Systems"] = ["SomeEngine.ECS", "SomeEngine.Job", "SomeEngine.Job.Contracts"],
            ["SomeEngine.Graphics"] = [],
            ["SomeEngine.Graphics.Direct3D12"] = ["SomeEngine.Graphics"],
            ["SomeEngine.Graphics.Null"] = ["SomeEngine.Graphics"],
            ["SomeEngine.Job"] = ["SomeEngine.Job.Contracts"],
            ["SomeEngine.Job.Dots"] = ["SomeEngine.Job"],
            ["SomeEngine.Render"] = ["SomeEngine.Core", "SomeEngine.Assets", "SomeEngine.ECS", "SomeEngine.Graphics"],
            ["SomeEngine.Render.Cluster"] = ["SomeEngine.Render", "SomeEngine.Core", "SomeEngine.Assets", "SomeEngine.ECS", "SomeEngine.Graphics", "SomeEngine.Serialization"],
            ["SomeEngine.RenderGraph"] = ["SomeEngine.Graphics", "SomeEngine.Job"],
        };

    private static readonly string[] RequiredCoverageAssemblies =
    [
        "SomeEngine.Assets",
        "SomeEngine.Core",
        "SomeEngine.ECS",
        "SomeEngine.ECS.Serialization",
        "SomeEngine.ECS.Systems",
        "SomeEngine.Graphics",
        "SomeEngine.Graphics.Direct3D12",
        "SomeEngine.Graphics.Null",
        "SomeEngine.Job",
        "SomeEngine.Job.Dots",
        "SomeEngine.Render",
        "SomeEngine.Render.Cluster",
        "SomeEngine.RenderGraph",
    ];

    private static readonly string[] RequiredExcludedProjectNames =
    [
        "SomeEngine.Editor",
        "SomeEngine.Animation",
        "SomeEngine.Physics",
        "SomeEngine.Rhi",
        "SomeEngine.Rhi.D3D12",
        "SomeEngine.Rhi.Tests",
        "SomeEngine.Rhi.WindowTests",
        "SomeEngine.Runtime",
        "SomeEngine.Runtime.Tests",
        "SomeEngine.UI",
    ];

    private static readonly string[] RequiredExcludedWorkspaceRoots =
    [
        "src/SomeEngine.Animation",
        "src/SomeEngine.Editor",
        "src/SomeEngine.Physics",
        "src/SomeEngine.Rhi",
        "src/SomeEngine.Rhi.D3D12",
        "src/SomeEngine.UI",
        "tests/SomeEngine.Rhi.Tests",
        "tools/SharpGenDebug",
        "tools/SomeEngine.Rhi.WindowTests",
    ];

    private static readonly string[] RequiredExcludedExternalRoots =
    [
        "external/Diligent-SharpGenTools",
        "external/DiligentCore",
    ];

    private static readonly string[] RequiredDomainBoundaries =
    [
        "SomeEngine.Assets",
        "SomeEngine.Render",
        "SomeEngine.Render.Cluster",
    ];

    private static readonly string[] RequiredBackendForbiddenReferences =
    [
        "SomeEngine.Rhi",
        "Rhi",
        "D3D12",
        "Direct3D",
        "DXGI",
        "Diligent",
        "SharpGen",
        "SomeEngine.Render.Graph",
        "RenderGraph",
        "FrameGraph",
        "RenderGraphHandle",
        "RenderPass",
        "RenderPipeline",
        "ComputePipeline",
        "GraphicsPipeline",
        "Device",
        "IQueue",
        "CommandList",
        "CommandQueue",
        "DeviceContext",
        "PipelineState",
        "ShaderResourceBinding",
        "RootSignature",
        "DescriptorSet",
        "CommandBuffer",
        "CommandEncoder",
        "RenderEncoder",
        "ComputeEncoder",
        "ResourceBinding",
        "ISwapchain",
        "Swapchain",
        "SwapChain",
        "Present",
        "Window",
        "Windowing",
        "BufferHandle",
        "TextureHandle",
        "GpuResource",
        "GpuResourceHandle",
        "GpuBuffer",
        "GpuBufferHandle",
        "GpuTexture",
        "GpuTextureHandle",
        "RenderContext",
        "PipelineCache",
        "Silk.NET",
        "ImGui",
        "SomeEngine.Runtime",
        "SomeEngine.Editor",
        "SomeEngine.UI",
        "EditorRenderer",
        "ClusterPipeline",
        "ClusterPass",
    ];

    private static readonly string[] RequiredAssetsForbiddenPathSegments =
    [
        "Direct3D",
        "DXGI",
        "Editor",
        "ImGui",
        "Present",
        "RHI",
        "RenderGraph",
        "Runtime",
        "Swapchain",
        "SwapChain",
        "UI",
        "Window",
        "Windowing",
    ];

    private static readonly string[] RequiredRenderForbiddenPathSegments =
    [
        "D3D12",
        "Direct3D",
        "DXGI",
        "Editor",
        "ImGui",
        "Present",
        "Pipelines",
        "RenderGraph",
        "RHI",
        "Swapchain",
        "SwapChain",
        "UI",
        "Window",
        "Windowing",
    ];

    private static readonly string[] RequiredClusterForbiddenPathSegments =
    [
        "D3D12",
        "Direct3D",
        "DXGI",
        "Editor",
        "ImGui",
        "Present",
        "Pipelines",
        "RenderGraph",
        "RHI",
        "Swapchain",
        "SwapChain",
        "UI",
        "Window",
        "Windowing",
    ];

    private static readonly string[] RequiredProductTestForbiddenReferences =
    [
        "SomeEngine.Rhi",
        "Diligent",
        "SharpGen",
        "D3D12",
        "Direct3D",
        "DXGI",
        "Rhi",
        "SomeEngine.Render.Graph",
        "RenderGraph",
        "FrameGraph",
        "RenderGraphHandle",
        "RenderPass",
        "RenderPipeline",
        "ComputePipeline",
        "GraphicsPipeline",
        "SomeEngine.Runtime",
        "SomeEngine.Editor",
        "SomeEngine.UI",
        "EditorRenderer",
        "Silk.NET",
        "Device",
        "IQueue",
        "DeviceContext",
        "PipelineState",
        "ShaderResourceBinding",
        "RootSignature",
        "DescriptorSet",
        "RenderEncoder",
        "ComputeEncoder",
        "ISwapchain",
        "Window",
        "Windowing",
        "Present",
        "BufferHandle",
        "TextureHandle",
        "RenderContext",
        "PipelineCache",
        "GpuResource",
        "GpuResourceHandle",
        "GpuBuffer",
        "GpuBufferHandle",
        "GpuTexture",
        "GpuTextureHandle",
        "ImGui",
        "imgui",
        "Swapchain",
        "SwapChain",
        "ClusterPipeline",
        "ClusterPass",
    ];

    private static readonly string[] RequiredProductBoundaryForbiddenReferences =
        RequiredProductTestForbiddenReferences;

    private static readonly string[] RequiredApiContracts =
    [
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject",
        "SomeEngine.Assets:SomeEngine.Assets.AssetType`1",
        "SomeEngine.Assets:SomeEngine.Assets.AssetId`1",
        "SomeEngine.Assets:SomeEngine.Assets.AssetHandle`1",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoader",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext",
        "SomeEngine.Assets:SomeEngine.Assets.AssetWriter",
        "SomeEngine.Assets:SomeEngine.Assets.AssetEntry",
        "SomeEngine.Assets:SomeEngine.Assets.IAssetStorage",
        "SomeEngine.Assets:SomeEngine.Assets.LooseAssetStorage",
        "SomeEngine.Assets:SomeEngine.Assets.AssetPackStorage",
        "SomeEngine.Assets:SomeEngine.Assets.BuiltInAssetIds",
        "SomeEngine.Assets:SomeEngine.Assets.AssetImportFingerprint",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest",
        "SomeEngine.Assets:SomeEngine.Assets.Data.ClusterBVHNode",
        "SomeEngine.Assets:SomeEngine.Assets.Data.GPUCluster",
        "SomeEngine.Assets:SomeEngine.Assets.Data.MeshPageHeader",
        "SomeEngine.Assets:SomeEngine.Assets.IAssetImporter",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.ClusterBuilder",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.ClusterBuilderOptions",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.ClusterLodConfig",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfImporterSettings",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfSourceImporter",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangShaderImporter",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangSourceImporter",
        "SomeEngine.Assets:SomeEngine.Assets.Schema.ClusterShaders",
        "SomeEngine.Assets:SomeEngine.Assets.Schema.Material",
        "SomeEngine.Assets:SomeEngine.Assets.Schema.MaterialInstance",
        "SomeEngine.Assets:SomeEngine.Assets.Schema.Mesh",
        "SomeEngine.Assets:SomeEngine.Assets.Schema.PassEntry",
        "SomeEngine.Assets:SomeEngine.Assets.Schema.Shader",
        "SomeEngine.Assets:SomeEngine.Assets.Schema.Texture",
        "SomeEngine.Render:SomeEngine.Render.Components.MeshInstance",
        "SomeEngine.Render:SomeEngine.Render.Components.MeshMaterialBinding",
        "SomeEngine.Render:SomeEngine.Render.Components.RenderMaterialBinding",
        "SomeEngine.Render:SomeEngine.Render.Frame.TemporalJitter",
        "SomeEngine.Render:SomeEngine.Render.Frame.TemporalResolveSettings",
        "SomeEngine.Render:SomeEngine.Render.Materials.MaterialPass",
        "SomeEngine.Render:SomeEngine.Render.Materials.ScalarLayout",
        "SomeEngine.Render:SomeEngine.Render.Systems.RenderWorld",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderResources",
    ];

    private static readonly string[] RequiredForbiddenProductTypes =
    [
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterPass",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterPipeline",
    ];

    private static readonly string[] RequiredApiMemberContracts =
    [
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:CreateStorage",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:CreateAsset",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:RegisterAssetAsync",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:OpenAsync",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:GetDependencies",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:GetReferencers",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:ImportAsync",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:List",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:Resolve",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Method:Validate",
        "SomeEngine.Assets:SomeEngine.Assets.AssetProject:Property:Manifest",
        "SomeEngine.Assets:SomeEngine.Assets.AssetType`1:Property:Name",
        "SomeEngine.Assets:SomeEngine.Assets.AssetType`1:Property:PathSuffix",
        "SomeEngine.Assets:SomeEngine.Assets.AssetType`1:Property:SchemaFingerprint",
        "SomeEngine.Assets:SomeEngine.Assets.AssetId`1:Property:Value",
        "SomeEngine.Assets:SomeEngine.Assets.AssetId`1:Property:IsValid",
        "SomeEngine.Assets:SomeEngine.Assets.AssetId`1:Method:Equals",
        "SomeEngine.Assets:SomeEngine.Assets.AssetId`1:Method:GetHashCode",
        "SomeEngine.Assets:SomeEngine.Assets.AssetId`1:Method:ToString",
        "SomeEngine.Assets:SomeEngine.Assets.AssetHandle`1:Property:Id",
        "SomeEngine.Assets:SomeEngine.Assets.AssetHandle`1:Property:Generation",
        "SomeEngine.Assets:SomeEngine.Assets.AssetHandle`1:Property:IsValid",
        "SomeEngine.Assets:SomeEngine.Assets.AssetHandle`1:Method:Equals",
        "SomeEngine.Assets:SomeEngine.Assets.AssetHandle`1:Method:GetHashCode",
        "SomeEngine.Assets:SomeEngine.Assets.AssetHandle`1:Method:ToString",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoader:Method:LoadAsync",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoader:Method:TryGet",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoader:Method:Get",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoader:Method:DisposeAsync",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Property:AssetGuid",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Property:AssetType",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Property:CancellationToken",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Method:Is",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Method:OpenAsync",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Method:Transfer",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Method:LoadAsync",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Method:TryGet",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Method:Get",
        "SomeEngine.Assets:SomeEngine.Assets.AssetLoadContext:Method:GetOptions",
        "SomeEngine.Assets:SomeEngine.Assets.AssetWriter:Method:Write",
        "SomeEngine.Assets:SomeEngine.Assets.AssetEntry:Property:AssetGuid",
        "SomeEngine.Assets:SomeEngine.Assets.AssetEntry:Property:AssetType",
        "SomeEngine.Assets:SomeEngine.Assets.AssetEntry:Property:SchemaFingerprint",
        "SomeEngine.Assets:SomeEngine.Assets.AssetEntry:Property:Publication",
        "SomeEngine.Assets:SomeEngine.Assets.IAssetStorage:Method:TryFind",
        "SomeEngine.Assets:SomeEngine.Assets.IAssetStorage:Method:OpenAsync",
        "SomeEngine.Assets:SomeEngine.Assets.LooseAssetStorage:Method:TryFind",
        "SomeEngine.Assets:SomeEngine.Assets.LooseAssetStorage:Method:OpenAsync",
        "SomeEngine.Assets:SomeEngine.Assets.AssetPackStorage:Method:TryFind",
        "SomeEngine.Assets:SomeEngine.Assets.AssetPackStorage:Method:OpenAsync",
        "SomeEngine.Assets:SomeEngine.Assets.AssetPackStorage:Method:DisposeAsync",
        "SomeEngine.Assets:SomeEngine.Assets.BuiltInAssetIds:Field:DefaultClusterRender",
        "SomeEngine.Assets:SomeEngine.Assets.BuiltInAssetIds:Field:DefaultClusterRenderPath",
        "SomeEngine.Assets:SomeEngine.Assets.AssetImportFingerprint:Property:ContentFingerprint",
        "SomeEngine.Assets:SomeEngine.Assets.AssetImportFingerprint:Property:Dependencies",
        "SomeEngine.Assets:SomeEngine.Assets.AssetImportFingerprint:Property:ImporterVersion",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Field:AssetIndexFileName",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Field:DependencyGraphFileName",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Field:SourceIndexFileName",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:AddAsset",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:AddSource",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:AssetsBySource",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:GetDependencies",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:GetReferencers",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:List",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:Load",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:Save",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:TryAssetPath",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:TryGetAsset",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:TrySourceAsset",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:TrySourceGuid",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Method:TrySourcePath",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Property:Assets",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Property:Dependencies",
        "SomeEngine.Assets:SomeEngine.Assets.AssetManifest:Property:Sources",
        "SomeEngine.Assets:SomeEngine.Assets.Data.GPUCluster:Field:SizeInBytes",
        "SomeEngine.Assets:SomeEngine.Assets.Data.GPUCluster:Method:PackU16",
        "SomeEngine.Assets:SomeEngine.Assets.Data.MeshPageHeader:Field:MaxPageSize",
        "SomeEngine.Assets:SomeEngine.Assets.Data.MeshPageHeader:Field:Size",
        "SomeEngine.Assets:SomeEngine.Assets.IAssetImporter:Method:GetFingerprint",
        "SomeEngine.Assets:SomeEngine.Assets.IAssetImporter:Method:ImportAsync",
        "SomeEngine.Assets:SomeEngine.Assets.IAssetImporter:Method:MatchesSourcePath",
        "SomeEngine.Assets:SomeEngine.Assets.IAssetImporter:Property:ImporterName",
        "SomeEngine.Assets:SomeEngine.Assets.IAssetImporter:Property:SourceExtensions",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.ClusterBuilder:Method:Process",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.ClusterBuilder:Method:ProcessMesh",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.ClusterBuilderOptions:Property:GenerateMissingTangents",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.ClusterLodConfig:Method:GetDefault",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfImporterSettings:Method:Default",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfImporterSettings:Property:GenerateTangents",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfImporterSettings:Property:LitMaterialTemplate",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfImporterSettings:Property:UnlitMaterialTemplate",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfSourceImporter:Method:GetFingerprint",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfSourceImporter:Method:ImportAsync",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfSourceImporter:Method:MatchesSourcePath",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfSourceImporter:Property:ImporterName",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.GltfSourceImporter:Property:SourceExtensions",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangSourceImporter:Method:GetFingerprint",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangSourceImporter:Method:ImportAsync",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangSourceImporter:Method:MatchesSourcePath",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangSourceImporter:Property:ImporterName",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangSourceImporter:Property:SourceExtensions",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangShaderImporter:Field:ImporterVersion",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangShaderImporter:Method:Import",
        "SomeEngine.Assets.Importers:SomeEngine.Assets.Importers.SlangShaderImporter:Method:ImportTransient",
        "SomeEngine.Assets:SomeEngine.Assets.Schema.PassEntry:Property:EntryPoint",
        "SomeEngine.Assets:SomeEngine.Assets.Schema.PassEntry:Property:ShaderGuid",
        "SomeEngine.Render:SomeEngine.Render.Components.MeshInstance:Field:BoundsExpansion",
        "SomeEngine.Render:SomeEngine.Render.Components.MeshInstance:Field:Mesh",
        "SomeEngine.Render:SomeEngine.Render.Frame.TemporalJitter:Field:DefaultSampleCount",
        "SomeEngine.Render:SomeEngine.Render.Frame.TemporalJitter:Method:ApplyToProjection",
        "SomeEngine.Render:SomeEngine.Render.Frame.TemporalJitter:Method:SamplePixels",
        "SomeEngine.Render:SomeEngine.Render.Frame.TemporalResolveSettings:Method:ToUniforms",
        "SomeEngine.Render:SomeEngine.Render.Frame.TemporalResolveSettings:Property:Default",
        "SomeEngine.Render:SomeEngine.Render.Materials.MaterialPass:Property:EntryPoint",
        "SomeEngine.Render:SomeEngine.Render.Materials.MaterialPass:Property:Shader",
        "SomeEngine.Render:SomeEngine.Render.Materials.MaterialPass:Property:State",
        "SomeEngine.Render:SomeEngine.Render.Materials.MaterialPass:Property:Target",
        "SomeEngine.Render:SomeEngine.Render.Materials.ScalarLayout:Field:Empty",
        "SomeEngine.Render:SomeEngine.Render.Materials.ScalarLayout:Field:HeaderByteSize",
        "SomeEngine.Render:SomeEngine.Render.Materials.ScalarLayout:Field:PayloadAlignment",
        "SomeEngine.Render:SomeEngine.Render.Materials.ScalarLayout:Method:FromFields",
        "SomeEngine.Render:SomeEngine.Render.Materials.ScalarLayout:Property:ByteSize",
        "SomeEngine.Render:SomeEngine.Render.Materials.ScalarLayout:Property:Fields",
        "SomeEngine.Render:SomeEngine.Render.Materials.ScalarLayout:Property:LayoutHash",
        "SomeEngine.Render:SomeEngine.Render.Materials.ScalarLayout:Property:PayloadByteSize",
        "SomeEngine.Render:SomeEngine.Render.Systems.RenderWorld:Method:Extract",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderResources:Method:AcknowledgeFaultReplay",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderResources:Method:CaptureDiagnostics",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderResources:Method:Dispose",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderResources:Method:Prepare",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderResources:Method:PrepareMeshesAsync",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderResources:Method:PumpStreaming",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderResources:Method:Shutdown",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderResources:Method:TryGetFaultReplayRequest",
    ];

    private static readonly string[] RequiredProductTypeContracts =
    [
        "SomeEngine.Render:SomeEngine.Render.Frame.RenderFrame",
        "SomeEngine.Render:SomeEngine.Render.Frame.RenderFrameUseLease",
        "SomeEngine.Render:SomeEngine.Render.Frame.RenderTimeline",
        "SomeEngine.Render:SomeEngine.Render.Frame.RenderTimelineLease",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterReadbackTicket",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterInstanceHeader",
    ];

    private static readonly string[] RequiredProductTypeMemberContracts =
    [
        "SomeEngine.Render:SomeEngine.Render.Frame.RenderFrame:Method:AcquireUse",
        "SomeEngine.Render:SomeEngine.Render.Frame.RenderFrameUseLease:Method:Dispose",
        "SomeEngine.Render:SomeEngine.Render.Frame.RenderFrameUseLease:Property:IsClosed",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding:Property:Bvh",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding:Property:DispatchExtent",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding:Property:InstanceData",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding:Property:InstanceHeaders",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding:Property:Instances",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding:Property:PageHeap",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding:Property:PreviousInstances",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterRenderBinding:Property:ReadbackTicket",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterReadbackTicket:Property:EpochId",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Field:CapacityBytes",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Method:CanFit",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Method:Free",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Method:Largest",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Method:ReserveFrees",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Method:TryAlloc",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Method:ValidateFrees",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Property:Capacity",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Property:FreeBytes",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.PageHeap:Property:UsedBytes",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterInstanceHeader:Field:BvhRoot",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterInstanceHeader:Field:BoundsExpansion",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterInstanceHeader:Field:DataFlags",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterInstanceHeader:Field:DataOffset",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterInstanceHeader:Field:MaterialSlotOffset",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterInstanceHeader:Method:Create",
        "SomeEngine.Render.Cluster:SomeEngine.Render.Cluster.ClusterInstanceHeader:Property:Inactive",
    ];
}
