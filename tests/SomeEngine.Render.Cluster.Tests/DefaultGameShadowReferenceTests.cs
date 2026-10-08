using System.Numerics;
using System.Text.Json;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Runtime;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DefaultGameShadowReferenceTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> ReferenceCases()
    {
        var camera = new SceneCamera { Position = Vector3.Zero, Target = new(0, 0, -8), Up = Vector3.UnitY,
            VerticalFieldOfView = 1, NearPlane = .1f, FarPlane = 100 };
        var directional = new ShadowReferenceInputs(256, 256, camera,
            [new(new(0, 0, -8), new(4, 4, .1f), true), new(new(0, 0, -6), new(.6f), false)],
            world => world.CreateEntity(new DirectionalLight(ShadowReferenceScene.LightDirection, Vector3.One, 3)));
        var point = directional with { AddLights = world => world.CreateEntity(new PointLight(new(-4, 2, -3), 20, Vector3.One, 30)) };
        yield return ["directional", directional, new[] { "--static-scene", "--hardware-raster" }, 32];
        yield return ["point", point, new[] { "--static-scene", "--hardware-raster" }, 32];
        yield return ["point-motion", point with { Meshes = [point.Meshes[0], point.Meshes[1] with
            { Motion = new MeshMotion { Amplitude = new(.75f, 0, 0), Seed = 0 } }] },
            new[] { "--dynamic-scene", "--hardware-raster" }, 32];
        yield return ["directional-contact", directional with { Meshes = [directional.Meshes[0],
            directional.Meshes[1] with { Position = new(0, 0, -7.25f) }] }, new[] { "--static-scene", "--hardware-raster" }, 32];
        yield return ["point-software", point, new[] { "--static-scene" }, 32];
        yield return ["directional-software", directional, new[] { "--static-scene" }, 32];
        yield return ["directional-grid", new ShadowReferenceInputs(1280, 720,
            new SceneCamera { Position = new(0, 0, -3), Target = new(0, 0, 65), Up = Vector3.UnitY,
                VerticalFieldOfView = MathF.PI / 4, NearPlane = .1f, FarPlane = 1000 },
            Enumerable.Range(0, 1024).Select(index => new ShadowReferenceScene.MeshPlacement(
                new(-23.25f + index % 32 * 1.5f, -23.25f + index / 32 * 1.5f, 65), new(.6f), true)).ToArray(),
            world => world.CreateEntity(new DirectionalLight(Vector3.Normalize(new(.35f, -.85f, .4f)), Vector3.One, 3))),
            new[] { "--static-scene", "--hardware-raster" }, 0];
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public async Task ReceiverShadowsMatchIndependentTriangleRays(string name, object sceneInputs, string[] options, int minimumShadowed)
    {
        var inputs = (ShadowReferenceInputs)sceneInputs;
        string engine = FindEngineDirectory();
        using var evidence = new ShadowEvidence("shadow-reference-" + name);
        string directory = evidence.Directory;
        string content = evidence.Content;
        await ShadowReferenceScene.BuildAsync(engine, content, inputs);
        string application = Path.Combine(engine, "templates/DefaultProject/Game.App/bin/Release/net10.0/Game.App.exe");
        SceneLinearImage shadowed = await CaptureAsync("shadowed", "--vsm");
        SceneLinearImage unshadowed = await CaptureAsync("unshadowed", "--no-vsm");
        await ShadowReferenceScene.WriteWorldAsync(content, inputs with { AddLights = null });
        SceneLinearImage ambient = await CaptureAsync("ambient", "--no-vsm");
        SceneFrame frame = shadowed.Frame;
        Assert.Equal(frame.View, unshadowed.Frame.View);
        Assert.Equal(frame.View, ambient.Frame.View);
        Assert.Equal(frame.Projection, unshadowed.Frame.Projection);
        Assert.Equal(frame.Projection, ambient.Frame.Projection);
        Assert.Equal(frame.Meshes, unshadowed.Frame.Meshes);
        Assert.Equal(frame.Meshes, ambient.Frame.Meshes);
        var actual = new Vector3[frame.Width * frame.Height];
        for (int pixel = 0; pixel < actual.Length; pixel++)
        {
            int channel = pixel * 4;
            float direct = (float)unshadowed.Pixels.Span[channel] - (float)ambient.Pixels.Span[channel];
            float shaded = (float)shadowed.Pixels.Span[channel] - (float)ambient.Pixels.Span[channel];
            actual[pixel] = new(direct > 0 ? shaded / direct : float.NaN,
                (float)shadowed.Pixels.Span[channel + 3], direct > 0 ? 1 : 0);
        }
        var rays = new RayShadowScene(Path.Combine(engine, "templates/DefaultProject/assets/Models/ico_sphere.glb"),
            frame.Meshes.Select(mesh => mesh.Transform).ToArray());
        Assert.True(Matrix4x4.Invert(frame.View * frame.Projection, out Matrix4x4 clipToWorld));
        SceneLight emitter = frame.Lights.Single();
        float sourceAngle = emitter.Kind == SceneLightKind.Directional
            ? (emitter.ShadowSettings.SourceAngleDegrees ?? .5357f) * MathF.PI / 180 : 0;
        bool areaEmitter = sourceAngle > 0 || emitter.ShadowSettings.SourceRadius > 0;
        var rayInputs = new RayShadowImage.Inputs
        {
            ClipToWorld = clipToWorld, CameraPosition = frame.CameraPosition,
            Width = (uint)frame.Width, Height = (uint)frame.Height,
            LightKind = (uint)emitter.Kind, LightPosition = emitter.Position, LightDirection = emitter.Direction,
            LightRange = emitter.Range, OuterConeCos = emitter.OuterConeCos,
            SourceAngleRadians = sourceAngle, SourceRadius = emitter.ShadowSettings.SourceRadius,
            NearPlane = frame.NearPlane, FarPlane = frame.FarPlane, SampleCount = RayShadowImage.SourceSampleCount,
        };
        object comparison;
        if (areaEmitter)
        {
            RayShadowImage.SoftPixel[] reference = await RayShadowImage.TraceSoftAsync(engine, rays, rayInputs, vulkan: false);
            RayShadowSoftComparison result = RayShadowSoftComparison.Write(directory, reference, frame.Width, frame.Height, actual);
            comparison = result;
        }
        else
        {
            uint[] reference = await RayShadowImage.TraceAsync(engine, rays, rayInputs, vulkan: false);
            RayShadowComparison result = RayShadowComparison.Write(directory, reference, frame.Width, frame.Height, actual);
            comparison = result;
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(new
        {
            SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"), Name = name,
            Frame = frame, Options = options, Comparison = comparison,
            SourceEmitter = emitter.ShadowSettings, ReferenceSamples = areaEmitter ? rayInputs.SampleCount : 1u,
            Reference = "Original source glTF BLAS/TLAS, UV alpha, actual captured transforms/camera and geometric-normal ULP origin; authored solar disk or spherical emitter; every eligible receiver contributes",
            Thresholds = new { MaximumPixelVisibilityError = .15f, MaximumMismatchFraction = .02f,
                MaximumMeanVisibilityError = .02f, MaximumFalseShadowFraction = .02f,
                MaximumMissedShadowFraction = .02f, MinimumRenderedReceiverCoverage = .98f, MaximumSelfHits = 0 },
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        output.WriteLine($"Independent shadow diagnostic: {directory}; {comparison}");
        if (comparison is RayShadowSoftComparison soft)
        {
            Assert.True(soft.ShadowedPixels >= minimumShadowed && soft.LitPixels >= 512, $"Reference receivers: {soft}");
            Assert.True(soft.RenderedReceiverCoverage >= .98f, $"Receiver coverage: {soft}");
            Assert.Equal(0, soft.SelfHitPixels);
            Assert.True(soft.MeanVisibilityError <= .02f && soft.MismatchFraction <= .02f &&
                soft.FalseShadowFraction <= .02f && soft.MissedShadowFraction <= .02f,
                $"Independent source emitter visibility: {soft}; evidence: {directory}");
        }
        else if (comparison is RayShadowComparison hard)
        {
            Assert.True(hard.AllReceivers.ShadowedPixels >= minimumShadowed && hard.AllReceivers.LitPixels >= 512,
                $"Reference receivers: {hard}");
            Assert.True(hard.RenderedReceiverCoverage >= .98f, $"Receiver coverage: {hard}");
            Assert.Equal(0, hard.SelfHitPixels);
            Assert.True(hard.AllReceivers.MeanVisibilityError <= .02f && hard.AllReceivers.MismatchFraction <= .02f &&
                hard.FalseShadowFraction <= .02f && hard.MissedShadowFraction <= .02f,
                $"Independent source triangle visibility: {hard}; evidence: {directory}");
        }
        evidence.Complete();

        async Task<SceneLinearImage> CaptureAsync(string image, string shadowOption)
        {
            string bmp = Path.Combine(directory, image + ".bmp");
            string raw = Path.Combine(directory, image + ".rgba16");
            await DefaultGameCapture.RunAsync(application, ["--test-scene", content, "--frames", "64", "--hidden-window", "--no-vsync",
                "--fixed-step", "0.016666667", "--synchronous-frames", "--no-debug-ui", "--no-taa", "--no-async-compute",
                "--no-mesh-shaders", shadowOption, "--frame-output", bmp, "--linear-frame-output", raw, .. options], bmp, output);
            return SceneLinearImage.Load(raw);
        }
    }

    private static string FindEngineDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
