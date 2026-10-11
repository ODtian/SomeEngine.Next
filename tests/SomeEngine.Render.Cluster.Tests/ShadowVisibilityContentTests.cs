using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.Graphics;
using SomeEngine.Render;
using SomeEngine.Render.Components;
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
            files.Source, files.Content, files.Materials);
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

    [Fact]
    public async Task RepeatedVisibilityConversionPreservesMaskedCoverage()
    {
        using var files = new MaterialFiles();
        await files.WriteAsync("""{"alphaMode":"MASK","alphaCutoff":0.5}""", """{"alphaMode":"OPAQUE"}""");
        await ShadowVisibilityContent.BuildAsync(FrameStageScene.FindRoot(),
            files.Source, files.Content, files.Materials);
        Guid firstShaderId = await files.ReadShaderIdAsync(0);
        Shader first = await files.ReadShaderAsync(0);
        Shader opaque = await files.ReadShaderAsync(1);
        await ShadowVisibilityContent.BuildAsync(FrameStageScene.FindRoot(),
            files.Source, files.Content, files.Materials);
        Assert.Equal(firstShaderId, await files.ReadShaderIdAsync(0));
        Shader second = await files.ReadShaderAsync(0);
        using Texture albedo = PixelTexture(255, 255, 255, 0);
        using Texture normal = PixelTexture(128, 128, 255, 255);
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, SoftwareRasterEdgeThreshold = 65536 }, plane);
        var firstMaterial = scene.CreateEmissiveMaterial(first, Vector3.Zero);
        firstMaterial.AlbedoMap = albedo;
        firstMaterial.NormalMap = normal;
        var secondMaterial = scene.CreateEmissiveMaterial(second, Vector3.Zero);
        secondMaterial.AlbedoMap = albedo;
        secondMaterial.NormalMap = normal;
        var opaqueMaterial = scene.CreateEmissiveMaterial(opaque, Vector3.Zero);
        opaqueMaterial.AlbedoMap = albedo;
        opaqueMaterial.NormalMap = normal;
        scene.SetMeshTransform(0, new(new Vector3(20, 0, -8), Quaternion.Identity));
        scene.SetMeshTransform(1, new(new Vector3(0, 0, -6), Quaternion.Identity));
        scene.UseMaterial(1, firstMaterial);
        byte[] firstFrame = await scene.ReadClusterColorAsync();
        scene.UseMaterial(1, secondMaterial);
        byte[] secondFrame = await scene.ReadClusterColorAsync();
        scene.UseMaterial(1, opaqueMaterial);
        byte[] opaqueFrame = await scene.ReadClusterColorAsync();
        AssertCoverage(0, firstFrame);
        AssertCoverage(0, secondFrame);
        AssertCoverage(1, opaqueFrame);
    }

    [Fact]
    public async Task MaterialsWithTheSameIndexKeepTheirSourceCoverageAfterRepeatedConversion()
    {
        using var files = new MaterialFiles();
        await files.WriteSourcesAsync(
            ["""{"alphaMode":"OPAQUE","doubleSided":false}"""],
            ["""{"alphaMode":"MASK","alphaCutoff":0.25,"doubleSided":true,"pbrMetallicRoughness":{"baseColorFactor":[1,1,1,0.5]}}"""]);
        Guid application = await files.WriteApplicationAsync(0, 1);
        await ShadowVisibilityContent.BuildAsync(FrameStageScene.FindRoot(), files.Source, files.Content, [application]);
        Guid opaqueShaderId = await files.ReadShaderIdAsync(0);
        Guid maskedShaderId = await files.ReadShaderIdAsync(1);
        await ShadowVisibilityContent.BuildAsync(FrameStageScene.FindRoot(), files.Source, files.Content, [application]);
        Assert.Equal(opaqueShaderId, await files.ReadShaderIdAsync(0));
        Assert.Equal(maskedShaderId, await files.ReadShaderIdAsync(1));
        Shader opaque = await files.ReadShaderAsync(0);
        Shader masked = await files.ReadShaderAsync(1);
        using Texture belowCutoff = PixelTexture(255, 255, 255, 64);
        using Texture aboveCutoff = PixelTexture(255, 255, 255, 192);
        using Texture normal = PixelTexture(128, 128, 255, 255);
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
            new ClusterPipelineOptions { MaxCandidates = 1024, SoftwareRasterEdgeThreshold = 65536 }, plane);
        var opaqueMaterial = scene.CreateEmissiveMaterial(opaque, Vector3.Zero);
        opaqueMaterial.AlbedoMap = belowCutoff;
        opaqueMaterial.NormalMap = normal;
        var maskedMaterial = scene.CreateEmissiveMaterial(masked, Vector3.Zero);
        maskedMaterial.AlbedoMap = belowCutoff;
        maskedMaterial.NormalMap = normal;
        scene.SetMeshTransform(0, new(new Vector3(20, 0, -8), Quaternion.Identity));
        scene.SetMeshTransform(1, new(new Vector3(0, 0, -6), Quaternion.Identity));
        scene.UseMaterial(1, opaqueMaterial);
        AssertCoverage(1, await scene.ReadClusterColorAsync());
        scene.UseMaterial(1, maskedMaterial);
        AssertCoverage(0, await scene.ReadClusterColorAsync());
        maskedMaterial.AlbedoMap = aboveCutoff;
        AssertCoverage(1, await scene.ReadClusterColorAsync());
        scene.SetMeshTransform(1, new(new Vector3(0, 0, -6),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI)));
        scene.UseMaterial(1, opaqueMaterial);
        AssertCoverage(0, await scene.ReadClusterColorAsync());
        scene.UseMaterial(1, maskedMaterial);
        AssertCoverage(1, await scene.ReadClusterColorAsync());
    }

    [Fact]
    public async Task UnreferencedMissingSourceDoesNotAffectVisibilityConversion()
    {
        using var files = new MaterialFiles();
        await files.WriteSourcesAsync(["{}"], ["""{"alphaMode":"MASK","doubleSided":true}"""]);
        Guid application = await files.WriteApplicationAsync(1);
        files.DeleteSource(0);
        byte[] untouched = await files.ReadMaterialBytesAsync(0);
        await ShadowVisibilityContent.BuildAsync(FrameStageScene.FindRoot(), files.Source, files.Content, [application]);
        Assert.Equal(untouched, await files.ReadMaterialBytesAsync(0));
        Guid shader = await files.ReadShaderIdAsync(1);
        Assert.NotEqual(Guid.Empty, shader);
        await ShadowVisibilityContent.BuildAsync(FrameStageScene.FindRoot(), files.Source, files.Content, [application]);
        Assert.Equal(shader, await files.ReadShaderIdAsync(1));
        Assert.Equal(untouched, await files.ReadMaterialBytesAsync(0));
    }

    [Fact]
    public async Task ReferencedMissingSourceFailsVisibilityConversion()
    {
        using var files = new MaterialFiles();
        await files.WriteAsync("{}");
        Guid application = await files.WriteApplicationAsync(0);
        files.DeleteSource(0);
        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() =>
            ShadowVisibilityContent.BuildAsync(FrameStageScene.FindRoot(), files.Source, files.Content, [application]));
        Assert.Equal("filePath", error.ParamName);
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
        internal Task WriteAsync(params string[] materials) => WriteSourcesAsync(materials);

        internal async Task WriteSourcesAsync(params string[][] sources)
        {
            Directory.CreateDirectory(Path.Combine(Source, "assets", "Shaders"));
            Directory.CreateDirectory(Path.Combine(Content, "assets", "Shaders"));
            string generated = Path.Combine(Source, "assets", "Shaders", "generated");
            Directory.CreateDirectory(generated);
            File.Copy(Path.Combine(FrameStageScene.FindRoot(), "templates", "DefaultProject", "assets", "Shaders",
                "generated", "render_instance_layout.slang"), Path.Combine(generated, "render_instance_layout.slang"));
            var manifest = new AssetManifest();
            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                string geometry = $"materials-{sourceIndex}.gltf";
                await File.WriteAllTextAsync(Path.Combine(Source, geometry),
                    """{"asset":{"version":"2.0"},"materials":[""" + string.Join(',', sources[sourceIndex]) + "]}");
                SourceGuid sourceGuid = SourceGuid.New();
                manifest.AddSource(sourceGuid, geometry);
                for (int index = 0; index < sources[sourceIndex].Length; index++)
                {
                    Guid id = Guid.NewGuid();
                    int materialIndex = _materials.Count;
                    _materials.Add(id);
                    string relative = Path.Combine("assets", $"material-{materialIndex}.material.asset");
                    using var material = new MaterialAsset(id) { Name = $"Material {materialIndex}" };
                    AssetEntry saved = await material.SaveAsync(Path.Combine(Source, relative));
                    manifest.AddAsset(saved.AssetGuid, saved.Name, relative, saved.AssetType, saved.SchemaFingerprint,
                        sourceGuid, subAssetKey: $"material:index:{index}", dependencies: saved.Dependencies, version: saved.Version);
                    File.Copy(Path.Combine(Source, relative), Path.Combine(Content, relative));
                }
            }
            manifest.Save(Path.Combine(Source, "Library", "AssetManifest"));
            manifest.Save(Path.Combine(Content, "Library", "AssetManifest"));
        }

        internal IReadOnlyList<Guid> Materials => _materials;

        internal void DeleteSource(int source) => File.Delete(Path.Combine(Source, $"materials-{source}.gltf"));

        internal async Task<byte[]> ReadMaterialBytesAsync(int material)
        {
            AssetManifest manifest = AssetManifest.Load(Path.Combine(Content, "Library", "AssetManifest"));
            return await File.ReadAllBytesAsync(Path.Combine(Content, manifest.Assets[_materials[material]].Path));
        }

        internal async Task<Guid> WriteApplicationAsync(params int[] materials)
        {
            AssetManifest manifest = AssetManifest.Load(Path.Combine(Source, "Library", "AssetManifest"));
            void Register(AssetEntry saved, string path)
            {
                manifest.AddAsset(saved.AssetGuid, saved.Name, path, saved.AssetType, saved.SchemaFingerprint,
                    dependencies: saved.Dependencies, version: saved.Version);
                File.Copy(Path.Combine(Source, path), Path.Combine(Content, path));
            }
            using var mesh = new MeshAsset(Guid.NewGuid());
            mesh.SetBlock(new MeshMaterialsBlock
            {
                Materials = materials.Select(index => new AssetId<MaterialAsset>(_materials[index])).ToArray(),
            });
            Register(await mesh.SaveAsync(Path.Combine(Source, "assets/selected.mesh.asset")), "assets/selected.mesh.asset");
            using var world = new World();
            world.CreateEntity(new MeshSource { Mesh = new(mesh.Id) });
            using var scene = new WorldAsset(Guid.NewGuid());
            scene.WriteWorld(world, SceneSerialization.CreateRegistry());
            Register(await scene.SaveAsync(Path.Combine(Source, "assets/selected.scene.asset")), "assets/selected.scene.asset");
            using var application = new ApplicationSettingsAsset(Guid.NewGuid());
            application.SetBlock(new ApplicationSettingsBlock { Scene = new(scene.Id) });
            Register(await application.SaveAsync(Path.Combine(Source, "assets/selected.runtime.asset")), "assets/selected.runtime.asset");
            manifest.Save(Path.Combine(Source, "Library", "AssetManifest"));
            manifest.Save(Path.Combine(Content, "Library", "AssetManifest"));
            return application.Id;
        }

        internal async Task<Guid> ReadShaderIdAsync(int material)
        {
            AssetManifest manifest = AssetManifest.Load(Path.Combine(Content, "Library", "AssetManifest"));
            using MaterialAsset asset = await MaterialAsset.OpenAsync(Path.Combine(Content, manifest.Assets[_materials[material]].Path));
            return asset.GetBlock<MaterialBlock>().Shader.Value;
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
