using System.Text.RegularExpressions;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class AssetSchemaContractTests
{
    private static readonly (string Name, string Suffix)[] CurrentRootContracts =
    [
        ("ClusterShaders", ".clusterrender.asset"),
        ("Material", ".material.asset"),
        ("MaterialInstance", ".materialinstance.asset"),
        ("Mesh", ".mesh.asset"),
        ("Shader", ".shader.asset"),
        ("Texture", ".texture.asset"),
    ];

    [Fact]
    public void AssetsProjectConsumesCurrentCSharpSchemaContracts()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string contractPath = Path.Combine(root, "src", "SomeEngine.Assets", "Schema", "AssetContracts.cs");
        Assert.True(File.Exists(contractPath), "The current AssetContracts.cs schema source must exist.");

        string contractText = File.ReadAllText(contractPath);
        string exactSchemaAttribute = Regex.Escape("[BinaryContract(BinaryCompatibility.ExactSchema)]");
        foreach ((string name, string suffix) in CurrentRootContracts)
        {
            Assert.Matches(
                $@"{exactSchemaAttribute}\s+public sealed partial class {Regex.Escape(name)}\b",
                contractText);

            string assetPath = Path.Combine(
                root,
                "src",
                "SomeEngine.Assets",
                "Schema",
                $"{name}.cs");
            Assert.True(File.Exists(assetPath), $"The one-type asset declaration '{assetPath}' must exist.");
            string assetText = File.ReadAllText(assetPath);
            Assert.Matches(
                $@"\[global::SomeEngine\.Assets\.Asset\(""{Regex.Escape(suffix)}""\)\]\s+" +
                $@"public partial class {Regex.Escape(name)}\b",
                assetText);
            Assert.DoesNotContain($"public {name}({name} source)", contractText, StringComparison.Ordinal);
        }

        string schemaDirectory = Path.GetDirectoryName(contractPath)!;
        string[] actualRoots = Directory.EnumerateFiles(schemaDirectory, "*.cs", SearchOption.TopDirectoryOnly)
            .SelectMany(static path => Regex.Matches(
                File.ReadAllText(path),
                @"\[global::SomeEngine\.Assets\.Asset\(""[^""]*""\)\]\s+public partial class (\w+)"))
            .Select(static match => match.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(CurrentRootContracts.Select(static contract => contract.Name), actualRoots);

        Assert.DoesNotContain("BinaryCompatibility.Additive", contractText, StringComparison.Ordinal);
        Assert.DoesNotContain("BinaryCompatibility.Migration", contractText, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviousSchema", contractText, StringComparison.Ordinal);
        Assert.DoesNotContain("public virtual", contractText, StringComparison.Ordinal);

        string[] productRoots =
        [
            Path.Combine(root, "src", "SomeEngine.Assets"),
            Path.Combine(root, "src", "SomeEngine.Assets.Importers"),
            Path.Combine(root, "src", "SomeEngine.Render"),
            Path.Combine(root, "src", "SomeEngine.Render.Cluster"),
        ];
        string[] productSources = productRoots
            .Where(Directory.Exists)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            .Where(static path => !IsBuildOutput(path))
            .ToArray();
        foreach (string source in productSources)
        {
            string text = File.ReadAllText(source);
            Assert.DoesNotContain("IAssetData", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IMutableAssetData", text, StringComparison.Ordinal);
            Assert.DoesNotContain("[Asset<", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\b(?:Texture|Mesh|Shader|Material|MaterialInstance|ClusterShaders)AssetData\b", text);
        }

        string[] duplicateDeclarations = productSources
            .Where(path => path.Contains(
                $"{Path.DirectorySeparatorChar}SomeEngine.Render",
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    @"public\s+(?:sealed\s+|abstract\s+|partial\s+)*class\s+(Texture|Mesh|Shader|Material|MaterialInstance|ClusterShaders)\b")
                .Select(match => $"{Path.GetRelativePath(root, path)}:{match.Groups[1].Value}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(duplicateDeclarations);
    }

    [Fact]
    public void LegacyFlatBufferSchemasAreAbsentFromProductAndBuildGraph()
    {
        string root = HarnessConfig.ResolveRepoRoot();
        string schemaDirectory = Path.Combine(root, "assets", "Schema");
        if (Directory.Exists(schemaDirectory))
        {
            Assert.Empty(Directory.EnumerateFiles(schemaDirectory, "*.fbs", SearchOption.AllDirectories));
        }

        string[] productRoots =
        [
            Path.Combine(root, "src", "SomeEngine.Assets"),
            Path.Combine(root, "src", "SomeEngine.Assets.Importers"),
        ];
        string[] leakedSchemas = productRoots
            .Where(Directory.Exists)
            .SelectMany(static directory => Directory.EnumerateFiles(directory, "*.fbs", SearchOption.AllDirectories))
            .Where(static path => !IsBuildOutput(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(leakedSchemas);

        string[] projectDeclarations =
        [
            Path.Combine(root, "src", "SomeEngine.Assets", "SomeEngine.Assets.csproj"),
            Path.Combine(root, "src", "SomeEngine.Assets.Importers", "SomeEngine.Assets.Importers.csproj"),
        ];
        foreach (string declaration in projectDeclarations)
        {
            string text = File.ReadAllText(declaration);
            Assert.False(text.Contains("FlatSharpSchema", StringComparison.OrdinalIgnoreCase));
            Assert.False(text.Contains("FlatSharp", StringComparison.OrdinalIgnoreCase));
            Assert.False(text.Contains("FlatBuffers", StringComparison.OrdinalIgnoreCase));
            Assert.False(text.Contains(".fbs", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
}
