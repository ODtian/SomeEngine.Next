using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SlangShaderSharp;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangComLifetimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-SlangLifetime-" + Guid.NewGuid().ToString("N"));
    private const string ValidSource = "[shader(\"compute\")][numthreads(1,1,1)] void Compute() {}";
    public SlangComLifetimeTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(SlangCompileTarget.Dxil, "dxil")]
    [InlineData(SlangCompileTarget.Spirv, "spirv")]
    public async Task CreatedShaderKeepsCompiledCodeAfterDefinitionsAndCompilerObjectsClose(SlangCompileTarget target, string backend)
    {
        string path = Path.Combine(_directory, "selected.slang");
        File.WriteAllText(path, ValidSource);
        Shader shader;
        using (var definition = ShaderTestCompilation.Define(path))
        {
            using var result = ShaderTestCompilation.Compile(definition, path);
            shader = await result.GetBlock<ShaderBlock>().CreateAsync(target, result.Name);
        }
        File.Delete(path);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using (shader)
        {
            Assert.True(shader.TryEntry(backend, "Compute", ShaderStage.Compute, out _, out var entry));
            Assert.True(entry.Code.Length > 0);
        }
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task RepeatedFailuresDoNotPoisonFollowingCompilations()
    {
        string path = Path.Combine(_directory, "changing.slang");
        using var definition = ShaderTestCompilation.Define(path);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            File.WriteAllText(path, "[shader(\"compute\")] void Compute( {");
            Assert.Throws<InvalidDataException>(() => ShaderTestCompilation.Compile(definition, path));
            File.WriteAllText(path, ValidSource);
            using var result = ShaderTestCompilation.Compile(definition, path);
            using Shader shader = await result.GetBlock<ShaderBlock>().CreateAsync(result.Name);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(definition.Id, result.Id);
            Assert.True(shader.TryEntry("spirv", "Compute", ShaderStage.Compute, out _, out var entry));
            Assert.True(entry.Code.Length > 0);
        }
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.meta"));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.asset"));
    }

    [Fact]
    public void CancellationLeavesTheDefinitionAvailableForAnotherCompilation()
    {
        string path = Path.Combine(_directory, "cancel.slang");
        File.WriteAllText(path, ValidSource);
        using var definition = ShaderTestCompilation.Define(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SlangShaderImporter.CompileDefinition(definition,
            Path.Combine(_directory, "selected.shader"), cancellation.Token));
        using var result = ShaderTestCompilation.Compile(definition, path);
        Assert.Equal(definition.Id, result.Id);
        Assert.NotEmpty(result.GetBlock<ShaderBlock>().Programs);
    }
}
