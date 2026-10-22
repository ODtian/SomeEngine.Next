using DefaultGame;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization.Containers;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class GpuClusterRenderingTests(SceneContentFixture content)
{
    [Fact]
    public async Task GpuEncodedGeometryUsesItsVertexEvaluatorBeforeTheFirstPicture()
    {
        using GpuClusterExample example = await GpuClusterExample.CreateAsync(content.DirectoryPath);
        var textureDescription = new TextureDesc(TextureDimension.Texture2D, 2, 1, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled);
        var tile = new TextureMipTile
        {
            Width = 2, Height = 1, RowPitch = 8, SlicePitch = 8,
            ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 8,
        };
        using var texture = Texture.Create(textureDescription,
            [new TexturePixels(tile, new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 })],
            TextureViewDimension.Texture2D);
        using var material = new UnlitMaterial
        {
            AlbedoMap = texture,
            MaterialSampler = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
                AddressType.ClampToEdge, AddressType.ClampToEdge, AddressType.ClampToEdge),
        };
        example.SetMaterial(material);
        SceneImage? image = null;
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "1"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                {
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                    frame.Scene.CaptureImage(value => image = value);
                }
            },
        };
        scene.InstallGeometry(example);

        int result = await Application.RunAsync([], application: scene);

        Assert.Equal(0, result);
        Assert.True(example.BuildCompleted);
        SceneImages.AssertColorRegion(image!, 26, 28, 3, 8, 0, 186, 0);
        SceneImages.AssertColorRegion(image!, 35, 28, 3, 8, 186, 0, 0);
        SceneImages.AssertColorRegion(image!, 0, 0, 8, 8, 0, 0, 0);
    }
}
