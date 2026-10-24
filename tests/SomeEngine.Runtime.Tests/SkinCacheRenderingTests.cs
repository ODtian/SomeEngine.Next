using System.Numerics;
using System.Text.Json;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Resources;
using Xunit.Abstractions;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    private readonly ITestOutputHelper _output;
    public SkinningRenderingTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Cached_skinning_matches_inline_current_and_previous_poses_for_two_characters_and_materials()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        await content.UseTwoMaterialsAsync();
        SceneImage inline = await CaptureAnimatedAsync(content);
        await content.SetCachedShaderAsync();
        SceneImage cached = await CaptureAnimatedAsync(content);
        SceneImages.AssertEqual(inline, cached, tolerance: 2);
        int blue = 0, other = 0;
        for (int pixel = 0; pixel < inline.Pixels.Length; pixel += 4)
        {
            if (inline.Pixels.Span[pixel + 2] > inline.Pixels.Span[pixel] + 20) blue++;
            else if (inline.Pixels.Span[pixel] > 32) other++;
        }
        Assert.True(blue > 1000 && other > 1000);
    }

    [Fact]
    public async Task Skinning_paths_report_cache_raster_and_complete_frame_costs_for_the_same_animated_workload()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteMeshesAsync(EncodeFloatInfluences);
        await content.UseTwoMaterialsAsync();
        await content.SetOutputSizeAsync(1920, 1080);
        ApplicationFrameTiming[] inline = await MeasurePanelAnimationAsync(content);
        await content.SetCachedShaderAsync();
        ApplicationFrameTiming[] cached = await MeasurePanelAnimationAsync(content);
        Assert.Equal(200, inline.Length);
        Assert.Equal(200, cached.Length);
        Assert.True(cached.Average(frame => GroupMs(frame, ClusterPipelineSystem.DeformationEvaluationTimeGroup)) > 0);
        Assert.Equal(0, inline.Sum(frame => GroupMs(frame, ClusterPipelineSystem.DeformationEvaluationTimeGroup)));
        Assert.True(inline.Average(frame => GroupMs(frame, ClusterPipelineSystem.RasterTimeGroup)) > 0);
        Assert.True(cached.Average(frame => GroupMs(frame, ClusterPipelineSystem.RasterTimeGroup)) > 0);
        _output.WriteLine("Workload: 1920x1080, 4 panels/16 vertices/8 triangles, 2 independent 2-bone characters, "
            + "2 materials, current+previous rows, CPU pose at 60 Hz; fixed camera/LOD=1; D3D12 hardware, async compute, "
            + "asynchronous game/render frames, VSync/TAA/VSM/UI disabled; 200 frames, warmup=64, samples=128; "
            + "4 timestamps/executed pass, same-queue resolves on both paths.");
        WriteTimes("inline", inline);
        WriteTimes("cache", cached);
    }

    private static double GroupMs(ApplicationFrameTiming frame, string group) =>
        frame.Gpu.Passes.Where(pass => pass.Group == group).Sum(pass => pass.CommandsMs);

    private void WriteTimes(string path, ApplicationFrameTiming[] frames)
    {
        ApplicationFrameTiming[] sample = frames.Where(frame => frame.Frame >= 64 && frame.Frame < 192).ToArray();
        _output.WriteLine(JsonSerializer.Serialize(new
        {
            Path = path,
            Samples = sample.Length,
            CpuMs = Distribution(sample.Select(frame => frame.CpuMs)),
            GameMs = Distribution(sample.Select(frame => frame.GameMs)),
            AnimationMs = Distribution(sample.Select(frame => frame.AnimationMs)),
            ExtractionMs = Distribution(sample.Select(frame => frame.ExtractionMs)),
            GpuMs = Distribution(sample.Select(frame => frame.Gpu.ElapsedMs)),
            CacheAllocationMs = Distribution(sample.Select(frame => GroupMs(frame, ClusterPipelineSystem.DeformationTimeGroup))),
            CacheEvaluationMs = Distribution(sample.Select(frame => GroupMs(frame, ClusterPipelineSystem.DeformationEvaluationTimeGroup))),
            RasterMs = Distribution(sample.Select(frame => GroupMs(frame, ClusterPipelineSystem.RasterTimeGroup))),
            GpuPasses = Distribution(sample.Select(frame => (double)frame.Gpu.Passes.Count)),
            TimestampQueries = Distribution(sample.Select(frame => frame.Gpu.Passes.Count * 4.0)),
            Queues = sample.SelectMany(frame => frame.Gpu.Passes).Select(pass => $"{pass.QueueType}:{pass.QueueIndex}").Distinct().ToArray(),
        }));
    }

    private static TimeDistribution Distribution(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        return new(ordered[0], ordered.Average(),
            (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2,
            ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], ordered[^1]);
    }

    private readonly record struct TimeDistribution(double Min, double Mean, double Median, double P95, double Max);

    private static async Task<ApplicationFrameTiming[]> MeasurePanelAnimationAsync(SkinContent content)
    {
        var skeleton = new Skeleton([-1, -1],
            [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Translation = Vector3.UnitY, Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2]);
        var storage = new PoseStorage();
        using SkeletonInstance first = storage.CreateInstance(skeleton);
        using SkeletonInstance second = storage.CreateInstance(skeleton);
        var clip = new AnimationClip(skeleton, 1, ClipSampler.Create<PanelClip, PanelSampler>(new()), [], -1);
        var times = new List<ApplicationFrameTiming>();
        int width = 0, height = 0;
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                PoseAnimation.AddSystems(frame.Systems.Animation);
                Entity firstTarget = PoseAnimation.CreateTarget(frame.World, first);
                Entity secondTarget = PoseAnimation.CreateTarget(frame.World, second);
                using var builder = new AnimationBuilder(frame.World);
                builder.Leaf<ClipParameters, ClipLeaf>(Entity.Null, new(clip, new(0), Loop: true));
                using CompiledGraph graph = builder.Compile();
                graph.Play(frame.World, firstTarget);
                PropertyAnimation.SetSpeed(frame.World, graph.Play(frame.World, secondTarget), .5f);
                foreach (Entity entity in frame.Scene.MeshEntities)
                {
                    bool right = frame.World.Read<WorldTransform>(entity).Qvvs.Position.X > 0;
                    frame.World.Add(entity, new MeshSkin(right ? second : first, Binding(right)));
                }
            },
            UpdateFrame = frame => (width, height) = (frame.Width, frame.Height),
            FrameMeasured = times.Add,
        };
        ApplicationResult result = await Application.RunAsync(
            ["--hardware-adapter", "--graphics-backend", "d3d12", "--hidden-window", "--frames", "200",
             "--static-scene", "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--async-compute",
             "--fixed-step", "0.016666667"], application);
        Assert.True(result.Succeeded, $"{result.Failure}: {result.Error}");
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
        return times.ToArray();
    }

    private sealed partial class SkinContent
    {
        public async Task SetOutputSizeAsync(int width, int height)
        {
            AssetEntry entry = project.Manifest.List(ApplicationSettingsAsset.FileType).Single();
            string path = Path.Combine(project.RootPath, entry.Path);
            using ApplicationSettingsAsset application = await ApplicationSettingsAsset.OpenAsync(path);
            ApplicationSettingsBlock settings = await application.GetBlockAsync<ApplicationSettingsBlock>();
            settings.Width = width;
            settings.Height = height;
            project.RegisterAsset(await application.SaveAsync(path));
        }

        public async Task SetCachedShaderAsync()
        {
            string evaluate = SkinEvaluator.Replace("USER_READER", FloatReader)
                .Replace("struct PanelSkin : IVertexEvaluate", "struct PanelSkin : IVertexDeformCache")
                .Replace("float3 getPosition(Vertex value)", CacheEncoding + "\nfloat3 getPosition(Vertex value)");
            string cached = shader
                .Replace("typedef StaticVertexEval VertexEvaluate;", "#include \"vertex_deform_cache.slang\"\n" + evaluate
                    + "\ntypedef PanelSkin DeformEvaluate;\ntypedef CachedVertexEval<PanelSkin> VertexEvaluate;")
                .Replace("typedef StaticPositionEval RasterVertexEvaluate;", "typedef VertexEvaluate RasterVertexEvaluate;")
                .Replace("#define CLUSTER_POSITION_RASTER 1", "#define CLUSTER_POSITION_RASTER 1\n"
                    + "#define CLUSTER_VERTEX_ENCODING \"someengine.cluster.positions.uint16.v1+someengine.tests.skin.float.v1\"\n"
                    + "#define CLUSTER_DEFORM_VERTEX_ENCODING CLUSTER_VERTEX_ENCODING");
            await File.WriteAllTextAsync(ShaderPath, cached + "\n#include \"cluster_deform.slang\"\n");
            await project.BuildAsync(ShaderPath.Replace(".slang", ".shader"));
        }

        public async Task UseTwoMaterialsAsync()
        {
            AssetEntry standard = project.Manifest.List(MaterialAsset.FileType).Single(item => item.Name == "Standard");
            using MaterialAsset original = await MaterialAsset.OpenAsync(Path.Combine(project.RootPath, standard.Path));
            using MaterialAsset blue = original.CreateCopy();
            blue.Name = "Blue";
            MaterialBlock parameters = await blue.GetBlockAsync<MaterialBlock>();
            parameters.SetParameter(new("BaseColorTint", MaterialParameterKind.Vector4) { Vector4 = new(.2f, .5f, 1, 1) });
            blue.SetBlock(parameters);
            project.RegisterAsset(await blue.SaveAsync(Path.Combine(project.RootPath, "assets/Materials/blue.material.asset")));
            for (int index = 2; index < 4; index++)
            {
                string path = Path.Combine(project.RootPath, $"assets/Models/panel-{index}.mesh.asset");
                using MeshAsset mesh = await MeshAsset.OpenAsync(path);
                mesh.SetBlock(new MeshMaterialsBlock { Materials = [new(blue.Id)] });
                project.RegisterAsset(await mesh.SaveAsync(path));
            }
        }
    }

    // This example material stores two StandardVertex values in 128 bytes per vertex.
    private const string CacheEncoding = """
        uint getCacheByteSize(uint count) { return count * 128u; }
        uint getCacheByteAlignment() { return 16u; }
        void writeCache(RWByteAddressBuffer cache, uint address, uint count, uint index, Vertex current, Vertex previous)
        {
            cache.Store<StandardVertex>(address + index * 128u, current);
            cache.Store<StandardVertex>(address + index * 128u + 64u, previous);
        }
        Vertex readCache(ByteAddressBuffer cache, uint address, uint count, VertexEvalContext context)
        {
            return cache.Load<StandardVertex>(address + context.localVertexIndex * 128u + context.IsPreviousFrame * 64u);
        }
        """;
}
