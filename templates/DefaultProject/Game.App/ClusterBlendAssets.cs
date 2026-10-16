using Game.Content;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Runtime;
using Texture = SomeEngine.Resources.Texture;

internal sealed class ClusterBlendAssets : IDisposable
{
    internal required Mesh Panel { get; init; }
    internal required Mesh Part { get; init; }
    internal required StandardMaterial Standard { get; init; }
    internal required UnlitMaterial Unlit { get; init; }
    internal required Texture Smoke { get; init; }
    internal required Texture Leaf { get; init; }

    internal static async Task<ClusterBlendAssets> OpenAsync(string root, RuntimeGraphicsBackend backend)
    {
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library/AssetManifest"));
        await using var loader = new AssetLoader(new LooseAssetStorage(root, manifest));
        IGlobalSession slang = Shader.CreateSlangGlobalSession();
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
            Smoke = await ReadTextureAsync("Blend smoke"),
            Leaf = await ReadTextureAsync("Blend leaf"),
        };

        Guid Id(string name, string type) => manifest.Assets.Values.Single(entry => entry.Name == name && entry.AssetType == type).AssetGuid;

        async Task<Mesh> ReadMeshAsync(string name)
        {
            using var file = await MeshAsset.OpenAsync(loader, new(Id(name, MeshAsset.FileType)));
            MeshBlock geometry = await file.GetBlockAsync<MeshBlock>();
            ClusterMeshBlock clusters = await file.GetBlockAsync<ClusterMeshBlock>();
            Mesh mesh = await clusters.CreateAsync(geometry, file.Name);
            await clusters.LoadPagesAsync(mesh, Enumerable.Range(0, mesh.PageCount));
            return mesh;
        }

        async Task<Material> ReadMaterialAsync(string name)
        {
            using var file = await MaterialAsset.OpenAsync(loader, new(Id(name, MaterialAsset.FileType)));
            return await file.CreateAsync(loader, slang, session);
        }

        async Task<Texture> ReadTextureAsync(string name)
        {
            using var file = await TextureAsset.OpenAsync(loader, new(Id(name, TextureAsset.FileType)));
            TextureBlock pixels = await file.GetBlockAsync<TextureBlock>();
            return await pixels.CreateAsync(name);
        }
    }

    public void Dispose()
    {
        Standard.Dispose();
        Unlit.Dispose();
        Smoke.Dispose();
        Leaf.Dispose();
    }
}
