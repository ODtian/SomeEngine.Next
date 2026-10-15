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
        string? referencePath = args.FirstOrDefault(static value => value.StartsWith("--blend-reference="))?["--blend-reference=".Length..];
        string[] selected = args.Skip(1).Where(static value => !value.StartsWith("--")).ToArray();
        using IGraphicsBackend backend = validate
            ? new ValidationLayer(D3D12GraphicsBackend.Create(new(Validation: new(DisableGpuBasedValidation: true))))
            : D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance), adapters, out _);
        AdapterInfo adapter = adapters.First(static value => value.HardwareAccelerated);
        using Device device = backend.CreateDevice(new(adapter.Id, [new(QueueType.Graphics)], requiredFeatures: DeviceFeatures.CalibratedTimestamps));
        var shaders = new ComparisonShaders();
        var methods = Enum.GetValues<BlendMethod>().Select(method =>
            (Name: method.ToString(), Method: method, Shaders: shaders)).ToList();
        if (referencePath is not null)
        {
            var referenceShaders = new ComparisonShaders(Path.GetFullPath(referencePath));
            methods.Insert(1, ("Tile reference", BlendMethod.Tile, referenceShaders));
            methods.Insert(2, ("Cluster reference", BlendMethod.Cluster, referenceShaders));
        }
        var results = new List<object>();
        var qualityChecks = new List<object>();
        bool correct = true;
        BlendWorkload[] cases =
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
        foreach (BlendWorkload sample in cases.Concat(BlendWorkloads.Scenes).Where(value => selected.Length == 0 || selected.Contains(value.Name)))
        {
            const int qualitySize = 128;
            var qualityData = sample.CreateData(qualitySize, qualitySize);
            Vector4[] reference = [];
            var errors = new List<(double Rmse, float Maximum, float Repeated)>();
            int affectedPixels = 0;
            foreach (var method in methods)
            {
                using var renderer = new BlendEffectRenderer(backend, device, qualityData, method.Method, sample.Distance, method.Shaders);
                renderer.Render();
                Vector4[] colors = renderer.ReadColors();
                if (method.Method == BlendMethod.Forward)
                {
                    reference = colors;
                    for (int pixel = 0; pixel < colors.Length; pixel++)
                    {
                        Vector3 background = new((float)(Half)(.08f + .2f * (pixel % qualitySize * 4 / qualitySize) / 3f),
                            (float)(Half)(.08f + .2f * (pixel / qualitySize * 4 / qualitySize) / 3f), (float)(Half).18f);
                        if (Vector3.Distance(new(colors[pixel].X, colors[pixel].Y, colors[pixel].Z), background) > .001f) affectedPixels++;
                    }
                }
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
                qualityChecks.Add(new { sample.Name, Scene = sample.Scene.ToString(), Method = method.Name, Width = qualitySize, Height = qualitySize,
                    AffectedPixels = affectedPixels,
                    Rmse = rmse, MaximumError = maximum, RepeatedFrameMaximumDifference = repeated });
                correct &= float.IsFinite(maximum) && maximum <= .003f && rmse <= .0005 && repeated == 0;
                Console.WriteLine($"{sample.Name} | {method.Name}: RMSE {rmse:F7}, max {maximum:F7}, repeated {repeated}; affected {affectedPixels}");
                if (maximum > .003f)
                {
                    int pixel = Enumerable.Range(0, colors.Length).MaxBy(index => Vector4.Distance(reference[index], colors[index]));
                    Console.WriteLine($"  Pixel {pixel % qualitySize},{pixel / qualitySize}: reference {reference[pixel]}, actual {colors[pixel]}");
                }
            }
            if (validate) continue;
            long started = Stopwatch.GetTimestamp();
            var data = sample.CreateData(1280, 720);
            double setupMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var renderers = methods.Select(method =>
                new BlendEffectRenderer(backend, device, data, method.Method, sample.Distance, method.Shaders)).ToArray();
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
                        Scene = sample.Scene.ToString(), Method = methods[index].Name, Width = 1280, Height = 720,
                        ItemCount = data.Bounds.Length, ReferenceCount = data.References.Length, AffectedPixels = affectedPixels,
                        QualityWidth = qualitySize, QualityHeight = qualitySize,
                        error.Rmse, MaximumError = error.Maximum, RepeatedFrameMaximumDifference = error.Repeated,
                        MedianGpuMs = times.TotalMs[12], P95GpuMs = times.TotalMs[22],
                        CellBuildMs = times.FirstPassMs[12], EffectMs = times.ResolveMs[12],
                        renderer.GeometryBytes, renderer.IntermediateBytes,
                        AdditionalGpuBytes = renderer.GeometryBytes + renderer.IntermediateBytes,
                        CpuGeometrySetupMs = setupMs, CommonWarmupGpuMs = warmupMs,
                        WarmupFrames = warmupFrames, RefreshDepthCellsEachFrame = methods[index].Method == BlendMethod.Cluster,
                        GpuClockState = ReadClocks(),
                    });
                    Console.WriteLine($"  {sample.Name} | {methods[index].Name}: {times.TotalMs[12]:F6} ms; build {times.FirstPassMs[12]:F6}; effect {times.ResolveMs[12]:F6}");
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
            Cluster = "16x16 XY tiles, 32 logarithmic view-depth slices; current shader uses 4-byte depth occupancy masks and 8x4 wave pixel regions; reference uses packed slice intervals; source-order candidates generated by wave ballots",
            Forward = "One hardware raster draw in source order; Xray and decals use conservative far-bound hardware early depth, then sampled-depth interval testing and shared materials; comparison follows the projection depth direction; alpha-zero pixels discard before fixed-function premultiplied FP16 blending",
            Quality = "128x128 RGB comparison against Forward; maximum <= .003, RMSE <= .0005, repeated-frame difference zero; per-fragment FP16 rounding in compute",
            Timing = "GPU timestamps include barriers, shading and blending; Cluster also refreshes all depth intervals each frame. ClusterCached builds intervals during static setup. Common background and static projected geometry/XY reference construction precede timing",
            Memory = "Explicit effect data, uniforms and cell masks; common opaque color/depth are inputs",
            ReferenceShaderSource = referencePath is null ? null : File.ReadAllText(referencePath),
            ShaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "blend_effect_resolve.slang")),
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

}
