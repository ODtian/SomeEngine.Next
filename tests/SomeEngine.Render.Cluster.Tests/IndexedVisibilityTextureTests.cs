using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class IndexedVisibilityTextureTests
{
    [Fact]
    public async Task ReplacingAnIndexedAlphaTextureChangesOnlyItsMaterialVisibility()
    {
        string root = FrameStageScene.FindRoot();
        using var source = new ShaderSourceBlock { SourceNames = ["indexed_visibility.slang"] };
        var description = TestShaderLoading.CreateSessionDescription(SlangCompileTarget.Dxil);
        description.SearchPaths = [Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        Shader shader = await source.CreateAsync(TestShaderLoading.Slang, description, "Indexed visibility texture");
        using Texture opaque = AlphaTexture(255);
        using Texture transparent = AlphaTexture(0);
        using var first = new UnlitMaterial { Shader = shader, AlbedoMap = opaque, BaseColorTint = new(.8f, .1f, .2f, 1) };
        using var second = new UnlitMaterial { Shader = shader, AlbedoMap = opaque, BaseColorTint = new(.1f, .2f, .8f, 1) };
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true,
                EnableFrameMetricsReadback = true }, plane);
        scene.SetMeshTransform(0, new(new Vector3(-.8f, 0, -3), Quaternion.Identity) { Stretch = new(.6f, .6f, 1) });
        scene.SetMeshTransform(1, new(new Vector3(.8f, 0, -3), Quaternion.Identity) { Stretch = new(.6f, .6f, 1) });
        scene.UseMaterial(0, first);
        scene.UseMaterial(1, second);
        RasterImage before = await scene.ReadRasterImageAsync();

        first.AlbedoMap = transparent;
        RasterImage changed = await scene.ReadNextRasterImageAsync();
        Assert.False(before.Color.AsSpan().SequenceEqual(changed.Color));
        first.AlbedoMap = opaque;
        scene.SetMeshTransform(0, new(new Vector3(-100, 0, -3), Quaternion.Identity));
        RasterImage expected = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(expected, changed, "Indexed alpha texture changes its material coverage");
    }

    private static Texture AlphaTexture(byte alpha) => Texture.Create(
        new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination),
        [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4 },
            new byte[] { 255, 255, 255, alpha })], TextureViewDimension.Texture2D);
}
