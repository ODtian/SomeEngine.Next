using System.Numerics;
using System.Text.Json;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Shadows;
using SomeEngine.Runtime;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[CollectionDefinition("Local light reference", DisableParallelization = true)]
public sealed class LocalLightReferenceCollection;

[Trait("Category", "Hardware")]
[Collection("Local light reference")]
public sealed class LocalLightRayShadowTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(SceneLightKind.Point, 20f, 30f, 40f)]
    [InlineData(SceneLightKind.Spot, 20f, 30f, 40f)]
    [InlineData(SceneLightKind.Point, 9.3f, 300f, 40f)]
    [InlineData(SceneLightKind.Spot, 20f, 30f, 20f)]
    public async Task LocalLightShadowsMatchIndependentSourceTriangles(SceneLightKind light, float range, float intensity, float outerDegrees)
    {
        ShadowReferenceInputs inputs = CreateInputs(world =>
        {
            if (light == SceneLightKind.Point) world.CreateEntity(new PointLight(new(-4, 2, -3), range, Vector3.One, intensity));
            else world.CreateEntity(new SpotLight(new(-4, 2, -3), range, Vector3.Normalize(new(4, -2, -5)),
                MathF.Cos(MathF.PI / 12), MathF.Cos(outerDegrees * MathF.PI / 180), Vector3.One, intensity));
        });
        using var scene = await LocalScene.CreateAsync(inputs, light.ToString(), output);
        var settings = new VirtualShadowMapSettings { DynamicResolutionEnabled = true,
            MaxFilterRadiusTexels = 0 };
        SceneLinearImage shadowed = await scene.CaptureAsync("shadowed", new(scene.Content, 64, [])
            { WaitForFrames = false, ShadowSettings = settings });
        SceneLinearImage unshadowed = await scene.CaptureAsync("unshadowed", new(scene.Content, 64, [])
            { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        await ShadowReferenceScene.WriteWorldAsync(scene.Content, inputs with { AddLights = null });
        SceneLinearImage ambient = await scene.CaptureAsync("ambient", new(scene.Content, 64, [])
            { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        RayShadowComparison comparison = await scene.CompareAsync(shadowed, unshadowed, ambient);
        AssertAccuracy(comparison);
        if (range < 10) Assert.True(comparison.RangeRejectedPixels > 0);
        if (light == SceneLightKind.Spot && outerDegrees == 20) Assert.True(comparison.ConeRejectedPixels > 0);
        if (light == SceneLightKind.Point)
            Assert.True(comparison.CubeFaceReceiverPixels.Count(count => count > 0) >= 2,
                "The point fixture must cross a cube-face boundary without removing either side.");
        scene.Complete();
    }

    [Fact]
    public async Task RepeatedLocalLightStartupAndShutdownPreservesIndependentShadows()
    {
        for (int repetition = 0; repetition < 3; repetition++)
        {
            ShadowReferenceInputs inputs = CreateInputs(world => world.CreateEntity(new SpotLight(new(-4, 2, -3), 20,
                Vector3.Normalize(new(4, -2, -5)), MathF.Cos(MathF.PI / 12), MathF.Cos(MathF.PI * 2 / 9), Vector3.One, 30)));
            using var scene = await LocalScene.CreateAsync(inputs, "spot", output);
            var settings = new VirtualShadowMapSettings { DynamicResolutionEnabled = true,
                MaxFilterRadiusTexels = 0 };
            SceneLinearImage shadowed = await scene.CaptureAsync("shadowed", new(scene.Content, 64, [])
                { WaitForFrames = false, ShadowSettings = settings });
            SceneLinearImage unshadowed = await scene.CaptureAsync("unshadowed", new(scene.Content, 64, [])
                { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
            await ShadowReferenceScene.WriteWorldAsync(scene.Content, inputs with { AddLights = null });
            SceneLinearImage ambient = await scene.CaptureAsync("ambient", new(scene.Content, 64, [])
                { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
            AssertAccuracy(await scene.CompareAsync(shadowed, unshadowed, ambient));
            scene.Complete();
        }
    }

    [Theory]
    [InlineData("point")]
    [InlineData("spot")]
    public async Task LocalLightPagePressureRecoversAfterCameraAndCasterMovementAndCanceledRefresh(string light)
    {
        ShadowReferenceInputs inputs = CreateInputs(world =>
        {
            if (light == "point") world.CreateEntity(new PointLight(new(-4, 2, -3), 20, Vector3.One, 30));
            else world.CreateEntity(new SpotLight(new(-4, 2, -3), 20, Vector3.Normalize(new(4, -2, -5)),
                MathF.Cos(MathF.PI / 12), MathF.Cos(MathF.PI * 2 / 9), Vector3.One, 30));
        });
        using var scene = await LocalScene.CreateAsync(inputs, light, output);
        var settings = new VirtualShadowMapSettings
        {
            VirtualResolution = 4096, AtlasSize = 1024, PageSize = 128,
            MaxShadowLights = 1, DynamicResolutionEnabled = false,
            MaxFilterRadiusTexels = 0,
        };
        Vector3[] cameraPositions = Enumerable.Range(0, 64).Select(frame =>
            new Vector3(.6f * frame / 63, .15f * MathF.Sin(MathF.PI * frame / 63), 0)).ToArray();
        Quaternion[] cameraRotations = Enumerable.Range(0, 64).Select(frame =>
            Quaternion.CreateFromYawPitchRoll(MathF.PI + .075f * frame / 63,
                .015f * MathF.Sin(MathF.PI * frame / 63), 0)).ToArray();
        SceneAction[] cameraActions = Enumerable.Range(0, 64).Select(frame =>
            new SceneAction(frame, edit => edit.SetCameraTransform(cameraPositions[frame], cameraRotations[frame]))).ToArray();
        var moveCaster = new SceneAction(32, edit =>
        {
            edit.SetEntityTransform(edit.MeshEntities[1], new TransformQvvs(new(-1, 0, -6), Quaternion.Identity, .6f));
            edit.RefreshShadowMaps();
            edit.CancelFrame();
        });
        SceneAction[] actions =
        [
            .. cameraActions,
            new(31, edit => edit.CaptureLinearImage(image => image.Save(Path.Combine(scene.Directory, "old-footprint.rgba16")))),
            moveCaster,
        ];
        SceneLinearImage shadowed = await scene.CaptureAsync("shadowed", new(scene.Content, 64, actions)
            { WaitForFrames = false, ShadowSettings = settings });
        SceneLinearImage unshadowed = await scene.CaptureAsync("unshadowed", new(scene.Content, 64,
        [
            .. cameraActions,
            new(31, edit => edit.CaptureLinearImage(image => image.Save(Path.Combine(scene.Directory, "unshadowed-old.rgba16")))),
            moveCaster,
        ]) { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        await ShadowReferenceScene.WriteWorldAsync(scene.Content, inputs with { AddLights = null });
        SceneLinearImage ambient = await scene.CaptureAsync("ambient", new(scene.Content, 64,
        [
            .. cameraActions,
            new(31, edit => edit.CaptureLinearImage(image => image.Save(Path.Combine(scene.Directory, "ambient-old.rgba16")))),
            moveCaster,
        ]) { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        SceneLinearImage oldShadowed = SceneLinearImage.Load(Path.Combine(scene.Directory, "old-footprint.rgba16"));
        Assert.Equal(31, oldShadowed.Frame.Frame);
        Assert.Equal(31u, oldShadowed.Frame.SubmittedFrame);
        Assert.Equal(cameraPositions[31], oldShadowed.Frame.CameraPosition);
        Assert.Equal(cameraPositions[63], shadowed.Frame.CameraPosition);
        Assert.NotEqual(oldShadowed.Frame.CameraPosition, shadowed.Frame.CameraPosition);
        Assert.NotEqual(new Vector3(oldShadowed.Frame.View.M13, oldShadowed.Frame.View.M23, oldShadowed.Frame.View.M33),
            new Vector3(shadowed.Frame.View.M13, shadowed.Frame.View.M23, shadowed.Frame.View.M33));
        Assert.True(oldShadowed.Frame.SceneSeconds < shadowed.Frame.SceneSeconds);
        Assert.Equal(63, shadowed.Frame.Frame);
        Assert.Equal(62u, shadowed.Frame.SubmittedFrame);
        RayShadowComparison comparison = await scene.CompareAsync(shadowed, unshadowed, ambient);
        RayShadowComparison oldFootprint = await scene.CompareAsync(
            oldShadowed,
            SceneLinearImage.Load(Path.Combine(scene.Directory, "unshadowed-old.rgba16")),
            SceneLinearImage.Load(Path.Combine(scene.Directory, "ambient-old.rgba16")), "old-footprint");
        AssertAccuracy(comparison);
        AssertAccuracy(oldFootprint);
        scene.Complete();
    }

    [Theory]
    [InlineData(SceneLightKind.Point)]
    [InlineData(SceneLightKind.Spot)]
    public async Task FirstResumedLocalLightFrameMatchesIndependentTrianglesWithoutGlobalRefresh(SceneLightKind light)
    {
        ShadowReferenceInputs inputs = CreateInputs(world =>
        {
            if (light == SceneLightKind.Point) world.CreateEntity(new PointLight(new(-4, 2, -3), 20, Vector3.One, 30));
            else world.CreateEntity(new SpotLight(new(-4, 2, -3), 20, Vector3.Normalize(new(4, -2, -5)),
                MathF.Cos(MathF.PI / 12), MathF.Cos(MathF.PI * 2 / 9), Vector3.One, 30));
        });
        using var scene = await LocalScene.CreateAsync(inputs, "first-resumed-" + light, output);
        var settings = new VirtualShadowMapSettings { VirtualResolution = 4096, AtlasSize = 1024, PageSize = 128,
            MaxShadowLights = 1, DynamicResolutionEnabled = false, MaxFilterRadiusTexels = 0 };
        SceneAction[] actions = [new(32, edit =>
        {
            edit.SetEntityTransform(edit.MeshEntities[1], new TransformQvvs(new(-1, 0, -6), Quaternion.Identity, .6f));
            edit.CancelFrame();
        })];
        SceneLinearImage shadowed = await scene.CaptureAsync("shadowed", new(scene.Content, 34, actions)
            { WaitForFrames = false, ShadowSettings = settings });
        SceneLinearImage unshadowed = await scene.CaptureAsync("unshadowed", new(scene.Content, 34, actions)
            { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        await ShadowReferenceScene.WriteWorldAsync(scene.Content, inputs with { AddLights = null });
        SceneLinearImage ambient = await scene.CaptureAsync("ambient", new(scene.Content, 34, actions)
            { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        Assert.Equal(33, shadowed.Frame.Frame);
        Assert.Equal(32u, shadowed.Frame.SubmittedFrame);
        AssertAccuracy(await scene.CompareAsync(shadowed, unshadowed, ambient));
        scene.Complete();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task EveryPointLightFacePreservesIndependentReceiverShadows(int face)
    {
        Vector3[] directions = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        Vector3 axis = directions[face];
        Vector3 side = face < 2 ? Vector3.UnitY : Vector3.UnitX;
        Vector3 scale = face < 2 ? new(.1f, 4, 4) : face < 4 ? new(4, .1f, 4) : new(4, 4, .1f);
        var inputs = new ShadowReferenceInputs(256, 256,
            new SceneCamera { Position = side * 3, Target = axis * 8, Up = face is 2 or 3 ? Vector3.UnitZ : Vector3.UnitY,
                VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
            [new(axis * 8, scale, true), new(axis * 6 + side, new(.6f), false)],
            world => world.CreateEntity(new PointLight(Vector3.Zero, 20, Vector3.One, 30)));
        using var scene = await LocalScene.CreateAsync(inputs, "point-face-" + face, output);
        var settings = new VirtualShadowMapSettings { VirtualResolution = 4096, AtlasSize = 2048, PageSize = 128,
            MaxShadowLights = 1, DynamicResolutionEnabled = false, MaxFilterRadiusTexels = 0 };
        SceneLinearImage shadowed = await scene.CaptureAsync("shadowed", new(scene.Content, 64, [])
            { WaitForFrames = false, ShadowSettings = settings });
        SceneLinearImage unshadowed = await scene.CaptureAsync("unshadowed", new(scene.Content, 64, [])
            { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        await ShadowReferenceScene.WriteWorldAsync(scene.Content, inputs with { AddLights = null });
        SceneLinearImage ambient = await scene.CaptureAsync("ambient", new(scene.Content, 64, [])
            { WaitForFrames = false, ShadowSettings = settings }, "--no-vsm");
        RayShadowComparison comparison = await scene.CompareAsync(shadowed, unshadowed, ambient);
        Assert.True(comparison.CubeFaceReceiverPixels[face] >= 512);
        AssertAccuracy(comparison);
        scene.Complete();
    }

    private static ShadowReferenceInputs CreateInputs(Action<World> addLight) => new(256, 256,
        new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
            VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 },
        [new(new(0, 0, -8), new(4, 4, .1f), true), new(new(0, 0, -6), new(.6f), false)], addLight);

    private static void AssertAccuracy(RayShadowComparison comparison)
    {
        Assert.True(comparison.AllReceivers.ShadowedPixels >= 32 && comparison.AllReceivers.LitPixels >= 512);
        Assert.True(comparison.RenderedReceiverCoverage >= .98f);
        Assert.Equal(0, comparison.SelfHitPixels);
        Assert.True(comparison.AllReceivers.MismatchFraction <= .02f &&
            comparison.AllReceivers.MeanVisibilityError <= .02f,
            $"Independent local-light visibility: {comparison}");
    }

    private sealed class LocalScene(string engine, ShadowEvidence evidence, ITestOutputHelper output) : IDisposable
    {
        private string directory => evidence.Directory;
        internal string Directory => directory;
        internal string Content => evidence.Content;
        internal void Complete() => evidence.Complete();
        private readonly List<object> _captures = [];

        internal static async Task<LocalScene> CreateAsync(ShadowReferenceInputs inputs, string name, ITestOutputHelper output)
        {
            string engine = FindEngine();
            var evidence = new ShadowEvidence("local-ray-" + name);
            var scene = new LocalScene(engine, evidence, output);
            try { await ShadowReferenceScene.BuildAsync(engine, scene.Content, inputs); return scene; }
            catch { scene.Dispose(); throw; }
        }

        internal async Task<SceneLinearImage> CaptureAsync(string name, SceneScript script, params string[] options)
        {
            string raw = Path.Combine(directory, name + ".rgba16");
            string[] arguments = ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window", "--no-vsync",
                "--frames", script.FrameCount.ToString(),
                "--no-debug-ui", "--fixed-step", "0.016666667", "--linear-frame-output", raw,
                "--frame-output", Path.Combine(directory, name + ".bmp"), .. options];
            ApplicationResult result = await Application.RunAsync(arguments, script);
            var capture = new { Arguments = arguments, result.Succeeded, result.CompletedFrames, result.Failure,
                SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
                script.FrameCount,
                Actions = script.Actions.Select(action => action.Frame).ToArray(), script.WaitForFrames, script.ShadowSettings };
            _captures.Add(capture);
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".capture.json"),
                JsonSerializer.Serialize(capture, new JsonSerializerOptions { WriteIndented = true }));
            Assert.True(result.Succeeded, $"{result.Failure}; {result.Error}");
            return SceneLinearImage.Load(raw);
        }

        internal async Task<RayShadowComparison> CompareAsync(SceneLinearImage shadowed,
            SceneLinearImage unshadowed, SceneLinearImage ambient, string? evidenceName = null)
        {
            SceneFrame frame = shadowed.Frame;
            foreach (SceneFrame compared in new[] { unshadowed.Frame, ambient.Frame })
            {
                Assert.Equal(frame.Frame, compared.Frame);
                Assert.Equal(frame.SubmittedFrame, compared.SubmittedFrame);
                Assert.Equal(frame.SceneSeconds, compared.SceneSeconds);
                Assert.Equal(frame.View, compared.View);
                Assert.Equal(frame.CameraPosition, compared.CameraPosition);
                Assert.Equal(frame.JitterPixels, compared.JitterPixels);
            }
            Assert.Equal(frame.Projection, unshadowed.Frame.Projection);
            Assert.Equal(frame.Projection, ambient.Frame.Projection);
            Assert.Equal(frame.Meshes, unshadowed.Frame.Meshes);
            Assert.Equal(frame.Meshes, ambient.Frame.Meshes);
            var rays = new RayShadowScene(Path.Combine(engine, "templates/DefaultProject/assets/Models/ico_sphere.glb"),
                frame.Meshes.Select(mesh => mesh.Transform).ToArray());
            SceneLight lamp = frame.Lights.Single();
            Assert.True(Matrix4x4.Invert(frame.View * frame.Projection, out Matrix4x4 clipToWorld));
            uint[] reference = await RayShadowImage.TraceAsync(engine, rays, new()
            {
                ClipToWorld = clipToWorld, CameraPosition = frame.CameraPosition,
                Width = (uint)frame.Width, Height = (uint)frame.Height,
                LightKind = lamp.Kind == SceneLightKind.Point ? 1u : 2u,
                LightPosition = lamp.Position, LightDirection = lamp.Direction,
                LightRange = lamp.Range, OuterConeCos = lamp.OuterConeCos,
                NearPlane = frame.NearPlane, FarPlane = frame.FarPlane,
            }, vulkan: false);
            var actual = new Vector3[frame.Width * frame.Height];
            for (int pixel = 0; pixel < actual.Length; pixel++)
            {
                int channel = pixel * 4;
                float direct = (float)unshadowed.Pixels.Span[channel] - (float)ambient.Pixels.Span[channel];
                float shaded = (float)shadowed.Pixels.Span[channel] - (float)ambient.Pixels.Span[channel];
                float visibility = direct > 0 ? shaded / direct : float.NaN;
                // No radiance/shading threshold removes receivers. An indeterminate ratio
                // is a full error, not an excluded edge or a silently lit pixel.
                actual[pixel] = new(visibility, (float)shadowed.Pixels.Span[channel + 3], direct > 0 ? 1 : 0);
            }
            string comparisonDirectory = evidenceName is null ? directory : Path.Combine(directory, evidenceName);
            System.IO.Directory.CreateDirectory(comparisonDirectory);
            RayShadowComparison comparison = RayShadowComparison.Write(comparisonDirectory, reference, frame.Width, frame.Height, actual);
            await File.WriteAllTextAsync(Path.Combine(comparisonDirectory, "comparison.json"), JsonSerializer.Serialize(new
            {
                SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
                Frame = frame, Captures = _captures, Comparison = comparison,
                Reference = "Independent source triangles and geometric-normal ULP origin; authored point range/cube-face or spot range/cone; actual submitted projection and transforms",
                Observation = "Unfiltered Standard user-material color: (shadowed - ambient)/(unshadowed - ambient). All eligible source receivers retained; nonpositive direct radiance is an error, not a denominator filter. No source replacement.",
                Pressure = "Configured 64-page local-light fixture is page pressure, not proof of a particular resident/missing mip. No private page probes.",
                Thresholds = new { MaximumPixelVisibilityError = .15f, MaximumMismatchFraction = .02f,
                    MaximumMeanVisibilityError = .02f, MinimumShadowedPixels = 32,
                    MinimumLitPixels = 512, MinimumRenderedReceiverCoverage = .98f, MaximumSelfHits = 0 },
                MeetsSourceGeometryAccuracyThresholds = comparison.AllReceivers.MeanVisibilityError <= .02f &&
                    comparison.AllReceivers.MismatchFraction <= .02f,
            }, new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
            output.WriteLine($"Local light independent reference: {directory}; {comparison}");
            return comparison;
        }

        public void Dispose() => evidence.Dispose();
    }

    private static string FindEngine()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Select a SomeEngine checkout for the independent local-light reference.");
    }
}
