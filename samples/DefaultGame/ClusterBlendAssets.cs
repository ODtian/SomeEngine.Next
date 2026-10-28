using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Collections;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using Texture = SomeEngine.Resources.Texture;

namespace DefaultGame;

internal sealed class ClusterBlendAssets
{
    internal required Mesh Panel { get; init; }
    internal required Mesh Part { get; init; }
    internal required StandardMaterial Standard { get; init; }
    internal required UnlitMaterial Unlit { get; init; }
    internal required UnlitMaterial DeformedPhong { get; init; }
    internal required Texture Smoke { get; init; }
    internal required Texture Leaf { get; init; }
    internal required Texture CoatingNormal { get; init; }
    internal Shader? PhysicsSkin { get; init; }

    internal static async Task<ClusterBlendAssets> OpenAsync(string root, RuntimeGraphicsBackend backend, string? skinShader = null)
    {
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library/AssetManifest"));
        await using var loader = new AssetLoader(new LooseAssetStorage(root, manifest));
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
        var shaders = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
        var textures = new WeakValues<AssetResourceKey<TextureAsset>, Texture>();
        bool vulkan = backend == RuntimeGraphicsBackend.Vulkan;
        var session = new SessionDesc
        {
            Targets = [new TargetDesc { Format = vulkan ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil,
                Profile = slang.FindProfile(vulkan ? "spirv_1_5" : "sm_6_6") }],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries =
            [
                new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(0, 0)),
                new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
                new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
            ],
        };
        return new()
        {
            Panel = await ReadMeshAsync("Blend panel"),
            Part = await ReadMeshAsync("Blend part"),
            Standard = (StandardMaterial)await ReadMaterialAsync("Standard"),
            Unlit = (UnlitMaterial)await ReadMaterialAsync("Unlit"),
            DeformedPhong = (UnlitMaterial)await ReadMaterialAsync("Deformed Phong"),
            Smoke = await ReadTextureAsync("Blend smoke"),
            Leaf = await ReadTextureAsync("Blend leaf"),
            CoatingNormal = await ReadTextureAsync("Blend coating normal"),
            PhysicsSkin = skinShader is null ? null : await ShaderAsset.GetAsync(loader,
                new(Id(skinShader, ShaderAsset.FileType)), shaders, slang, session),
        };

        Guid Id(string name, string type) => manifest.Assets.Values.Single(entry => entry.Name == name && entry.AssetType == type).AssetGuid;

        async Task<Mesh> ReadMeshAsync(string name)
        {
            using var file = await MeshAsset.OpenAsync(loader, new(Id(name, MeshAsset.FileType)));
            MeshBlock geometry = await file.GetBlockAsync<MeshBlock>();
            if (await file.TryGetBlockAsync<ClusterGeometryBlock>() is { } clusters)
            {
                ClusterVertexAttributesBlock attributes = await file.GetBlockAsync<ClusterVertexAttributesBlock>();
                Mesh mesh = await clusters.CreateAsync(geometry, attributes, file.Name);
                await clusters.LoadPagesAsync(mesh, attributes, Enumerable.Range(0, mesh.PageCount));
                return mesh;
            }
            ClusterMeshBlock packed = await file.GetBlockAsync<ClusterMeshBlock>();
            Mesh packedMesh = await packed.CreateAsync(geometry, file.Name);
            await packed.LoadPagesAsync(packedMesh, Enumerable.Range(0, packedMesh.PageCount));
            return packedMesh;
        }

        async Task<Material> ReadMaterialAsync(string name)
        {
            using var file = await MaterialAsset.OpenAsync(loader, new(Id(name, MaterialAsset.FileType)));
            using MaterialBlock content = await file.ResolveAsync(loader);
            var materialShaders = new Dictionary<AssetId<ShaderAsset>, Shader>();
            var materialTextures = new Dictionary<AssetId<TextureAsset>, Texture>();
            if (!content.Shader.IsEmpty)
                materialShaders.Add(content.Shader, await ShaderAsset.GetAsync(loader, content.Shader, shaders, slang, session));
            foreach (MaterialParameter parameter in content.Parameters)
                if (parameter.Kind == MaterialParameterKind.Texture && !parameter.Texture.IsEmpty &&
                    !materialTextures.ContainsKey(parameter.Texture))
                    materialTextures.Add(parameter.Texture, await TextureAsset.GetAsync(loader, parameter.Texture, textures));
            return await EngineSceneMaterials.CreateAsync(file, loader, materialShaders, materialTextures);
        }

        async Task<Texture> ReadTextureAsync(string name)
        {
            return await TextureAsset.GetAsync(loader, new(Id(name, TextureAsset.FileType)), textures);
        }
    }
}
