using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class VirtualShadowCapacityRenderingTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task GrowingThePagePoolRestoresTheSameShadowsAsStartingWithTheLargerPool(string backend)
    {
        VirtualShadowMapSettings small = new()
        {
            VirtualResolution = 4096, AtlasSize = 2048, MaxPages = 16, MaxShadowLights = 2,
            DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
            DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
            DirectionalDynamicReceiverRefresh = false, MaxFilterRadiusTexels = 0,
            DirectionalRayCount = 0, LocalRayCount = 0,
            // At 128 pixels, this local resolution requests enough pages to exercise the small pool.
            LocalResolutionLodBias = -3, LocalMovingResolutionLodBias = -3,
        };
        VirtualShadowMapSettings larger = small with { MaxPages = 512 };
        VirtualShadowFeature? shadows = null;
        global::SomeEngine.RenderGraph.RenderGraph? graph = null;
        SceneImage? constrained = null, grown = null, reference = null;
        SceneFrame? grownFrame = null;
        VirtualShadowFrameQuality? constrainedQuality = null, grownQuality = null;
        var application = new ApplicationScene(await content.Shadows)
        {
            Initialize = MakeCastersStatic,
            CreateFrameFeatures = rendering =>
            {
                shadows = rendering.Shadows;
                graph = rendering.Graph;
                return [];
            },
            UpdateFrame = frame =>
            {
                if (frame.Index != 12) return;
                ulong version = graph!.StructureVersion;
                shadows!.SetSettings(larger);
                Assert.True(graph.StructureVersion > version);
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(application, 24,
        [
            new(11, edit =>
            {
                edit.CaptureImage(image => constrained = image);
                edit.CaptureLinearImage(image => constrainedQuality = image.Frame.Quality!.Shadows);
            }),
            new(23, edit =>
            {
                edit.CaptureImage(image => grown = image);
                edit.CaptureLinearImage(image =>
                {
                    grownFrame = image.Frame;
                    grownQuality = image.Frame.Quality!.Shadows;
                });
            }),
        ]) { ShadowSettings = small }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");

        Assert.True(constrainedQuality!.AllocationFailures > 0u,
            $"Small pool: requested={constrainedQuality.RequestedPages}, dirty={constrainedQuality.DirtyPages}, " +
            $"failures={constrainedQuality.AllocationFailures}, fineFailures={constrainedQuality.FineAllocationFailures}, " +
            $"mapped={constrainedQuality.Views.Sum(view => view.Mips.Sum(mip => (long)mip.MappedPages))}, " +
            $"views={constrainedQuality.Views.Length}, " +
            $"projection={string.Join(';', constrainedQuality.Views.Select(view => $"{view.Projection.MinMipLevel}/{view.Projection.Flags}/{view.BaseResolutionLodBias}"))}; " +
            $"grown pool: requested={grownQuality!.RequestedPages}, " +
            $"dirty={grownQuality.DirtyPages}, failures={grownQuality.AllocationFailures}, " +
            $"mapped={grownQuality.Views.Sum(view => view.Mips.Sum(mip => (long)mip.MappedPages))}.");
        Assert.Equal(0u, grownQuality!.AllocationFailures);
        Assert.Equal(larger.MaxPages, grownFrame!.ShadowSettings.MaxPages);
        Assert.All(grownQuality.Views, view => Assert.Equal(larger.MaxPages, view.Projection.MaxPages));
        Assert.True(grownQuality.Views.Sum(view => view.Mips.Sum(mip => (long)mip.MappedPages)) > small.MaxPages);
        SceneImages.AssertChanged(constrained!, grown!);

        var referenceApplication = new ApplicationScene(await content.Shadows) { Initialize = MakeCastersStatic };
        await SceneImages.RunAsync(backend, new SceneScript(referenceApplication, 24,
        [
            new(23, edit => edit.CaptureImage(image => reference = image)),
        ]) { ShadowSettings = larger }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");
        SceneImages.AssertEqual(reference!, grown!, 2);
    }

    private static void MakeCastersStatic(ApplicationFrame frame)
    {
        foreach (Entity entity in frame.Scene.MeshEntities)
        {
            MeshInstance instance = frame.World.Read<MeshInstance>(entity);
            instance.DepthCacheClass = DepthCacheClass.Static;
            frame.World.Replace(entity, instance);
        }
    }
}
