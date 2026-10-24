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
    public async Task GpuMeshReplacementUpdatesItsPictureAndGeometryRevision()
    {
        using GpuClusterExample example = await GpuClusterExample.CreateAsync(content.DirectoryPath);
        var material = new UnlitMaterial { BaseColorTint = new(1, 0, 0, 1) };
        example.SetMaterial(material);
        // These dimensions and subdivisions are example data spanning several geometry pages.
        example.SetGrid(32, new(2, 2));
        SceneImage? before = null, building = null, after = null;
        ApplicationResult? completed = null;
        ulong beforeRevision = 0;
        var scene = new ApplicationScene(content.DirectoryPath)
        {
            SceneCompleted = value => completed = value,
            Options = RuntimeStartupOptions.Parse(["--hidden-window", "--synchronous-frames", "--static-scene",
                "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "4"]),
            UpdateFrame = frame =>
            {
                if (frame.Index == 0)
                    foreach (var entity in frame.Scene.MeshEntities) frame.Scene.DeleteEntity(entity);
                if (frame.Index == 1)
                {
                    beforeRevision = example.GeometryRevision;
                    frame.Scene.CaptureImage(value => before = value);
                }
                if (frame.Index == 2)
                {
                    example.SetGrid(32, new(4, 2));
                    frame.Scene.CaptureImage(value => building = value);
                }
                if (frame.Index == 3) frame.Scene.CaptureImage(value => after = value);
            },
        };
        scene.InstallGeometry(example);

        int result = await Application.RunAsync([], application: scene);

        Assert.True(result == 0, completed?.Error?.ToString());
        Assert.True(example.BuildCompleted);
        Assert.Equal(beforeRevision + 1, example.GeometryRevision);
        SceneImages.AssertEqual(before!, building!);
        SceneImages.AssertColorRegion(before!, 28, 28, 8, 8, 186, 0, 0);
        SceneImages.AssertColorRegion(before!, 18, 28, 3, 8, 0, 0, 0);
        SceneImages.AssertColorRegion(after!, 18, 28, 3, 8, 186, 0, 0);
        SceneImages.AssertColorRegion(after!, 43, 28, 3, 8, 186, 0, 0);
    }

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
        var texture = Texture.Create(textureDescription,
            [new TexturePixels(tile, new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 })],
            TextureViewDimension.Texture2D);
        var material = new UnlitMaterial
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
