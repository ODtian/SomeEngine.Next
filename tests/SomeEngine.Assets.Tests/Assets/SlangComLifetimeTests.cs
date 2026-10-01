using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SlangShaderSharp;
using System.Runtime.CompilerServices;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangComLifetimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SomeEngine-SlangLifetime-" + Guid.NewGuid().ToString("N"));
    private const string ValidSource = "[shader(\"compute\")][numthreads(1,1,1)] void Compute() {}";
    public SlangComLifetimeTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData(SlangCompileTarget.Dxil)]
    [InlineData(SlangCompileTarget.Spirv)]
    public async Task CreatedShaderKeepsCompiledCodeAfterDefinitionsAndCompilerObjectsClose(
        SlangCompileTarget target)
    {
        string path = Path.Combine(_directory, "selected.slang");
        File.WriteAllText(path, ValidSource);
        Shader shader;
        using (var definition = ShaderTestCompilation.Define(path))
        {
            using var result = ShaderTestCompilation.Compile(definition, path);
            shader = await ShaderTestCompilation.CreateAsync(result);
        }
        File.Delete(path);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        int targetIndex = shader.RequireSlangTarget(target);
        Assert.True(shader.TryGetSlangEntry(
            targetIndex,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection entry));
        Assert.True(
            shader.GetSlangEntryBlob(targetIndex, entry) is not null ||
            !shader.GetSlangEntryBytes(targetIndex, entry).IsEmpty);
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
            Shader shader = await ShaderTestCompilation.CreateAsync(result);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Assert.Equal(definition.Id, result.Id);
            int targetIndex = shader.RequireSlangTarget(SlangCompileTarget.Spirv);
            Assert.True(shader.TryGetSlangEntry(
                targetIndex,
                "Compute",
                SlangStage.Compute,
                out EntryPointReflection entry));
            Assert.True(
                shader.GetSlangEntryBlob(targetIndex, entry) is not null ||
                !shader.GetSlangEntryBytes(targetIndex, entry).IsEmpty);
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
        Assert.Contains(
            ShaderBlock.TargetCodeKey(ShaderTestCompilation.CreateSessionDescription(), 0),
            result.GetBlock<ShaderBlock>().ContentKeys);
    }

    [Fact]
    public async Task UnreferencedShaderIsCollectedAndTheCompilerRemainsUsable()
    {
        string path = Path.Combine(_directory, "collected.slang");
        File.WriteAllText(path, ValidSource);
        WeakReference shader = CreateUnreferencedShader(path);

        for (int attempt = 0; attempt < 8 && shader.IsAlive; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Assert.False(shader.IsAlive);
        using ShaderAsset definition = ShaderTestCompilation.Define(path);
        using ShaderAsset result = ShaderTestCompilation.Compile(definition, path);
        Shader replacement = await ShaderTestCompilation.CreateAsync(result);
        int targetIndex = replacement.RequireSlangTarget(SlangCompileTarget.Spirv);
        Assert.True(replacement.TryGetSlangEntry(
            targetIndex,
            "Compute",
            SlangStage.Compute,
            out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnreferencedShader(string path)
    {
        using ShaderAsset definition = ShaderTestCompilation.Define(path);
        using ShaderAsset result = ShaderTestCompilation.Compile(definition, path);
        Shader shader = ShaderTestCompilation.CreateAsync(result).AsTask().GetAwaiter().GetResult();
        return new WeakReference(shader);
    }
}
