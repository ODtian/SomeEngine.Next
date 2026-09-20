using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderFileTests
{
    [Fact]
    public async Task ClosedShaderCannotReadNewContentOrBeUpdated()
    {
        string source = System.IO.Path.Combine(SomeEngine.Tests.TestProjectPaths.ProjectRoot(),
            "assets", "Shaders", "hello_triangle.shader.asset");
        await using Shader.Asset file = await Shader.Asset.OpenAsync(source);
        using Shader shader = await Shader.CreateAsync(file, ShaderTarget.Spirv);
        shader.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => shader.LoadBackendAsync(ShaderTarget.Dxil).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => shader.UpdateAsync(file).AsTask());
    }

    [Fact]
    public async Task ObserverFailureDoesNotPreventOtherShaderObserversFromSeeingCommittedContent()
    {
        string source = System.IO.Path.Combine(SomeEngine.Tests.TestProjectPaths.ProjectRoot(),
            "assets", "Shaders", "hello_triangle.shader.asset");
        await using Shader.Asset file = await Shader.Asset.OpenAsync(source);
        using Shader shader = await Shader.CreateAsync(file);
        ulong revision = shader.Revision;
        int notifications = 0;
        shader.Changed += _ => throw new InvalidOperationException("test observer failure");
        shader.Changed += _ => notifications++;
        await Assert.ThrowsAsync<AggregateException>(() => shader.UpdateAsync(file).AsTask());
        Assert.Equal(revision + 1, shader.Revision);
        Assert.Equal(1, notifications);
        Assert.True(shader.TryProgram("spirv", out _));
        Assert.True(shader.TryProgram("dxil", out _));
    }

    [Fact]
    public async Task AdditionalBackendCanBeReadAfterTheAssetAndLoaderClose()
    {
        using var directory = new AssetTestDirectory();
        string source = System.IO.Path.Combine(SomeEngine.Tests.TestProjectPaths.ProjectRoot(),
            "assets", "Shaders", "hello_triangle.shader.asset");
        string path = directory.File("hello.shader.asset");
        File.Copy(source, path);
        var project = new AssetProject(directory.Path, []);
        using var shaderFile = await Shader.Asset.OpenAsync(Path.Combine(project.RootPath, path));
        Guid id = project.RegisterAsset(shaderFile.Describe());
        var loader = new AssetLoader(project.CreateStorage());
        Shader.Asset file = await Shader.Asset.OpenAsync(loader, new AssetId<Shader>(id));
        using Shader shader = await Shader.CreateAsync(file, ShaderTarget.Spirv);
        Assert.True(shader.TryProgram("spirv", out _));
        Assert.False(shader.TryProgram("dxil", out _));
        await file.DisposeAsync();
        await loader.DisposeAsync();
        await shader.LoadBackendAsync(ShaderTarget.Dxil);
        Assert.True(shader.TryProgram("spirv", out _));
        Assert.True(shader.TryProgram("dxil", out _));
    }

    [Fact]
    public async Task FailedBytecodeReadDoesNotReplaceExistingShaderContent()
    {
        using var directory = new AssetTestDirectory();
        string source = System.IO.Path.Combine(SomeEngine.Tests.TestProjectPaths.ProjectRoot(),
            "assets", "Shaders", "hello_triangle.shader.asset");
        await using Shader.Asset original = await Shader.Asset.OpenAsync(source);
        using Shader shader = await Shader.CreateAsync(original);
        string before = shader.Name!;
        ulong revision = shader.Revision;
        byte[] bytes = await File.ReadAllBytesAsync(source);
        await using var document = original.RetainSource()!;
        var descriptor = await document.FindChunkAsync(original.Programs![0].DataChunk.Key);
        bytes[checked((int)(descriptor!.Value.Offset + descriptor.Value.StoredLength / 2))] ^= 0x80;
        string broken = directory.File("broken.shader.asset");
        await File.WriteAllBytesAsync(broken, bytes);
        await using Shader.Asset replacement = await Shader.Asset.OpenAsync(broken);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => shader.UpdateAsync(replacement).AsTask());
        Assert.Equal(before, shader.Name);
        Assert.Equal(revision, shader.Revision);
        Assert.True(shader.TryProgram("spirv", out _));
        Assert.True(shader.TryProgram("dxil", out _));
    }
}
