using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class AssetSchemaContractTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void AssetsProjectConsumesAcceptedOfflineSchemaContracts()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        ProjectConfig assetsProject = Config.Projects.ProductProjects.Single(project => project.Name == "SomeEngine.Assets");
        string projectPath = Path.Combine(root, assetsProject.Path);
        Assert.True(File.Exists(projectPath), "SomeEngine.Assets project must exist before schema contracts can be checked.");

        var failures = new List<string>();
        string projectDirectory = Path.GetDirectoryName(projectPath)!;
        var acceptedRootSchemas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["assets/Schema/shader_asset.fbs"] = "ShaderAsset",
            ["assets/Schema/material_asset.fbs"] = "MaterialAsset",
            ["assets/Schema/material_instance_asset.fbs"] = "MaterialInstanceAsset",
            ["assets/Schema/mesh_asset.fbs"] = "MeshAsset",
            ["assets/Schema/texture_asset.fbs"] = "TextureAsset",
            ["assets/Schema/cluster_render_asset.fbs"] = "ClusterRenderAsset",
        };

        string[] declarationFiles = ProjectDeclarationFiles(projectPath, root).ToArray();
        var flatSharpSchemas = declarationFiles
            .SelectMany(declarationFile => ReadFlatSharpSchemaIncludes(declarationFile)
                .Select(include => NormalizeDeclaredSchemaPath(root, declarationFile, include)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (flatSharpSchemas.Count == 0)
        {
            var document = XDocument.Load(projectPath);
            flatSharpSchemas = document
                .Descendants()
                .Where(element => element.Name.LocalName == "FlatSharpSchema")
                .Select(element => element.Attribute("Include")?.Value ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(include => Normalize(Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(projectDirectory, include)))))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        foreach (string removalFailure in FindForbiddenFlatSharpSchemaRemovals(root, declarationFiles, acceptedRootSchemas.Keys))
        {
            failures.Add(removalFailure);
        }

        foreach ((string schemaPath, string rootType) in acceptedRootSchemas)
        {
            string fullPath = Path.Combine(root, schemaPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                failures.Add($"Accepted asset schema {schemaPath} must exist.");
                continue;
            }

            if (!flatSharpSchemas.Contains(schemaPath))
            {
                failures.Add($"SomeEngine.Assets must compile accepted asset schema {schemaPath}.");
            }

            string text = File.ReadAllText(fullPath);
            if (!text.Contains("namespace SomeEngine.Assets.Schema;", StringComparison.Ordinal))
            {
                failures.Add($"{schemaPath} must stay in SomeEngine.Assets.Schema.");
            }

            if (!text.Contains($"root_type {rootType};", StringComparison.Ordinal))
            {
                failures.Add($"{schemaPath} must declare root_type {rootType}.");
            }
        }

        RequireFile(root, "assets/Schema/common_types.fbs", failures);
        RequireFile(root, "assets/Schema/asset_refs.fbs", failures);
        RequireText(root, "assets/Schema/material_asset.fbs", "table PassEntry", failures);
        RequireText(root, "assets/Schema/material_asset.fbs", "passes: [PassEntry]", failures);
        RequireText(root, "assets/Schema/shader_asset.fbs", "table ShaderReflectionData", failures);
        RequireText(root, "assets/Schema/shader_asset.fbs", "table ShaderMaterialBinding", failures);
        RequireText(root, "assets/Schema/shader_asset.fbs", "table ShaderMaterialScalarLayout", failures);
        RequireText(root, "assets/Schema/cluster_render_asset.fbs", "table ClusterRenderAsset", failures);
        RequireText(root, "assets/Schema/cluster_render_asset.fbs", "cluster_bvh_traverse: ShaderAssetRef", failures);
        RequireText(root, "assets/Schema/cluster_render_asset.fbs", "cluster_cull: ShaderAssetRef", failures);
        RequireText(root, "assets/Schema/cluster_render_asset.fbs", "cluster_shade_binning: ShaderAssetRef", failures);
        RequireText(root, "assets/Schema/cluster_render_asset.fbs", "bvh_patch: ShaderAssetRef", failures);
        RequireText(root, "assets/Schema/mesh_asset.fbs", "bvh_offset:ulong", failures);
        RequireText(root, "assets/Schema/mesh_asset.fbs", "quant_origin:Vec3", failures);
        RequireText(root, "assets/Schema/mesh_asset.fbs", "quant_step:float", failures);
        RequireText(root, "assets/Schema/mesh_asset.fbs", "regions:[MeshRegion]", failures);

        Assert.True(
            failures.Count == 0,
            "Accepted offline asset schema contracts are not wired into SomeEngine.Assets:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AssetSchemaProjectDeclarationScanIncludesProjectLocalPropsTargetsAndRootBuildDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessAssetSchemaContractTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectPath = Path.Combine(tempRoot, "SomeEngine.Assets.csproj");
            string buildDirectory = Path.Combine(tempRoot, "build");
            Directory.CreateDirectory(buildDirectory);

            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                Path.Combine(buildDirectory, "Schemas.props"),
                """
                <Project>
                  <ItemGroup>
                    <FlatSharpSchema Include="..\assets\Schema\from_props.fbs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Schemas.targets"),
                """
                <Project>
                  <ItemGroup>
                    <FlatSharpSchema Include="..\assets\Schema\from_targets.fbs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.props"),
                """
                <Project>
                  <ItemGroup>
                    <FlatSharpSchema Include="assets\Schema\from_root.fbs" />
                  </ItemGroup>
                </Project>
                """);

            string[] includes = ProjectDeclarationFiles(projectPath, tempRoot)
                .SelectMany(ReadFlatSharpSchemaIncludes)
                .Select(path => path.Replace('\\', '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "../assets/Schema/from_props.fbs",
                    "../assets/Schema/from_targets.fbs",
                    "assets/Schema/from_root.fbs",
                ],
                includes);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void AssetSchemaRemovalScanRejectsProjectLocalPropsTargetsAndRootBuildDeclarations()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessAssetSchemaRemovalTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            string projectPath = Path.Combine(tempRoot, "SomeEngine.Assets.csproj");
            string buildDirectory = Path.Combine(tempRoot, "build");
            Directory.CreateDirectory(buildDirectory);

            File.WriteAllText(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <FlatSharpSchema Remove="assets\Schema\from_project.fbs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(buildDirectory, "Schemas.props"),
                """
                <Project>
                  <ItemGroup>
                    <FlatSharpSchema Remove="..\assets\Schema\from_props.fbs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Schemas.targets"),
                """
                <Project>
                  <ItemGroup>
                    <FlatSharpSchema Remove="@(FlatSharpSchema)" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.props"),
                """
                <Project>
                  <ItemGroup>
                    <FlatSharpSchema Remove="assets\Schema\from_root_props.fbs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Build.targets"),
                """
                <Project>
                  <ItemGroup>
                    <FlatSharpSchema Remove="assets\Schema\*.fbs" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(
                Path.Combine(tempRoot, "Directory.Packages.props"),
                """
                <Project>
                  <ItemGroup>
                    <FlatSharpSchema Remove="assets\Schema\from_packages.fbs" />
                  </ItemGroup>
                </Project>
                """);

            string[] failures = FindForbiddenFlatSharpSchemaRemovals(
                    tempRoot,
                    ProjectDeclarationFiles(projectPath, tempRoot),
                    ["assets/Schema/from_project.fbs"])
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Contains(failures, failure => failure.Contains("from_project.fbs", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("from_props.fbs", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("@(FlatSharpSchema)", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("from_root_props.fbs", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("*.fbs", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("from_packages.fbs", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("Directory.Build.targets", StringComparison.Ordinal));
            Assert.Contains(failures, failure => failure.Contains("Directory.Packages.props", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static IEnumerable<string> ReadFlatSharpSchemaIncludes(string declarationFile)
        => XDocument.Load(declarationFile)
            .Descendants()
            .Where(element => element.Name.LocalName == "FlatSharpSchema")
            .Select(element => element.Attribute("Include")?.Value ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value));

    private static IEnumerable<string> ReadFlatSharpSchemaRemoves(string declarationFile)
        => XDocument.Load(declarationFile)
            .Descendants()
            .Where(element => element.Name.LocalName == "FlatSharpSchema")
            .Select(element => element.Attribute("Remove")?.Value ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value));

    private static IEnumerable<string> FindForbiddenFlatSharpSchemaRemovals(
        string repoRoot,
        IEnumerable<string> declarationFiles,
        IEnumerable<string> acceptedSchemas)
    {
        var accepted = acceptedSchemas.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string declarationFile in declarationFiles)
        {
            string relativeDeclaration = Normalize(Path.GetRelativePath(repoRoot, declarationFile));
            foreach (string remove in ReadFlatSharpSchemaRemoves(declarationFile))
            {
                string normalizedRemove = NormalizeDeclaredSchemaPath(repoRoot, declarationFile, remove);
                if (IsForbiddenSchemaRemoval(remove, normalizedRemove, accepted))
                {
                    yield return $"{relativeDeclaration} removes accepted asset schema declaration with FlatSharpSchema Remove=\"{remove}\".";
                }
            }
        }
    }

    private static bool IsForbiddenSchemaRemoval(string rawRemove, string normalizedRemove, HashSet<string> acceptedSchemas)
    {
        string normalizedRaw = Normalize(rawRemove);
        return acceptedSchemas.Contains(normalizedRemove)
               || normalizedRemove.StartsWith("assets/Schema/", StringComparison.OrdinalIgnoreCase)
               || normalizedRaw.Contains("assets/Schema/", StringComparison.OrdinalIgnoreCase)
               || normalizedRaw.Contains("@(", StringComparison.Ordinal)
               || normalizedRaw.Contains("$(", StringComparison.Ordinal)
               || normalizedRaw.Contains('*')
               || normalizedRaw.Contains('?');
    }

    private static string NormalizeDeclaredSchemaPath(string repoRoot, string declarationFile, string schemaPath)
    {
        if (schemaPath.Contains("@(", StringComparison.Ordinal)
            || schemaPath.Contains("$(", StringComparison.Ordinal)
            || schemaPath.Contains('*')
            || schemaPath.Contains('?'))
        {
            return Normalize(schemaPath);
        }

        return Normalize(Path.GetRelativePath(
            repoRoot,
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(declarationFile)!, schemaPath))));
    }

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
                         .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                         .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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

    private static void RequireFile(string root, string relativePath, List<string> failures)
    {
        if (!File.Exists(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))))
        {
            failures.Add($"Accepted asset schema include {relativePath} must exist.");
        }
    }

    private static void RequireText(string root, string relativePath, string requiredText, List<string> failures)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            return;
        }

        if (!File.ReadAllText(path).Contains(requiredText, StringComparison.Ordinal))
        {
            failures.Add($"{relativePath} must contain '{requiredText}'.");
        }
    }

    private static string Normalize(string path)
        => path.Replace('\\', '/');
}
