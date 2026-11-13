#define SOMEENGINE_DEVELOPMENT
using System.Diagnostics;
using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class ShaderSourceReloadTests(SceneContentFixture content)
{
    [Fact(Timeout = 240_000)]
    public async Task MaterialSourceEditsChangeItsColorAndCompileFailuresKeepTheLastSuccessfulMaterial()
    {
        DirectoryInfo engine = new(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(engine.FullName, "SomeEngine.slnx"))) engine = engine.Parent!;
        string sharedShaders = Path.Combine(engine.FullName, "samples", "DefaultGame", "assets", "Shaders");
        Guid shaderId = Guid.NewGuid();
        string directory = Path.Combine(content.DirectoryPath, "assets", "Shaders", "Reload-" + shaderId.ToString("N"));
        string source = Path.Combine(directory, "ReloadMaterial.slang");
        string definition = Path.Combine(directory, "ReloadMaterial.shader");
        Directory.CreateDirectory(directory);
        try
        {
            string materialSource = File.ReadAllText(Path.Combine(sharedShaders, "unlit.slang"))
                .Replace("import unlit_material;", "import unlit_material;\n" + MaterialSurfaceSource)
                .Replace("UnlitMaterial Surface;", "ReloadMaterialSurface Surface;");
            await File.WriteAllTextAsync(source, materialSource);
            using (ShaderAsset template = await ShaderAsset.OpenSourceAsync(Path.Combine(sharedShaders, "unlit.shader")))
            using (ShaderAsset asset = template.CreateCopy(shaderId))
            {
                asset.Name = "Reload material";
                ShaderSourceBlock inputs = asset.GetBlock<ShaderSourceBlock>();
                inputs.SourceNames = ["ReloadMaterial.slang"];
                await asset.SaveSourceAsync(definition);
            }
            var project = new AssetProject(content.DirectoryPath, [],
                shaderIncludeDirectories: [sharedShaders, Path.Combine(engine.FullName, "assets", "Shaders")],
                shaderTargets: [SlangCompileTarget.Dxil]);
            await project.BuildAsync(definition);

            var frames = new List<int>();
            var greenFramesAfterFailure = new List<int>();
            SceneImage? red = null, green = null;
            Shader? firstShader = null, greenShader = null;
            UnlitMaterial? material = null;
            Exception? compileFailure = null;
            Stopwatch? editing = null;
            ReloadStep step = ReloadStep.Red;
            var scene = new ApplicationScene(content.DirectoryPath)
            {
                Options = ApplicationOptions.Parse(["--graphics-backend", "d3d12", "--hidden-window",
                    "--synchronous-frames", "--static-scene", "--hardware-raster", "--no-mesh-shaders",
                    "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm"]),
                CreateFrameFeatures = rendering =>
                {
                    firstShader = rendering.ShaderLibrary.GetAsync(rendering.Files, new AssetId<ShaderAsset>(shaderId))
                        .AsTask().GetAwaiter().GetResult();
                    return [];
                },
                ShaderBuildFailed = error => compileFailure = error,
            };
            scene.WatchShaderSources(content.DirectoryPath);
            scene.UpdateFrame = frame =>
            {
                frames.Add(frame.Index);
                if (editing?.Elapsed > TimeSpan.FromSeconds(90))
                    scene.Options = scene.Options with { FrameLimit = frame.Index + 1 };
                if (firstShader is null) return;
                if (material is null)
                {
                    var entity = frame.Scene.MeshEntities.Single(entity => frame.Scene.GetMaterial(entity).Name == "red sphere");
                    material = (UnlitMaterial)frame.Scene.GetMaterial(entity);
                    material.Shader = firstShader;
                    frame.Scene.ReplaceMaterial(entity, material);
                    frame.Scene.SetEntityTransform(entity, new(new Vector3(0, 0, 6), Quaternion.Identity));
                    foreach (var other in frame.Scene.MeshEntities.Where(other => other != entity).ToArray())
                        frame.Scene.DeleteEntity(other);
                }
                if (frame.Index < 3) return;
                frame.Scene.CaptureImage(image =>
                {
                    int left = image.Width / 2 - 2, top = image.Height / 2 - 2;
                    switch (step)
                    {
                        case ReloadStep.Red:
                            SceneImages.AssertColorRegion(image, left, top, 4, 4, 255, 0, 0);
                            red = image;
                            editing = Stopwatch.StartNew();
                            File.WriteAllText(source, materialSource.Replace("float4(1024, 0, 0, 1)", "float4(0, 1024, 0, 1)"));
                            step = ReloadStep.Green;
                            break;
                        case ReloadStep.Green:
                            if (ReferenceEquals(material.Shader, firstShader))
                            {
                                SceneImages.AssertColorRegion(image, left, top, 4, 4, 255, 0, 0);
                                break;
                            }
                            SceneImages.AssertColorRegion(image, left, top, 4, 4, 0, 255, 0);
                            green = image;
                            greenShader = material.Shader;
                            File.WriteAllText(source, "struct ReloadMaterialSurface {\n");
                            step = ReloadStep.CompileFailure;
                            break;
                        case ReloadStep.CompileFailure:
                            SceneImages.AssertColorRegion(image, left, top, 4, 4, 0, 255, 0);
                            if (compileFailure is null) break;
                            SceneImages.AssertEqual(green!, image);
                            greenFramesAfterFailure.Add(image.Frame);
                            step = ReloadStep.LastSuccessfulColor;
                            break;
                        case ReloadStep.LastSuccessfulColor:
                            SceneImages.AssertColorRegion(image, left, top, 4, 4, 0, 255, 0);
                            SceneImages.AssertEqual(green!, image);
                            greenFramesAfterFailure.Add(image.Frame);
                            if (greenFramesAfterFailure.Count == 3)
                            {
                                scene.Options = scene.Options with { FrameLimit = image.Frame + 1 };
                                step = ReloadStep.Complete;
                            }
                            break;
                    }
                });
            };

            ApplicationResult result = await Application.RunAsync(["--hardware-adapter"], scene);

            Assert.True(result.Succeeded, result.Error?.ToString());
            Assert.Equal(ReloadStep.Complete, step);
            Assert.Equal(Enumerable.Range(0, frames.Count), frames);
            Assert.True(green!.Frame > red!.Frame);
            Assert.NotSame(firstShader, greenShader);
            Assert.Same(greenShader, material!.Shader);
            Assert.IsType<InvalidDataException>(compileFailure);
            Assert.Equal(3, greenFramesAfterFailure.Count);
            Assert.Equal(Enumerable.Range(greenFramesAfterFailure[0], 3), greenFramesAfterFailure);
        }
        finally
        {
            try
            {
                var project = new AssetProject(content.DirectoryPath, []);
                if (project.Manifest.TryGetAsset(shaderId, out _)) await project.DeleteAssetAsync(shaderId);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task ShaderSourceEditsUpdateTheRunningImageAndCompileFailuresKeepTheLastSuccessfulColor()
    {
        Guid shaderId = Guid.NewGuid();
        string directory = Path.Combine(content.DirectoryPath, "assets", "Shaders", "Reload-" + shaderId.ToString("N"));
        string source = Path.Combine(directory, "Color.slang");
        string definition = Path.Combine(directory, "Color.shader");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(source, RedSource);
            using (var asset = new ShaderAsset(shaderId) { Name = "Reload color" })
            {
                asset.GetBlock<ShaderSourceBlock>().SourceNames = ["Color.slang"];
                await asset.SaveSourceAsync(definition);
            }
            var project = new AssetProject(content.DirectoryPath, [], shaderTargets: [SlangCompileTarget.Dxil]);
            await project.BuildAsync(definition);

            var frames = new List<int>();
            var greenFramesAfterFailure = new List<int>();
            SceneImage? red = null, green = null;
            Exception? compileFailure = null;
            ReloadColorFeature? feature = null;
            Stopwatch? editing = null;
            ReloadStep step = ReloadStep.Red;
            var scene = new ApplicationScene(content.DirectoryPath)
            {
                Options = ApplicationOptions.Parse(["--graphics-backend", "d3d12", "--hidden-window",
                    "--synchronous-frames", "--static-scene", "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm"]),
                CreateColorFeature = rendering => feature = new ReloadColorFeature(rendering, new(shaderId)),
                ShaderBuildFailed = error => compileFailure = error,
            };
            scene.WatchShaderSources(content.DirectoryPath);
            scene.UpdateFrame = frame =>
            {
                frames.Add(frame.Index);
                if (editing?.Elapsed > TimeSpan.FromSeconds(60))
                    scene.Options = scene.Options with { FrameLimit = frame.Index + 1 };
                frame.Scene.CaptureImage(image =>
                {
                    int left = image.Width / 2 - 8, top = image.Height / 2 - 8;
                    switch (step)
                    {
                        case ReloadStep.Red:
                            SceneImages.AssertColorRegion(image, left, top, 16, 16, 255, 0, 0);
                            red = image;
                            editing = Stopwatch.StartNew();
                            File.WriteAllText(source, RedSource.Replace("float4(1024, 0, 0, 1)", "float4(0, 1024, 0, 1)"));
                            step = ReloadStep.Green;
                            break;
                        case ReloadStep.Green:
                            if (feature!.Replacements == 0)
                            {
                                SceneImages.AssertColorRegion(image, left, top, 16, 16, 255, 0, 0);
                                break;
                            }
                            SceneImages.AssertColorRegion(image, left, top, 16, 16, 0, 255, 0);
                            green = image;
                            File.WriteAllText(source, "void Main( {\n");
                            step = ReloadStep.CompileFailure;
                            break;
                        case ReloadStep.CompileFailure:
                            SceneImages.AssertColorRegion(image, left, top, 16, 16, 0, 255, 0);
                            if (compileFailure is null) break;
                            greenFramesAfterFailure.Add(image.Frame);
                            step = ReloadStep.LastSuccessfulColor;
                            break;
                        case ReloadStep.LastSuccessfulColor:
                            SceneImages.AssertColorRegion(image, left, top, 16, 16, 0, 255, 0);
                            greenFramesAfterFailure.Add(image.Frame);
                            if (greenFramesAfterFailure.Count == 3)
                            {
                                scene.Options = scene.Options with { FrameLimit = image.Frame + 1 };
                                step = ReloadStep.Complete;
                            }
                            break;
                    }
                });
            };

            ApplicationResult result = await Application.RunAsync(["--warp"], scene);

            Assert.True(result.Succeeded, result.Error?.ToString());
            Assert.Equal(ReloadStep.Complete, step);
            Assert.Equal(Enumerable.Range(0, frames.Count), frames);
            Assert.True(green!.Frame > red!.Frame);
            Assert.Equal(1, feature!.Replacements);
            Assert.IsType<InvalidDataException>(compileFailure);
            Assert.Equal(3, greenFramesAfterFailure.Count);
            Assert.Equal(Enumerable.Range(greenFramesAfterFailure[0], 3), greenFramesAfterFailure);
        }
        finally
        {
            try
            {
                var project = new AssetProject(content.DirectoryPath, []);
                if (project.Manifest.TryGetAsset(shaderId, out _)) await project.DeleteAssetAsync(shaderId);
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private enum ReloadStep : byte { Red, Green, CompileFailure, LastSuccessfulColor, Complete }

    private sealed class ReloadColorFeature : IApplicationColorFeature
    {
        private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
        private readonly RenderPipelines _pipelines;
        private readonly LinkedComputePipeline _pipeline;
        private readonly GraphTextureId _color;

        internal ReloadColorFeature(ApplicationRendering rendering, AssetId<ShaderAsset> shaderId)
        {
            _graph = rendering.Graph;
            _pipelines = rendering.Pipelines;
            Shader shader = rendering.ShaderLibrary.GetAsync(rendering.Files, shaderId).AsTask().GetAwaiter().GetResult();
            _pipeline = LinkedComputePipeline.Create(_pipelines, shader, "Main", "Reload color");
            using RenderGraphEdit edit = _graph.BeginEdit();
            _color = edit.CreatePersistentTexture(new(TextureDimension.Texture2D, (uint)rendering.Width,
                (uint)rendering.Height, 1, 1, rendering.ViewCount, 1, Format.R16G16B16A16Float,
                TextureUsages.Storage | TextureUsages.Sampled | TextureUsages.CopySource, label: "Reload color"));
            GraphTextureUavId output = edit.CreateTextureUav(_color, dimension: TextureViewDimension.Texture2DArray);
            var groups = new DispatchArguments(((uint)rendering.Width + 7u) / 8u,
                ((uint)rendering.Height + 7u) / 8u, rendering.ViewCount);
            GraphParameterResourceBinding[] bindings = [GraphParameterResourceBinding.StorageTexture(output,
                PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete)];
            CompletePass = edit.AddComputeFramePass("Write reload color", PassQueueSelection.AnyOfType(QueueType.Graphics),
                groups, new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                _pipeline.Pipeline, _pipeline.ParameterLayout, bindings,
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data),
                rendering.Region);
            edit.Commit();
        }

        public GraphPassId CompletePass { get; }
        internal int Replacements { get; private set; }
        public void ReplaceShader(Shader previous, Shader next)
        {
            if (!_pipeline.ReplaceShader(_pipelines, previous, next)) return;
            using RenderGraphEdit edit = _graph.BeginEdit();
            PassDefinition pass = edit.GetPass(CompletePass);
            pass.SetPipeline(_pipeline.Pipeline);
            pass.SetParameterBlock(_pipeline.ParameterLayout);
            edit.Commit();
            Replacements++;
        }
        public void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady) => graph.OrderAfter(CompletePass, colorReady);
        public GraphTextureId Record(ref RenderGraphFrame graph, GraphTextureId color, GraphTextureId depth) => _color;
        public void Dispose()
        {
            if (!_graph.IsDisposed && !_graph.IsLost)
            {
                using RenderGraphEdit edit = _graph.BeginEdit();
                edit.Remove(CompletePass);
                edit.Remove(_color);
                edit.Commit();
            }
            _pipeline.Dispose();
        }
    }

    private const string MaterialSurfaceSource = """
        struct ReloadMaterialSurface : ISurfaceEvaluate<SurfaceEval, Triangle>, ITransparentSurfaceEvaluate<SurfaceEval, Triangle>
        {
            Texture2D AlbedoMap;
            SamplerState MaterialSampler;
            float4 color() { return float4(1024, 0, 0, 1); }
            void evaluateSurface<TOutput : ISurfaceOutput>(SurfacePixelInput input, SurfaceEval surface,
                float3 bary, Triangle triangle, TOutput output)
            { output.Write(input.PixelCoord, color()); }
            void evaluateDebug<TOutput : ISurfaceOutput>(uint mode, SurfacePixelInput input, SurfaceEval surface,
                float3 bary, Triangle triangle, TOutput output)
            { output.Write(input.PixelCoord, color()); }
            float4 evaluateTransparency(uint mode, SurfacePixelInput input, SurfaceEval surface, float3 bary, Triangle triangle)
            { return color(); }
            void markShadowPages(SurfacePixelInput input, SurfaceEval surface, float3 bary, Triangle triangle) {}
        };
        """;

    private const string RedSource = """
        RWTexture2DArray<float4> Color;
        [shader("compute")] [numthreads(8, 8, 1)]
        void Main(uint3 pixel : SV_DispatchThreadID)
        {
            uint width, height, layers;
            Color.GetDimensions(width, height, layers);
            if (pixel.x < width && pixel.y < height && pixel.z < layers)
                Color[pixel] = float4(1024, 0, 0, 1);
        }
        """;
}
