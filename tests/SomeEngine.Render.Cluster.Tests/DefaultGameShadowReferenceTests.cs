using System.Text.Json;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DefaultGameShadowReferenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("directional")]
    [InlineData("point")]
    [InlineData("point-motion")]
    [InlineData("directional-contact")]
    [InlineData("point-software")]
    [InlineData("directional-software")]
    [InlineData("directional-grid")]
    public async Task ReceiverShadowsMatchIndependentTriangleRays(string light)
    {
        string engine = FindEngineDirectory();
        string? evidence = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_EVIDENCE");
        string directory = Path.Combine(evidence ?? Path.GetTempPath(), $"shadow-reference-{light}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string content = Path.Combine(directory, "content");
        try
        {
            await ShadowReferenceScene.BuildAsync(engine, content, light);
            string application = Path.Combine(engine, "templates", "DefaultProject", "Game.App", "bin",
                "Release", "net10.0", "Game.App.exe");
            await CaptureAsync(application, content, Path.Combine(directory, "shadowed.bmp"), true, light);
            await CaptureAsync(application, content, Path.Combine(directory, "unshadowed.bmp"), false, light);
            await ShadowReferenceScene.WriteWorldAsync(content, light, illuminated: false);
            await CaptureAsync(application, content, Path.Combine(directory, "ambient.bmp"), false, light);

            var reference = new ShadowReferenceRays(Path.Combine(engine, "templates", "DefaultProject",
                "assets", "Models", "ico_sphere.glb"), light);
            ShadowReferenceComparison comparison = ShadowReferenceImage.Compare(directory, reference, light);
            string comparisonPath = Path.Combine(directory, "comparison.json");
            await File.WriteAllTextAsync(comparisonPath, JsonSerializer.Serialize(new
            {
                SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
                Scene = ShadowReferenceScene.Description(light),
                Reference = "CPU Moller-Trumbore triangle intersections from the original glTF, with independent bounding-box traversal",
                Conditions = $"{ShadowReferenceScene.ImageWidth(light)}x{ShadowReferenceScene.ImageHeight(light)}; 64 frames; fixed 1/60 s; {(ShadowReferenceScene.UsesSoftwareRaster(light) ? "mixed HW/SW" : "hardware VS/PS")}; synchronous; TAA off; async compute off; UI hidden; D3D12",
                Thresholds = new { MinimumShadowedPixels = ShadowReferenceScene.UsesDefaultGrid(light) ? 0 : 32,
                    MinimumLitPixels = 512, MaximumVisibilityError = .15f,
                    MaximumMismatchFraction = .02f, MaximumMissedShadowFraction = .02f,
                    MaximumFalseShadowFraction = .02f, EdgeExclusionPixels = 2 },
                Comparison = comparison,
            }, new JsonSerializerOptions { WriteIndented = true }));
            output.WriteLine($"Independent shadow comparison: {comparisonPath}");
            output.WriteLine($"Shadowed={comparison.ShadowedPixels}, lit={comparison.LitPixels}, " +
                $"mismatched={comparison.MismatchedPixels}/{comparison.ComparedPixels}, " +
                $"mean visibility error={comparison.MeanVisibilityError:G6}, maximum={comparison.MaximumVisibilityError:G6}");
            int minimumShadowed = ShadowReferenceScene.UsesDefaultGrid(light) ? 0 : 32;
            Assert.True(comparison.ShadowedPixels >= minimumShadowed && comparison.LitPixels >= 512,
                $"The independent reference must contain at least {minimumShadowed} shadowed and 512 illuminated receiver pixels.");
            Assert.True(comparison.MismatchFraction <= .02f &&
                comparison.MissedShadowPixels <= comparison.ShadowedPixels * .02f &&
                comparison.FalseShadowPixels <= comparison.LitPixels * .02f,
                $"DefaultGame {light} shadows differ from independent triangle rays at " +
                $"{comparison.MismatchedPixels}/{comparison.ComparedPixels} interior receiver pixels " +
                $"({comparison.MismatchFraction:P2}), with {comparison.MissedShadowPixels}/{comparison.ShadowedPixels} " +
                $"missed shadows and {comparison.FalseShadowPixels}/{comparison.LitPixels} false shadows; " +
                $"allowed 2% in each group. Evidence: {comparisonPath}");
        }
        finally
        {
            // Each run writes only its own generated content. The requested images and comparison
            // remain in the evidence directory; ordinary test runs remove their whole scratch directory.
            if (Directory.Exists(content)) Directory.Delete(content, recursive: true);
            if (evidence is null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private async Task CaptureAsync(string application, string content, string image, bool shadows, string light)
    {
        List<string> arguments = ["--test-scene", content, "--frames", "64", "--hidden-window", "--no-vsync",
            ShadowReferenceScene.HasMotion(light) ? "--dynamic-scene" : "--static-scene",
            "--fixed-step", "0.016666667", "--synchronous-frames", "--no-debug-ui",
            "--no-taa", "--no-async-compute", "--no-mesh-shaders",
            shadows ? "--vsm" : "--no-vsm", "--frame-output", image];
        if (!ShadowReferenceScene.UsesSoftwareRaster(light)) arguments.Add("--hardware-raster");
        await DefaultGameCapture.RunAsync(application, arguments, image, output);
    }

    private static string FindEngineDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Select an engine checkout containing SomeEngine.slnx.");
    }
}
