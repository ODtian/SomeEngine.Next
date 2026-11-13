using System.Numerics;
using SomeEngine.ECS.Queries;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using SlangShaderSharp;

namespace SomeEngine.Render.Tests;

public sealed class VirtualShadowViewTests
{
    [Fact]
    public void ShadowViewsExposeTheirTargetAndPreviousProjectionOnTheSameRenderingEntity()
    {
        using var world = new RenderWorld();
        using var shadows = new VirtualShadowViews(world, new VirtualShadowMapSettings());
        var view = new RenderView(Matrix4x4.Identity, Matrix4x4.Identity, 128, 128);
        Matrix4x4 previous = Matrix4x4.CreateTranslation(1, 2, 3);
        var query = world.Query(new QueryDefinitionBuilder().Read<RenderView>()
            .Read<RenderViewTarget>().Read<RenderPreviousView>());
        shadows.Update([view], [previous]);

        var entities = new List<SomeEngine.ECS.Entities.Entity>();
        world.ExecuteQuery(query, values =>
        {
            foreach (QueryRow row in values.Rows)
            {
                entities.Add(row.Entity);
                Assert.Equal(view, row.Read<RenderView>());
                Assert.Equal(new RenderViewTarget(ViewTargetKind.ShadowPages), row.Read<RenderViewTarget>());
                Assert.Equal(previous, row.Read<RenderPreviousView>().ViewProjection);
            }
        });
        var entity = Assert.Single(entities);
        shadows.Update([view with { CameraCut = true }], [Matrix4x4.Identity]);
        Assert.True(world.Read<RenderView>(entity).CameraCut);
        Assert.Equal(Matrix4x4.Identity, world.Read<RenderPreviousView>(entity).ViewProjection);
        world.ReleaseQuery(query);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectionalViewsExposeTheConfiguredDynamicReceiverRefresh(bool receiverRefresh)
    {
        using var world = new RenderWorld();
        using var shadows = new VirtualShadowViews(world,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 1,
                DirectionalClipmapLevels = 2, DirectionalReceiverMask = true,
                DirectionalDynamicReceiverRefresh = receiverRefresh,
            });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        var camera = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);

        shadows.PrepareViews(lights, lights.OrderForUpload(Vector3.Zero), camera);

        Assert.Equal(2, shadows.Views.Length);
        foreach (VirtualShadowView view in shadows.Views)
            Assert.Equal(!receiverRefresh, (view.Flags & VirtualShadowViewFlags.DirectionalDynamicDepthCache) != 0u);
    }

    [Fact]
    public void ProjectionHistoryKeepsCachedViewsAndMovingALightInvalidatesOnlyItsViews()
    {
        using var world = new RenderWorld();
        using var shadows = new VirtualShadowViews(world,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2, DirectionalClipmapLevels = 2,
            });
        var view = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 0.1f, 100), 128, 128, false);
        var lights = new RenderLightSet();
        uint[]? firstGenerations = null;
        for (int frameIndex = 0; frameIndex < 6; frameIndex++)
        {
            lights.Clear();
            lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
            lights.AddPoint(new RenderPointLight(frameIndex == 5 ? Vector3.UnitX : Vector3.Zero,
                10, Vector3.One, 1, uint.MaxValue));
            VirtualShadowView[] views = shadows.PrepareViews(lights, lights.OrderForUpload(Vector3.Zero), view).ToArray();
            if (firstGenerations is null) firstGenerations = views.Select(value => value.CacheGeneration).ToArray();
            else
                for (int index = 0; index < views.Length; index++)
                    if (frameIndex == 5 && index >= 2)
                        Assert.NotEqual(firstGenerations[index], views[index].CacheGeneration);
                    else
                        Assert.Equal(firstGenerations[index], views[index].CacheGeneration);
            shadows.Submit();
        }
    }

    [Fact]
    public void LocalProjectionHistorySurvivesGpuLightPriorityChanges()
    {
        using var world = new RenderWorld();
        using var shadows = new VirtualShadowViews(world,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2,
                DistantLightMode = VirtualShadowDistantLightMode.Disabled,
            });
        var lights = new RenderLightSet();
        var leftLight = new RenderPointLight(new(-4, 0, -8), 20, Vector3.One, 1, uint.MaxValue,
            new LightShadowSettings(SourceRadius: .25f));
        var rightLight = new RenderSpotLight(new(4, 0, -8), 20, -Vector3.UnitZ, .9f, .5f, Vector3.One, 1, uint.MaxValue,
            new LightShadowSettings(SourceRadius: .5f));
        lights.AddPoint(world.CreateEntity(leftLight), leftLight);
        lights.AddSpot(world.CreateEntity(rightLight), rightLight);
        Vector3 leftCamera = new(-3, 0, 0);
        var receiver = new RenderView(Matrix4x4.CreateLookAt(leftCamera, leftCamera - Vector3.UnitZ, Vector3.UnitY),
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);

        VirtualShadowView[] firstViews = shadows.PrepareViews(lights, lights.OrderForUpload(leftCamera),
            receiver).ToArray();
        uint[] firstIndices = new uint[2];
        VirtualShadowLightViews.WriteIndices(firstViews, firstIndices);
        shadows.Submit();
        Vector3 rightCamera = new(3, 0, 0);
        receiver = receiver with
        {
            View = Matrix4x4.CreateLookAt(rightCamera, rightCamera - Vector3.UnitZ, Vector3.UnitY),
        };
        ReadOnlySpan<VirtualShadowView> secondViews = shadows.PrepareViews(lights, lights.OrderForUpload(rightCamera),
            receiver);
        uint[] secondIndices = new uint[2];
        VirtualShadowLightViews.WriteIndices(secondViews, secondIndices);

        Assert.Equal(7, firstViews.Length);
        Assert.Equal(firstViews.Length, secondViews.Length);
        Assert.Equal(0u, firstViews[0].LightIndex);
        Assert.Equal(1u, firstViews[6].LightIndex);
        Assert.Equal(1u, secondViews[0].LightIndex);
        Assert.Equal(0u, secondViews[6].LightIndex);
        Assert.Equal(new uint[] { 0, 6 }, firstIndices);
        Assert.Equal(new uint[] { 6, 0 }, secondIndices);
        for (int index = 0; index < firstViews.Length; index++)
        {
            Assert.Equal(firstViews[index].CacheGeneration, secondViews[index].CacheGeneration);
            Assert.Equal(firstViews[index].LightPosition, secondViews[index].LightPosition);
            Assert.Equal(firstViews[index].SourceRadius, secondViews[index].SourceRadius);
        }
    }

    [Fact]
    public void ExistingLightHistorySurvivesLightRemovalAndAddition()
    {
        using var world = new RenderWorld();
        using var shadows = new VirtualShadowViews(world, new VirtualShadowMapSettings
        {
            VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 3, DirectionalClipmapLevels = 2,
        });
        var removedLight = new RenderDirectionalLight(Vector3.UnitY, Vector3.One, 1, uint.MaxValue);
        var retainedLight = new RenderDirectionalLight(Vector3.Normalize(new Vector3(1, -1, .5f)), Vector3.One, 1, uint.MaxValue);
        var removedEntity = world.CreateEntity(removedLight);
        var retainedEntity = world.CreateEntity(retainedLight);
        var lights = new RenderLightSet();
        lights.AddDirectional(removedEntity, removedLight);
        lights.AddDirectional(retainedEntity, retainedLight);
        var camera = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);

        VirtualShadowView[] first = shadows.PrepareViews(lights, lights.OrderForUpload(Vector3.Zero), camera).ToArray();
        shadows.Submit();
        world.DestroyEntity(removedEntity);
        lights.Clear();
        lights.AddDirectional(retainedEntity, retainedLight);
        VirtualShadowView[] remaining = shadows.PrepareViews(lights, lights.OrderForUpload(Vector3.Zero), camera).ToArray();

        Assert.Equal(4, first.Length);
        Assert.Equal(2, remaining.Length);
        Assert.Equal(4, shadows.PreviousViewCount);
        Assert.Equal(1u, first[2].LightIndex);
        Assert.Equal(0u, remaining[0].LightIndex);
        for (int level = 0; level < remaining.Length; level++)
        {
            Assert.Equal(first[level + 2].CacheGeneration, remaining[level].CacheGeneration);
            Assert.True((remaining[level].Flags & VirtualShadowViewFlags.PreviousProjectionValid) != 0u);
        }
        var history = new VirtualShadowView[4];
        var projections = new Matrix4x4[2];
        Assert.True(shadows.TryCopyHistoryViews(history, projections));
        Assert.Equal(first[2].LightViewProjection, projections[0]);
        shadows.Submit();
        var addedLight = new RenderPointLight(new(100, 0, -100), 5, Vector3.One, 1, uint.MaxValue);
        lights.AddPoint(world.CreateEntity(addedLight), addedLight);
        ReadOnlySpan<VirtualShadowView> added = shadows.PrepareViews(lights, lights.OrderForUpload(Vector3.Zero), camera);

        Assert.Equal(8, added.Length);
        Assert.Equal(2, shadows.PreviousViewCount);
        for (int level = 0; level < remaining.Length; level++)
        {
            Assert.Equal(remaining[level].CacheGeneration, added[level].CacheGeneration);
            Assert.True((added[level].Flags & VirtualShadowViewFlags.PreviousProjectionValid) != 0u);
        }
    }

    [Fact]
    public void OverlappingDirectionalFamiliesKeepSeparateProjectionHistory()
    {
        using var world = new RenderWorld();
        using var shadows = new VirtualShadowViews(world, new VirtualShadowMapSettings
        {
            VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 1,
            DirectionalFirstClipmapLevel = 8, DirectionalClipmapLevels = 2,
            FirstPersonFirstClipmapLevel = 8, FirstPersonLastClipmapLevel = 9,
        });
        var light = new RenderDirectionalLight(Vector3.UnitY, Vector3.One, 1, uint.MaxValue);
        var lights = new RenderLightSet();
        lights.AddDirectional(world.CreateEntity(light), light);
        var camera = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);

        VirtualShadowView[] first = shadows.PrepareViews(lights, lights.OrderForUpload(Vector3.Zero), camera,
            hasFirstPersonCasters: true).ToArray();
        shadows.Submit();
        ReadOnlySpan<VirtualShadowView> next = shadows.PrepareViews(lights, lights.OrderForUpload(Vector3.Zero), camera,
            hasFirstPersonCasters: true);

        Assert.Equal(4, next.Length);
        Assert.Equal(first[0].ClipmapLevel, first[2].ClipmapLevel);
        Assert.True((first[2].Flags & VirtualShadowViewFlags.FirstPersonShadow) != 0u);
        for (int index = 0; index < next.Length; index++)
        {
            Assert.Equal(first[index].CacheGeneration, next[index].CacheGeneration);
            Assert.True((next[index].Flags & VirtualShadowViewFlags.PreviousProjectionValid) != 0u);
        }
    }

    [Fact]
    public void ScrollingTheCameraPreservesThePageAddressOfFixedWorldGeometry()
    {
        using var world = new RenderWorld();
        using var shadows = new VirtualShadowViews(world,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 1,
                DirectionalClipmapLevels = 2, DirectionalFirstClipmapLevel = 7,
            });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        Vector3[] cameras = [Vector3.Zero, new(0, 0, 2), new(0, 0, -2),
            new(2, 0, 0), new(-2, 0, 0), new(2, 0, 2), new(-2, 0, -2)];
        (int X, int Y)? firstPage = null;
        uint firstGeneration = 0;
        foreach (Vector3 camera in cameras)
        {
            var receiver = new RenderView(Matrix4x4.CreateLookAt(camera, camera - Vector3.UnitZ, Vector3.UnitY),
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
            VirtualShadowView view = shadows.PrepareViews(lights, lights.OrderForUpload(camera), receiver)[0];
            Vector4 clip = view.ProjectPosition(new(.5f, 0, .5f));
            Vector2 uv = new Vector2(clip.X, -clip.Y) / (2 * clip.W) + new Vector2(.5f);
            Assert.InRange(uv.X, 0, .99999f);
            Assert.InRange(uv.Y, 0, .99999f);
            var page = (view.VirtualPageOriginX + (int)MathF.Floor(uv.X * view.VirtualResolution / view.PageSize),
                view.VirtualPageOriginY + (int)MathF.Floor(uv.Y * view.VirtualResolution / view.PageSize));
            if (firstPage is null)
            {
                firstPage = page;
                firstGeneration = view.CacheGeneration;
            }
            Assert.Equal(firstPage.Value, page);
            Assert.Equal(firstGeneration, view.CacheGeneration);
            shadows.Submit();
        }
    }

}
