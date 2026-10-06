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
        await CaptureAsync("unshadowed", "--no-vsm");
        await ShadowReferenceScene.WriteWorldAsync(content, inputs with { AddLights = null });
        await CaptureAsync("ambient", "--no-vsm");
        var reference = new ShadowReferenceRays(Path.Combine(engine, "templates/DefaultProject/assets/Models/ico_sphere.glb"),
            inputs, shadowed.Frame);
        ShadowReferenceComparison comparison = ShadowReferenceImage.Compare(directory, reference, inputs.Width, inputs.Height);
        await File.WriteAllTextAsync(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(new
        {
            SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"), Name = name,
            Frame = shadowed.Frame, Options = options, Comparison = comparison,
            Reference = "Legacy CPU original-triangle diagnostic; captured submitted transforms/projection; edge-excluded, not full acceptance",
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        output.WriteLine($"Independent shadow diagnostic: {directory}; {comparison}");
        Assert.True(comparison.ShadowedPixels >= minimumShadowed && comparison.LitPixels >= 512);
        Assert.True(comparison.MismatchFraction <= .02f &&
            comparison.MissedShadowPixels <= comparison.ShadowedPixels * .02f &&
            comparison.FalseShadowPixels <= comparison.LitPixels * .02f, $"{comparison}; evidence: {directory}");
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
