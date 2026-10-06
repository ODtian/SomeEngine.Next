using SlangShaderSharp;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Runtime.Output.Tests;

public sealed class SoftwareVisibilityShaderTests
{
    [Theory]
    [InlineData("changing_caster_material.slang", SlangCompileTarget.Dxil)]
    [InlineData("changing_caster_material.slang", SlangCompileTarget.Spirv)]
    [InlineData("sw_raster.slang", SlangCompileTarget.Dxil)]
    [InlineData("sw_raster.slang", SlangCompileTarget.Spirv)]
    public async Task SoftwareRasterShadersCompileWithAuthoredUvAndEmptyVisibilityData(string file, SlangCompileTarget target)
    {
        Assert.False(Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession global).Failed);
        string engine = FindEngine();
        string sourceDirectory = Path.Combine(engine, file == "sw_raster.slang"
            ? "assets/Shaders" : "tests/SomeEngine.Render.Cluster.Tests/Shaders");
        using var source = new ShaderSourceBlock { SourceNames = [file] };
        var shader = await source.CreateAsync(global, new SessionDesc
        {
            SearchPaths = [sourceDirectory, Path.Combine(engine, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
                Path.Combine(engine, "templates/DefaultProject/assets/Shaders"), Path.Combine(engine, "assets/Shaders")],
            Targets = [new TargetDesc { Format = target, Profile = global.FindProfile(target == SlangCompileTarget.Dxil ? "sm_6_6" : "glsl_460") }],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries = [new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1)),
                new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0)),
                new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1))],
        }, "User software visibility data");
        string entryName = file == "sw_raster.slang" ? "CSSWRasterBufferAtomic64" : "ClusterRasterSoftwareMeshletBufferAtomic64";
        var entry = shader.RequireSlangEntry(0, entryName, SlangStage.Compute);
        ISlangBlob? code = shader.GetSlangEntryBlob(0, entry);
        Assert.NotNull(code);
        Assert.True(code.GetBufferSize() > 0);
    }

    private static string FindEngine()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
