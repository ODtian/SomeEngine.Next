using System.Buffers;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderRuntimeCompilationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneralCreationCompilesUncookedSettingsWithoutChangingSavedCode(bool addTarget)
    {
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("runtime-settings.slang");
        await File.WriteAllTextAsync(sourcePath, """
            RWStructuredBuffer<uint> values;
            [shader("compute")] [numthreads(1,1,1)]
            void WriteValue() { values[0] = 37; }
            """);
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        TargetDesc[] targets = SlangShaderImporter.CreateDefaultTargets(slang);
        SessionDesc cooked = SlangShaderImporter.CreateSessionDescription(sourcePath, [targets[1]]);
        string savedPath = files.File("runtime-settings.shader.asset");
        using (ShaderAsset definition = ShaderTestCompilation.Define(sourcePath))
        {
            await definition.CompileAsync(slang, cooked);
            await definition.SaveAsync(savedPath);
        }
        File.Delete(sourcePath);
        using ShaderAsset asset = await ShaderAsset.OpenAsync(savedPath);
        ShaderInputVersion[] savedInputs = asset.GetBlock<ShaderBlock>().InputVersions.ToArray();
        Shader saved = await asset.CreateSavedAsync(slang, cooked);
        Assert.NotEmpty(saved.GetSlangEntryBytes(0, 0).ToArray());
        SessionDesc requested = cooked;
        if (addTarget)
            requested.Targets = [targets[1], targets[0]];
        else
            requested.CompilerOptionEntries = cooked.CompilerOptionEntries!.Select(option =>
                option.Name == CompilerOptionName.NoMangle
                    ? new CompilerOptionEntry(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0))
                    : option).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => asset.CreateSavedAsync(slang, requested).AsTask());

        Shader runtime = await asset.CreateAsync(slang, requested);

        Assert.Equal(requested.Targets!.Length, runtime.SlangTargetCount);
        for (int target = 0; target < runtime.SlangTargetCount; target++)
        {
            Assert.Equal(requested.Targets[target].Format, runtime.GetSlangTarget(target).Format);
            ISlangBlob? code = runtime.GetSlangEntryBlob(target, 0);
            Assert.NotNull(code);
            Assert.NotEmpty(code.Buffer.ToArray());
        }
        Assert.Equal(savedInputs, asset.GetBlock<ShaderBlock>().InputVersions.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => asset.CreateSavedAsync(slang, requested).AsTask());
        Assert.Equal(saved.GetSlangEntryBytes(0, 0).ToArray(),
            (await asset.CreateSavedAsync(slang, cooked)).GetSlangEntryBytes(0, 0).ToArray());
    }

    [Fact]
    public async Task GeneralCreationCompilesAnUncookedDeclaredSpecializationWithoutChangingSavedCode()
    {
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("runtime-specialization.slang");
        await File.WriteAllTextAsync(sourcePath, """
            RWStructuredBuffer<uint> values;
            [shader("compute")] [numthreads(1,1,1)]
            void WriteSelected<let selected : bool>() { values[0] = selected ? 37 : 19; }
            [shader("compute")] [numthreads(1,1,1)]
            void WriteOrdinary() { values[0] = 11; }
            """);
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        TargetDesc target = SlangShaderImporter.CreateDefaultTargets(slang)[1];
        SessionDesc description = SlangShaderImporter.CreateSessionDescription(sourcePath, [target]);
        string savedPath = files.File("runtime-specialization.shader.asset");
        using (ShaderAsset definition = ShaderTestCompilation.Define(sourcePath))
        {
            definition.GetBlock<ShaderSourceBlock>().Specializations.Add(
                new ShaderSpecialization { EntryPoint = "WriteSelected", Arguments = ["true"] });
            await definition.CompileAsync(slang, description);
            Shader complete = await definition.CreateSavedAsync(slang, description);
            Assert.NotEmpty(complete.RequireSpecialization("WriteSelected", ["true"]).GetSlangEntryBytes(0, 0).ToArray());
            Assert.True(definition.GetBlock<ShaderBlock>().RemoveBytes(
                ShaderBlock.EntryCodeKey(description, 0, 0, ["true"], "WriteSelected")));
            await definition.SaveAsync(savedPath);
        }
        File.Delete(sourcePath);
        using ShaderAsset asset = await ShaderAsset.OpenAsync(savedPath);
        ShaderInputVersion[] savedInputs = asset.GetBlock<ShaderBlock>().InputVersions.ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => asset.CreateSavedAsync(slang, description).AsTask());

        Shader runtime = await asset.CreateAsync(slang, description);

        ISlangBlob? ordinary = runtime.GetSlangEntryBlob(0,
            runtime.RequireSlangEntry(0, "WriteOrdinary", SlangStage.Compute));
        Assert.NotNull(ordinary);
        Assert.NotEmpty(ordinary.Buffer.ToArray());
        ISlangBlob? specialized = runtime.RequireSpecialization("WriteSelected", ["true"]).GetSlangEntryBlob(0, 0);
        Assert.NotNull(specialized);
        Assert.NotEmpty(specialized.Buffer.ToArray());
        Assert.Equal(savedInputs, asset.GetBlock<ShaderBlock>().InputVersions.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => asset.CreateSavedAsync(slang, description).AsTask());
    }
}
