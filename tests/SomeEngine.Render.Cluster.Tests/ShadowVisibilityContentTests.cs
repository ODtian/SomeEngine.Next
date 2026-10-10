using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class ShadowVisibilityContentTests
{
    [Fact]
    public async Task ConvertedDoubleSidedMaterialCoversBackFacingSurface()
    {
        using var files = new MaterialFiles();
        await files.WriteAsync("""{"doubleSided":false}""", """{"doubleSided":true}""");
        await ShadowVisibilityContent.BuildAsync(FrameStageScene.FindRoot(),
            files.Source, files.Content, files.Geometry, false);
        Shader frontOnly = await files.ReadShaderAsync(0);
        Shader bothFaces = await files.ReadShaderAsync(1);
        using Texture albedo = PixelTexture(255, 255, 255, 255);
        using Texture normal = PixelTexture(128, 128, 255, 255);
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, SoftwareRasterEdgeThreshold = 65536 }, plane);
        var frontMaterial = scene.CreateEmissiveMaterial(frontOnly, Vector3.Zero);
        frontMaterial.AlbedoMap = albedo;
        frontMaterial.NormalMap = normal;
        var bothMaterial = scene.CreateEmissiveMaterial(bothFaces, Vector3.Zero);
        bothMaterial.AlbedoMap = albedo;
        bothMaterial.NormalMap = normal;
        scene.SetMeshTransform(0, new(new Vector3(20, 0, -8), Quaternion.Identity));
        scene.SetMeshTransform(1, new(new Vector3(0, 0, -6),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI)));
        scene.UseMaterial(1, frontMaterial);
        byte[] rejected = await scene.ReadClusterColorAsync();
        scene.UseMaterial(1, bothMaterial);
        byte[] covered = await scene.ReadClusterColorAsync();
        AssertCoverage(0, rejected);
        AssertCoverage(1, covered);
    }

    private static void AssertCoverage(float expected, byte[] frame)
    {
        float coverage = (float)MemoryMarshal.Read<Half>(frame.AsSpan((32 * 64 + 32) * 8 + 2, 2));
        ImageArtifacts.AssertEqual(new(1, 1, Format.R32Float, BitConverter.GetBytes(expected)),
            new(1, 1, Format.R32Float, BitConverter.GetBytes(coverage)));
    }

    private static Texture PixelTexture(byte red, byte green, byte blue, byte alpha) => Texture.Create(
        new TextureDesc(TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1, Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination),
        [new TexturePixels(new TextureMipTile { Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4 },
            new byte[] { red, green, blue, alpha })], TextureViewDimension.Texture2D);

    private sealed class MaterialFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "shadow-visibility-" + Guid.NewGuid().ToString("N"));
        private readonly List<Guid> _materials = [];
        internal string Source => Path.Combine(_directory, "source");
        internal string Content => Path.Combine(_directory, "converted");
        internal string Geometry => Path.Combine(Source, "materials.gltf");

        internal async Task WriteAsync(params string[] materials)
        {
            Directory.CreateDirectory(Path.Combine(Source, "assets", "Shaders"));
            Directory.CreateDirectory(Path.Combine(Content, "assets", "Shaders"));
            string generated = Path.Combine(Source, "assets", "Shaders", "generated");
            Directory.CreateDirectory(generated);
            File.Copy(Path.Combine(FrameStageScene.FindRoot(), "templates", "DefaultProject", "assets", "Shaders",
                "generated", "render_instance_layout.slang"), Path.Combine(generated, "render_instance_layout.slang"));
            await File.WriteAllTextAsync(Geometry,
                """{"asset":{"version":"2.0"},"materials":[""" + string.Join(',', materials) + "]}");
            var manifest = new AssetManifest();
            for (int index = 0; index < materials.Length; index++)
            {
                Guid id = Guid.NewGuid();
                _materials.Add(id);
                string relative = Path.Combine("assets", $"material-{index}.material.asset");
                using var material = new MaterialAsset(id) { Name = $"Material {index}" };
                AssetEntry saved = await material.SaveAsync(Path.Combine(Source, relative));
                manifest.AddAsset(saved.AssetGuid, saved.Name, relative, saved.AssetType, saved.SchemaFingerprint,
                    subAssetKey: $"material:index:{index}", dependencies: saved.Dependencies, version: saved.Version);
                File.Copy(Path.Combine(Source, relative), Path.Combine(Content, relative));
            }
            manifest.Save(Path.Combine(Source, "Library", "AssetManifest"));
            manifest.Save(Path.Combine(Content, "Library", "AssetManifest"));
        }

        internal async Task<Shader> ReadShaderAsync(int material)
        {
            AssetManifest manifest = AssetManifest.Load(Path.Combine(Content, "Library", "AssetManifest"));
            using MaterialAsset asset = await MaterialAsset.OpenAsync(Path.Combine(Content, manifest.Assets[_materials[material]].Path));
            var shaderId = asset.GetBlock<MaterialBlock>().Shader;
            using ShaderAsset shader = await ShaderAsset.OpenAsync(Path.Combine(Content, manifest.Assets[shaderId.Value].Path));
            return await TestShaderLoading.CreateAsync(shader, SlangShaderSharp.SlangCompileTarget.Dxil);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
