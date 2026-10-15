using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;

namespace SomeEngine.Transparency.Research;

public static class BlendEffectApplication
{
    public static int Run(string[] args)
    {
        string output = Path.GetFullPath(args.Length == 0
            ? "wiki/architecture/rendering/Transparency-Cluster-Effects-2026-10-15.json" : args[0]);
        bool validate = args.Contains("--validate");
        string[] selected = args.Skip(1).Where(static value => value != "--validate").ToArray();
        using IGraphicsBackend backend = validate
            ? new ValidationLayer(D3D12GraphicsBackend.Create(new(Validation: new(DisableGpuBasedValidation: true))))
            : D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance), adapters, out _);
        AdapterInfo adapter = adapters.First(static value => value.HardwareAccelerated);
        using Device device = backend.CreateDevice(new(adapter.Id, [new(QueueType.Graphics)], requiredFeatures: DeviceFeatures.CalibratedTimestamps));
        var shaders = new ComparisonShaders();
        var results = new List<object>();
        var qualityChecks = new List<object>();
        bool correct = true;
        BlendCase[] cases =
        [
            new("Xray panels 8", BlendEffect.Xray, 8, 4),
            new("Xray panels 32", BlendEffect.Xray, 32, 4),
            new("Xray panels 128", BlendEffect.Xray, 128, 4),
            new("Xray spheres 8", BlendEffect.Xray, 8, 4, true),
            new("Xray deep range", BlendEffect.Xray, 32, 128),
            new("Decals 8", BlendEffect.Decals, 8, 4),
            new("Decals 32", BlendEffect.Decals, 32, 4),
            new("Decals 128", BlendEffect.Decals, 128, 4),
        ];
        foreach (BlendCase sample in cases.Where(value => selected.Length == 0 || selected.Contains(value.Name)))
        {
            const int qualitySize = 128;
            var qualityData = new BlendEffectData(sample.Effect, sample.Count, qualitySize, qualitySize, sample.Spheres);
            Vector4[] reference = [];
            var errors = new List<(double Rmse, float Maximum, float Repeated)>();
            foreach (BlendMethod method in Enum.GetValues<BlendMethod>())
            {
                using var renderer = new BlendEffectRenderer(backend, device, qualityData, method, sample.Distance, shaders);
                renderer.Render();
                Vector4[] colors = renderer.ReadColors();
                if (method == BlendMethod.Forward) reference = colors;
                double squared = 0;
                float maximum = 0;
                for (int pixel = 0; pixel < colors.Length; pixel++)
                {
                    Vector4 error = Vector4.Abs(reference[pixel] - colors[pixel]);
                    squared += error.X * error.X + error.Y * error.Y + error.Z * error.Z;
                    maximum = MathF.Max(maximum, MathF.Max(error.X, MathF.Max(error.Y, error.Z)));
                }
                renderer.Render();
                float repeated = colors.Zip(renderer.ReadColors(), static (a, b) => Vector4.Distance(a, b)).Max();
                double rmse = Math.Sqrt(squared / (colors.Length * 3));
                errors.Add((rmse, maximum, repeated));
                qualityChecks.Add(new { sample.Name, Method = method.ToString(), Width = qualitySize, Height = qualitySize,
                    Rmse = rmse, MaximumError = maximum, RepeatedFrameMaximumDifference = repeated });
                correct &= float.IsFinite(maximum) && maximum <= .003f && rmse <= .0005 && repeated == 0;
                Console.WriteLine($"{sample.Name} | {method}: RMSE {rmse:F7}, max {maximum:F7}, repeated {repeated}");
                if (maximum > .003f)
                {
                    int pixel = Enumerable.Range(0, colors.Length).MaxBy(index => Vector4.Distance(reference[index], colors[index]));
                    Console.WriteLine($"  Pixel {pixel % qualitySize},{pixel / qualitySize}: reference {reference[pixel]}, actual {colors[pixel]}");
                }
            }
            if (validate) continue;
            long started = Stopwatch.GetTimestamp();
            var data = new BlendEffectData(sample.Effect, sample.Count, 1280, 720, sample.Spheres);
            double setupMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var renderers = Enum.GetValues<BlendMethod>().Select(method =>
                new BlendEffectRenderer(backend, device, data, method, sample.Distance, shaders)).ToArray();
            try
            {
                double warmupMs = 0;
                while (warmupMs < 250) warmupMs += renderers[0].Measure(0, 64).TotalMs.Sum();
                for (int index = 0; index < renderers.Length; index++)
                {
                    BlendEffectRenderer renderer = renderers[index];
                    TransparencyTimes estimate = renderer.Measure(0, 16);
                    int warmupFrames = Math.Max(32, (int)Math.Ceiling(250 / estimate.TotalMs.Average()));
                    TransparencyTimes times = renderer.Measure(warmupFrames, 24);
                    Array.Sort(times.TotalMs); Array.Sort(times.FirstPassMs); Array.Sort(times.ResolveMs);
                    var error = errors[index];
                    results.Add(new
                    {
                        sample.Name, Effect = sample.Effect.ToString(), sample.Count, sample.Distance, sample.Spheres,
                        Method = ((BlendMethod)index).ToString(), Width = 1280, Height = 720,
                        QualityWidth = qualitySize, QualityHeight = qualitySize,
                        error.Rmse, MaximumError = error.Maximum, RepeatedFrameMaximumDifference = error.Repeated,
                        MedianGpuMs = times.TotalMs[12], P95GpuMs = times.TotalMs[22],
                        CellBuildMs = times.FirstPassMs[12], EffectMs = times.ResolveMs[12],
                        renderer.GeometryBytes, renderer.IntermediateBytes,
                        AdditionalGpuBytes = renderer.GeometryBytes + renderer.IntermediateBytes,
                        CpuGeometrySetupMs = setupMs, CommonWarmupGpuMs = warmupMs,
                        WarmupFrames = warmupFrames, RefreshDepthCellsEachFrame = (BlendMethod)index == BlendMethod.Cluster,
                        GpuClockState = ReadClocks(),
                    });
                    Console.WriteLine($"  {sample.Name} | {(BlendMethod)index}: {times.TotalMs[12]:F6} ms; build {times.FirstPassMs[12]:F6}; effect {times.ResolveMs[12]:F6}");
                }
            }
            finally { foreach (var renderer in renderers) renderer.Dispose(); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Device = adapter.Name, Driver = adapter.DriverVersion, CapturedAtUtc = DateTimeOffset.UtcNow,
            Correct = correct, NativeValidation = validate, MinimumWarmupFrames = validate ? 0 : 32,
            MinimumWarmupGpuMsPerMethod = validate ? 0 : 250, Samples = validate ? 0 : 24,
            Geometry = "Static projected triangles with interpolated UV and normals; colored screen/view-depth decal projectors",
            Material = "Shared 64x64 RGBA pattern, interpolated normals, diffuse and specular lighting for occluded Xray; textured color decals after opaque shading",
            Cluster = "16x16 XY tiles, 32 logarithmic view-depth slices; 4-byte slice interval per item; source-order candidate masks generated by wave ballots",
            Forward = "One hardware raster draw in source order; Xray and decals use conservative far-bound hardware early depth, then sampled-depth interval testing and shared materials; comparison follows the projection depth direction; alpha-zero pixels discard before fixed-function premultiplied FP16 blending",
            Quality = "128x128 RGB comparison against Forward; maximum <= .003, RMSE <= .0005, repeated-frame difference zero; per-fragment FP16 rounding in compute",
            Timing = "GPU timestamps include barriers, shading and blending; Cluster also refreshes all depth intervals each frame. ClusterCached builds intervals during static setup. Common background and static projected geometry/XY reference construction precede timing",
            Memory = "Explicit effect data, uniforms and cell masks; common opaque color/depth are inputs",
            QualityChecks = qualityChecks,
            Measurements = results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
        return correct ? 0 : 1;
    }

    private static string ReadClocks()
    {
        using Process process = Process.Start(new ProcessStartInfo("nvidia-smi",
            "--query-gpu=clocks.gr,clocks.mem,power.draw,temperature.gpu --format=csv,noheader,nounits")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        string result = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return result;
    }

    private sealed record BlendCase(string Name, BlendEffect Effect, int Count, float Distance, bool Spheres = false);
}
