using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderFileTests
{
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
        Shader oldShader = await asset.CreateAsync(ShaderTestCompilation.Slang, spirv);
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

        await Assert.ThrowsAnyAsync<InvalidDataException>(() => replacement
            .CreateAsync(ShaderTestCompilation.Slang, sessionDescription)
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
