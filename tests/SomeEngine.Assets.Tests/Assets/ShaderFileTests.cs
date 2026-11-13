using System.Buffers.Binary;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderFileTests
{
    [Fact]
    public async Task ShaderLibraryGeneratesTheRequestedTargetAndItsVariantsFromSavedIr()
    {
        using var files = new AssetTestDirectory();
        string sourcePath = files.File("values.slang");
        await File.WriteAllTextAsync(sourcePath, """
            RWStructuredBuffer<uint> Result;
            [shader("compute")] [numthreads(1,1,1)]
            void Clear() { Result[0] = 0; }
            [shader("compute")] [numthreads(1,1,1)]
            void WriteValue<let value:uint>() { Result[0] = value; }
            """);
        using var asset = new ShaderAsset { Name = "Target variants" };
        ShaderSourceBlock source = asset.GetBlock<ShaderSourceBlock>();
        source.SourceNames = ["values.slang"];
        source.Specializations =
        [new() { EntryPoint = "WriteValue", Arguments = ["3"] },
         new() { EntryPoint = "WriteValue", Arguments = ["7"] }];
        SessionDesc dxil = OneTarget(SlangCompileTarget.Dxil);
        dxil.SearchPaths = [files.Path];
        await asset.CompileAsync(ShaderTestCompilation.Slang, dxil);
        var project = new AssetProject(files.Path, []);
        project.RegisterAsset(await asset.SaveAsync(files.File("values.shader.asset")));
        File.Delete(sourcePath);

        SessionDesc spirv = OneTarget(SlangCompileTarget.Spirv);
        spirv.FileSystem = new EmptyShaderFileSystem();
        await using var loader = new AssetLoader(project.CreateStorage());
        using var shaders = new ShaderLibrary(ShaderTestCompilation.Slang, spirv);
        Shader shader = await shaders.GetAsync(loader, asset.Reference);

        Assert.Equal(SlangCompileTarget.Spirv, shader.GetSlangTarget(0).Format);
        EntryPointReflection clear = shader.RequireSlangEntry(0, "Clear", SlangStage.Compute);
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(shader.GetSlangEntryBlob(0, clear)!.Buffer));
        Assert.True(shader.TryGetSpecialization("WriteValue", ["3"], out Shader first));
        Assert.True(shader.TryGetSpecialization("WriteValue", ["7"], out Shader second));
        EntryPointReflection firstEntry = first.RequireSlangEntry(0, "WriteValue", SlangStage.Compute);
        EntryPointReflection secondEntry = second.RequireSlangEntry(0, "WriteValue", SlangStage.Compute);
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(first.GetSlangEntryBlob(0, firstEntry)!.Buffer));
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(second.GetSlangEntryBlob(0, secondEntry)!.Buffer));
        Assert.NotEqual(first.GetSlangEntryBlob(0, firstEntry)!.Buffer.ToArray(),
            second.GetSlangEntryBlob(0, secondEntry)!.Buffer.ToArray());
    }

    [Fact]
    public async Task ShaderRemainsUsableAfterAssetAndFileReferencesDisappear()
    {
        using var files = new AssetTestDirectory();
        string path = await WriteShaderFileAsync(files);
        Shader shader;
        await using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
            shader = await ShaderTestCompilation.CreateAsync(asset);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        int targetIndex = shader.RequireSlangTarget(SlangCompileTarget.Dxil);
        EntryPointReflection entry = shader.RequireSlangEntry(
            targetIndex,
            "VSMain",
            SlangStage.Vertex);
        Assert.True(
            shader.GetSlangEntryBlob(targetIndex, entry) is not null ||
            !shader.GetSlangEntryBytes(targetIndex, entry).IsEmpty);
    }

    [Fact]
    public async Task NewVersionCreatesAnotherShaderAndLeavesOldReadersUnchanged()
    {
        using var files = new AssetTestDirectory();
        string path = await WriteShaderFileAsync(files);
        await using ShaderAsset asset = await ShaderAsset.OpenAsync(path);

        SessionDesc spirv = OneTarget(SlangCompileTarget.Spirv);
        Shader oldShader = await SomeEngine.Resources.ShaderLibrary.CreateAsync(asset, ShaderTestCompilation.Slang, spirv);
        Shader newShader = await ShaderTestCompilation.CreateAsync(asset);

        Assert.NotSame(oldShader, newShader);
        Assert.True(oldShader.TryGetSlangTarget(SlangCompileTarget.Spirv, out _));
        Assert.False(oldShader.TryGetSlangTarget(SlangCompileTarget.Dxil, out _));
        Assert.True(newShader.TryGetSlangTarget(SlangCompileTarget.Spirv, out _));
        Assert.True(newShader.TryGetSlangTarget(SlangCompileTarget.Dxil, out _));
    }

    [Fact]
    public async Task FailedReplacementBuildLeavesPreviouslyPublishedShaderUsable()
    {
        using var files = new AssetTestDirectory();
        string source = await WriteShaderFileAsync(files);
        await using ShaderAsset original = await ShaderAsset.OpenAsync(source);
        Shader published = await ShaderTestCompilation.CreateAsync(original);
        SessionDesc sessionDescription = ShaderTestCompilation.CreateSessionDescription();
        ulong codeKey = ShaderBlock.EntryCodeKey(sessionDescription, 0, 0);
        byte[] bytes = await File.ReadAllBytesAsync(source);
        await using var document = await SomeEngine.Serialization.Containers.BinaryDocument.OpenAsync(
            SomeEngine.Serialization.IO.FileRangeSource.Open(source),
            ShaderAsset.WireType,
            ownsSource: true);
        AssetBlockDescription block = original.BlockDescriptions.Single(
            value => value.BlockTypeId == ShaderBlock.BlockTypeId);
        var chunk = await document.FindChunkAsync(block.GetStorageKey(codeKey));
        Assert.NotNull(chunk);
        bytes[checked((int)(chunk.Value.Offset + chunk.Value.StoredLength / 2))] ^= 0x80;
        string broken = files.File("broken.shader.asset");
        await File.WriteAllBytesAsync(broken, bytes);
        await using ShaderAsset replacement = await ShaderAsset.OpenAsync(broken);

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => SomeEngine.Resources.ShaderLibrary.CreateAsync(replacement, ShaderTestCompilation.Slang, sessionDescription)
            .AsTask());

        Assert.True(published.TryGetSlangTarget(SlangCompileTarget.Dxil, out _));
        Assert.True(published.TryGetSlangTarget(SlangCompileTarget.Spirv, out _));
    }

    private static async Task<string> WriteShaderFileAsync(AssetTestDirectory files)
    {
        using var asset = new ShaderAsset { Name = "triangle" };
        asset.GetBlock<ShaderSourceBlock>().SourceNames = ["hello_triangle.slang"];
        SessionDesc settings = ShaderTestCompilation.CreateSessionDescription();
        settings.SearchPaths = [SomeEngine.Tests.TestProjectPaths.ShaderDirectory()];
        await asset.CompileAsync(ShaderTestCompilation.Slang, settings);
        string path = files.File("triangle.shader.asset");
        await asset.SaveAsync(path);
        return path;
    }

    private static SessionDesc OneTarget(SlangCompileTarget format)
    {
        SessionDesc description = ShaderTestCompilation.CreateSessionDescription();
        description.Targets =
        [
            description.Targets!.Single(target => target.Format == format),
        ];
        return description;
    }
}
