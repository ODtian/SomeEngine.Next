using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class BoundaryDocumentationTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void ProductBoundaryWikiMatchesAcceptedProjectFacts()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string path = Path.Combine(root, "wiki", "architecture", "Product-Boundary.md");
        Assert.True(File.Exists(path), "wiki/architecture/Product-Boundary.md must exist when it declares the accepted boundary.");

        string markdown = File.ReadAllText(path);
        string completedThisRun = SliceSection(markdown, "## 本轮已完成", "## 本轮未完成");
        string outsideThisRun = SliceSection(markdown, "## 不属于本轮", "## External dependency policy");
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects)
        {
            if (!completedThisRun.Contains($"`{project.Name}`", StringComparison.Ordinal))
            {
                failures.Add($"Product boundary wiki `本轮已完成` must list product project {project.Name}.");
            }
        }

        foreach (ProjectConfig project in Config.Projects.BuildSupportProjects)
        {
            if (!completedThisRun.Contains(project.Name, StringComparison.Ordinal))
            {
                failures.Add($"Product boundary wiki `本轮已完成` must mention build-support project {project.Name}.");
            }
        }

        foreach (string excludedProject in Config.Architecture.ExcludedProjectNames)
        {
            if (completedThisRun.Contains(excludedProject, StringComparison.Ordinal))
            {
                failures.Add($"Product boundary wiki `本轮已完成` must not list `不属于本轮` project {excludedProject}.");
            }
        }

        foreach (string requiredPhrase in new[]
        {
            "Runtime",
            "legacy RHI",
            "Cluster execution",
            "ImGui/editor window integration",
            "DiligentCore",
            "Diligent-SharpGenTools",
        })
        {
            if (!outsideThisRun.Contains(requiredPhrase, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Product boundary wiki `不属于本轮` must mention {requiredPhrase}.");
            }
        }

        foreach (string requiredPhrase in new[]
        {
            "SomeEngine.Graphics.Direct3D12",
            "D3D12",
            "SomeEngine.RenderGraph",
            "swapchain/present",
        })
        {
            if (!completedThisRun.Contains(requiredPhrase, StringComparison.OrdinalIgnoreCase)
                && !markdown.Contains(requiredPhrase, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Product boundary wiki must record accepted Graphics/RenderGraph fact {requiredPhrase}.");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Product boundary wiki contradicts harness/config.json:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void MaterialPassIsNotMechanicallyBannedAsRenderExecution()
    {
        var renderBoundary = Config.Architecture.DomainBoundaries
            .Single(boundary => boundary.Name == "SomeEngine.Render");

        Assert.DoesNotContain("Pass", renderBoundary.ForbiddenReferences);
        Assert.DoesNotContain("MaterialPass", renderBoundary.ForbiddenReferences);
        Assert.DoesNotContain("PassEntry", renderBoundary.ForbiddenReferences);
        Assert.DoesNotContain("PassVersion", renderBoundary.ForbiddenReferences);
    }

    [Fact]
    public void RenderBoundaryWikiMatchesAcceptedBackendFreeFacts()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string path = Path.Combine(root, "wiki", "architecture", "Render-Boundaries.md");
        Assert.True(File.Exists(path), "wiki/architecture/Render-Boundaries.md must exist when it declares Render boundaries.");

        string markdown = File.ReadAllText(path);
        var failures = new List<string>();

        RequirePhrases(
            markdown,
            "Render boundary wiki",
            failures,
            "SomeEngine.Render",
            "backend-free",
            "MaterialPass",
            "material/asset semantics",
            "RenderGraph",
            "legacy RHI",
            "D3D12/Direct3D",
            "ImGui",
            "present/swapchain",
            "Pipelines",
            "SomeEngine.Render.Cluster",
            "Cluster execution",
            "material planning",
            "shader identity sets",
            "GPU resources",
            "command buffers/encoders",
            "descriptor/root-signature/resource-binding",
            "pipeline caches");

        Assert.True(
            failures.Count == 0,
            "Render boundary wiki omits accepted first-round facts:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void HarnessDefinitionWikiMatchesAcceptedHardWarningSplit()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string path = Path.Combine(root, "wiki", "harness", "Harness-Definition.md");
        Assert.True(File.Exists(path), "wiki/harness/Harness-Definition.md must exist when it declares hard and warning checks.");

        string markdown = File.ReadAllText(path);
        var failures = new List<string>();

        RequirePhrases(
            markdown,
            "Harness definition wiki",
            failures,
            "SE001",
            "SE002",
            "SE020",
            "SE021",
            "SE022",
            "SE023",
            "SE024",
            "SE030",
            "SE010",
            "SE031",
            "SE052",
            "warning bucket",
            "Coverage",
            "Category=Performance",
            "Functional product tests",
            "Runtime",
            "legacy RHI/RG",
            "Diligent/SharpGen",
            "D3D12/Direct3D/DXGI",
            "ImGui/window/present",
            "execution-shaped Cluster");

        Assert.True(
            failures.Count == 0,
            "Harness definition wiki omits accepted hard/warning facts:\n" + string.Join("\n", failures));
    }

    private static string SliceSection(string markdown, string startMarker, string endMarker)
    {
        int start = markdown.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{startMarker} must exist.");

        int end = markdown.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end >= 0, $"{endMarker} must exist after {startMarker}.");

        return markdown[start..end];
    }

    private static void RequirePhrases(
        string markdown,
        string label,
        List<string> failures,
        params string[] phrases)
    {
        foreach (string phrase in phrases)
        {
            if (!markdown.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{label} must mention {phrase}.");
            }
        }
    }
}
