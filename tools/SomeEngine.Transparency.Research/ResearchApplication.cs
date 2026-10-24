using System.Numerics;
using System.Text.Json;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Transparency.Research;

public static class ResearchApplication
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--blend-effects") return BlendEffectApplication.Run(args[1..]);
        if (args.Length > 0 && args[0] == "--compare") return ComparisonApplication.Run(args[1..]);
        if (args.Length > 0 && args[0] == "--export-lucid") return ComparisonApplication.ExportLucidInput(args[1]);
        string output = Path.GetFullPath(args.Length == 0 ? "artifacts/transparency-research.json" : args[0]);
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance), [], out int adapterCount);
        var adapters = new AdapterInfo[adapterCount];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance), adapters, out _);
        AdapterInfo adapter = adapters.First(adapter => adapter.HardwareAccelerated);
        using Device device = backend.CreateDevice(new(adapter.Id, [new(QueueType.Graphics)], requiredFeatures: DeviceFeatures.CalibratedTimestamps));
        const float colorError = 1f / 255;
        bool withinBounds = true;
        var measurements = new List<object>();
        var cases = (from panels in new[] { 8, 32, 128 }
                     from alpha in new[] { .1f, .5f, .9f }
                     select new ResearchCase("panels", panels, alpha,
                         (width, height) => ResearchGeometry.CreatePanels(panels, alpha, width, height)))
            .Append(new ResearchCase("spheres", 8, .5f,
                (width, height) => ResearchGeometry.CreateSpheres(8, .5f, width, height)))
            .Append(new ResearchCase("overlapping spheres", 8, .5f,
                (width, height) => ResearchGeometry.CreateSpheres(8, .5f, width, height, .025f)));
        foreach (ResearchCase sample in cases)
        {
            int panels = sample.Models;
            float alpha = sample.Alpha;
            const int size = 128;
            TriangleHierarchy geometry = sample.CreateGeometry(size, size);
            using var quality = new GpuTransparencyRenderer(backend, device, size, size, geometry, colorError, inspect: true);
            quality.Render();
            var result = quality.Read();
            double squaredError = 0, weightedSquaredError = 0;
            float? repeatedColorDifference = null;
            if (sample.Geometry == "panels" && panels == 32 && alpha == .5f)
            {
                quality.Render();
                var repeated = quality.Read();
                repeatedColorDifference = result.Colors.Zip(repeated.Colors,
                    static (a, b) => Vector4.Distance(a, b)).Max();
            }
            float maximumError = 0, maximumBound = 0;
            long surfaces = 0, visits = 0;
            int covered = 0;
            for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                int pixel = row * size + column;
                Vector2 point = new((column + .5f) / size * 2 - 1, 1 - (row + .5f) / size * 2);
                Vector3 reference = TransparencyEvaluator.CompositeReference(geometry, point, 1, new(.125f));
                Vector3 weighted = TransparencyEvaluator.CompositeWeighted(geometry, point, 1, new(.125f));
                weightedSquaredError += Vector3.DistanceSquared(weighted, reference);
                Vector4 actual = result.Colors[pixel];
                Vector3 error = Vector3.Abs(reference - new Vector3(actual.X, actual.Y, actual.Z));
                squaredError += error.LengthSquared();
                maximumError = MathF.Max(maximumError, MathF.Max(error.X, MathF.Max(error.Y, error.Z)));
                maximumBound = MathF.Max(maximumBound, BitConverter.UInt32BitsToSingle(result.Results[pixel * 4 + 2]));
                surfaces += result.Results[pixel * 4];
                visits += result.Results[pixel * 4 + 1];
                if (result.Results[pixel * 4] != 0) covered++;
            }
            using var performance = new GpuTransparencyRenderer(backend, device, 1280, 720,
                sample.CreateGeometry(1280, 720), colorError);
            for (int frame = 0; frame < 8; frame++) performance.Render();
            var times = new double[24];
            for (int frame = 0; frame < times.Length; frame++) times[frame] = performance.Render();
            Array.Sort(times);
            measurements.Add(new
            {
                Geometry = sample.Geometry, Models = panels, Triangles = geometry.Triangles.Length,
                Alpha = alpha, QualityWidth = size, QualityHeight = size,
                Rmse = Math.Sqrt(squaredError / (size * size * 3)), MaximumError = maximumError, MaximumBound = maximumBound,
                WeightedRmse = Math.Sqrt(weightedSquaredError / (size * size * 3)), RepeatedColorDifference = repeatedColorDifference,
                MeanSurfaces = (double)surfaces / covered, MeanNodeVisits = (double)visits / covered,
                GeometryBytes = performance.GeometryBytes, AdditionalColorBytes = 0,
                Width = 1280, Height = 720, MedianGpuMs = times[times.Length / 2], P95GpuMs = times[(int)(times.Length * .95)],
                WithinBound = maximumError <= colorError + .0005f,
            });
            withinBounds &= maximumError <= colorError + .0005f;
            Console.WriteLine($"{panels} {sample.Geometry}, alpha {alpha:F1}: maximum error {maximumError:F6}, GPU {times[times.Length / 2]:F3} ms, surfaces {(double)surfaces / covered:F1}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Device = adapter.Name, Driver = adapter.DriverVersion, ColorError = colorError, WithinBounds = withinBounds,
            CapturedAtUtc = DateTimeOffset.UtcNow, Measurements = measurements,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
        return withinBounds ? 0 : 1;
    }

    private sealed record ResearchCase(string Geometry, int Models, float Alpha, Func<int, int, TriangleHierarchy> CreateGeometry);
}
