using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using Xunit.Abstractions;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class DefaultGameRayShadowTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("spheres")]
    [InlineData("sponza")]
    [InlineData("bistro")]
    public async Task ThreeSceneShadowsMatchHardwareRayTracing(string scene)
    {
        string engine = FindEngine();
        string? evidence = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_EVIDENCE");
        string directory = Path.Combine(evidence ?? Path.GetTempPath(), $"ray-shadow-{scene}-{Guid.NewGuid():N}");
        string content = Path.Combine(directory, "content");
        Directory.CreateDirectory(directory);
        try
        {
            bool spheres = scene == "spheres";
            string sourceContent = spheres ? Path.Combine(engine, "templates", "DefaultProject")
                : Path.Combine(engine, "samples", "ImportedScenes", "Apps", scene == "sponza" ? "Sponza" : "Bistro");
            Assert.True(Directory.Exists(Path.Combine(sourceContent, "Library", "AssetManifest")),
                $"Build the DefaultGame {scene} content before running the three-scene RT shadow regression.");
            if (spheres) await ShadowReferenceScene.BuildAsync(engine, content, "directional-grid");
            else CopyContent(sourceContent, content);
            await CaptureAsync(engine, content, Path.Combine(directory, "scene.bmp"));
            await BuildShadowMaskAsync(engine, sourceContent, content);
            await CaptureAsync(engine, content, Path.Combine(directory, "vsm-shadow.bmp"));
            string geometry = spheres ? Path.Combine(sourceContent, "assets", "Models", "ico_sphere.glb")
                : Path.Combine(sourceContent, "assets", "Imported", scene == "sponza" ? "Sponza" : "Bistro",
                    scene == "sponza" ? "Sponza.gltf" : "Bistro.glb");
            var rays = new RayShadowScene(geometry, spheres);
            var inputs = await ReadSceneInputsAsync(content);
            Vector3 camera = inputs.Camera.Position, target = inputs.Camera.Target;
            float fieldOfView = inputs.Camera.VerticalFieldOfView;
            int width = inputs.Width, height = inputs.Height;
            float near = inputs.Camera.NearPlane, far = inputs.Camera.FarPlane;
            Matrix4x4.Invert(Matrix4x4.CreateLookAt(camera, target, Vector3.UnitY) *
                Matrix4x4.CreatePerspectiveFieldOfView(fieldOfView, (float)width / height, near, far), out Matrix4x4 clipToWorld);
            uint[] reference = await RayShadowImage.TraceAsync(engine, rays, new()
            {
                ClipToWorld = clipToWorld, CameraPosition = camera, Width = (uint)width, Height = (uint)height,
                LightDirection = Vector3.Normalize(inputs.Light.Direction),
                LightKind = 0, NormalBias = .01f, NearPlane = near, FarPlane = far,
            }, vulkan: false);
            var comparison = Compare(directory, reference, width, height);
            await File.WriteAllTextAsync(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(new
            {
                Scene = scene, SourceCommit = Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_SOURCE"),
                Reference = "Hardware DXR inline RayQuery against source glTF BLAS/TLAS; bilinear-repeat alpha-mask intersections; source normals, tangent space and normal textures",
                Camera = new { Position = camera, Target = target, FieldOfView = fieldOfView, Near = near, Far = far },
                Light = inputs.Light,
                Conditions = "1280x720; D3D12 RTX hardware; static scene; 64 frames; fixed 1/60 s; VS/PS; TAA off; async compute off; synchronous; UI hidden",
                SourceGeometry = new { Meshes = rays.Meshes.Length, Instances = rays.Instances.Length,
                    Triangles = rays.Indices.Length / 3, AlphaMaskedGeometries = rays.Geometries.Count(value => value.AlphaCutoff >= 0) },
                Thresholds = new { EdgeExclusionPixels = 2, MaximumFalseShadowFraction = .02f,
                    MaximumMissedShadowFraction = .02f, MinimumComparedPixels = 512 },
                Comparison = comparison,
            }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            output.WriteLine($"Hardware RT shadow comparison: {directory}; {comparison}");
            Assert.True(comparison.ComparedPixels >= 512, "The scene must deliver visible receiver pixels for the RT comparison.");
            Assert.True(comparison.MismatchFraction <= .02f &&
                comparison.FalseShadowPixels <= comparison.LitPixels * .02f &&
                comparison.MissedShadowPixels <= comparison.ShadowedPixels * .02f,
                $"DefaultGame {scene} differs from hardware RT shadows: {comparison}. Evidence: {directory}");
        }
        finally
        {
            if (Directory.Exists(content)) Directory.Delete(content, recursive: true);
            if (evidence is null) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(SceneCamera Camera, DirectionalLight Light, int Width, int Height)> ReadSceneInputsAsync(string content)
    {
        AssetManifest manifest = AssetManifest.Load(Path.Combine(content, "Library", "AssetManifest"));
        await using var assets = new AssetLoader(new LooseAssetStorage(content, manifest));
        using ApplicationSettingsAsset settings = await ApplicationSettingsAsset.OpenAsync(assets,
            new(manifest.List(ApplicationSettingsAsset.FileType).Single().AssetGuid));
        ApplicationSettingsBlock values = settings.GetBlock<ApplicationSettingsBlock>();
        using WorldAsset file = await WorldAsset.OpenAsync(assets, values.Scene);
        using World world = await file.LoadAsync(SceneSerialization.CreateRegistry());
        Entity camera = Entities(new QueryDefinitionBuilder().Read<SceneCamera>()).Single();
        Entity light = Entities(new QueryDefinitionBuilder().Read<DirectionalLight>()).First();
        return (world.Read<SceneCamera>(camera), world.Read<DirectionalLight>(light), values.Width, values.Height);

        Entity[] Entities(QueryDefinitionBuilder definition)
        {
            QueryHandle query = world.Query(definition);
            var entities = new List<Entity>();
            try
            {
                world.ExecuteQuery(query, ref entities, static (QueryCursor rows, ref List<Entity> values) =>
                {
                    foreach (var row in rows.Rows) values.Add(row.Entity);
                });
                return entities.ToArray();
            }
            finally { world.ReleaseQuery(query); }
        }
    }

    private async Task CaptureAsync(string engine, string content, string image)
    {
        string executable = Path.Combine(engine, "templates", "DefaultProject", "Game.App", "bin", "Release", "net10.0", "Game.App.exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        string[] arguments = ["--test-scene", content, "--frames", "64", "--static-scene", "--hidden-window",
            "--fixed-step", "0.016666667", "--no-vsync", "--synchronous-frames", "--no-debug-ui", "--no-taa",
            "--no-async-compute", "--hardware-raster", "--no-mesh-shaders", "--vsm", "--frame-output", image];
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        string command = executable + " " + string.Join(" ", arguments.Select(value => '"' + value + '"'));
        output.WriteLine(command);
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException(command);
        }
        string log = $"{command}\nExitCode: {process.ExitCode}\n{await stdout}\n{await stderr}";
        await File.WriteAllTextAsync(Path.ChangeExtension(image, ".log"), log);
        Assert.True(process.ExitCode == 0, log);
        Assert.True(File.Exists(image), log);
    }

    private static void CopyContent(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "assets"), "*.asset", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(source, "Library", "AssetManifest"), "*", SearchOption.AllDirectories)))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static async Task BuildShadowMaskAsync(string engine, string sourceContent, string content)
    {
        string shaders = Path.Combine(content, "assets", "Shaders");
        foreach (string source in Directory.EnumerateFiles(Path.Combine(sourceContent, "assets", "Shaders"), "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".slang") || path.EndsWith(".shader")))
        {
            string target = Path.Combine(shaders, Path.GetRelativePath(Path.Combine(sourceContent, "assets", "Shaders"), source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        foreach (string material in new[] { "standard_material.slang", "scene_material.slang" })
        {
            string path = Path.Combine(shaders, material);
            if (!File.Exists(path)) continue;
            string text = File.ReadAllText(path).Replace("\r\n", "\n");
            text = text.Replace("            if (dot(mappedNormal, -light.Direction) <= 0.0)\n                continue;",
                "            if (dot(mappedNormal, -light.Direction) < 0.15)\n            {\n                outputColor[ctx.pixelCoord] = float4(0,0,0,1);\n                return;\n            }");
            text = text.Replace("outputColor[ctx.pixelCoord] = float4(finalColor, 1.0);",
                "outputColor[ctx.pixelCoord] = float4(0,0,0,1);");
            text = text.Replace("            finalColor += EvaluateDirectionalLight(",
                "            outputColor[ctx.pixelCoord] = float4(shadow, 1.0, 0.0, 1.0);\n            return;\n            finalColor += EvaluateDirectionalLight(");
            File.WriteAllText(path, text);
        }
        var project = new AssetProject(content, [], sourceFormats: [new ShaderSourceFormat([Path.Combine(engine, "assets", "Shaders")])]);
        foreach (string definition in Directory.EnumerateFiles(shaders, "*.shader")
            .Where(path => Path.GetFileName(path) == "standard.shader" || Path.GetFileName(path).StartsWith("scene_", StringComparison.Ordinal)))
            await project.BuildAsync(definition);
    }

    private static ShadowReferenceComparison Compare(string directory, uint[] reference, int width, int height)
    {
        byte[] bitmap = File.ReadAllBytes(Path.Combine(directory, "vsm-shadow.bmp"));
        int data = BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(10));
        int signedHeight = BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(22));
        Assert.Equal(width, BinaryPrimitives.ReadInt32LittleEndian(bitmap.AsSpan(18)));
        Assert.Equal(height, Math.Abs(signedHeight));
        var actual = new int[width * height];
        Array.Fill(actual, -1);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = data + ((signedHeight < 0 ? y : height - 1 - y) * width + x) * 4;
            if (bitmap[offset + 1] >= 128) actual[y * width + x] = bitmap[offset + 2] >= 128 ? 1 : 0;
        }
        int shadowed = 0, lit = 0, missed = 0, falseShadow = 0;
        var difference = new byte[actual.Length];
        using var csv = new StreamWriter(Path.Combine(directory, "ray-comparison.csv"));
        csv.WriteLine("x,y,rayVisibility,vsmVisibility,mismatch");
        for (int y = 2; y < height - 2; y++)
        for (int x = 2; x < width - 2; x++)
        {
            int pixel = y * width + x;
            if (reference[pixel] > 1 || actual[pixel] < 0) continue;
            bool interior = true;
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                interior &= reference[pixel + dy * width + dx] == reference[pixel] && actual[pixel + dy * width + dx] >= 0;
            if (!interior) continue;
            if (reference[pixel] == 0) shadowed++;
            else lit++;
            bool mismatch = actual[pixel] != reference[pixel];
            if (mismatch)
            {
                if (reference[pixel] == 0) missed++;
                else falseShadow++;
                difference[pixel] = 255;
            }
            csv.WriteLine($"{x},{y},{reference[pixel]},{actual[pixel]},{(mismatch ? 1 : 0)}");
        }
        WriteBitmap(Path.Combine(directory, "rt-shadow.bmp"), width, height,
            reference.Select(value => value > 1 ? (byte)127 : (byte)(value * 255)).ToArray());
        WriteBitmap(Path.Combine(directory, "shadow-error.bmp"), width, height, difference);
        int compared = lit + shadowed, errors = missed + falseShadow;
        float fraction = compared == 0 ? 1 : (float)errors / compared;
        return new(shadowed, lit, errors, compared, fraction, fraction, errors == 0 ? 0 : 1, missed, falseShadow);
    }

    private static void WriteBitmap(string path, int width, int height, byte[] pixels)
    {
        byte[] bytes = new byte[54 + pixels.Length * 4];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 32);
        for (int pixel = 0; pixel < pixels.Length; pixel++)
        {
            bytes[54 + pixel * 4] = bytes[55 + pixel * 4] = bytes[56 + pixel * 4] = pixels[pixel];
            bytes[57 + pixel * 4] = 255;
        }
        File.WriteAllBytes(path, bytes);
    }

    private static string FindEngine()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Select a SomeEngine checkout for the DefaultGame RT shadow regression.");
    }
}
