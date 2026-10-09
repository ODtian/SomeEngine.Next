using SlangShaderSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Runtime.Output.Tests;

public sealed class ReferenceShaderTests
{
    [Fact]
    public async Task IndependentSourceRayShaderCompilesWithoutGraphicsDevice()
    {
        SlangResult result = Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession global);
        Assert.False(result.Failed);
        string engine = FindEngine();
        using var source = new ShaderSourceBlock { SourceNames = ["ray_shadow_reference.slang"] };
        var shader = await source.CreateAsync(global, new SessionDesc
        {
            SearchPaths = [Path.Combine(engine, "tests/SomeEngine.Render.Cluster.Tests/Shaders")],
            Targets = [new TargetDesc { Format = SlangCompileTarget.Spirv, Profile = global.FindProfile("glsl_460") }],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
        }, "Independent source triangles");
        var entry = shader.RequireSlangEntry(0, "TraceRayShadow", SlangStage.Compute);
        ISlangBlob? code = shader.GetSlangEntryBlob(0, entry);
        Assert.NotNull(code);
        Assert.True(code.GetBufferSize() > 0);
    }

    private static string FindEngine()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Select the checkout containing the source-ray shader.");
    }
}
