using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Lighting;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Shadows;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using SlangShaderSharp;

namespace SomeEngine.Render.Tests;

public sealed class VirtualShadowViewTests(RenderTestDevice graphics) : IClassFixture<RenderTestDevice>
{
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
        using var systems = new RenderFrameSystems(world);
        systems.Add(lights);
        using var graph = new RenderGraph.RenderGraph(graphics.Backend, graphics.Device,
            [graphics.Backend.GetQueue(graphics.Device, QueueType.Graphics)]);
        Assert.True(graphics.DataLock.TryBeginRead(out var read));
        using (read)
        using (var frame = graph.BeginFrame()) systems.Update(read, frame);
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
        VirtualShadowView[] views = shadows.PrepareViews(lights.RequireCurrent(), receiver, 1, 1, 1).ToArray();
        // Native local bias is the nonmoving global bias plus the authored
        // per-light bias: .5 + .25 = .75, and .5 + .75 = 1.25.
        Assert.Equal(.75f, views[0].ResolutionLodBias);
        Assert.Equal(1.25f, views[6].ResolutionLodBias);
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
        Vector4 clip = Vector4.Transform(new Vector4(2.56f, 0, 0, 1), view.LightViewProjection);
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
                DirectionalClipmapLevels = 2, DirectionalFirstClipmapLevel = 0,
                WorldUnitsPerCentimeter = 1f,
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
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        foreach (Vector3 camera in cameras)
        {
            var receiver = new RenderView(Matrix4x4.CreateLookAt(camera, camera - Vector3.UnitZ, Vector3.UnitY),
                Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, .1f, 100), 128, 128);
            shadows.BeginFrame();
            VirtualShadowView view = shadows.PrepareViews(lights, receiver, 1, 1, 1)[0];
            shadows.AcquireFrameResources();
            Vector4 clip = Vector4.Transform(new Vector4(.5f, 0, .5f, 1), view.LightViewProjection);
            Vector2 uv = new Vector2(clip.X, -clip.Y) / (2 * clip.W) + new Vector2(.5f);
            Assert.InRange(uv.X, 0, .99999f);
            Assert.InRange(uv.Y, 0, .99999f);
            var page = (view.VirtualPageOriginX + (int)MathF.Floor(uv.X * view.VirtualResolution / view.PageSize),
                view.VirtualPageOriginY + (int)MathF.Floor(uv.Y * view.VirtualResolution / view.PageSize));
            if (firstPage is null)
                firstPage = page;
            Assert.Equal(firstPage.Value, page);
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.ObserveExecution(new ViewCommit(shadows));
            int count = frame.Execute(completions);
            for (int index = 0; index < count; index++)
                Assert.Equal(WaitStatus.Completed, graphics.Backend.WaitCpu(completions[index], TimeSpan.FromSeconds(10)));
        }
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
            MarkPagesShader = shader, MarkCoarsePagesShader = shader,
            AllocatePagesShader = shader, ClearPagesShader = shader,
            InitializeStateShader = shader, InvalidateCastersShader = shader,
            BuildPagePoolShader = shader, CompactViewsShader = shader,
            InitializePageHiZShader = shader, ReducePageHiZShader = shader,
            CompletePageDepthShader = shader, DynamicResolutionShader = shader,
            ResetFrame = entry, ResetReceiverMasks = entry, MarkPages = entry, MarkCoarsePages = entry,
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
