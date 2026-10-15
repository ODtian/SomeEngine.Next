using System.Numerics;
using System.Diagnostics;
using System.Text.Json;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Transparency.Research;

public static class ComparisonApplication
{
    public static int Run(string[] args)
    {
        string output = Path.GetFullPath(args.Length == 0 ? "wiki/architecture/rendering/Transparency-Comparison-2026-10-15.json" : args[0]);
        string[] selected = args.Skip(1).ToArray();
        bool rgba8 = selected.Contains("--rgba8");
        bool tilesOnly = selected.Contains("--tiles-only");
        string? tileReference = selected.FirstOrDefault(static value => value.StartsWith("--tile-reference=", StringComparison.Ordinal));
        if (tileReference is not null) tileReference = Path.GetFullPath(tileReference["--tile-reference=".Length..]);
        selected = selected.Where(static value => !value.StartsWith("--", StringComparison.Ordinal)).ToArray();
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance), adapters, out _);
        AdapterInfo adapter = adapters.First(static adapter => adapter.HardwareAccelerated);
        using Device device = backend.CreateDevice(new(adapter.Id, [new(QueueType.Graphics)], requiredFeatures: DeviceFeatures.CalibratedTimestamps));
        var shaders = new ComparisonShaders();
        var cases = CreateCases();
        var measurements = new List<object>();
        bool correct = true;
        foreach (ComparisonCase sample in cases.Where(sample => selected.Length == 0 || selected.Contains(sample.Geometry)))
        {
            const int qualitySize = 128, width = 1280, height = 720;
            TriangleHierarchy geometry = sample.CreateGeometry(qualitySize, qualitySize);
            if (rgba8) geometry = QuantizeColors(geometry);
            var reference = new Vector3[qualitySize * qualitySize];
            for (int row = 0; row < qualitySize; row++)
            for (int column = 0; column < qualitySize; column++)
                reference[row * qualitySize + column] = TransparencyEvaluator.CompositeReference(geometry,
                    new((column + .5f) / qualitySize * 2 - 1, 1 - (row + .5f) / qualitySize * 2), 1, new(.125f));
            TriangleHierarchy performanceGeometry = sample.CreateGeometry(width, height);
            if (rgba8) performanceGeometry = QuantizeColors(performanceGeometry);
            var methods = new List<ComparisonMethod>();
            methods.Add(new("Coverage traversal", (w, h, g) => new GpuTransparencyRenderer(backend, device, w, h, g, 1f / 255)));
            methods.Add(new("WBOIT 2015", (w, h, g) => new RasterTransparencyRenderer(backend, device, w, h, g, TransparencyComparison.Weighted, shaders)));
            methods.Add(new("WBOIT cubic", (w, h, g) => new RasterTransparencyRenderer(backend, device, w, h, g, TransparencyComparison.WeightedCubic, shaders)));
            methods.Add(new("MBOIT 4 FP32", (w, h, g) => new RasterTransparencyRenderer(backend, device, w, h, g, TransparencyComparison.FourMoments, shaders)));
            methods.Add(new("MBOIT 6 FP32", (w, h, g) => new RasterTransparencyRenderer(backend, device, w, h, g, TransparencyComparison.SixMoments, shaders)));
            methods.Add(new("PPLL sort", (w, h, g) => new RasterTransparencyRenderer(backend, device, w, h, g, TransparencyComparison.PixelSort, shaders)));
            methods.Add(new("Tile visibility hardware", (w, h, g) => new RasterTransparencyRenderer(backend, device, w, h, g, TransparencyComparison.TileHardware, shaders)));
            methods.Add(new("Tile visibility software", (w, h, g) => new RasterTransparencyRenderer(backend, device, w, h, g, TransparencyComparison.TileSoftware, shaders)));
            if (tileReference is not null)
            {
                methods.Insert(methods.Count - 2, new("Tile visibility hardware reference", (w, h, g) =>
                    new RasterTransparencyRenderer(backend, device, w, h, g, TransparencyComparison.TileHardware, shaders, tileReference)));
                methods.Insert(methods.Count - 1, new("Tile visibility software reference", (w, h, g) =>
                    new RasterTransparencyRenderer(backend, device, w, h, g, TransparencyComparison.TileSoftware, shaders, tileReference)));
            }
            if (tilesOnly) methods.RemoveAll(static method => !method.Name.StartsWith("Tile visibility", StringComparison.Ordinal));
            var quality = new List<(ComparisonMethod Method, ImageError Error, float? RepeatedDifference, float? SubmissionDifference)>();
            foreach (ComparisonMethod method in methods)
            {
                using ITransparencyRenderer renderer = method.Create(qualitySize, qualitySize, geometry);
                renderer.Render();
                Vector4[] colors = renderer.ReadColors();
                ImageError error = Compare(colors, reference);
                float? repeatedDifference = null, submissionDifference = null;
                if (sample.Geometry == "shaded overlapping spheres" && method.Name.StartsWith("Tile visibility", StringComparison.Ordinal))
                {
                    renderer.Render();
                    repeatedDifference = colors.Zip(renderer.ReadColors(), static (a, b) => Vector4.Distance(a, b)).Max();
                    TriangleHierarchy reversed = new(geometry.Triangles.Reverse().ToArray());
                    using ITransparencyRenderer reverseRenderer = method.Create(qualitySize, qualitySize, reversed);
                    reverseRenderer.Render();
                    submissionDifference = colors.Zip(reverseRenderer.ReadColors(), static (a, b) => Vector4.Distance(a, b)).Max();
                    correct &= repeatedDifference == 0 && submissionDifference <= .0005f;
                }
                quality.Add((method, error, repeatedDifference, submissionDifference));
                bool sourceCorrect = error.NonFinitePixels == 0 &&
                    (method.Name == "Coverage traversal" || method.Name.StartsWith("Tile visibility", StringComparison.Ordinal) ? error.Maximum <= 1f / 255 + .0005f :
                     method.Name == "PPLL sort" ? error.Maximum <= .0006f : true);
                correct &= sourceCorrect;
                Console.WriteLine(sample.Geometry + " " + sample.Models + " alpha " + sample.Alpha +
                    " | " + method.Name + " | RMSE " + error.Rmse.ToString("F6") + " max " + error.Maximum.ToString("F6"));
            }
            var renderers = quality.Select(item => item.Method.Create(width, height, performanceGeometry)).ToArray();
            try
            {
                double warmupGpuMs = 0;
                TransparencyTimes warmup = renderers[^1].Measure(0, 16);
                warmupGpuMs += warmup.TotalMs.Sum();
                while (warmupGpuMs < 250)
                {
                    int frames = (int)Math.Ceiling((250 - warmupGpuMs) / warmup.TotalMs.Average());
                    warmup = renderers[^1].Measure(0, frames);
                    warmupGpuMs += warmup.TotalMs.Sum();
                }
                var measured = renderers.Select(static renderer => renderer.Measure(32, 24)).ToArray();
                string gpuClocks = ReadGpuClocks();
                var times = measured.Select(static data => data.TotalMs).ToArray();
                var firstPass = measured.Select(static data => data.FirstPassMs.Length == 0 ? new double[24] : data.FirstPassMs).ToArray();
                var secondPass = measured.Select(static data => data.SecondPassMs.Length == 0 ? new double[24] : data.SecondPassMs).ToArray();
                var resolves = measured.Select(static data => data.ResolveMs).ToArray();
                for (int method = 0; method < renderers.Length; method++)
                {
                    Array.Sort(times[method]); Array.Sort(firstPass[method]); Array.Sort(secondPass[method]); Array.Sort(resolves[method]);
                    ITransparencyRenderer renderer = renderers[method];
                    RasterTransparencyRenderer? raster = renderer as RasterTransparencyRenderer;
                    ImageError error = quality[method].Error;
                    measurements.Add(new
                    {
                        Geometry = sample.Geometry, Models = sample.Models, Alpha = sample.Alpha, Triangles = geometry.Triangles.Length,
                        CoverageGroups = performanceGeometry.CoverageRanges.Length,
                        SeparatedCoverage = performanceGeometry.SeparatedCoverage,
                        Method = quality[method].Method.Name, QualityWidth = qualitySize, QualityHeight = qualitySize,
                        Rmse = error.Rmse, MeanAbsoluteError = error.MeanAbsolute, MaximumError = error.Maximum,
                        MeanSignedError = new[] { error.MeanSigned.X, error.MeanSigned.Y, error.MeanSigned.Z },
                        NonFinitePixels = error.NonFinitePixels, Width = width, Height = height,
                        RepeatedFrameMaximumDifference = quality[method].RepeatedDifference,
                        SubmissionOrderMaximumDifference = quality[method].SubmissionDifference,
                        MedianGpuMs = times[method][12], P95GpuMs = times[method][22],
                        FirstPassMs = firstPass[method][12], SecondPassMs = secondPass[method][12], ResolveMs = resolves[method][12],
                        GeometryBytes = renderer.GeometryBytes, IntermediateBytes = renderer.IntermediateBytes,
                        AdditionalGpuBytes = renderer.GeometryBytes + renderer.IntermediateBytes + 256,
                        FragmentCount = raster?.FragmentCount, MaximumFragmentsPerPixel = raster?.MaximumFragmentsPerPixel,
                        SortAlgorithm = raster is { FragmentCount: > 0 }
                            ? raster.MaximumFragmentsPerPixel <= 32 ? "Local insertion" : "Linked merge" : null,
                        CommonWarmupGpuMs = warmupGpuMs, GpuClockState = gpuClocks,
                    });
                    Console.WriteLine("  " + quality[method].Method.Name + ": " + times[method][12].ToString("F3") + " ms, " +
                        ((renderer.GeometryBytes + renderer.IntermediateBytes + 256) / 1048576.0).ToString("F3") + " MiB");
                }
            }
            finally { foreach (ITransparencyRenderer renderer in renderers) renderer.Dispose(); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            Device = adapter.Name, Driver = adapter.DriverVersion, CapturedAtUtc = DateTimeOffset.UtcNow,
            ColorError = 1f / 255, WarmupFrames = 32, Samples = 24, BatchedGpuSubmission = true, Correct = correct,
            InputColor = rgba8 ? "RGBA8 UNORM" : "RGBA32F",
            ReferenceShaderSource = tileReference is null ? null : File.ReadAllText(tileReference),
            Moment4Bias = 5e-7, Moment6Bias = 5e-6, MomentOverestimation = .25,
            Geometry = "Projected triangles with scalar alpha and constant or flat shaded triangle colors",
            Timing = "GPU timestamps include method clears, transparent raster draws, method barriers and composite; common background initialization and static geometry setup occur before timing",
            Memory = "Explicit GPU buffer data and render target pixels, with the common opaque color and depth provided as inputs",
            Measurements = measurements,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(output);
        return correct ? 0 : 1;
    }

    public static int ExportLucidInput(string path)
    {
        ComparisonCase[] cases = CreateCases().ToArray();
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(cases.Length * 2);
        foreach (ComparisonCase sample in cases)
        foreach ((int width, int height) in new[] { (128, 128), (1280, 720) })
        {
            TriangleHierarchy geometry = QuantizeColors(sample.CreateGeometry(width, height));
            byte[] name = System.Text.Encoding.UTF8.GetBytes(sample.Geometry);
            writer.Write(name.Length); writer.Write(name);
            writer.Write(sample.Models); writer.Write(sample.Alpha);
            writer.Write(width); writer.Write(height); writer.Write(geometry.Triangles.Length);
            writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(geometry.Triangles.AsSpan()));
            if (width == 128)
            {
                for (int row = 0; row < height; row++)
                for (int column = 0; column < width; column++)
                {
                    Vector3 reference = TransparencyEvaluator.CompositeReference(geometry,
                        new((column + .5f) / width * 2 - 1, 1 - (row + .5f) / height * 2), 1, new(.125f));
                    writer.Write(reference.X); writer.Write(reference.Y); writer.Write(reference.Z);
                }
            }
        }
        return 0;
    }

    private static TriangleHierarchy QuantizeColors(TriangleHierarchy geometry) => new(geometry.Triangles.Select(static triangle =>
        triangle with { Color = new(MathF.Round(triangle.Color.X * 255) / 255, MathF.Round(triangle.Color.Y * 255) / 255,
            MathF.Round(triangle.Color.Z * 255) / 255, MathF.Round(triangle.Color.W * 255) / 255) }).ToArray());

    private static IEnumerable<ComparisonCase> CreateCases() =>
        (from panels in new[] { 8, 32, 128 }
         from alpha in new[] { .1f, .5f, .9f }
         select new ComparisonCase("panels", panels, alpha,
             (width, height) => ResearchGeometry.CreatePanels(panels, alpha, width, height)))
        .Append(new("spheres", 8, .5f, (width, height) => ResearchGeometry.CreateSpheres(8, .5f, width, height)))
        .Append(new("shaded separated spheres", 8, .5f, (width, height) => ResearchGeometry.CreateShadedSpheres(8, .5f, width, height, .075f)))
        .Append(new("overlapping spheres", 8, .5f, (width, height) => ResearchGeometry.CreateSpheres(8, .5f, width, height, .025f)))
        .Append(new("shaded overlapping spheres", 8, .5f, (width, height) => ResearchGeometry.CreateShadedSpheres(8, .5f, width, height)));

    private static ImageError Compare(Vector4[] actual, Vector3[] reference)
    {
        double squared = 0, absolute = 0;
        Vector3 signed = Vector3.Zero;
        float maximum = 0;
        int nonFinite = 0;
        for (int index = 0; index < actual.Length; index++)
        {
            Vector3 value = new(actual[index].X, actual[index].Y, actual[index].Z);
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) { nonFinite++; continue; }
            Vector3 difference = value - reference[index];
            Vector3 magnitude = Vector3.Abs(difference);
            squared += difference.LengthSquared();
            absolute += magnitude.X + magnitude.Y + magnitude.Z;
            signed += difference;
            maximum = MathF.Max(maximum, MathF.Max(magnitude.X, MathF.Max(magnitude.Y, magnitude.Z)));
        }
        return new(Math.Sqrt(squared / (actual.Length * 3)), absolute / (actual.Length * 3), maximum, signed / actual.Length, nonFinite);
    }

    private static string ReadGpuClocks()
    {
        using Process process = Process.Start(new ProcessStartInfo("nvidia-smi",
            "--query-gpu=clocks.gr,clocks.mem,power.draw,temperature.gpu --format=csv,noheader,nounits")
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        string result = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return result;
    }

    private sealed record ComparisonCase(string Geometry, int Models, float Alpha, Func<int, int, TriangleHierarchy> CreateGeometry);
    private sealed record ComparisonMethod(string Name, Func<int, int, TriangleHierarchy, ITransparencyRenderer> Create);
    private readonly record struct ImageError(double Rmse, double MeanAbsolute, float Maximum, Vector3 MeanSigned, int NonFinitePixels);
}
