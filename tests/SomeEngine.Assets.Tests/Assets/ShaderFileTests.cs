using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SlangShaderSharp;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderFileTests
{
    [Fact]
    public async Task ClosedShaderCannotReadNewContentOrBeUpdated()
    {
        string source = SomeEngine.Tests.TestProjectPaths.BuiltShaderPath("hello_triangle.shader.asset");
        await using ShaderAsset file = await ShaderAsset.OpenAsync(source);
        using Shader shader = await file.GetBlock<ShaderBlock>().CreateAsync(SlangCompileTarget.Spirv, file.Name);
        shader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => shader.TryProgram("dxil", out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => file.GetBlock<ShaderBlock>().UpdateAsync(shader, file.Name).AsTask());
    }

    [Fact]
    public async Task ObserverFailureDoesNotPreventOtherShaderObserversFromSeeingCommittedContent()
    {
        string source = SomeEngine.Tests.TestProjectPaths.BuiltShaderPath("hello_triangle.shader.asset");
        await using ShaderAsset file = await ShaderAsset.OpenAsync(source);
        using Shader shader = await file.GetBlock<ShaderBlock>().CreateAsync(file.Name);
        ulong revision = shader.Revision;
        int notifications = 0;
        shader.Changed += _ => throw new InvalidOperationException("test observer failure");
        shader.Changed += _ => notifications++;
        await Assert.ThrowsAsync<AggregateException>(() => file.GetBlock<ShaderBlock>().UpdateAsync(shader, file.Name).AsTask());
        Assert.Equal(revision + 1, shader.Revision);
        Assert.Equal(1, notifications);
        Assert.True(shader.TryProgram("spirv", out _));
        Assert.True(shader.TryProgram("dxil", out _));
    }

    [Fact]
    public async Task ReadyShaderSurvivesClosedSourcesAndAdditionalBackendsRequireExplicitAssetUpdate()
    {
        using var directory = new AssetTestDirectory();
        string source = SomeEngine.Tests.TestProjectPaths.BuiltShaderPath("hello_triangle.shader.asset");
        string path = directory.File("hello.shader.asset");
        File.Copy(source, path);
        var project = new AssetProject(directory.Path, []);
        using var shaderFile = await ShaderAsset.OpenAsync(Path.Combine(project.RootPath, path));
        Guid id = project.RegisterAsset(shaderFile.Describe());
        var loader = new AssetLoader(project.CreateStorage());
        ShaderAsset file = await ShaderAsset.OpenAsync(loader, new AssetId<ShaderAsset>(id));
        using Shader shader = await file.GetBlock<ShaderBlock>().CreateAsync(SlangCompileTarget.Spirv, file.Name);
        Assert.True(shader.TryProgram("spirv", out _));
        Assert.False(shader.TryProgram("dxil", out _));
        await file.DisposeAsync();
        await loader.DisposeAsync();
        Assert.True(shader.TryProgram("spirv", out _));
        Assert.False(shader.TryProgram("dxil", out _));
        using ShaderAsset reopened = await ShaderAsset.OpenAsync(path);
        await reopened.GetBlock<ShaderBlock>().UpdateAsync(shader, reopened.Name);
        Assert.True(shader.TryProgram("spirv", out _));
        Assert.True(shader.TryProgram("dxil", out _));
    }

    [Fact]
    public async Task FailedBytecodeReadDoesNotReplaceExistingShaderContent()
    {
        using var directory = new AssetTestDirectory();
        string source = SomeEngine.Tests.TestProjectPaths.BuiltShaderPath("hello_triangle.shader.asset");
        await using ShaderAsset original = await ShaderAsset.OpenAsync(source);
        using Shader shader = await original.GetBlock<ShaderBlock>().CreateAsync(original.Name);
        string before = shader.Name!;
        ulong revision = shader.Revision;
        byte[] bytes = await File.ReadAllBytesAsync(source);
        await using var document = await SomeEngine.Serialization.Containers.BinaryDocument.OpenAsync(SomeEngine.Serialization.IO.FileRangeSource.Open(source), ShaderAsset.WireType, ownsSource: true);
        var descriptor = await document.FindChunkAsync(original.BlockDescriptions.Single(block => block.BlockTypeId == ShaderBlock.BlockTypeId).GetStorageKey(original.GetBlock<ShaderBlock>().Programs[0].DataChunkKey));
        bytes[checked((int)(descriptor!.Value.Offset + descriptor.Value.StoredLength / 2))] ^= 0x80;
        string broken = directory.File("broken.shader.asset");
        await File.WriteAllBytesAsync(broken, bytes);
        await using ShaderAsset replacement = await ShaderAsset.OpenAsync(broken);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => replacement.GetBlock<ShaderBlock>().UpdateAsync(shader, replacement.Name).AsTask());
        Assert.Equal(before, shader.Name);
        Assert.Equal(revision, shader.Revision);
        Assert.True(shader.TryProgram("spirv", out _));
        Assert.True(shader.TryProgram("dxil", out _));
    }
}
