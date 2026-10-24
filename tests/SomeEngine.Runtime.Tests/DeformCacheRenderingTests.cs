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
        using DeformContent content = await CreateContentAsync();
        SceneImage? cached = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.Project.RootPath, 4,
            [new(3, scene => scene.CaptureImage(image => cached = image))]), "--no-taa", "--no-vsm");

        await File.WriteAllTextAsync(content.ShaderPath, content.Shader
            .Replace("typedef CachedVertexEval<UserDeformation> VertexEvaluate;", "typedef UserDeformation VertexEvaluate;")
            .Replace("\n#include \"cluster_deform.slang\"\n", ""));
        await content.Project.BuildAsync(content.ShaderPath.Replace(".slang", ".shader"));
        SceneImage? inline = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(content.Project.RootPath, 4,
            [new(3, scene => scene.CaptureImage(image => inline = image))]), "--no-taa", "--no-vsm");
        SceneImage reference = Assert.IsType<SceneImage>(inline);
        int center = (reference.Height / 2 * reference.Width + reference.Width / 2) * 4;
        Assert.InRange(reference.Pixels.Span[center], (byte)16, byte.MaxValue);
        SceneImages.AssertEqual(reference, Assert.IsType<SceneImage>(cached));
    }

    private sealed record DeformContent(AssetProject Project, string Shader) : IDisposable
    {
        internal string ShaderPath => Path.Combine(Project.RootPath, "assets", "Shaders", "standard.slang");
        public void Dispose() => Directory.Delete(Project.RootPath, recursive: true);
    }

    private static async Task<DeformContent> CreateContentAsync()
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
            shader = shader.Replace("typedef LinearSurface DomainEvaluate;", "typedef UserSurface DomainEvaluate;");
            shader += "\n#include \"cluster_deform.slang\"\n";
            await File.WriteAllTextAsync(shaderPath, shader);
            await DefaultContent.BuildAsync(content, root);
            var project = new AssetProject(content, [], sourceFormats: [new ShaderSourceFormat([Path.Combine(root, "assets", "Shaders")])]);
            AssetEntry sceneEntry = project.Manifest.List(WorldAsset.FileType).Single(entry => entry.Name == "Default");
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
            return new(project, shader);
        }
        catch { Directory.Delete(content, recursive: true); throw; }
    }

    private const string UserDeformation = """
        #include "vertex_deform_cache.slang"
        struct UserVertex
        {
            StandardVertex Surface;
            uint Marker;
        };
        struct UserDeformation : IVertexDeformCache
        {
            typedef UserVertex Vertex;
            Vertex evaluate(VertexEvalContext context)
            {
                WaveVertexEval wave;
                Vertex result;
                result.Surface = wave.evaluate(context);
                result.Marker = 7u;
                return result;
            }
            float3 getPosition(Vertex value) { return value.Marker == 7u ? value.Surface.position : 0.0.xxx; }
            Vertex readLane(Vertex value, uint lane)
            {
                StaticVertexEval vertex;
                Vertex result;
                result.Surface = vertex.readLane(value.Surface, lane);
                result.Marker = WaveReadLaneAt(value.Marker, lane);
                return result;
            }
            uint getCacheByteSize(uint count) { return 32u + count * 128u; }
            uint getCacheByteAlignment() { return 32u; }
            void writeCache(RWByteAddressBuffer cache, uint address, uint count, uint index, Vertex current, Vertex previous)
            {
                uint offset = address + 32u + index * 128u;
                cache.Store<StandardVertex>(offset, current.Surface);
                cache.Store(offset + 60u, current.Marker ^ 0x12345678u);
                cache.Store<StandardVertex>(offset + 64u, previous.Surface);
                cache.Store(offset + 124u, previous.Marker ^ 0x12345678u);
            }
            Vertex readCache(ByteAddressBuffer cache, uint address, uint count, VertexEvalContext context)
            {
                uint offset = address + 32u + context.localVertexIndex * 128u + context.IsPreviousFrame * 64u;
                Vertex result;
                result.Surface = cache.Load<StandardVertex>(offset);
                result.Marker = cache.Load(offset + 60u) ^ 0x12345678u;
                return result;
            }
        };
        struct UserSurface : IDomainEvaluate<UserVertex>
        {
            typedef Triangle TriangleData;
            typedef SurfaceEval Surface;
            LinearSurface source;
            TriangleData makeTriangle(UserVertex a, UserVertex b, UserVertex c, uint materialIndex)
            { return source.makeTriangle(a.Surface, b.Surface, c.Surface, materialIndex); }
            float3 cornerPosition(TriangleData value, uint corner) { return source.cornerPosition(value, corner); }
            float3 position(TriangleData value, float3 bary) { return source.position(value, bary); }
            Surface eval(TriangleData value, float3 bary) { return source.eval(value, bary); }
            SurfaceBarycentrics reconstruct(TriangleData value, SurfaceReconstructionInput input)
            { return source.reconstruct(value, input); }
            void bounds(TriangleData value, out float3 minimum, out float3 maximum)
            { source.bounds(value, minimum, maximum); }
            void patchBounds(TriangleData value, float3 a, float3 b, float3 c, out float3 minimum, out float3 maximum)
            { source.patchBounds(value, a, b, c, minimum, maximum); }
            DomainErrorBounds errorBounds(TriangleData value, float3 a, float3 b, float3 c, float3 p, float3 q, float3 r)
            { return source.errorBounds(value, a, b, c, p, q, r); }
        };
        """;
}
