using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.Core.Math;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Rendering content")]
public sealed class VirtualShadowCacheRenderingTests(RenderingContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task MovingAStaticCasterRestoresItsPreviousReceiverCoverage(string backend)
    {
        SceneImage? before = null, moved = null, reference = null;
        TransformQvvs destination = new(new Vector3(100, 0, 6), Quaternion.Identity, .6f);
        var application = new ApplicationScene(await content.Shadows) { Initialize = MakeCastersStatic };
        await SceneImages.RunAsync(backend, new SceneScript(application, 24,
        [
            new(19, edit => edit.CaptureImage(image => before = image)),
            new(20, edit =>
            {
                edit.SetEntityTransform(edit.MeshEntities[1], destination);
                edit.CaptureImage(image => moved = image);
            }),
        ]) { ShadowSettings = Settings() }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");
        var referenceApplication = new ApplicationScene(await content.Shadows)
        {
            Initialize = frame =>
            {
                frame.Scene.SetEntityTransform(frame.Scene.MeshEntities[1], destination);
                MakeCastersStatic(frame);
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(referenceApplication, 21,
        [
            new(20, edit => edit.CaptureImage(image => reference = image)),
        ]) { ShadowSettings = Settings() }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");
        SceneImages.AssertChanged(before!, moved!);
        SceneImages.AssertEqual(reference!, moved!, 2);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task DeletedCasterRowReuseRestoresTheCachedReceiver(string backend)
    {
        SceneImage? before = null, removed = null, reference = null;
        Entity caster = default;
        var application = new ApplicationScene(await content.Shadows)
        {
            Initialize = frame =>
            {
                caster = frame.Scene.MeshEntities[1];
                frame.Scene.AddEntity(caster, new TransformQvvs(new(100, 0, 6), Quaternion.Identity, .6f));
                MakeCastersStatic(frame);
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(application, 24,
        [
            new(19, edit => edit.CaptureImage(image => before = image)),
            new(20, edit => { edit.DeleteEntity(caster); edit.CaptureImage(image => removed = image); }),
        ]) { ShadowSettings = Settings() }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");
        var referenceApplication = new ApplicationScene(await content.Shadows)
        {
            Initialize = frame =>
            {
                Entity original = frame.Scene.MeshEntities[1];
                frame.Scene.AddEntity(original, new TransformQvvs(new(100, 0, 6), Quaternion.Identity, .6f));
                frame.Scene.DeleteEntity(original);
                MakeCastersStatic(frame);
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(referenceApplication, 21,
        [
            new(20, edit => edit.CaptureImage(image => reference = image)),
        ]) { ShadowSettings = Settings() }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");
        SceneImages.AssertChanged(before!, removed!);
        SceneImages.AssertEqual(reference!, removed!, 2);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task AddedCasterInvalidatesOnlyItsProjectedPageRegion(string backend)
    {
        SceneLinearImage? before = null, added = null;
        Vector3 position = new(1, 0, 6);
        const float radius = .05f;
        // Request receiver detail beyond the added caster's dilated page region.
        VirtualShadowMapSettings settings = Settings() with { DirectionalMinCameraViewportWidth = 1024 };
        var application = new ApplicationScene(await content.Shadows)
        {
            Initialize = frame =>
            {
                MakeCastersStatic(frame);
                frame.Scene.SetDirectionalLight(frame.Scene.PointLights.Single(), new(Vector3.Normalize(new(0, -.2f, 1)), Vector3.One, 3));
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(application, 24,
        [
            new(19, edit => edit.CaptureLinearImage(image => before = image)),
            new(20, edit =>
            {
                edit.AddEntity(edit.MeshEntities[1], new TransformQvvs(position, Quaternion.Identity, radius));
                edit.CaptureLinearImage(image => added = image);
            }),
        ]) { ShadowSettings = settings }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");

        var beforeViews = before!.Frame.Quality!.Shadows!.Views;
        var addedViews = added!.Frame.Quality!.Shadows!.Views;
        Assert.All(beforeViews, view => Assert.Equal(0u, view.DirtyPages));
        int dilation = (int)MathF.Ceiling(MathF.Max(settings.BlockerSearchRadiusTexels, settings.MaxFilterRadiusTexels) / settings.PageSize);
        ulong upperBound = 0;
        ulong allocatedPages = 0;
        ulong dirtyPages = 0;
        foreach (var view in addedViews.Where(view => view.LightKind == VirtualShadowLightKind.Directional))
        {
            var previous = beforeViews.Single(value => value.LightKind == view.LightKind && value.ClipmapLevel == view.ClipmapLevel);
            foreach (var mip in previous.Mips)
            {
                allocatedPages += mip.MappedPages;
                upperBound += Math.Min(mip.MappedPages,
                    ProjectedPageCount(view.Projection, mip.Mip, position - new Vector3(radius), position + new Vector3(radius), dilation));
            }
            dirtyPages += view.DirtyPages;
        }
        Assert.True(upperBound < allocatedPages);
        Assert.InRange(dirtyPages, 1ul, upperBound);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task DeletedCasterRestoresTheCachedReceiver(string backend)
    {
        SceneImage? before = null, removed = null, reference = null;
        var application = new ApplicationScene(await content.Shadows) { Initialize = MakeCastersStatic };
        await SceneImages.RunAsync(backend, new SceneScript(application, 24,
        [
            new(19, edit => edit.CaptureImage(image => before = image)),
            new(20, edit =>
            {
                edit.DeleteEntity(edit.MeshEntities[1]);
                edit.CaptureImage(image => removed = image);
            }),
        ]) { ShadowSettings = Settings() }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");
        var referenceApplication = new ApplicationScene(await content.Shadows)
        {
            Initialize = frame =>
            {
                frame.Scene.DeleteEntity(frame.Scene.MeshEntities[1]);
                MakeCastersStatic(frame);
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(referenceApplication, 21,
        [
            new(20, edit => edit.CaptureImage(image => reference = image)),
        ]) { ShadowSettings = Settings() }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");
        SceneImages.AssertChanged(before!, removed!);
        SceneImages.AssertEqual(reference!, removed!, 2);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task AddingADistantPointLightPreservesDirectionalCachedPages(string backend)
    {
        SceneLinearImage? before = null, added = null;
        var application = new ApplicationScene(await content.Shadows)
        {
            Initialize = frame =>
            {
                MakeCastersStatic(frame);
                frame.Scene.SetDirectionalLight(frame.Scene.PointLights.Single(), new(Vector3.Normalize(new(0, -.2f, 1)), Vector3.One, 3));
            },
            UpdateFrame = frame =>
            {
                if (frame.Index == 20) frame.World.CreateEntity(new PointLight(new(1000, 1000, 1000), 1, Vector3.One, 1));
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(application, 24,
        [
            new(19, edit => edit.CaptureLinearImage(image => before = image)),
            new(20, edit =>
            {
                edit.CaptureLinearImage(image => added = image);
            }),
        ]) { ShadowSettings = Settings() }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");

        var beforeViews = before!.Frame.Quality!.Shadows!.Views
            .Where(view => view.LightKind == VirtualShadowLightKind.Directional).ToArray();
        var addedViews = added!.Frame.Quality!.Shadows!.Views
            .Where(view => view.LightKind == VirtualShadowLightKind.Directional).ToArray();
        Assert.NotEmpty(beforeViews);
        Assert.Equal(beforeViews.Length, addedViews.Length);
        Assert.Contains(beforeViews, view => view.Mips.Any(mip => mip.MappedPages > 0));
        Assert.All(beforeViews, view => Assert.Equal(0u, view.DirtyPages));
        Assert.All(addedViews, view => Assert.Equal(0u, view.DirtyPages));
        SceneImages.AssertLinearEqual(before, added, .001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task StaticPrefilteredDirectionalPagesKeepTheirHistory(string backend)
    {
        SceneLinearImage? filtered = null, reference = null;
        var filteredApplication = new ApplicationScene(await content.Shadows)
        {
            Initialize = frame =>
            {
                MakeCastersStatic(frame);
                frame.Scene.SetDirectionalLight(frame.Scene.PointLights.Single(), new(Vector3.Normalize(new(0, -.2f, 1)), Vector3.One, 3));
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(filteredApplication, 24,
        [
            new(23, edit => edit.CaptureLinearImage(image => filtered = image)),
        ]) { ShadowSettings = Settings() with { PrefilteredDistantProjectEnable = true, PrefilteredDistant = true } },
            "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");
        var referenceApplication = new ApplicationScene(await content.Shadows)
        {
            Initialize = frame =>
            {
                MakeCastersStatic(frame);
                frame.Scene.SetDirectionalLight(frame.Scene.PointLights.Single(), new(Vector3.Normalize(new(0, -.2f, 1)), Vector3.One, 3));
            },
        };
        await SceneImages.RunAsync(backend, new SceneScript(referenceApplication, 24,
        [
            new(23, edit => edit.CaptureLinearImage(image => reference = image)),
        ]) { ShadowSettings = Settings() }, "--no-mesh-shaders", "--hardware-raster", "--no-taa", "--vsm");

        var views = filtered!.Frame.Quality!.Shadows!.Views
            .Where(view => view.LightKind == VirtualShadowLightKind.Directional).ToArray();
        Assert.NotEmpty(views);
        Assert.Contains(views, view => view.Mips.Any(mip => mip.MappedPages > 0));
        Assert.All(views, view => Assert.Equal(0u, view.DirtyPages));
        Assert.All(views, view => Assert.True(view.HistoryValid));
        SceneImages.AssertLinearEqualRegion(reference!, filtered, 32, 32, 64, 64, .003f);
    }

    private static VirtualShadowMapSettings Settings() => new()
    {
        VirtualResolution = 4096, AtlasSize = 2048, MaxShadowLights = 2,
        DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
        DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
        DirectionalDynamicReceiverRefresh = false,
    };

    private static void MakeCastersStatic(ApplicationFrame frame)
    {
        foreach (Entity entity in frame.Scene.MeshEntities)
        {
            MeshInstance instance = frame.World.Read<MeshInstance>(entity);
            instance.DepthCacheClass = DepthCacheClass.Static;
            frame.World.Replace(entity, instance);
        }
    }

    private static uint ProjectedPageCount(VirtualShadowView view, uint mip, Vector3 minimum, Vector3 maximum, int dilation)
    {
        Vector2 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = new((corner & 1) == 0 ? minimum.X : maximum.X,
                (corner & 2) == 0 ? minimum.Y : maximum.Y, (corner & 4) == 0 ? minimum.Z : maximum.Z);
            Vector4 clip = view.ProjectPosition(point);
            Vector2 ndc = new(clip.X / clip.W, clip.Y / clip.W);
            low = Vector2.Min(low, ndc);
            high = Vector2.Max(high, ndc);
        }
        if (low.X > 1 || high.X < -1 || low.Y > 1 || high.Y < -1) return 0;
        Vector2 uvLow = new((MathF.Max(low.X, -1) + 1) * .5f, (1 - MathF.Min(high.Y, 1)) * .5f);
        Vector2 uvHigh = new((MathF.Min(high.X, 1) + 1) * .5f, (1 - MathF.Max(low.Y, -1)) * .5f);
        int dimension = (int)Math.Max((view.VirtualResolution / view.PageSize) >> (int)mip, 1u);
        int firstX = Math.Clamp((int)(uvLow.X * dimension), 0, dimension - 1) - dilation;
        int firstY = Math.Clamp((int)(uvLow.Y * dimension), 0, dimension - 1) - dilation;
        int lastX = Math.Clamp((int)(uvHigh.X * dimension), 0, dimension - 1) + dilation;
        int lastY = Math.Clamp((int)(uvHigh.Y * dimension), 0, dimension - 1) + dilation;
        return (uint)((Math.Min(lastX, dimension - 1) - Math.Max(firstX, 0) + 1)
            * (Math.Min(lastY, dimension - 1) - Math.Max(firstY, 0) + 1));
    }
}
