using System.Collections.Generic;
using System.IO;
using System.Linq;
using SomeEngine.Harness.Core;
using Xunit;

namespace SomeEngine.Harness.Architecture;

public sealed class AssetContentBoundaryTests
{
    private static readonly HarnessConfig Config = HarnessConfig.Load();

    [Fact]
    public void ExactAssetBoundaryTokensCatchPrefixedBackendNamesWithoutCatchingLowercaseWords()
    {
        Assert.True(ContainsForbiddenAssetToken("interface IRhiDevice {}", "Rhi"));
        Assert.True(ContainsForbiddenAssetToken("DiligentSharpGenBinding", "SharpGen"));
        Assert.True(ContainsForbiddenAssetToken("CreateWindow", "Window"));
        Assert.True(ContainsForbiddenAssetToken("Some.Windowing", "Windowing"));
        Assert.True(ContainsForbiddenAssetToken("some.windowing", "Windowing"));
        Assert.True(ContainsForbiddenAssetToken("SwapchainPresent", "Present"));

        Assert.False(ContainsForbiddenAssetToken("present = false", "Present"));
        Assert.False(ContainsForbiddenAssetToken("window not configured", "Window"));
    }

    [Fact]
    public void FirstRoundAssetContentDoesNotCarryExcludedBackendOrUiContracts()
    {
        string repoRoot = HarnessConfig.ResolveRepoRoot();
        string assetsRoot = Path.Combine(repoRoot, "assets");
        Assert.True(Directory.Exists(assetsRoot), "First-round assets root must exist.");

        DomainBoundaryConfig assetsBoundary = Config.Architecture.DomainBoundaries
            .Single(boundary => boundary.Name == "SomeEngine.Assets");
        var forbiddenReferences = assetsBoundary.ForbiddenReferences
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        var forbiddenPathSegments = assetsBoundary.ForbiddenPathSegments
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToArray();
        var failures = new List<string>();

        foreach (string path in BoundaryAssetPaths(assetsRoot))
        {
            string relativeToAssets = Path.GetRelativePath(assetsRoot, path);
            string relativeToRepo = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            foreach (string segment in forbiddenPathSegments)
            {
                if (ContainsForbiddenPathSegment(relativeToAssets, segment))
                {
                    failures.Add($"{relativeToRepo} is under excluded first-round asset path segment '{segment}'.");
                }
            }
        }

        foreach (string file in BoundaryAssetFiles(assetsRoot))
        {
            string relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            string text = File.ReadAllText(file);

            foreach (string forbidden in forbiddenReferences)
            {
                if (ContainsForbiddenAssetToken(relative, forbidden)
                    || ContainsForbiddenAssetToken(text, forbidden))
                {
                    failures.Add($"{relative} contains excluded first-round asset token '{forbidden}'.");
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            "First-round asset files carry backend/UI contracts outside the accepted boundary:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void AssetBoundaryFilesIncludeUppercaseTextContractExtensions()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "SomeEngineHarnessAssetBoundaryFiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            foreach (string fileName in new[]
            {
                "Contract.FBS",
                "Contract.SLANG",
                "Contract.MATERIAL",
                "Contract.YML",
            })
            {
                File.WriteAllText(Path.Combine(tempRoot, fileName), "");
            }

            Directory.CreateDirectory(Path.Combine(tempRoot, "obj"));
            File.WriteAllText(Path.Combine(tempRoot, "obj", "Ignored.SLANG"), "");

            string[] files = BoundaryAssetFiles(tempRoot)
                .Select(file => Path.GetFileName(file)!)
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                [
                    "Contract.FBS",
                    "Contract.MATERIAL",
                    "Contract.SLANG",
                    "Contract.YML",
                ],
                files);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static IEnumerable<string> BoundaryAssetFiles(string assetsRoot)
        => Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories)
            .Where(path => IsBoundaryAssetExtension(Path.GetExtension(path)))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> BoundaryAssetPaths(string assetsRoot)
        => Directory.EnumerateFileSystemEntries(assetsRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private static bool IsBoundaryAssetExtension(string extension)
        => NormalizeExtension(extension) is ".fbs"
            or ".slang"
            or ".asset"
            or ".meta"
            or ".json"
            or ".gltf"
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

    private static bool ContainsForbiddenAssetToken(string text, string token)
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
