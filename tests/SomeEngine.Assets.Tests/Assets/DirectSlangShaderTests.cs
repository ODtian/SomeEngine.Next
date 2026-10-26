using System.Buffers;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Job;
using System.Runtime.InteropServices.Marshalling;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class DirectSlangShaderTests
{
    [Fact]
    public async Task EntryMetadataCompletesWithZeroWorkers()
    {
        using var files = new AssetTestDirectory();
        await File.WriteAllTextAsync(files.File("metadata.slang"), """
            RWStructuredBuffer<uint> values;
            [shader("compute")][numthreads(1,1,1)] void Compute() { values[0] = 7; }
            """);
        using var block = new ShaderSourceBlock { SourceNames = ["metadata.slang"] };
        ShaderCompilation compiled = await block.CompileAsync(SlangShaderImporter.GlobalSession,
            new SessionDesc { Targets = CreateTargets(), SearchPaths = [files.Path] }, "metadata");
        Shader shader = compiled.Shader;
        var location = Assert.Single(shader.GetSlangUsedParameters(0, 0));
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 0 });
        try
        {
            IMetadata metadata = await shader.GetSlangEntryMetadataAsync(0, 0).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.False(metadata.IsParameterLocationUsed(location.Category, checked((nuint)location.Space),
                checked((nuint)location.Register), out bool used).Failed);
            Assert.True(used);
        }
        finally { JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 }); }
    }

    [Fact]
    public async Task ShaderCodeGenerationUsesCallingSchedulerAndKeepsEntryOrder()
    {
        using var files = new AssetTestDirectory();
        await File.WriteAllTextAsync(files.File("entries.slang"), """
            RWStructuredBuffer<uint> values;
            [shader("compute")][numthreads(1,1,1)] void First() { values[0] = 7; }
            [shader("compute")][numthreads(1,1,1)] void Second() { values[1] = 19; }
            """);
        using var block = new ShaderSourceBlock { SourceNames = ["entries.slang"] };
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        var result = new Task<ShaderCompilation>[1];
        await scheduler.Schedule(new CompileShader(block,
            new SessionDesc { Targets = CreateTargets(), SearchPaths = [files.Path] }, result)).WaitAsync();
        Shader shader = (await result[0]).Shader;
        Assert.Equal("First", shader.GetSlangReflection(0).GetEntryPointByIndex(0).Name);
        Assert.Equal("Second", shader.GetSlangReflection(0).GetEntryPointByIndex(1).Name);
        Assert.NotEmpty(shader.GetSlangEntryBlob(0, 0)!.Buffer.ToArray());
        Assert.NotEmpty(shader.GetSlangEntryBlob(0, 1)!.Buffer.ToArray());
        Assert.True(scheduler.GetStats().ScheduledJobs > 1);
    }

    private readonly record struct CompileShader(ShaderSourceBlock SourceBlock, SessionDesc Description,
        Task<ShaderCompilation>[] Result) : IJob
    {
        public void Execute() => Result[0] = SourceBlock.CompileAsync(
            SlangShaderImporter.GlobalSession, Description, "entries").AsTask();
    }

    [Fact]
    public async Task SourceHashesDescribeTheBytesSuppliedByTheCompilerFileSystem()
    {
        using var files = new AssetTestDirectory();
        string path = files.File("changing.slang");
        await File.WriteAllTextAsync(path, Source);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using var block = new ShaderSourceBlock { SourceNames = ["changing.slang"] };
        ShaderCompilation compiled = await block.CompileAsync(SlangShaderImporter.GlobalSession,
            new SessionDesc { Targets = CreateTargets(), SearchPaths = [files.Path],
                FileSystem = new ChangingShaderFileSystem(path, bytes) }, "changing");
        ShaderSourceFile input = Assert.Single(compiled.Files);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), input.ContentHash);
        Assert.NotEqual(ImportHash.FileSha256(path), input.ContentHash);
        Assert.True(compiled.Shader.TryGetSlangEntry(0, "Compute", SlangStage.Compute, out _));
    }

    [Fact]
    public async Task SourceBlockCompilesRelativeSourceAndRecordsTheReadBytes()
    {
        using var files = new AssetTestDirectory();
        string directory = files.File("definitions");
        Directory.CreateDirectory(directory);
        string path = files.File("relative.slang");
        await File.WriteAllTextAsync(path, Source);
        using var block = new ShaderSourceBlock { SourceNames = ["../relative.slang"] };
        ShaderCompilation compiled = await block.CompileAsync(SlangShaderImporter.GlobalSession,
            new SessionDesc { Targets = CreateTargets(), SearchPaths = [directory] }, "relative");
        ShaderSourceFile input = Assert.Single(compiled.Files);
        Assert.Equal(path, input.Path);
        Assert.Equal(ImportHash.FileSha256(path), input.ContentHash);
        Assert.Single(compiled.SerializedSources);
        block.SetSerializedSource(0, compiled.SerializedSources[0].Span);
        File.Delete(path);
        Shader restored = await block.CreateAsync(SlangShaderImporter.GlobalSession,
            new SessionDesc { Targets = CreateTargets() }, "restored");
        Assert.True(restored.TryGetSlangEntry(0, "Compute", SlangStage.Compute, out _));
    }

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
        Assert.True(shader.HasSlangData);
        Assert.Equal(1, shader.SlangTargetCount);
        Assert.Equal(1, shader.SlangEntryCount);
        Assert.Equal(SlangCompileTarget.Spirv, shader.GetSlangTarget(0).Format);
        Assert.True(shader.TryGetSlangEntry(
            0,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection entry));
        Assert.Equal("Compute", entry.Name);
        Assert.Equal(SlangStage.Compute, entry.Stage);
        Assert.NotEmpty(GetCode(shader, 0, entry));
        Assert.Equal(entry, shader.GetSlangReflection(0).GetEntryPointByIndex(0));
    }

    [Fact]
    public void SerializedSlangUnitRecreatesResidentReflectionAndCode()
    {
        TargetDesc[] targets = CreateTargets();
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        byte[] serializedBytes = SerializeSource(slang, targets);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        ISlangBlob serialized = Slang.CreateBlob(serializedBytes);

        AssertSuccess(slang.CreateSession(new SessionDesc { Targets = targets }, out ISession secondSession));
        IModule? secondUnit = secondSession.LoadModuleFromIRBlob(
            "SerializedSlangUnit",
            "SerializedSlangUnit.slang",
            serialized,
            out ISlangBlob? reloadDiagnostics);
        string reloadText = DiagnosticText(reloadDiagnostics);
        Assert.NotNull(secondUnit);
        Assert.True(string.IsNullOrEmpty(reloadText), reloadText);
        AssertSuccess(secondUnit!.GetDefinedEntryPoint(0, out IEntryPoint entry));
        SlangResult combineResult = secondSession.CreateCompositeComponentType(
            [secondUnit, entry],
            out IComponentType combinedCode,
            out ISlangBlob? combineDiagnostics);
        string combineText = DiagnosticText(combineDiagnostics);
        AssertSuccess(combineResult, combineText);
        Assert.NotNull(combinedCode);
        SlangResult linkResult = combinedCode.Link(
            out IComponentType linkedCode,
            out ISlangBlob? linkDiagnostics);
        string linkText = DiagnosticText(linkDiagnostics);
        AssertSuccess(linkResult, linkText);
        Assert.NotNull(linkedCode);

        Shader shader = Shader.CreateFromSlang(
            "serialized",
            slang,
            secondSession,
            [secondUnit],
            [entry],
            linkedCode,
            targets);
        Assert.True(shader.TryGetSlangEntry(
            0,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection reflection));
        Assert.Equal(SlangStage.Compute, reflection.Stage);
        Assert.NotEmpty(GetCode(shader, 0, reflection));

        static byte[] SerializeSource(IGlobalSession slang, TargetDesc[] targets)
        {
            AssertSuccess(slang.CreateSession(
                new SessionDesc { Targets = targets },
                out ISession session));
            ISlangBlob source = Slang.CreateBlob(System.Text.Encoding.UTF8.GetBytes(Source));
            IModule? unit = session.LoadModuleFromSource(
                "SerializedSlangUnit",
                "SerializedSlangUnit.slang",
                source,
                out ISlangBlob? diagnostics);
            string diagnosticText = DiagnosticText(diagnostics);
            Assert.NotNull(unit);
            Assert.True(string.IsNullOrEmpty(diagnosticText), diagnosticText);
            AssertSuccess(unit!.Serialize(out ISlangBlob serialized));
            Assert.True(serialized.GetBufferSize() > 0);
            return serialized.Buffer.ToArray();
        }
    }

    [Fact]
    public async Task SourceBlockCompilesAResidentShaderOnDemand()
    {
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("on-demand.slang");
        await File.WriteAllTextAsync(sourcePath, Source);

        Shader shader;
        using (ShaderAsset definition = ShaderTestCompilation.Define(sourcePath))
        using (ShaderAsset built = ShaderTestCompilation.Compile(definition, sourcePath))
        using (ShaderSourceBlock block = built.GetBlock<ShaderSourceBlock>().Duplicate())
        {
            File.Delete(sourcePath);
            shader = await block.CreateAsync(
                SlangShaderImporter.GlobalSession,
                new SessionDesc { Targets = CreateTargets() },
                "on-demand");
        }

        Assert.Equal("on-demand", shader.Name);
        Assert.True(shader.TryGetSlangEntry(
            0,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection entry));
        Assert.Equal("Compute", entry.Name);
        Assert.NotEmpty(GetCode(shader, 0, entry));
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
        Shader shader = await source.CreateAsync(
            SlangShaderImporter.GlobalSession,
            new SessionDesc { Targets = CreateTargets() },
            definitionPath);

        Assert.True(shader.TryGetSlangEntry(
            0,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection entry));
        Assert.Equal(SlangStage.Compute, entry.Stage);
        Assert.NotEmpty(GetCode(shader, 0, entry));
    }

    [Fact]
    public async Task SourceBlockAppliesDirectSlangSpecializationArguments()
    {
        const string specializedSource = """
            RWStructuredBuffer<uint> values;

            interface IQuality
            {
                static uint GetValue();
            }

            struct HighQuality : IQuality
            {
                static uint GetValue() { return 100; }
            }

            struct LowQuality : IQuality
            {
                static uint GetValue() { return 1; }
            }

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void Compute<T : IQuality>(uint3 threadId : SV_DispatchThreadID)
            {
                values[threadId.x] = T::GetValue();
            }
            """;

        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        TargetDesc[] targets = CreateTargets();
        var sessionDescription = new SessionDesc { Targets = targets };
        AssertSuccess(slang.CreateSession(sessionDescription, out ISession session));
        ISlangBlob source = Slang.CreateBlob(System.Text.Encoding.UTF8.GetBytes(specializedSource));
        IModule? unit = session.LoadModuleFromSource(
            "SpecializedSourceBlock",
            "SpecializedSourceBlock.slang",
            source,
            out ISlangBlob? loadDiagnostics);
        string loadText = DiagnosticText(loadDiagnostics);
        Assert.NotNull(unit);
        Assert.True(string.IsNullOrEmpty(loadText), loadText);
        AssertSuccess(unit!.Serialize(out ISlangBlob serialized));

        using var block = new ShaderSourceBlock { SourceNames = ["SpecializedSourceBlock.slang"] };
        block.SetSerializedSource(0, serialized.Buffer);
        Shader high = await block.CreateAsync(
            slang,
            sessionDescription,
            "high",
            new[] { SpecializationArg.FromExpression("HighQuality") });
        Shader low = await block.CreateAsync(
            slang,
            sessionDescription,
            "low",
            new[] { SpecializationArg.FromExpression("LowQuality") });

        Assert.True(high.TryGetSlangEntry(
            0,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection highEntry));
        Assert.True(low.TryGetSlangEntry(
            0,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection lowEntry));
        byte[] highCode = GetCode(high, 0, highEntry);
        byte[] lowCode = GetCode(low, 0, lowEntry);
        Assert.False(highCode.AsSpan().SequenceEqual(lowCode));

        using var savedCode = new ShaderBlock();
        block.Specializations =
        [
            new() { EntryPoint = "Compute", Arguments = ["HighQuality"] },
            new() { EntryPoint = "Compute", Arguments = ["LowQuality"] },
        ];
        savedCode.SetEntryCode(sessionDescription, 0, 0, highCode, ["HighQuality"], "Compute");
        savedCode.SetEntryUsage(sessionDescription, 0, 0, high.GetSlangUsedParameters(0, 0), ["HighQuality"], "Compute");
        savedCode.SetEntryCode(sessionDescription, 0, 0, lowCode, ["LowQuality"], "Compute");
        savedCode.SetEntryUsage(sessionDescription, 0, 0, low.GetSlangUsedParameters(0, 0), ["LowQuality"], "Compute");
        Shader savedBlockHigh = await savedCode.CreateAsync(
            block,
            slang,
            sessionDescription,
            "saved-high",
            "Compute",
            new[] { SpecializationArg.FromExpression("HighQuality") });
        Shader savedBlockLow = await savedCode.CreateAsync(
            block,
            slang,
            sessionDescription,
            "saved-low",
            "Compute",
            new[] { SpecializationArg.FromExpression("LowQuality") });
        Assert.True(savedBlockHigh.TryGetSlangEntry(
            0,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection savedHighEntry));
        Assert.True(savedBlockLow.TryGetSlangEntry(
            0,
            "Compute",
            SlangStage.Compute,
            out EntryPointReflection savedLowEntry));
        Assert.True(highCode.AsSpan().SequenceEqual(
            GetCode(savedBlockHigh, 0, savedHighEntry)));
        Assert.True(lowCode.AsSpan().SequenceEqual(
            GetCode(savedBlockLow, 0, savedLowEntry)));
    }

    private static Shader CompileSource(TargetDesc[] targets)
    {
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        AssertSuccess(slang.CreateSession(new SessionDesc { Targets = targets }, out ISession session));
        ISlangBlob source = Slang.CreateBlob(System.Text.Encoding.UTF8.GetBytes(Source));
        IModule? sourceUnit = session.LoadModuleFromSource(
            "DirectSlangShader",
            "DirectSlangShader.slang",
            source,
            out ISlangBlob? loadDiagnostics);
        string loadText = DiagnosticText(loadDiagnostics);
        Assert.NotNull(sourceUnit);
        Assert.True(string.IsNullOrEmpty(loadText), loadText);
        AssertSuccess(sourceUnit!.GetDefinedEntryPoint(0, out IEntryPoint entry));
        SlangResult combineResult = session.CreateCompositeComponentType(
            [sourceUnit, entry],
            out IComponentType combinedCode,
            out ISlangBlob? combineDiagnostics);
        string combineText = DiagnosticText(combineDiagnostics);
        AssertSuccess(combineResult, combineText);
        Assert.NotNull(combinedCode);
        SlangResult linkResult = combinedCode.Link(
            out IComponentType linkedCode,
            out ISlangBlob? linkDiagnostics);
        string linkText = DiagnosticText(linkDiagnostics);
        AssertSuccess(linkResult, linkText);
        Assert.NotNull(linkedCode);

        return Shader.CreateFromSlang(
            "direct",
            slang,
            session,
            [sourceUnit],
            [entry],
            linkedCode,
            targets);
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

    private static byte[] GetCode(
        Shader shader,
        int targetIndex,
        EntryPointReflection entry)
    {
        ISlangBlob? blob = shader.GetSlangEntryBlob(targetIndex, entry);
        return blob is null
            ? shader.GetSlangEntryBytes(targetIndex, entry).ToArray()
            : blob.Buffer.ToArray();
    }

    private static void AssertSuccess(SlangResult result, string diagnostics = "")
        => Assert.True(result.Succeeded, diagnostics.Length == 0 ? result.ToString() : diagnostics);

}

[GeneratedComClass]
internal partial class ChangingShaderFileSystem(string sourcePath, byte[] sourceBytes) : ISlangFileSystem
{
    public unsafe void* CastAs(Guid guid) => null;
    public SlangResult LoadFile(string path, out ISlangBlob outBlob)
    {
        if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), sourcePath))
        { outBlob = null!; return SlangResult.SLANG_E_NOT_FOUND; }
        outBlob = Slang.CreateBlob(sourceBytes);
        File.WriteAllText(sourcePath, "changed after the compiler read its input");
        return SlangResult.SLANG_OK;
    }
}
