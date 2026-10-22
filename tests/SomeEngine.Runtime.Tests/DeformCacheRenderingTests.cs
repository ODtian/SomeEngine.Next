using Game.Build;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using System.Numerics;

namespace SomeEngine.Runtime.Tests;

public sealed class DeformCacheRenderingTests
{
    [Fact]
    public async Task User_encoded_deformation_matches_inline_geometry_in_the_application_image()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "SomeEngine.slnx"))) root = Path.GetDirectoryName(root)!;
        string content = Path.Combine(Path.GetTempPath(), $"SomeEngine-Deform-{Guid.NewGuid():N}");
        Directory.CreateDirectory(content);
        try
        {
            string source = Path.Combine(root, "templates", "DefaultProject");
            foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "assets"), "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(content, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            Directory.CreateDirectory(Path.Combine(content, "Game.Build"));
            File.Copy(Path.Combine(source, "Game.Build", "Game.Build.csproj"), Path.Combine(content, "Game.Build", "Game.Build.csproj"));
            string shaderPath = Path.Combine(content, "assets", "Shaders", "standard.slang");
            string shader = await File.ReadAllTextAsync(shaderPath);
            shader = shader.Replace("typedef StaticVertexEval VertexEvaluate;", UserDeformation + "\ntypedef CachedVertexEval<UserDeformation> VertexEvaluate;\ntypedef UserDeformation DeformEvaluate;");
            shader = shader.Replace("typedef StaticPositionEval RasterVertexEvaluate;", "typedef VertexEvaluate RasterVertexEvaluate;");
            shader += "\n#include \"cluster_deform.slang\"\n";
            await File.WriteAllTextAsync(shaderPath, shader);
            await DefaultContent.BuildAsync(content, root);
            var project = new AssetProject(content, [], sourceFormats: [new ShaderSourceFormat([Path.Combine(root, "assets", "Shaders")])]);
            AssetEntry sceneEntry = project.Manifest.List(WorldAsset.FileType).Single();
            using (var world = new World())
            {
                var sphere = world.CreateEntity(new WorldTransform { Qvvs = new(new(0, 0, 4), Quaternion.Identity) });
                world.Add(sphere, new MeshSource { Mesh = new(project.Manifest.List(MeshAsset.FileType).Single().AssetGuid), BoundsExpansion = .3f });
                world.CreateEntity(new SceneCamera { Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
                    VerticalFieldOfView = 1f, NearPlane = .1f, FarPlane = 100f });
                world.CreateEntity(new DirectionalLight(new(.35f, -.85f, .4f), Vector3.One, 3));
                using var scene = new WorldAsset(sceneEntry.AssetGuid) { Name = "Deform cache" };
                scene.WriteWorld(world, SceneSerialization.CreateRegistry());
                project.RegisterAsset(await scene.SaveAsync(Path.Combine(content, sceneEntry.Path)));
            }
            string cached = Path.Combine(content, "cached.bmp");
            Assert.Equal(0, await Application.RunAsync([
                "--content-directory", content, "--frames", "4", "--hidden-window", "--no-debug-ui",
                "--static-scene", "--no-taa", "--no-vsm", "--synchronous-frames", "--fixed-step", "0.016666667",
                "--frame-output", cached]));

            await File.WriteAllTextAsync(shaderPath, shader.Replace("typedef CachedVertexEval<UserDeformation> VertexEvaluate;", "typedef UserDeformation VertexEvaluate;"));
            await project.BuildAsync(shaderPath.Replace(".slang", ".shader"));
            string inline = Path.Combine(content, "inline.bmp");
            Assert.Equal(0, await Application.RunAsync([
                "--content-directory", content, "--frames", "4", "--hidden-window", "--no-debug-ui",
                "--static-scene", "--no-taa", "--no-vsm", "--synchronous-frames", "--fixed-step", "0.016666667",
                "--frame-output", inline]));
            Assert.Equal(await File.ReadAllBytesAsync(inline), await File.ReadAllBytesAsync(cached));
        }
        finally { Directory.Delete(content, recursive: true); }
    }

    private const string UserDeformation = """
        #include "vertex_deform_cache.slang"
        struct UserDeformation : IVertexDeformCache
        {
            typedef StandardVertex Vertex;
            Vertex evaluate(VertexEvalContext context)
            {
                WaveVertexEval wave;
                return wave.evaluate(context);
            }
            float3 getPosition(Vertex value) { return value.position; }
            Vertex readLane(Vertex value, uint lane)
            {
                StaticVertexEval vertex;
                return vertex.readLane(value, lane);
            }
            uint getCacheByteSize(uint count) { return 32u + count * 120u; }
            uint getCacheByteAlignment() { return 32u; }
            void writeCache(RWByteAddressBuffer cache, uint address, uint count, uint index, Vertex current, Vertex previous)
            {
                cache.Store<Vertex>(address + 32u + index * 60u, current);
                cache.Store<Vertex>(address + 32u + (count + index) * 60u, previous);
            }
            Vertex readCache(ByteAddressBuffer cache, uint address, uint count, VertexEvalContext context)
            {
                uint index = context.localVertexIndex + context.IsPreviousFrame * count;
                return cache.Load<Vertex>(address + 32u + index * 60u);
            }
        };
        """;
}
