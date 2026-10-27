using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Shadows;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using SlangShaderSharp;

namespace SomeEngine.Render.Tests;

public sealed class VirtualShadowViewTests(RenderTestDevice graphics) : IClassFixture<RenderTestDevice>
{
    [Fact]
    public async Task FarWorldOriginKeepsTheSubUnitClipmapSnappingOffset()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings { VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 1,
                DirectionalFirstClipmapLevel = 6, DirectionalClipmapLevels = 2,
                WorldUnitsPerCentimeter = 1 });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitZ, Vector3.One, 1, uint.MaxValue));
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128,
            WorldOrigin: new(1099511627776.125, 0, 0));
        shadows.BeginFrame();
        VirtualShadowView view = shadows.PrepareViews(lights, receiver, 1, 1, 1)[0];
        Vector4 clip = view.ProjectPosition(Vector3.Zero);
        // Native level6 has radius128. The integer-aligned far origin plus1/8
        // projects to -1/2048 in the horizontal light axis.
        Assert.Equal(-.00048828125f, clip.X / clip.W);
    }

    [Fact]
    public async Task DirectionalClipmapsHaveOneMipWhileLocalLightsKeepTheirMipChain()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2,
                DirectionalClipmapLevels = 2,
            });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitZ, Vector3.One, 1, uint.MaxValue));
        lights.AddSpot(new RenderSpotLight(Vector3.Zero, 10, -Vector3.UnitZ, .5f, .25f,
            Vector3.One, 1, uint.MaxValue));
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
        shadows.BeginFrame();
        VirtualShadowView[] views = shadows.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        // Native clipmaps render only mip zero. Local512/128 maps have4,2,1-page levels.
        Assert.Equal(new uint[] { 1, 1, 3 }, views.Select(view => view.MipLevelCount).ToArray());
    }

    [Fact]
    public async Task OrthographicCameraTargetCentersItsClipmapOrigin()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings { VirtualResolution = 512, AtlasSize = 256,
                MaxShadowLights = 1, DirectionalFirstClipmapLevel = 6, DirectionalClipmapLevels = 1 });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(-Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        Vector3 target = new(10, 0, 0);
        var receiver = new RenderView(Matrix4x4.Identity, Matrix4x4.CreateOrthographic(1, 1, .1f, 100),
            128, 128, CameraTarget: target);
        shadows.BeginFrame();
        VirtualShadowView view = shadows.PrepareViews(lights, receiver, 1, 1, 1)[0];
        Vector4 clip = view.ProjectPosition(target);
        // Radius 1.28 snaps the target center to 10.24. The .24 difference
        // over the 2.56 projection half-width gives an absolute NDC .09375.
        Assert.InRange(MathF.Abs(clip.Y / clip.W), .09374f, .09376f);
        Assert.Equal(target, view.ClipmapWorldOrigin);
    }

    [Fact]
    public async Task CameraDepthMovementUsesCachedRangeUntilTheNativeGuardbandIsCrossed()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings { VirtualResolution = 512, AtlasSize = 256,
                MaxShadowLights = 1, DirectionalFirstClipmapLevel = 6, DirectionalClipmapLevels = 1 });
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.AddCopyFramePass("Commit directional depth range", PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref CopyPassCommandScope _, in int _) => { });
            edit.Commit();
        }
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(-Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        float[] cameraHeights = [0, 1100, 1200];
        float[] expectedDepths = [.5f, .5f, .96875f];
        for (int index = 0; index < cameraHeights.Length; index++)
        {
            Vector3 camera = new(0, cameraHeights[index], 0);
            var receiver = new RenderView(Matrix4x4.CreateLookAt(camera, camera - Vector3.UnitZ, Vector3.UnitY),
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
            shadows.BeginFrame();
            VirtualShadowView view = shadows.PrepareViews(lights, receiver, 1, 1, 1)[0];
            Vector4 clip = view.ProjectPosition(Vector3.Zero);
            // Level 6 has radius 1.28 and Z radius 1280. Movement 1100 is inside
            // 0.9*1280-1.28; movement 1200 adopts the new center, so world zero
            // projects to (1280+1200)/2560 = .96875.
            Assert.InRange(clip.Z / clip.W, expectedDepths[index] - 5e-7f, expectedDepths[index] + 5e-7f);
            shadows.AcquireFrameResources();
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.ObserveExecution(new ViewCommit(shadows));
            int count = frame.Execute(completions);
            for (int completion = 0; completion < count; completion++)
                Assert.Equal(WaitStatus.Completed, graphics.Backend.WaitCpu(completions[completion], TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task CopiedDirectionalViewsKeepEachLightAxisAndLevelProjection()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2,
                DirectionalFirstClipmapLevel = 0, DirectionalClipmapLevels = 2,
                FirstPersonFirstClipmapLevel = 2, FirstPersonLastClipmapLevel = 3,
                WorldUnitsPerCentimeter = 1, DirectionalDepthRangeScale = 1,
            });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitZ, Vector3.One, 1, uint.MaxValue));
        lights.AddDirectional(new RenderDirectionalLight(-Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
        shadows.BeginFrame();
        VirtualShadowView[] views = shadows.PrepareViews(lights, receiver, 1, 1, 1,
            hasFirstPersonCasters: true).ToArray();
        Assert.Equal(8, views.Length);
        var renderViews = new RenderView[views.Length];
        Assert.Equal(8, shadows.CopyPreparedRenderViews(renderViews));

        int[] levels = [0, 1, 0, 1, 2, 3, 2, 3];
        // Native level radii 2, 4, 8, 16 give full widths 8, 16, 32, 64.
        float[] projectionScales = [.25f, .125f, .25f, .125f, .0625f, .03125f, .0625f, .03125f];
        for (int index = 0; index < views.Length; index++)
        {
            Vector3 direction = index % 4 < 2 ? Vector3.UnitZ : -Vector3.UnitY;
            Vector3 up = index % 4 < 2 ? Vector3.UnitY : Vector3.UnitX;
            Assert.Equal(levels[index], views[index].ClipmapLevel);
            Assert.Equal(direction, views[index].LightDirection);
            Assert.Equal(-Vector3.UnitZ, Vector3.TransformNormal(direction, renderViews[index].View));
            Assert.Equal(Vector3.UnitY, Vector3.TransformNormal(up, renderViews[index].View));
            Assert.Equal(projectionScales[index], renderViews[index].Projection.M11);
            Assert.Equal(projectionScales[index], renderViews[index].Projection.M22);
            Assert.Equal(-projectionScales[index], renderViews[index].Projection.M33);
            Assert.Equal(views[index].LightViewProjection,
                renderViews[index].View * renderViews[index].Projection);
            Assert.Equal(index < 4 ? RenderViewFamily.Shadow : RenderViewFamily.FirstPersonShadow,
                renderViews[index].Family);
        }
    }

    [Fact]
    public async Task DirectionalLevelsCoverTheirNativeDepthRange()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings { VirtualResolution = 512, AtlasSize = 256,
                MaxShadowLights = 1, DirectionalFirstClipmapLevel = 6, DirectionalClipmapLevels = 2 });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(-Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
        shadows.BeginFrame();
        ReadOnlySpan<VirtualShadowView> views = shadows.PrepareViews(lights, receiver, 1, 1, 1);
        // Native level 6 radius 128 centimeters gives a Z radius 1280 meters at
        // ZRangeScale 1000. World Y 1200 is 80 meters from the light-facing edge
        // of its 2560-meter depth range: forward depth is 1/32. Level 7 doubles
        // the same independent world example.
        Vector4 first = views[0].ProjectPosition(new(0, 1200, 0));
        Vector4 second = views[1].ProjectPosition(new(0, 2400, 0));
        Assert.InRange(first.Z / first.W, .0312495f, .0312505f);
        Assert.InRange(second.Z / second.W, .0312495f, .0312505f);
    }

    [Fact]
    public async Task OrthographicCameraUsesTheNativeClipmapWidthAndFirstLevel()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 1,
                DirectionalResolutionLodBias = 0f,
            });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitZ, Vector3.One, 1, uint.MaxValue,
            new(0f, LightMobility.Static)));
        var receiver = new RenderView(Matrix4x4.Identity, Matrix4x4.CreateOrthographic(4f, 4f, .1f, 100f), 128, 128);
        shadows.BeginFrame();
        VirtualShadowView[] views = shadows.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        // Native ortho projection scale is one. Width400cm dominates viewport128,
        // so .5*512/400=.64 gives the absolute bias lower bound zero.
        // Half-width200cm selects first level seven, through last level22.
        Assert.Equal(0f, views[0].ResolutionLodBias);
        Assert.Equal(16, views.Length);
        Vector4 clip = views[0].ProjectPosition(new(5.12f, 0, 0));
        Assert.InRange(MathF.Abs(clip.X / clip.W), .99999f, 1.00001f);
    }

    [Fact]
    public async Task MovingPointLightBiasDecaysOverTenRenderedFramesAndRestartsAfterMovement()
    {
        using var world = new RenderWorld();
        var light = new RenderPointLight(Vector3.Zero, 10, Vector3.One, 1, uint.MaxValue);
        var entity = world.CreateEntity(light);
        var lights = new RenderLightSetSystem();
        using var extraction = new RenderExtractionSystems(world);
        using var systems = new RenderPrepareSystems(world, extraction);
        systems.Add(lights);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.AddCopyFramePass("commit light movement frames", PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref CopyPassCommandScope _, in int _) => { });
            edit.Commit();
        }
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings { VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 1 });
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
        // Native moving bias is one, static bias is zero. First activity is one;
        // five rendered frames later it is .5, ten later zero, and movement resets it.
        float[] expectedBiases = [1f, .9f, .8f, .7f, .6f, .5f, .4f, .3f, .2f, .1f, 0f, 1f];
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        try
        {
            for (int index = 0; index < expectedBiases.Length; index++)
            {
                if (index == 11)
                {
                    world.AcquireSystemVersion();
                    world.Replace(entity, light with { Position = Vector3.UnitX });
                }
                Assert.True(graph.TryOpenNextFrame(out var nextFrame, TimeSpan.FromSeconds(10)));
                using var nextFrameResources = nextFrame;
                Assert.True(graphics.Coordinator.TryBeginPrepare(out var prepare));
                using (prepare)
                {
                    systems.Update(prepare);
                    prepare.Commit();
                }
                Assert.True(graphics.Coordinator.TryBeginFrame(out var read));
                using (read)
                using (var frame = graph.BeginFrame(nextFrame))
                {
                    shadows.BeginFrame();
                    VirtualShadowView view = shadows.PrepareViews(lights.RequireCurrent(), receiver, 1, 1, 1)[0];
                    Assert.InRange(view.ResolutionLodBias, expectedBiases[index] - .000001f, expectedBiases[index] + .000001f);
                    shadows.AcquireFrameResources();
                    frame.ObserveExecution(new ViewCommit(shadows));
                    Array.Clear(completions);
                    int count;
                    try { count = frame.Execute(completions); }
                    catch
                    {
                        QueueCompletion[] submitted = completions.Where(value => value != default).ToArray();
                        if (submitted.Length != 0) read.Complete(submitted);
                        throw;
                    }
                    Assert.True(count > 0);
                    read.Complete(completions.AsSpan(0, count));
                    for (int completion = 0; completion < count; completion++)
                        Assert.Equal(WaitStatus.Completed, graphics.Backend.WaitCpu(completions[completion], TimeSpan.FromSeconds(10)));
                }
            }
        }
        finally
        {
            graphics.Coordinator.WaitForTrackedSubmissions();
            Assert.True(graphics.Coordinator.TryBeginPrepare(out var shutdown));
            using (shutdown)
            {
                systems.Shutdown(shutdown);
                shutdown.Commit();
            }
        }
    }

    [Fact]
    public async Task ShadowViewsCarryNativeGeometryAndDicingParameters()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings { VirtualResolution = 512, AtlasSize = 256,
                MaxShadowLights = 1, DirectionalClipmapLevels = 1 });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
        shadows.BeginFrame();
        VirtualShadowView view = shadows.PrepareViews(lights, receiver, 1, 1, 1)[0];
        Assert.Equal(.5f, view.GeometryLodScale);
        Assert.Equal(1f, view.MaxPixelsPerEdge);
        Assert.Equal(32f, view.MinPixelsPerEdgeHardware);
        Assert.Equal(2f, view.DicingRate);
    }

    [Fact]
    public async Task PointShadowViewsUseEachLightResolutionBiasFromTheGameWorld()
    {
        using var game = new World();
        game.CreateEntity(new PointLight(Vector3.Zero, 10, Vector3.One, 1,
            ShadowSettings: new(.25f, LightMobility.Static)));
        game.CreateEntity(new PointLight(Vector3.UnitX, 10, Vector3.One, 1,
            ShadowSettings: new(.75f, LightMobility.Static)));
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world, new LightRenderExtractor());
        extraction.Extract(game);
        graphics.Apply(extraction, world);
        var lights = new RenderLightSetSystem();
        using var systems = new RenderPrepareSystems(world, extraction);
        systems.Add(lights);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        try
        {
            Assert.True(graph.TryOpenNextFrame(out var nextFrame, TimeSpan.FromSeconds(10)));
            using (nextFrame)
            {
                Assert.True(graphics.Coordinator.TryBeginPrepare(out var prepare));
                using (prepare)
                {
                    systems.Update(prepare);
                    prepare.Commit();
                }
            }
            VirtualShadowMapShaders shaders = await CreateShaders();
            using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
                new VirtualShadowMapSettings
                {
                    VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2,
                    LocalResolutionLodBias = .5f,
                });
            var receiver = new RenderView(Matrix4x4.Identity,
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
            shadows.BeginFrame();
            RenderLightSet currentLights = lights.RequireCurrent();
            VirtualShadowView[] views = shadows.PrepareViews(currentLights, receiver, 1, 1, 1).ToArray();
            // Native local bias is the nonmoving global bias plus the authored
            // per-light bias: .5 + .25 = .75, and .5 + .75 = 1.25.
            var expectedResolutionBiases = new Dictionary<Vector3, float>
            {
                [Vector3.Zero] = .75f,
                [Vector3.UnitX] = 1.25f,
            };
            Assert.Equal(12, views.Length);
            Assert.Equal(2, currentLights.Points.Count);
            uint[] observedFaces = new uint[2];
            foreach (VirtualShadowView view in views)
            {
                Assert.InRange(view.LightIndex, 0u, 1u);
                Assert.InRange(view.FaceIndex, 0u, 5u);
                uint faceBit = 1u << (int)view.FaceIndex;
                Assert.Equal(0u, observedFaces[(int)view.LightIndex] & faceBit);
                observedFaces[(int)view.LightIndex] |= faceBit;
                var light = currentLights.Points[(int)view.LightIndex];
                Vector3 position = light.Position;
                Assert.Equal(expectedResolutionBiases[position], view.ResolutionLodBias);
                Assert.Equal(-position, view.WorldTranslation);
                (Vector3 forward, Vector3 up) = view.FaceIndex switch
                {
                    0u => (Vector3.UnitX, Vector3.UnitY),
                    1u => (-Vector3.UnitX, Vector3.UnitY),
                    2u => (Vector3.UnitY, -Vector3.UnitZ),
                    3u => (-Vector3.UnitY, Vector3.UnitZ),
                    4u => (Vector3.UnitZ, Vector3.UnitY),
                    5u => (-Vector3.UnitZ, Vector3.UnitY),
                    _ => throw new ArgumentOutOfRangeException(nameof(view.FaceIndex)),
                };
                Matrix4x4 expectedView = Matrix4x4.CreateLookAt(Vector3.Zero, forward, up);
                Matrix4x4 expectedProjection = Matrix4x4.CreatePerspectiveFieldOfView(
                    MathF.PI * 0.5f, 1.0f, shadows.Settings.LocalNearPlane, light.Range);
                Assert.Equal(MatrixBits(expectedView * expectedProjection), MatrixBits(view.LightViewProjection));
            }
            Assert.All(observedFaces, faces => Assert.Equal(0x3fu, faces));

            static int[] MatrixBits(Matrix4x4 matrix) =>
            [
                BitConverter.SingleToInt32Bits(matrix.M11),
                BitConverter.SingleToInt32Bits(matrix.M12),
                BitConverter.SingleToInt32Bits(matrix.M13),
                BitConverter.SingleToInt32Bits(matrix.M14),
                BitConverter.SingleToInt32Bits(matrix.M21),
                BitConverter.SingleToInt32Bits(matrix.M22),
                BitConverter.SingleToInt32Bits(matrix.M23),
                BitConverter.SingleToInt32Bits(matrix.M24),
                BitConverter.SingleToInt32Bits(matrix.M31),
                BitConverter.SingleToInt32Bits(matrix.M32),
                BitConverter.SingleToInt32Bits(matrix.M33),
                BitConverter.SingleToInt32Bits(matrix.M34),
                BitConverter.SingleToInt32Bits(matrix.M41),
                BitConverter.SingleToInt32Bits(matrix.M42),
                BitConverter.SingleToInt32Bits(matrix.M43),
                BitConverter.SingleToInt32Bits(matrix.M44),
            ];
        }
        finally
        {
            graphics.Coordinator.WaitForTrackedSubmissions();
            Assert.True(graphics.Coordinator.TryBeginPrepare(out var shutdown));
            using (shutdown)
            {
                systems.Shutdown(shutdown);
                shutdown.Commit();
            }
        }
    }

    [Fact]
    public async Task DirectionalFirstLevelSixCoversTheNativeExtentInMeters()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 1,
                DirectionalClipmapLevels = 1, DirectionalFirstClipmapLevel = 6,
            });
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitZ, Vector3.One, 1, uint.MaxValue));
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
        shadows.BeginFrame();
        VirtualShadowView view = shadows.PrepareViews(lights, receiver, 1, 1, 1)[0];
        // Native first level six has a radius of 128 centimeters. The snapped
        // projection spans four radii, or 5.12 meters, so 2.56 is its edge.
        Vector4 clip = view.ProjectPosition(new(2.56f, 0, 0));
        Assert.InRange(MathF.Abs(clip.X / clip.W), .99999f, 1.00001f);
    }

    [Fact]
    public async Task ProjectionHistoryKeepsCachedViewsAndMovingALightInvalidatesOnlyItsViews()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2, DirectionalClipmapLevels = 2,
            });
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.AddCopyFramePass("commit shadow view state", PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref CopyPassCommandScope _, in int _) => { });
            edit.Commit();
        }
        var view = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 0.1f, 100), 128, 128, false);
        var lights = new RenderLightSet();
        uint[]? firstGenerations = null;
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 6; frameIndex++)
        {
            lights.Clear();
            lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
            lights.AddPoint(new RenderPointLight(frameIndex == 5 ? Vector3.UnitX : Vector3.Zero,
                10, Vector3.One, 1, uint.MaxValue));
            shadows.BeginFrame();
            VirtualShadowView[] views = shadows.PrepareViews(lights, view, 1, 1, 1).ToArray();
            shadows.AcquireFrameResources();
            if (firstGenerations is null) firstGenerations = views.Select(value => value.CacheGeneration).ToArray();
            else
                for (int index = 0; index < views.Length; index++)
                    if (frameIndex == 5 && index >= 2)
                        Assert.NotEqual(firstGenerations[index], views[index].CacheGeneration);
                    else
                        Assert.Equal(firstGenerations[index], views[index].CacheGeneration);
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.ObserveExecution(new ViewCommit(shadows));
            int count = frame.Execute(completions);
            for (int index = 0; index < count; index++)
                Assert.Equal(WaitStatus.Completed, graphics.Backend.WaitCpu(completions[index], TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task ScrollingTheCameraPreservesThePageAddressOfFixedWorldGeometry()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        using var shadows = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders,
            new VirtualShadowMapSettings
            {
                VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 1,
                DirectionalClipmapLevels = 2, DirectionalFirstClipmapLevel = 7,
            });
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            edit.AddCopyFramePass("commit scrolling views", PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { },
                static (ref CopyPassCommandScope _, in int _) => { });
            edit.Commit();
        }
        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        Vector3[] cameras = [Vector3.Zero, new(0, 0, 2), new(0, 0, -2),
            new(2, 0, 0), new(-2, 0, 0), new(2, 0, 2), new(-2, 0, -2)];
        (int X, int Y)? firstPage = null;
        uint firstGeneration = 0;
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        foreach (Vector3 camera in cameras)
        {
            var receiver = new RenderView(Matrix4x4.CreateLookAt(camera, camera - Vector3.UnitZ, Vector3.UnitY),
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
            shadows.BeginFrame();
            VirtualShadowView view = shadows.PrepareViews(lights, receiver, 1, 1, 1)[0];
            shadows.AcquireFrameResources();
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
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.ObserveExecution(new ViewCommit(shadows));
            int count = frame.Execute(completions);
            for (int index = 0; index < count; index++)
                Assert.Equal(WaitStatus.Completed, graphics.Backend.WaitCpu(completions[index], TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task CompletedEmptyFrameForgetsEarlierDistantLightUpdates()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2,
            DistantLightMode = VirtualShadowDistantLightMode.All,
            MaximumDistantUpdatesPerFrame = 1,
        };
        using var exercised = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders, settings);
        using var control = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders, settings);
        using var exercisedGraph = CreateDistantViewGraph();
        using var controlGraph = CreateDistantViewGraph();
        using var world = new RenderWorld();
        var first = new RenderSpotLight(new(0, 0, -10), 10, -Vector3.UnitZ, .8f, .6f,
            Vector3.One, 1, uint.MaxValue, new(0f, LightMobility.Static));
        var second = first with { Position = new(2, 0, -10) };
        var lights = new RenderLightSet();
        lights.AddSpot(world.CreateEntity(first), first);
        lights.AddSpot(world.CreateEntity(second), second);
        var empty = new RenderLightSet();
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);

        exercised.BeginFrame();
        VirtualShadowView[] initial = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Equal(2, initial.Length);
        Assert.All(initial, view => Assert.Equal(0u, view.Flags & VirtualShadowViewFlags.CachedDistantLight));
        CompleteDistantViewFrame(exercised, exercisedGraph);

        exercised.BeginFrame();
        VirtualShadowView[] earlier = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Single(earlier, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
        CompleteDistantViewFrame(exercised, exercisedGraph);

        exercised.BeginFrame();
        Assert.Empty(exercised.PrepareViews(empty, receiver, 1, 1, 1).ToArray());
        CompleteDistantViewFrame(exercised, exercisedGraph);
        for (int frame = 0; frame < 3; frame++)
        {
            control.BeginFrame();
            Assert.Empty(control.PrepareViews(empty, receiver, 1, 1, 1).ToArray());
            CompleteDistantViewFrame(control, controlGraph);
        }

        exercised.BeginFrame();
        VirtualShadowView[] reentered = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Equal(2, reentered.Length);
        Assert.All(reentered, view => Assert.Equal(0u, view.Flags & VirtualShadowViewFlags.CachedDistantLight));
        CompleteDistantViewFrame(exercised, exercisedGraph);
        control.BeginFrame();
        VirtualShadowView[] fresh = control.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Equal(2, fresh.Length);
        Assert.All(fresh, view => Assert.Equal(0u, view.Flags & VirtualShadowViewFlags.CachedDistantLight));
        CompleteDistantViewFrame(control, controlGraph);

        exercised.BeginFrame();
        VirtualShadowView[] actual = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        control.BeginFrame();
        VirtualShadowView[] expected = control.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        try
        {
            Assert.Equal(expected.Select(view => view.LightPosition), actual.Select(view => view.LightPosition));
            Assert.Equal(expected.Select(view => view.LightViewProjection), actual.Select(view => view.LightViewProjection));
            Assert.Single(actual, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
            Assert.Single(expected, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
            Assert.Equal(
                expected.Select(view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u),
                actual.Select(view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u));
        }
        finally
        {
            exercised.Discard()?.Dispose();
            control.Discard()?.Dispose();
        }
    }

    [Fact]
    public async Task CompletedEmptyFrameDoesNotCommitCanceledDistantLightUpdates()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2,
            DistantLightMode = VirtualShadowDistantLightMode.All,
            MaximumDistantUpdatesPerFrame = 1,
        };
        using var exercised = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders, settings);
        using var control = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders, settings);
        using var exercisedGraph = CreateDistantViewGraph();
        using var controlGraph = CreateDistantViewGraph();
        using var world = new RenderWorld();
        var first = new RenderSpotLight(new(0, 0, -10), 10, -Vector3.UnitZ, .8f, .6f,
            Vector3.One, 1, uint.MaxValue, new(0f, LightMobility.Static));
        var second = first with { Position = new(2, 0, -10) };
        var lights = new RenderLightSet();
        lights.AddSpot(world.CreateEntity(first), first);
        lights.AddSpot(world.CreateEntity(second), second);
        var empty = new RenderLightSet();
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);

        exercised.BeginFrame();
        VirtualShadowView[] initial = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Equal(2, initial.Length);
        Assert.All(initial, view => Assert.Equal(0u, view.Flags & VirtualShadowViewFlags.CachedDistantLight));
        CompleteDistantViewFrame(exercised, exercisedGraph);

        exercised.BeginFrame();
        VirtualShadowView[] canceled = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Single(canceled, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
        exercised.Discard()?.Dispose();

        exercised.BeginFrame();
        Assert.Empty(exercised.PrepareViews(empty, receiver, 1, 1, 1).ToArray());
        CompleteDistantViewFrame(exercised, exercisedGraph);
        for (int frame = 0; frame < 2; frame++)
        {
            control.BeginFrame();
            Assert.Empty(control.PrepareViews(empty, receiver, 1, 1, 1).ToArray());
            CompleteDistantViewFrame(control, controlGraph);
        }

        exercised.BeginFrame();
        VirtualShadowView[] reentered = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Equal(2, reentered.Length);
        Assert.All(reentered, view => Assert.Equal(0u, view.Flags & VirtualShadowViewFlags.CachedDistantLight));
        CompleteDistantViewFrame(exercised, exercisedGraph);
        control.BeginFrame();
        VirtualShadowView[] fresh = control.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Equal(2, fresh.Length);
        Assert.All(fresh, view => Assert.Equal(0u, view.Flags & VirtualShadowViewFlags.CachedDistantLight));
        CompleteDistantViewFrame(control, controlGraph);

        exercised.BeginFrame();
        VirtualShadowView[] actual = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        control.BeginFrame();
        VirtualShadowView[] expected = control.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        try
        {
            Assert.Equal(expected.Select(view => view.LightPosition), actual.Select(view => view.LightPosition));
            Assert.Equal(expected.Select(view => view.LightViewProjection), actual.Select(view => view.LightViewProjection));
            Assert.Single(actual, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
            Assert.Single(expected, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
            Assert.Equal(
                expected.Select(view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u),
                actual.Select(view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u));
        }
        finally
        {
            exercised.Discard()?.Dispose();
            control.Discard()?.Dispose();
        }
    }

    [Fact]
    public async Task DiscardedEmptyFramePreservesCommittedDistantLightViews()
    {
        VirtualShadowMapShaders shaders = await CreateShaders();
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 512, AtlasSize = 256, MaxShadowLights = 2,
            DistantLightMode = VirtualShadowDistantLightMode.All,
            MaximumDistantUpdatesPerFrame = 1,
        };
        using var exercised = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders, settings);
        using var control = new VirtualShadowMap(graphics.Backend, graphics.Device, shaders, settings);
        using var exercisedGraph = CreateDistantViewGraph();
        using var controlGraph = CreateDistantViewGraph();
        using var world = new RenderWorld();
        var first = new RenderSpotLight(new(0, 0, -10), 10, -Vector3.UnitZ, .8f, .6f,
            Vector3.One, 1, uint.MaxValue, new(0f, LightMobility.Static));
        var second = first with { Position = new(2, 0, -10) };
        var lights = new RenderLightSet();
        lights.AddSpot(world.CreateEntity(first), first);
        lights.AddSpot(world.CreateEntity(second), second);
        var receiver = new RenderView(Matrix4x4.Identity,
            Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);

        exercised.BeginFrame();
        Assert.Equal(2, exercised.PrepareViews(lights, receiver, 1, 1, 1).Length);
        CompleteDistantViewFrame(exercised, exercisedGraph);
        control.BeginFrame();
        Assert.Equal(2, control.PrepareViews(lights, receiver, 1, 1, 1).Length);
        CompleteDistantViewFrame(control, controlGraph);

        exercised.BeginFrame();
        VirtualShadowView[] exercisedEarlier = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Single(exercisedEarlier, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
        CompleteDistantViewFrame(exercised, exercisedGraph);
        control.BeginFrame();
        VirtualShadowView[] controlEarlier = control.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        Assert.Single(controlEarlier, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
        CompleteDistantViewFrame(control, controlGraph);

        exercised.BeginFrame();
        Assert.Empty(exercised.PrepareViews(new RenderLightSet(), receiver, 1, 1, 1).ToArray());
        exercised.Discard()?.Dispose();

        exercised.BeginFrame();
        VirtualShadowView[] actual = exercised.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        control.BeginFrame();
        VirtualShadowView[] expected = control.PrepareViews(lights, receiver, 1, 1, 1).ToArray();
        try
        {
            Assert.Single(actual, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
            Assert.Single(expected, view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u);
            Assert.Equal(
                expected.Select(view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u),
                actual.Select(view => (view.Flags & VirtualShadowViewFlags.CachedDistantLight) != 0u));
            var actualHistory = new VirtualShadowView[2];
            var expectedHistory = new VirtualShadowView[2];
            var actualProjections = new Matrix4x4[2];
            var expectedProjections = new Matrix4x4[2];
            Assert.True(exercised.TryCopyCommittedViews(actualHistory, actualProjections));
            Assert.True(control.TryCopyCommittedViews(expectedHistory, expectedProjections));
            Assert.Equal(expectedHistory.Select(view => view.LightPosition), actualHistory.Select(view => view.LightPosition));
            Assert.Equal(expectedHistory.Select(view => view.LightViewProjection), actualHistory.Select(view => view.LightViewProjection));
            Assert.Equal(expectedHistory.Select(view => view.WorldTranslation), actualHistory.Select(view => view.WorldTranslation));
            Assert.Equal(expectedProjections, actualProjections);
        }
        finally
        {
            exercised.Discard()?.Dispose();
            control.Discard()?.Dispose();
        }
    }

    private RenderGraph.RenderGraph CreateDistantViewGraph()
    {
        var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        using RenderGraphEdit edit = graph.BeginEdit();
        edit.AddCopyFramePass("Commit distant light views", PassQueueSelection.AnyOfType(QueueType.Graphics), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition _, ref int _) => { },
            static (ref CopyPassCommandScope _, in int _) => { });
        edit.Commit();
        return graph;
    }

    // Only submission/wait fixture plumbing: no view preparation, inputs, modes, or light-state assertions.
    private void CompleteDistantViewFrame(VirtualShadowMap shadows, RenderGraph.RenderGraph graph)
    {
        shadows.AcquireFrameResources();
        using RenderGraphFrame frame = graph.BeginFrame();
        frame.ObserveExecution(new ViewCommit(shadows));
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int count = frame.Execute(completions);
        Assert.True(count > 0);
        for (int index = 0; index < count; index++)
            Assert.Equal(WaitStatus.Completed, graphics.Backend.WaitCpu(completions[index], TimeSpan.FromSeconds(10)));
    }

    private static async Task<VirtualShadowMapShaders> CreateShaders()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders", "vsm_frame_reset.shader.asset");
        using ShaderAsset asset = await ShaderAsset.OpenAsync(path);
        Shader shader = await TestShaderLoading.CreateAsync(asset);
        int targetIndex = shader.RequireSlangTarget(SlangCompileTarget.Dxil);
        EntryPointReflection entry = shader.RequireSlangEntry(
            targetIndex,
            "ResetVirtualShadowFrame",
            SlangStage.Compute);
        return new VirtualShadowMapShaders
        {
            ResetFrameShader = shader, ResetReceiverMasksShader = shader,
            MarkCoarsePagesShader = shader,
            AllocatePagesShader = shader, ClearPagesShader = shader,
            InitializeStateShader = shader, InvalidateCastersShader = shader,
            BuildPagePoolShader = shader, CompactViewsShader = shader,
            InitializePageHiZShader = shader, ReducePageHiZShader = shader, CompletePageDepthShader = shader,
            DynamicResolutionShader = shader,
            ResetFrame = entry, ResetReceiverMasks = entry, MarkCoarsePages = entry,
            AllocatePages = entry, ClearPages = entry, InitializeState = entry, InvalidateCasters = entry,
            BuildPagePool = entry, CompactViews = entry, InitializePageHiZ = entry,
            ReducePageHiZ = entry, CompletePageDepth = entry, DynamicResolution = entry,
        };
    }

    private sealed class ViewCommit(VirtualShadowMap shadows) : IRenderGraphFrameExecutionObserver
    {
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) =>
            shadows.FinishFrame(result, contentComplete: true)?.Dispose();
    }
}
