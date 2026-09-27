using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class DirectSlangShaderTests
{
    private const string Source = """
        RWStructuredBuffer<uint> values;

        [shader("compute")]
        [numthreads(8, 1, 1)]
        void Compute(uint3 threadId : SV_DispatchThreadID)
        {
            values[threadId.x] = threadId.x;
        }
        """;

    [Fact]
    public void ResidentShaderUsesDirectSlangReflectionAndCode()
    {
        TargetDesc[] targets = CreateTargets();
        Shader shader = CompileSource(targets);
        using (shader)
        {
            Assert.True(shader.HasSlangData);
            Assert.Equal(1, shader.SlangTargetCount);
            Assert.Equal(1, shader.SlangEntryCount);
            Assert.Equal(SlangCompileTarget.Spirv, shader.GetSlangTarget(0).Format);
            Assert.True(shader.TryGetSlangEntry(
                0,
                "Compute",
                SlangStage.Compute,
                out EntryPointReflection entry,
                out ISlangBlob code));
            Assert.Equal("Compute", entry.Name);
            Assert.Equal(SlangStage.Compute, entry.Stage);
            Assert.True(code.GetBufferSize() > 0);
            Assert.Equal(entry, shader.GetSlangReflection(0).GetEntryPointByIndex(0));
        }

        Assert.Throws<ObjectDisposedException>(() => shader.GetSlangReflection(0));
    }

    [Fact]
    public void SerializedSlangUnitRecreatesResidentReflectionAndCode()
    {
        TargetDesc[] targets = CreateTargets();
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        AssertSuccess(slang.CreateSession(new SessionDesc { Targets = targets }, out ISession firstSession));
        ISlangBlob source = Slang.CreateBlob(System.Text.Encoding.UTF8.GetBytes(Source));
        IModule? firstUnit = null;
        ISlangBlob? serialized = null;
        try
        {
            firstUnit = firstSession.LoadModuleFromSource(
                "SerializedSlangUnit",
                "SerializedSlangUnit.slang",
                source,
                out ISlangBlob? loadDiagnostics);
            string loadText = DiagnosticText(loadDiagnostics);
            Release(loadDiagnostics);
            Assert.NotNull(firstUnit);
            AssertSuccess(firstUnit!.Serialize(out serialized));
            Assert.NotNull(serialized);
            Assert.True(serialized!.GetBufferSize() > 0);
            Assert.True(string.IsNullOrEmpty(loadText), loadText);
        }
        finally
        {
            Release(source);
            Release(firstUnit);
            Release(firstSession);
        }

        AssertSuccess(slang.CreateSession(new SessionDesc { Targets = targets }, out ISession secondSession));
        IModule? secondUnit = null;
        IEntryPoint? entry = null;
        IComponentType? combinedCode = null;
        IComponentType? linkedCode = null;
        bool shaderOwnsSlang = false;
        try
        {
            secondUnit = secondSession.LoadModuleFromIRBlob(
                "SerializedSlangUnit",
                "SerializedSlangUnit.slang",
                serialized!,
                out ISlangBlob? reloadDiagnostics);
            string reloadText = DiagnosticText(reloadDiagnostics);
            Release(reloadDiagnostics);
            Release(serialized);
            serialized = null;
            Assert.NotNull(secondUnit);
            Assert.True(string.IsNullOrEmpty(reloadText), reloadText);
            AssertSuccess(secondUnit!.GetDefinedEntryPoint(0, out entry));
            SlangResult combineResult = secondSession.CreateCompositeComponentType(
                [secondUnit!, entry!],
                out combinedCode,
                out ISlangBlob? combineDiagnostics);
            string combineText = DiagnosticText(combineDiagnostics);
            Release(combineDiagnostics);
            AssertSuccess(combineResult, combineText);
            Assert.NotNull(combinedCode);
            SlangResult linkResult = combinedCode!.Link(out linkedCode, out ISlangBlob? linkDiagnostics);
            string linkText = DiagnosticText(linkDiagnostics);
            Release(linkDiagnostics);
            AssertSuccess(linkResult, linkText);
            Assert.NotNull(linkedCode);
            Release(combinedCode);
            combinedCode = null;

            shaderOwnsSlang = true;
            using Shader shader = Shader.CreateFromSlang(
                "serialized",
                secondSession,
                [secondUnit!],
                [entry!],
                linkedCode!,
                targets);
            Assert.True(shader.TryGetSlangEntry(
                0,
                "Compute",
                SlangStage.Compute,
                out EntryPointReflection reflection,
                out ISlangBlob code));
            Assert.Equal(SlangStage.Compute, reflection.Stage);
            Assert.True(code.GetBufferSize() > 0);
        }
        finally
        {
            Release(serialized);
            Release(combinedCode);
            if (!shaderOwnsSlang)
            {
                Release(linkedCode);
                Release(entry);
                Release(secondUnit);
                Release(secondSession);
            }
        }
    }

    [Fact]
    public async Task SourceBlockCompilesAResidentShaderOnDemand()
    {
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("on-demand.slang");
        string definitionPath = files.File("on-demand.shader");
        await File.WriteAllTextAsync(sourcePath, Source);

        Shader shader;
        using (var block = new ShaderSourceBlock
        {
            SourceFiles = [Path.GetFileName(sourcePath)],
        })
        {
            shader = await block.CreateAsync(
                SlangShaderImporter.GlobalSession,
                new SessionDesc { Targets = CreateTargets() },
                definitionPath);
        }

        File.Delete(sourcePath);
        using (shader)
        {
            Assert.Equal("on-demand", shader.Name);
            Assert.True(shader.TryGetSlangEntry(
                0,
                "Compute",
                SlangStage.Compute,
                out EntryPointReflection entry,
                out ISlangBlob code));
            Assert.Equal("Compute", entry.Name);
            Assert.True(code.GetBufferSize() > 0);
        }
    }

    [Fact]
    public async Task SavedSourceBlockCompilesAfterTheWorkspaceSourceIsRemoved()
    {
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("saved-source.slang");
        string definitionPath = files.File("test-inputs.shader");
        string assetPath = files.File("saved-source.shader.asset");
        await File.WriteAllTextAsync(sourcePath, Source);

        using (ShaderAsset definition = ShaderTestCompilation.Define(sourcePath))
        using (ShaderAsset built = ShaderTestCompilation.Compile(definition, sourcePath))
            await built.SaveAsync(assetPath);

        File.Delete(sourcePath);
        using ShaderAsset reopened = await ShaderAsset.OpenAsync(assetPath);
        ShaderSourceBlock source = await reopened.GetBlockAsync<ShaderSourceBlock>();
        using Shader shader = await source.CreateAsync(
            SlangShaderImporter.GlobalSession,
            new SessionDesc { Targets = CreateTargets() },
            definitionPath);

        Assert.True(shader.TryGetSlangEntry(
            0,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection entry,
            out ISlangBlob code));
        Assert.Equal(SlangStage.Compute, entry.Stage);
        Assert.True(code.GetBufferSize() > 0);
    }

    private static Shader CompileSource(TargetDesc[] targets)
    {
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        AssertSuccess(slang.CreateSession(new SessionDesc { Targets = targets }, out ISession session));
        ISlangBlob source = Slang.CreateBlob(System.Text.Encoding.UTF8.GetBytes(Source));
        IModule? sourceUnit = null;
        IEntryPoint? entry = null;
        IComponentType? combinedCode = null;
        IComponentType? linkedCode = null;
        bool shaderOwnsSlang = false;
        try
        {
            sourceUnit = session.LoadModuleFromSource(
                "DirectSlangShader",
                "DirectSlangShader.slang",
                source,
                out ISlangBlob? loadDiagnostics);
            string loadText = DiagnosticText(loadDiagnostics);
            Release(loadDiagnostics);
            Release(source);
            source = null!;
            Assert.NotNull(sourceUnit);
            Assert.True(string.IsNullOrEmpty(loadText), loadText);
            AssertSuccess(sourceUnit!.GetDefinedEntryPoint(0, out entry));
            SlangResult combineResult = session.CreateCompositeComponentType(
                [sourceUnit!, entry!],
                out combinedCode,
                out ISlangBlob? combineDiagnostics);
            string combineText = DiagnosticText(combineDiagnostics);
            Release(combineDiagnostics);
            AssertSuccess(combineResult, combineText);
            Assert.NotNull(combinedCode);
            SlangResult linkResult = combinedCode!.Link(out linkedCode, out ISlangBlob? linkDiagnostics);
            string linkText = DiagnosticText(linkDiagnostics);
            Release(linkDiagnostics);
            AssertSuccess(linkResult, linkText);
            Assert.NotNull(linkedCode);
            Release(combinedCode);
            combinedCode = null;

            shaderOwnsSlang = true;
            return Shader.CreateFromSlang(
                "direct",
                session,
                [sourceUnit!],
                [entry!],
                linkedCode!,
                targets);
        }
        finally
        {
            Release(source);
            Release(combinedCode);
            if (!shaderOwnsSlang)
            {
                Release(linkedCode);
                Release(entry);
                Release(sourceUnit);
                Release(session);
            }
        }
    }

    private static TargetDesc[] CreateTargets()
    {
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        return
        [
            new TargetDesc
            {
                Format = SlangCompileTarget.Spirv,
                Profile = slang.FindProfile("spirv_1_5"),
            },
        ];
    }

    private static string DiagnosticText(ISlangBlob? diagnostics)
        => diagnostics is null || diagnostics.GetBufferSize() == 0
            ? string.Empty
            : diagnostics.AsString;

    private static void AssertSuccess(SlangResult result, string diagnostics = "")
        => Assert.True(result.Succeeded, diagnostics.Length == 0 ? result.ToString() : diagnostics);

    private static void Release(object? value)
    {
        if (value is ComObject com)
            com.FinalRelease();
    }
}
