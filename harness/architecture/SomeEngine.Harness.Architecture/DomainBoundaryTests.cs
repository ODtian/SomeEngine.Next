using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class DomainBoundaryTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void ExactDomainBoundaryTokensCatchPrefixedBackendNamesWithoutCatchingLowercaseWords()
    {
        Assert.True(ContainsForbiddenReference("private IRhiDevice? device;", "Rhi"));
        Assert.True(ContainsForbiddenReference("public sealed class DiligentSharpGenBinding { }", "SharpGen"));
        Assert.True(ContainsForbiddenReference("public void CreateWindow() { }", "Window"));
        Assert.True(ContainsForbiddenReference("PackageReference Include=\"Some.Windowing\"", "Windowing"));
        Assert.True(ContainsForbiddenReference("PackageReference Include=\"some.windowing\"", "Windowing"));
        Assert.True(ContainsForbiddenReference("public void SwapchainPresent() { }", "Present"));

        Assert.False(ContainsForbiddenReference("bool present = false;", "Present"));
        Assert.False(ContainsForbiddenReference("string message = \"window not configured\";", "Window"));
    }

    [Fact]
    public void DomainSourceFilesIncludeTextContractAssets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessDomainSourceFiles", Guid.NewGuid().ToString("N"));
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

            Directory.CreateDirectory(Path.Combine(tempRoot, "bin"));
            File.WriteAllText(Path.Combine(tempRoot, "bin", "Ignored.json"), "");

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
    public void DomainBoundariesDoNotReferenceBackendExecution()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var failures = new List<string>();

        foreach (DomainBoundaryConfig boundary in Config.Architecture.DomainBoundaries)
        {
            string boundaryRoot = Path.Combine(repoRoot, boundary.Path);
            if (!Directory.Exists(boundaryRoot))
            {
                failures.Add($"Domain boundary {boundary.Name} root '{boundary.Path}' does not exist");
                continue;
            }

            foreach (string path in BoundaryPaths(boundaryRoot))
            {
                string relativeToBoundary = Path.GetRelativePath(boundaryRoot, path);
                string relativeToRepo = Path.GetRelativePath(repoRoot, path);
                foreach (string segment in boundary.ForbiddenPathSegments)
                {
                    if (ContainsForbiddenPathSegment(relativeToBoundary, segment))
                    {
                        failures.Add($"{boundary.Name} contains forbidden backend path {relativeToRepo}");
                    }
                }
            }

            foreach (string file in SourceFiles(boundaryRoot))
            {
                string relative = Path.GetRelativePath(repoRoot, file);
                string text = File.ReadAllText(file);
                foreach (string forbidden in boundary.ForbiddenReferences)
                {
                    if (ContainsForbiddenReference(relative, forbidden) || ContainsForbiddenReference(text, forbidden))
                    {
                        failures.Add($"{boundary.Name} source {relative} contains forbidden reference '{forbidden}'");
                    }
                }
            }

            foreach (string projectFile in ProjectDeclarationFiles(boundaryRoot, repoRoot))
            {
                string relative = Path.GetRelativePath(repoRoot, projectFile);
                string text = File.ReadAllText(projectFile);
                foreach (string forbidden in boundary.ForbiddenReferences)
                {
                    if (ContainsForbiddenReference(text, forbidden))
                    {
                        failures.Add($"{boundary.Name} project declaration {relative} contains forbidden reference '{forbidden}'");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Domain boundaries depend on backend execution details:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DomainProjectDeclarationFilesIncludeRootBuildDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessDomainDeclarations", Guid.NewGuid().ToString("N"));
        string boundaryRoot = Path.Combine(tempRoot, "src", "SomeEngine.Render");
        Directory.CreateDirectory(boundaryRoot);

        try
        {
            File.WriteAllText(Path.Combine(boundaryRoot, "SomeEngine.Render.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(boundaryRoot, "Local.props"), "<Project />");
            File.WriteAllText(Path.Combine(tempRoot, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(tempRoot, "Directory.Build.targets"), "<Project />");
            File.WriteAllText(Path.Combine(tempRoot, "Directory.Packages.props"), "<Project />");

            string[] files = ProjectDeclarationFiles(boundaryRoot, tempRoot)
                .Select(file => Path.GetFileName(file)!)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "Directory.Build.props",
                    "Directory.Build.targets",
                    "Directory.Packages.props",
                    "Local.props",
                    "SomeEngine.Render.csproj",
                ],
                files);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DomainAssembliesDoNotReferenceBackendExecutionAssemblies()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var projectsByName = Config.Projects.ProductProjects
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (DomainBoundaryConfig boundary in Config.Architecture.DomainBoundaries)
        {
            if (!projectsByName.TryGetValue(boundary.Name, out ProjectConfig? project))
            {
                failures.Add($"Domain boundary {boundary.Name} has no matching first-round product project.");
                continue;
            }

            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{boundary.Name} must be built before compiled domain references can be checked.");
                continue;
            }

            foreach (string reference in ReadAssemblyReferences(assemblyPath))
            {
                foreach (string forbidden in boundary.ForbiddenReferences)
                {
                    if (ContainsForbiddenReference(reference, forbidden))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} references forbidden first-round domain assembly {reference} via token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Compiled domain assemblies depend on backend execution details:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void DomainAssembliesDoNotDeclareBackendExecutionNames()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        var projectsByName = Config.Projects.ProductProjects
            .ToDictionary(project => project.Name, StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (DomainBoundaryConfig boundary in Config.Architecture.DomainBoundaries)
        {
            if (!projectsByName.TryGetValue(boundary.Name, out ProjectConfig? project))
            {
                failures.Add($"Domain boundary {boundary.Name} has no matching first-round product project.");
                continue;
            }

            string? assemblyPath = FindBuiltAssembly(repoRoot, project);
            if (assemblyPath is null)
            {
                failures.Add($"{boundary.Name} must be built before compiled domain symbol names can be checked.");
                continue;
            }

            foreach (string declaredName in ReadDeclaredSymbolNames(assemblyPath))
            {
                foreach (string forbidden in boundary.ForbiddenReferences)
                {
                    if (ContainsForbiddenReference(declaredName, forbidden))
                    {
                        failures.Add($"{Path.GetRelativePath(repoRoot, assemblyPath)} declares first-round domain symbol {declaredName} via forbidden token '{forbidden}'.");
                    }
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "Compiled domain assemblies declare backend execution names:\n" + string.Join("\n", failures));
    }

    private static IEnumerable<string> SourceFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => IsDomainSourceBoundaryExtension(Path.GetExtension(path)))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static bool IsDomainSourceBoundaryExtension(string extension)
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

    private static string FullName(string namespaceName, string name)
        => string.IsNullOrEmpty(namespaceName) ? name : namespaceName + "." + name;

    private static IEnumerable<string> BoundaryPaths(string root)
        => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ProjectDeclarationFiles(string root, string repoRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path) is ".csproj" or ".props" or ".targets")
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(file))
            {
                yield return file;
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

    private static bool ContainsForbiddenReference(string text, string token)
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
