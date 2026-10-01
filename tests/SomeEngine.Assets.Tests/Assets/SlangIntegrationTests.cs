using System.Buffers;
using System.Security.Cryptography;
using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class SlangIntegrationTests
{
    [Fact]
    public async Task CompilationReturnsDirectEntriesForEveryTarget()
    {
        const string source = """
            [shader("vertex")]
            float4 vertexMain(float4 position : POSITION) : SV_Position
            {
                return position;
            }

            [shader("fragment")]
            float4 pixelMain() : SV_Target0
            {
                return float4(1, 0, 0, 1);
            }
            """;
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("compiled.slang");
        File.WriteAllText(sourcePath, source);

        using ShaderAsset asset = ShaderTestCompilation.Compile(sourcePath);
        Shader shader = await ShaderTestCompilation.CreateAsync(asset);

        Assert.Equal(2, shader.SlangTargetCount);
        Assert.Equal(2, shader.SlangEntryCount);
        foreach (SlangCompileTarget target in new[]
                 {
                     SlangCompileTarget.Dxil,
                     SlangCompileTarget.Spirv,
                 })
        {
            int targetIndex = shader.RequireSlangTarget(target);
            EntryPointReflection vertex = shader.RequireSlangEntry(
                targetIndex,
                "vertexMain",
                SlangStage.Vertex);
            EntryPointReflection pixel = shader.RequireSlangEntry(
                targetIndex,
                "pixelMain",
                SlangStage.Fragment);
            Assert.Equal(SlangStage.Vertex, vertex.Stage);
            Assert.Equal(SlangStage.Fragment, pixel.Stage);
            Assert.NotEmpty(GetCode(shader, targetIndex, vertex));
            Assert.NotEmpty(GetCode(shader, targetIndex, pixel));
        }
    }

    [Fact]
    public async Task DirectReflectionExposesGlobalShaderFields()
    {
        const string source = """
            struct Parameters { float4 Color; }
            ConstantBuffer<Parameters> ParametersData;
            Texture2D TextureData;

            [shader("fragment")]
            float4 pixelMain() : SV_Target0
            {
                return TextureData.Load(int3(0, 0, 0)) * ParametersData.Color;
            }
            """;
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("reflection.slang");
        File.WriteAllText(sourcePath, source);

        using ShaderAsset asset = ShaderTestCompilation.Compile(sourcePath);
        Shader shader = await ShaderTestCompilation.CreateAsync(asset);
        int targetIndex = shader.RequireSlangTarget(SlangCompileTarget.Spirv);
        ShaderReflection reflection = shader.GetSlangReflection(targetIndex);
        VariableLayoutReflection globals = reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        Assert.NotEqual(VariableLayoutReflection.Null, globals);
        TypeLayoutReflection fields = globals.TypeLayout;
        if (fields.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            fields = fields.ElementTypeLayout;

        string[] names = Enumerable.Range(0, checked((int)fields.FieldCount))
            .Select(index => fields.GetFieldByIndex(checked((uint)index)).Name)
            .ToArray();
        Assert.Contains("ParametersData", names);
        Assert.Contains("TextureData", names);
    }

    [Fact]
    public async Task DefinitionRetainsIdentityAndSerializedSource()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain() {}
            """;
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("identity.slang");
        string definitionPath = files.File("identity.shader");
        File.WriteAllText(sourcePath, source);

        using ShaderAsset definition = ShaderTestCompilation.Define(sourcePath);
        await definition.SaveSourceAsync(definitionPath);
        using ShaderAsset first = SlangShaderImporter.CompileDefinition(
            definition,
            definitionPath);
        using ShaderAsset reopened = await ShaderAsset.OpenSourceAsync(definitionPath);
        using ShaderAsset second = SlangShaderImporter.CompileDefinition(
            reopened,
            definitionPath);
        Shader firstShader = await ShaderTestCompilation.CreateAsync(first);
        Shader secondShader = await ShaderTestCompilation.CreateAsync(second);

        Assert.Equal(definition.Id, first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.NotNull(first.GetBlock<ShaderBlock>().ImportTrace);
        Assert.Contains(
            ShaderSourceBlock.SerializedSourceKey(0),
            first.GetBlock<ShaderSourceBlock>().ContentKeys);
        Assert.Equal(
            EntryHash(firstShader, SlangCompileTarget.Spirv, "computeMain", SlangStage.Compute),
            EntryHash(secondShader, SlangCompileTarget.Spirv, "computeMain", SlangStage.Compute));
    }

    [Fact]
    public void IncludeChangesUpdateTheImportFingerprint()
    {
        using var files = new AssetTestDirectory();
        string includePath = files.File("common.slang");
        string sourcePath = files.File("include.slang");
        File.WriteAllText(includePath, "float4 Color() { return float4(1, 0, 0, 1); }");
        File.WriteAllText(
            sourcePath,
            "#include \"common.slang\"\n[shader(\"fragment\")] float4 pixelMain() : SV_Target0 { return Color(); }");
        using ShaderAsset definition = ShaderTestCompilation.Define(sourcePath);
        using ShaderAsset first = ShaderTestCompilation.Compile(definition, sourcePath);
        string firstHash = first.GetBlock<ShaderBlock>().ImportTrace!.ContentFingerprint!;

        File.WriteAllText(includePath, "float4 Color() { return float4(0, 1, 0, 1); }");
        using ShaderAsset second = ShaderTestCompilation.Compile(definition, sourcePath);
        ImportTrace trace = second.GetBlock<ShaderBlock>().ImportTrace!;

        Assert.NotEqual(firstHash, trace.ContentFingerprint);
        Assert.Contains(
            trace.Dependencies!,
            dependency => dependency.Path!.EndsWith("common.slang", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DifferentShaderBehaviorProducesDifferentEntryCode()
    {
        const string firstSource = """
            RWStructuredBuffer<float> Values;
            [shader("compute")]
            [numthreads(64, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                Values[id.x] = float(id.x) * 2.0;
            }
            """;
        const string secondSource = """
            RWStructuredBuffer<float> Values;
            [shader("compute")]
            [numthreads(64, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                Values[id.x] = float(id.x) * 3.0 + 1.0;
            }
            """;
        using var files = new AssetTestDirectory();
        Shader first = await Compile(files.File("first.slang"), firstSource);
        Shader second = await Compile(files.File("second.slang"), secondSource);

        Assert.NotEqual(
            EntryHash(first, SlangCompileTarget.Spirv, "computeMain", SlangStage.Compute),
            EntryHash(second, SlangCompileTarget.Spirv, "computeMain", SlangStage.Compute));
    }

    private static async ValueTask<Shader> Compile(string sourcePath, string source)
    {
        File.WriteAllText(sourcePath, source);
        using ShaderAsset asset = ShaderTestCompilation.Compile(sourcePath);
        return await ShaderTestCompilation.CreateAsync(asset);
    }

    private static string EntryHash(
        Shader shader,
        SlangCompileTarget target,
        string entryName,
        SlangStage stage)
    {
        int targetIndex = shader.RequireSlangTarget(target);
        EntryPointReflection entry = shader.RequireSlangEntry(targetIndex, entryName, stage);
        return Convert.ToHexString(SHA256.HashData(GetCode(shader, targetIndex, entry)));
    }

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
}
