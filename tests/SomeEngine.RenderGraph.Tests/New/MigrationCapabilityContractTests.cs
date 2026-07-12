using System.Reflection;
using SomeEngine.RenderGraph;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

/// <summary>
/// Red contract tests authored by agent run 0004 before product migration.  Names inherited from
/// the checkpoint are deliberate continuity requirements; the transparent-cache replacement is
/// tested semantically instead of restoring the checkpoint's second retained compiler.
/// </summary>
public sealed class MigrationCapabilityContractTests
{
    private static readonly Assembly RenderGraphAssembly = typeof(RenderGraph).Assembly;

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void TemporalHistoryContractExists()
    {
        Type lifetime = RequirePublicType("SomeEngine.RenderGraph.ResourceLifetime");
        Assert.True(lifetime.IsEnum, "ResourceLifetime must be an enum.");
        Assert.Contains("Temporal", Enum.GetNames(lifetime));
        Assert.Contains("Persistent", Enum.GetNames(lifetime));

        Type bufferResourceDesc = RequirePublicType("SomeEngine.RenderGraph.BufferResourceDesc");
        Type textureResourceDesc = RequirePublicType("SomeEngine.RenderGraph.TextureResourceDesc");
        RequirePublicStaticMethod(bufferResourceDesc, "Temporal");
        RequirePublicStaticMethod(textureResourceDesc, "Temporal");
        RequirePublicInstanceMethod(typeof(BufferId), "History", typeof(int));
        RequirePublicInstanceMethod(typeof(TextureId), "History", typeof(int));
        RequirePublicInstanceMethod(typeof(GraphBuilder), "CreateBuffer", bufferResourceDesc.MakeByRefType());
        RequirePublicInstanceMethod(typeof(GraphBuilder), "CreateTexture", textureResourceDesc.MakeByRefType());
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void ExtractedResourceContractExists()
    {
        RequirePublicInstanceMethod(typeof(GraphBuilder), "Export", typeof(BufferId));
        RequirePublicInstanceMethod(typeof(GraphBuilder), "Export", typeof(TextureId));

        Type export = RequirePublicType("SomeEngine.RenderGraph.ResourceExport");
        Assert.NotNull(export.GetProperty("Completion", BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(export.GetProperty("FinalState", BindingFlags.Instance | BindingFlags.Public));
        Assert.True(
            export.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Any(static property => property.Name is "Buffer" or "Texture"),
            "A resource export must publish a buffer or texture handle.");
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void CaptureReplayContractExists()
    {
        Type capture = RequirePublicType("SomeEngine.RenderGraph.Capture");
        Type replayExecutor = RequirePublicType("SomeEngine.RenderGraph.ReplayExecutor");
        RequirePublicInstanceMethod(capture, "ToJson", typeof(bool));
        RequirePublicInstanceMethod(capture, "ToDot");
        RequirePublicStaticMethod(replayExecutor, "Execute");

        PropertyInfo? schemaVersion = capture.GetProperty("SchemaVersion", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(schemaVersion);
        Assert.Equal(typeof(int), schemaVersion!.PropertyType);
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void ShaderParameterGenerationContractExists()
    {
        _ = RequirePublicType("SomeEngine.RenderGraph.PassParametersAttribute");
        _ = RequirePublicType("SomeEngine.RenderGraph.ShaderParametersAttribute");
        Type pairing = RequirePublicType("SomeEngine.RenderGraph.ShaderParameterBinding");
        Assert.NotNull(pairing.GetProperty("Shader", BindingFlags.Instance | BindingFlags.Public));
        Assert.NotNull(pairing.GetProperty("LayoutHash", BindingFlags.Instance | BindingFlags.Public));

        string root = FindRepositoryRoot();
        string[] generatorFiles = Directory.GetFiles(
            Path.Combine(root, "src"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(static path => path.Contains("Generator", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Contains(generatorFiles, static path =>
            File.ReadAllText(path).Contains("PassParameters", StringComparison.Ordinal) &&
            File.ReadAllText(path).Contains("ShaderParameters", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void LegacyVariantCapabilityHasAcceptedTransparentCacheReplacement()
    {
        Assert.Null(RenderGraphAssembly.GetType("SomeEngine.RenderGraph.Template", throwOnError: false));
        Assert.Null(RenderGraphAssembly.GetType("SomeEngine.RenderGraph.Variants", throwOnError: false));

        Type? cache = RenderGraphAssembly.GetType("SomeEngine.RenderGraph.CompilationCache", throwOnError: false);
        Assert.NotNull(cache);
        Assert.NotNull(typeof(RenderGraphOptions).GetProperty("CompilationCacheEntryLimit"));
        Assert.NotNull(typeof(RenderGraphOptions).GetProperty("CompilationCachePayloadByteBudget"));
        Assert.NotNull(typeof(RenderGraphOptions).GetProperty("CompileOptimizedPlansAsynchronously"));
    }

    [Fact]
    [Trait("Category", "CapabilityContinuity")]
    public void RequiredRenderGraphWarpTestsDoNotSilentlyReturn()
    {
        string root = FindRepositoryRoot();
        string testRoot = Path.Combine(root, "tests", "SomeEngine.RenderGraph.Tests");
        string[] offenders = Directory.GetFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith(
                $"{Path.DirectorySeparatorChar}MigrationCapabilityContractTests.cs",
                StringComparison.OrdinalIgnoreCase))
            .Where(static path =>
            {
                string source = File.ReadAllText(path);
                return source.Contains("OperatingSystem.IsWindows()", StringComparison.Ordinal) &&
                       (source.Contains("return;", StringComparison.Ordinal) ||
                        source.Contains("return ;", StringComparison.Ordinal));
            })
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Required D3D12/WARP tests must fail closed through the lane fixture, not silently return: " +
            string.Join(", ", offenders));
    }

    private static Type RequirePublicType(string name)
    {
        Type? type = RenderGraphAssembly.GetType(name, throwOnError: false);
        Assert.True(type is { IsPublic: true }, $"Missing public capability type {name}.");
        return type!;
    }

    private static MethodInfo RequirePublicInstanceMethod(Type type, string name, params Type[] parameters)
    {
        MethodInfo? method = type.GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: parameters,
            modifiers: null);
        Assert.True(method is not null, $"Missing public method {type.FullName}.{name}({string.Join(", ", parameters.Select(static value => value.Name))}).");
        return method!;
    }

    private static MethodInfo RequirePublicStaticMethod(Type type, string name)
    {
        MethodInfo? method = type.GetMethods(BindingFlags.Static | BindingFlags.Public)
            .SingleOrDefault(value => value.Name == name);
        Assert.True(method is not null, $"Missing public static method {type.FullName}.{name}.");
        return method!;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SomeEngine.slnx from the test output directory.");
    }
}
