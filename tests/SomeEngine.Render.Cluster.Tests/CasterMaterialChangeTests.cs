using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Shadows;
using SomeEngine.Resources;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class CasterMaterialChangeTests
{
    [Theory]
    [InlineData("D3D12", true)]
    [InlineData("D3D12", false)]
    [InlineData("Vulkan", true)]
    [InlineData("Vulkan", false)]
    public async Task InPlaceVertexScalarChangeMatchesIndependentTranslatedCasterInItsFirstFrame(string backend, bool hardware)
    {
        var shader = await LoadShaderAsync(backend);
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, EnableFrameMetricsReadback = true,
            ForceHardwareRaster = hardware, SoftwareRasterEdgeThreshold = 64 };
        var settings = Shadows();
        await using var changed = await FrameStageScene.CreateAsync(backend, true, options, plane, settings);
        Configure(changed);
        var material = Assert.IsType<Game.Content.UnlitMaterial>(changed.CreateRasterMaterial(shader, Vector4.One));
        material.BaseColorTint = new(1, 1, 1, 0);
        changed.UseMaterial(1, material);
        byte[] before = await changed.ReadClusterColorAsync();
        material.BaseColorTint = Vector4.One;
        RasterImage first = await changed.ReadNextRasterImageAsync();
        await using var fresh = await FrameStageScene.CreateAsync(backend, true, options, plane, settings);
        Configure(fresh);
        fresh.SetMeshTransform(1, new(new Vector3(.4f, 4, -4), Quaternion.Identity) { Stretch = new(2, 1.5f, .1f) });
        fresh.UseMaterial(1, fresh.CreateRasterMaterial(RasterMaterialKind.Ordinary, Vector4.One));
        RasterImage expected = await fresh.ReadNextRasterImageAsync();
        Assert.NotEqual(before, expected.Color);
        RasterImage.AssertEqual(expected, first);
    }

    [Theory]
    [InlineData("D3D12", true)]
    [InlineData("D3D12", false)]
    [InlineData("Vulkan", true)]
    [InlineData("Vulkan", false)]
    public async Task InPlacePatternedAlphaChangesPreserveHolesWithoutGlobalRefresh(string backend, bool hardware)
    {
        var shader = await LoadShaderAsync(backend);
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions { MaxCandidates = 65536, EnableFrameMetricsReadback = true,
            ForceHardwareRaster = hardware, SoftwareRasterEdgeThreshold = 64 };
        var settings = Shadows();
        await using var changed = await FrameStageScene.CreateAsync(backend, true, options, plane, settings);
        Configure(changed);
        using Texture texture = Pattern(0);
        var material = Assert.IsType<Game.Content.UnlitMaterial>(changed.CreateRasterMaterial(shader, Vector4.One));
        material.BaseColorTint = new(1, 1, 1, 0);
        material.AlbedoMap = texture;
        changed.UseMaterial(1, material);
        byte[] before = await changed.ReadClusterColorAsync();
        Task update = texture.UpdateAsync(Pattern(1)).AsTask();
        // This frame completes the replacement transfer. The next frame
        // observes the public texture-update completion boundary.
        await changed.ReadNextRasterImageAsync();
        await update;
        RasterImage first = await changed.ReadNextRasterImageAsync();
        await using var fresh = await FrameStageScene.CreateAsync(backend, true, options, plane, settings);
        Configure(fresh);
        using Texture expectedTexture = Pattern(1);
        var expectedMaterial = Assert.IsType<Game.Content.UnlitMaterial>(fresh.CreateRasterMaterial(shader, Vector4.One));
        expectedMaterial.BaseColorTint = new(1, 1, 1, 0);
        expectedMaterial.AlbedoMap = expectedTexture;
        fresh.UseMaterial(1, expectedMaterial);
        RasterImage expected = await fresh.ReadNextRasterImageAsync();
        Assert.NotEqual(before, expected.Color);
        RasterImage.AssertEqual(expected, first);
    }

    private static void Configure(FrameStageScene scene)
    {
        scene.SetMeshTransform(0, new(new Vector3(0, 0, -8), Quaternion.Identity) { Stretch = new(3, 3, .1f) });
        scene.SetMeshTransform(1, new(new Vector3(0, 4, -4), Quaternion.Identity) { Stretch = new(2, 1.5f, .1f) });
        scene.SetBoundsExpansion(1, .5f);
        scene.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)));
    }
    private static VirtualShadowMapSettings Shadows() => new() { VirtualResolution = 4096, AtlasSize = 2048,
        MaxShadowLights = 1, DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
        DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11, DynamicResolutionEnabled = false };
    private static async Task<Shader> LoadShaderAsync(string backend)
    {
        string root = FrameStageScene.FindRoot();
        using var source = new ShaderSourceBlock { SourceNames = ["changing_caster_material.slang"] };
        var description = TestShaderLoading.CreateSessionDescription(backend == "Vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil);
        description.SearchPaths = [Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        return await source.CreateAsync(TestShaderLoading.Slang, description, "User patterned and shifted caster");
    }
    private static Texture Pattern(int offset)
    {
        byte[] pixels = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int pixel = (y * 4 + x) * 4;
            pixels[pixel] = pixels[pixel + 1] = pixels[pixel + 2] = 255;
            pixels[pixel + 3] = (x + y + offset) % 2 == 0 ? (byte)255 : (byte)0;
        }
        return Texture.Create(new TextureDesc(TextureDimension.Texture2D, 4, 4, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination),
            [new TexturePixels(new TextureMipTile { Width = 4, Height = 4, RowPitch = 16, SlicePitch = 64 }, pixels)], TextureViewDimension.Texture2D);
    }
}
