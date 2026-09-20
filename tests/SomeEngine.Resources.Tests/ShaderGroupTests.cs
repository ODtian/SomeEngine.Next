namespace SomeEngine.Resources.Tests;

public sealed class ShaderGroupTests
{
    [Fact]
    public void ClusterEntriesKeepDirectShaderReferencesAndDisposeTheirDependencies()
    {
        using var shared = new Shader { Name = "shared" };
        using var unrelated = new Shader { Name = "unrelated" };
        var vertex = new ShaderEntry(shared, "vertexMain", ShaderStage.Vertex);
        var pixel = new ShaderEntry(shared, "pixelMain", ShaderStage.Pixel);
        using var group = new ClusterShaders
        {
            Name = "raster",
            Operations = [new ShaderOperation
            {
                Role = ClusterShaderOperationRole.HardwareVisibilityRaster,
                Shaders = [vertex, pixel],
            }],
        };
        Assert.Same(shared, group.Operations[0].Shaders[0].Shader);
        Assert.Same(shared, group.Operations[0].Shaders[1].Shader);
        Assert.Equal("vertexMain", group.Operations[0].Shaders[0].EntryPoint);
        Assert.Equal(ShaderStage.Pixel, group.Operations[0].Shaders[1].Stage);
        group.Dispose();
        Assert.True(shared.IsDisposed);
        Assert.False(unrelated.IsDisposed);
        group.Dispose();
    }

    [Fact]
    public void ShadowEntriesShareReadyShadersAndCloseAllTheirDependencies()
    {
        using var first = new Shader { Name = "first" };
        using var second = new Shader { Name = "second" };
        using var group = new VirtualShadowMapShaders
        {
            MarkPages = new(first, "mark", ShaderStage.Compute),
            ClearPages = new(first, "clear", ShaderStage.Compute),
            ReducePageHiZ = new(second, "reduce", ShaderStage.Compute),
        };
        Assert.Same(group.MarkPages.Shader, group.ClearPages.Shader);
        Assert.NotSame(group.MarkPages.Shader, group.ReducePageHiZ.Shader);
        group.Dispose();
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => first.TryProgram("spirv", out _));
    }
}
