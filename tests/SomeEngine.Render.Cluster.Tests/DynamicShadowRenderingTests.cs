using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Core.Math;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DynamicShadowRenderingTests
{
    [Fact]
    public Task AnAutomaticShadowCasterMovesAgainAfterReturningToStaticCache()
        => CheckAutomaticShadowCacheAsync(null, usePackedAssets: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MaterialOrderPreservesAutomaticShadowCache(bool unlitFirst)
        => CheckAutomaticShadowCacheAsync(unlitFirst);

    [Theory]
    [InlineData(0, true)]
    [InlineData(30, false)]
    public Task SimultaneousMaterialSourcesPreserveAutomaticShadowCache(int extraOffscreenRows, bool usePackedAssets)
        => CheckAutomaticShadowCacheAsync(null, extraOffscreenRows, usePackedAssets);

    private static async Task CheckAutomaticShadowCacheAsync(
        bool? unlitFirst, int extraOffscreenRows = 30, bool usePackedAssets = true)
    {
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        var options = new ClusterPipelineOptions
        { ForceHardwareRaster = true, MaxCandidates = 65536, EnableFrameMetricsReadback = true };
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 1,
            WorldUnitsPerCentimeter = 1,
            DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalResolutionLodBias = 0, DirectionalMovingResolutionLodBias = 0,
            DirectionalReceiverMask = true, MaxFilterRadiusTexels = 0,
            PrefilteredDistantProjectEnable = true, PrefilteredDistant = true,
            DirectionalCoarsePageMode = VirtualShadowCoarsePageMode.Disabled,
            WorkLoadBudget = float.MaxValue,
            DynamicResolutionEnabled = false,
        };
        string sharedPack = Path.Combine(FrameStageScene.FindRoot(), "samples", "DefaultGame", "Library", "shared.assetpack");
        await using var storage = usePackedAssets
            ? new AssetPackStorage([await AssetPack.OpenAsync(sharedPack)]) : null;
        await using var scene = await FrameStageScene.CreateAsync("D3D12", true, options, plane, settings,
            instanceOptions: new SomeEngine.Render.Instances.RenderInstanceOptions { RowCapacity = 64, BatchCapacity = 8 },
            assetStorage: storage);
        Vector3 translation = new(0, 1000, 1000);
        scene.SetView(new(Matrix4x4.CreateTranslation(-translation),
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 64, 64));
        scene.SetMeshTransform(0, new(new Vector3(0, 0, -8) + translation, Quaternion.Identity) { Stretch = new(3, 3, .1f) });
        TransformQvvs outside = new(new Vector3(20, 4, -4) + translation, Quaternion.Identity) { Stretch = new(1.6f, 1.6f, .1f) };
        scene.SetMeshTransform(1, outside);
        void AddUnlitMeshes()
        {
            scene.AddGeometryMesh(plane, new(new Vector3(0, 2, -6) + translation, Quaternion.Identity) { Stretch = new(.6f, .6f, .1f) });
            // Exercise both a complete static-bit word and a partially occupied final word.
            for (int instance = 0; instance < extraOffscreenRows; instance++) scene.AddGeometryMesh(plane, outside);
        }
        // The original case creates both materials before rendering the first frame.
        // Ordered cases keep the first material in bin zero while the other is added.
        if (unlitFirst is null) AddUnlitMeshes();
        else if (unlitFirst.Value)
        {
            scene.UseMaterial("Unlit");
            AddUnlitMeshes();
        }
        // A zero-angle emitter keeps the first-frame cache comparison deterministic.
        scene.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)), new(SourceAngleDegrees: 0f));

        RasterImage exposed = await scene.ReadNextRasterImageAsync();
        if (unlitFirst == true) scene.UseMaterial("Standard");
        else if (unlitFirst == false) AddUnlitMeshes();
        for (int frame = 1; frame < 4; frame++) exposed = await scene.ReadNextRasterImageAsync();
        AssertUnlitPlaneIsWhite(exposed);

        scene.SetMeshTransform(1, new(new Vector3(0, 4, -4) + translation, Quaternion.Identity) { Stretch = new(1.6f, 1.6f, .1f) });
        RasterImage covered = await scene.ReadNextRasterImageAsync();
        for (int frame = 1; frame < 4; frame++) covered = await scene.ReadNextRasterImageAsync();
        Assert.NotEqual(exposed.Color, covered.Color);

        scene.SetMeshTransform(1, outside);
        RasterImage restored = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(exposed, restored);
        for (int frame = 0; frame < 102; frame++) restored = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(exposed, restored);
        await scene.DiscardNextFrameAsync();
        RasterImage.AssertEqual(exposed, await scene.ReadNextRasterImageAsync());
        foreach (DepthCacheClass cacheClass in new[] { DepthCacheClass.Dynamic, DepthCacheClass.Static,
            DepthCacheClass.Auto, (DepthCacheClass)uint.MaxValue, DepthCacheClass.Auto })
        {
            scene.SetMeshDepthCacheClass(1, cacheClass);
            RasterImage.AssertEqual(exposed, await scene.ReadNextRasterImageAsync());
        }
        scene.SetMeshTransform(1, new(new Vector3(0, 4, -4) + translation, Quaternion.Identity) { Stretch = new(1.6f, 1.6f, .1f) });
        RasterImage coveredAgain = await scene.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(covered, coveredAgain);
        scene.SetMeshTransform(1, outside);
        RasterImage.AssertEqual(exposed, await scene.ReadNextRasterImageAsync());
    }

    private static void AssertUnlitPlaneIsWhite(RasterImage image)
    {
        // Strictly inside the unchanged Unlit plane in this fixed 64x64 view.
        for (int y = 9; y <= 15; y++)
            for (int x = 28; x <= 35; x++)
            {
                ReadOnlySpan<Half> rgba = MemoryMarshal.Cast<byte, Half>(image.Color.AsSpan((y * 64 + x) * 8, 8));
                for (int channel = 0; channel < 4; channel++)
                    Assert.True(rgba[channel] == (Half)1,
                        $"Unlit plane ({x}, {y}) channel {channel} was {(float)rgba[channel]}, expected 1.");
            }
    }
}

internal sealed partial class FrameStageScene
{
    internal void SetMeshDepthCacheClass(int index, DepthCacheClass cacheClass)
    {
        MeshInstance mesh = _gameWorld.Read<MeshInstance>(_meshes[index]);
        mesh.DepthCacheClass = cacheClass;
        _gameWorld.Replace(_meshes[index], mesh);
    }
}
