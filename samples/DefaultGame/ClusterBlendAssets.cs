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
    internal required Shader StandardBothShader { get; init; }
    internal required Shader UnlitBothShader { get; init; }
    internal required Shader DeformedPhongBothShader { get; init; }
    internal required Texture Smoke { get; init; }
    internal required Texture Leaf { get; init; }
    internal required Texture CoatingNormal { get; init; }

    internal static async Task<ClusterBlendAssets> OpenAsync(string root, ApplicationGraphicsBackend backend)
    {
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library/AssetManifest"));
        await using var loader = SomeEngine.Assets.Importers.EngineShaderContent.OpenFiles(root, manifest);
        using var shaders = new ShaderLibrary(backend == ApplicationGraphicsBackend.Vulkan
            ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil);
        var textures = new WeakValues<AssetResourceKey<TextureAsset>, Texture>();
        return new()
        {
            Panel = await ReadMeshAsync("Blend panel"),
            Part = await ReadMeshAsync("Blend part"),
            Standard = (StandardMaterial)await ReadMaterialAsync("Standard"),
            Unlit = (UnlitMaterial)await ReadMaterialAsync("Unlit"),
            DeformedPhong = (UnlitMaterial)await ReadMaterialAsync("Deformed Phong"),
            StandardBothShader = await shaders.GetAsync(loader, new AssetId<ShaderAsset>(Id("Standard both faces", ShaderAsset.FileType))),
            UnlitBothShader = await shaders.GetAsync(loader, new AssetId<ShaderAsset>(Id("Unlit both faces", ShaderAsset.FileType))),
            DeformedPhongBothShader = await shaders.GetAsync(loader, new AssetId<ShaderAsset>(Id("Deformed Phong both faces", ShaderAsset.FileType))),
            Smoke = await ReadTextureAsync("Blend smoke"),
            Leaf = await ReadTextureAsync("Blend leaf"),
            CoatingNormal = await ReadTextureAsync("Blend coating normal"),
        };

        Guid Id(string name, string type) => manifest.Assets.Values.Single(entry => entry.Name == name && entry.AssetType == type).AssetGuid;

        async Task<Mesh> ReadMeshAsync(string name)
        {
            using var file = await MeshAsset.OpenAsync(loader, new(Id(name, MeshAsset.FileType)));
            MeshBlock geometry = await file.GetBlockAsync<MeshBlock>();
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
                materialShaders.Add(content.Shader, await shaders.GetAsync(loader, content.Shader));
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
