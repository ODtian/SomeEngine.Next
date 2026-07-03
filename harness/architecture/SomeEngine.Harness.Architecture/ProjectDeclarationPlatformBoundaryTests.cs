using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class ProjectDeclarationPlatformBoundaryTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void FirstRoundSourceProjectDeclarationsDoNotUseExcludedUiPlatformDeclarations()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.ProductProjects.Concat(Config.Projects.BuildSupportProjects))
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(projectPath))
            {
                continue;
            }

            foreach (string declarationFile in ProjectDeclarationFiles(projectPath, repoRoot))
            {
                AddExcludedUiPlatformDeclarations(project.Name, declarationFile, repoRoot, failures);
            }
        }

        Assert.True(
            failures.Count == 0,
            "First-round source project declarations enable UI/window platform integration outside the accepted boundary:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void FirstRoundTestProjectDeclarationsDoNotUseExcludedUiPlatformDeclarations()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (ProjectConfig project in Config.Projects.TestProjects)
        {
            string projectPath = Path.Combine(repoRoot, project.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(projectPath))
            {
                continue;
            }

            foreach (string declarationFile in ProjectDeclarationFiles(projectPath, repoRoot))
            {
                AddExcludedUiPlatformDeclarations(project.Name, declarationFile, repoRoot, failures);
            }
        }

        Assert.True(
            failures.Count == 0,
            "First-round product-test declarations enable UI/window platform integration outside the accepted boundary:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void UiPlatformDeclarationScanCoversProjectLocalPropsTargetsAndRootDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessUiPlatformDeclarations", Guid.NewGuid().ToString("N"));
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
                <Project Sdk="Microsoft.NET.Sdk.WindowsDesktop">
                  <PropertyGroup>
                    <TargetFramework>net10.0-windows</TargetFramework>
                    <OutputType>WinExe</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <FrameworkReference Include="Microsoft.WindowsDesktop.App" />
                  </ItemGroup>
                  <Sdk Name="Microsoft.NET.Sdk.WindowsDesktop" />
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(buildDirectory, "Local.props"),
                """
                <Project>
                  <PropertyGroup>
                    <UseWPF>true</UseWPF>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(projectDirectory, "Local.targets"),
                """
                <Project>
                  <PropertyGroup>
                    <UseWindowsForms>true</UseWindowsForms>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.props"),
                """
                <Project>
                  <PropertyGroup>
                    <EnableWindowsTargeting>true</EnableWindowsTargeting>
                    <UseWinUI>true</UseWinUI>
                  </PropertyGroup>
                  <ItemGroup>
                    <FrameworkReference Update="Microsoft.WindowsDesktop.App.WPF" />
                  </ItemGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.targets"),
                """
                <Project>
                  <PropertyGroup>
                    <TargetPlatformIdentifier>Windows</TargetPlatformIdentifier>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Packages.props"),
                """
                <Project>
                  <ItemGroup>
                    <FrameworkReference Update="Microsoft.WindowsDesktop.App.WindowsForms" />
                  </ItemGroup>
                </Project>
                """);

            var failures = new List<string>();
            foreach (string declarationFile in ProjectDeclarationFiles(projectPath, tempRoot))
            {
                AddExcludedUiPlatformDeclarations("SomeEngine.Sample", declarationFile, tempRoot, failures);
            }

            Assert.Contains(failures, failure => failure.Contains("WindowsDesktop", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("SDK element", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("net10.0-windows", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("WinExe", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("UseWPF", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("UseWindowsForms", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("EnableWindowsTargeting", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("UseWinUI", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("Microsoft.WindowsDesktop.App", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("Microsoft.WindowsDesktop.App.WPF", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("TargetPlatformIdentifier", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("Microsoft.WindowsDesktop.App.WindowsForms", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("Directory.Build.targets", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("Directory.Packages.props", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static void AddExcludedUiPlatformDeclarations(
        string projectName,
        string declarationFile,
        string repoRoot,
        List<string> failures)
    {
        string relative = Normalize(Path.GetRelativePath(repoRoot, declarationFile));
        XDocument document = XDocument.Load(declarationFile);

        foreach (XElement projectElement in document.Root?.DescendantsAndSelf().Where(element => element.Name.LocalName == "Project") ?? [])
        {
            string sdk = projectElement.Attribute("Sdk")?.Value ?? "";
            if (sdk.Contains("WindowsDesktop", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{projectName} declaration {relative} uses excluded UI/window SDK '{sdk}'.");
            }
        }

        foreach (XElement sdkElement in document.Descendants().Where(element => element.Name.LocalName == "Sdk"))
        {
            string sdk = sdkElement.Attribute("Name")?.Value?.Trim() ?? "";
            if (sdk.Contains("WindowsDesktop", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{projectName} declaration {relative} uses excluded UI/window SDK element '{sdk}'.");
            }
        }

        foreach (XElement property in document.Descendants())
        {
            string name = property.Name.LocalName;
            string value = property.Value.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (name is "UseWPF" or "UseWindowsForms" or "EnableWindowsTargeting" or "UseWinUI"
                && IsTrue(value))
            {
                failures.Add($"{projectName} declaration {relative} enables excluded UI/window property {name}={value}.");
                continue;
            }

            if ((name is "TargetFramework" or "TargetFrameworks")
                && TargetsWindowsPlatform(value))
            {
                failures.Add($"{projectName} declaration {relative} targets excluded UI/window platform framework '{value}'.");
                continue;
            }

            if (name == "TargetPlatformIdentifier"
                && string.Equals(value, "Windows", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{projectName} declaration {relative} targets excluded UI/window platform identifier {name}={value}.");
                continue;
            }

            if (name == "OutputType"
                && string.Equals(value, "WinExe", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{projectName} declaration {relative} uses excluded UI/window output type '{value}'.");
            }
        }

        foreach (XElement frameworkReference in document.Descendants().Where(element => element.Name.LocalName == "FrameworkReference"))
        {
            string identity = ReadItemIdentity(frameworkReference);
            if (IsExcludedUiFrameworkReference(identity))
            {
                failures.Add($"{projectName} declaration {relative} uses excluded UI/window framework reference '{identity}'.");
            }
        }
    }

    private static bool IsTrue(string value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static bool TargetsWindowsPlatform(string value)
        => value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(targetFramework => targetFramework.Contains("-windows", StringComparison.OrdinalIgnoreCase));

    private static string ReadItemIdentity(XElement item)
        => item.Attribute("Include")?.Value?.Trim()
           ?? item.Attribute("Update")?.Value?.Trim()
           ?? "";

    private static bool IsExcludedUiFrameworkReference(string identity)
        => identity.Contains("WindowsDesktop", StringComparison.OrdinalIgnoreCase)
           || identity.Contains("WindowsForms", StringComparison.OrdinalIgnoreCase)
           || identity.Contains("WPF", StringComparison.OrdinalIgnoreCase)
           || identity.Contains("WinUI", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ProjectDeclarationFiles(string projectPath, string repoRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(projectPath))
        {
            yield return projectPath;
        }

        string projectDirectory = Path.GetDirectoryName(projectPath) ?? "";
        if (!string.IsNullOrEmpty(projectDirectory) && Directory.Exists(projectDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
                         .Where(path => Path.GetExtension(path) is ".props" or ".targets")
                         .Where(path => !IsGeneratedOutputPath(path))
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(file))
                {
                    yield return file;
                }
            }
        }

        foreach (string fileName in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            string path = Path.Combine(repoRoot, fileName);
            if (File.Exists(path) && seen.Add(path))
            {
                yield return path;
            }
        }
    }

    private static bool IsGeneratedOutputPath(string path)
    {
        string[] segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/');
}
