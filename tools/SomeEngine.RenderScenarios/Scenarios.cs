using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;
using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.Graphics;
using Texture = SomeEngine.Assets.Schema.TextureData;

if (args.Length != 3)
    throw new ArgumentException("Usage: SomeEngine.RenderScenarios <built-runtime-directory> <new-output-directory> <phong|factors|materials|wpo-alpha|displacement|sparse-motion>");
string source = Path.GetFullPath(args[0]);
string destination = Path.GetFullPath(args[1]);
string mode = args[2];
if (mode is not ("phong" or "factors" or "materials" or "wpo-alpha" or "displacement" or "sparse-motion"))
    throw new ArgumentException("Unknown scenario: " + mode);
if (!File.Exists(Path.Combine(source, "SomeEngine.Runtime.dll")))
    throw new ArgumentException("Source must contain a built SomeEngine.Runtime.");
string sourcePrefix = Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar;
if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("The scenario directory must be outside the built runtime directory.");
if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
    throw new ArgumentException("Use a new or empty scenario directory.");
foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
{
    string output = Path.Combine(destination, Path.GetRelativePath(source, file));
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.Copy(file, output);
}
string shaderPath = Path.Combine(destination, "assets", "Shaders", "cluster_shade_material.slang");
string shader = File.ReadAllText(shaderPath);
if (mode is "phong" or "factors" or "materials")
    shader = shader.Replace("#include \"triangle_surface.slang\"", "#include \"triangle_surface.slang\"\n#include \"phong_tessellation.slang\"")
        .Replace("typedef LinearSurface DomainEvaluate;", "typedef PhongTessellation DomainEvaluate;") + "\n#include \"cluster_tessellation_entries.slang\"\n";
else if (mode == "wpo-alpha")
{
    string pixel = """
struct CheckerPixel : IPixelEvaluate
{
    PixelEvaluateResult evaluate(PixelEvaluateInput input)
    {
        PixelEvaluateResult result;
        result.Visible = ((input.PixelCoord.x / 8u + input.PixelCoord.y / 8u) & 1u) == 0u ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
};
""";
    shader = shader.Replace("typedef StaticVertexEval VertexEvaluate;", pixel + "\ntypedef WaveVertexEval VertexEvaluate;")
        .Replace("typedef OpaquePixelEvaluate PixelEvaluate;", "typedef CheckerPixel PixelEvaluate;");
}
else if (mode == "displacement")
{
    string domain = """
#include "texture_displacement.slang"
struct SampledDisplacement : IDomainEvaluate
{
    Texture2D<float> AlbedoMap;
    Texture2D<float2> NormalMap;
    SamplerState MaterialSampler;
    TextureDisplacement surface() { return MakeTextureDisplacement(AlbedoMap, NormalMap, MaterialSampler, float2(0.0, 1.0), 0.15, -0.075); }
    float3 position(Triangle t, float3 b) { return surface().position(t,b); }
    SurfaceEval eval(Triangle t, float3 b) { return surface().eval(t,b); }
    SurfaceBarycentrics reconstruct(Triangle t, SurfaceReconstructionInput i) { return surface().reconstruct(t,i); }
    void bounds(Triangle t, out float3 lo, out float3 hi) { surface().bounds(t,lo,hi); }
    void patchBounds(Triangle t, float3 b0, float3 b1, float3 b2, out float3 lo, out float3 hi) { surface().patchBounds(t,b0,b1,b2,lo,hi); }
    DomainErrorBounds errorBounds(Triangle t, float3 b0, float3 b1, float3 b2, float3 p0, float3 p1, float3 p2) { return surface().errorBounds(t,b0,b1,b2,p0,p1,p2); }
};
""";
    shader = shader.Replace("typedef LinearSurface DomainEvaluate;", domain + "\ntypedef SampledDisplacement DomainEvaluate;") + "\n#include \"cluster_tessellation_entries.slang\"\n";
}

if (mode == "materials")
{
    const string pixel = """
struct MaskPixel : IPixelEvaluate
{
    Texture2D<float4> AlbedoMap;
    SamplerState MaterialSampler;
    PixelEvaluateResult evaluate(PixelEvaluateInput input)
    {
        PixelEvaluateResult result;
        result.Visible = AlbedoMap.SampleLevel(MaterialSampler, float2(input.PixelCoord.xy % 64u) / 64.0, 0.0).x > 0.5 ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
};
""";
    shader = shader.Replace("typedef OpaquePixelEvaluate PixelEvaluate;", pixel + "\ntypedef MaskPixel PixelEvaluate;");
}
File.WriteAllText(shaderPath, shader);
AssetProject project = new AssetProject(destination, [new GltfSourceImporter(), new SlangSourceImporter()]);
await project.ImportAsync(shaderPath);
IAssetStorage storage = project.CreateStorage();
Guid sceneId = project.Resolve("assets/Scenes/default.scene.asset")!.Value;
if (!storage.TryFind(sceneId, out AssetEntry entry)) throw new InvalidOperationException();
await using var loader = new AssetLoader(storage);
await using Asset<SomeEngine.ECS.World> sceneFile = await loader.OpenAsync(new AssetId<SomeEngine.ECS.World>(sceneId));
SceneData scene = sceneFile.GetFieldsGenerated<SceneData>();
scene.Name = mode + " rendering scenario";
scene.MeshInstances = scene.MeshInstances!.Take(mode == "phong" ? 1 : mode == "wpo-alpha" ? 8 : mode == "sparse-motion" ? 128 : 4).ToArray();
for (int index = 0; index < scene.MeshInstances.Count; index++)
{
    SceneMeshInstance instance = scene.MeshInstances[index];
    instance.Position = new SceneVector3 { X = 0, Y = 0, Z = 12 };
    instance.Scale = new SceneVector3 { X = 0.8f, Y = 0.8f, Z = 0.8f };
    instance.BoundsExpansion = mode == "wpo-alpha" ? .31f : 1;
}
if (mode is not ("phong" or "sparse-motion"))
{
    for (int index = 0; index < scene.MeshInstances.Count; index++)
    {
        SceneMeshInstance instance = scene.MeshInstances[index];
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(mode == "factors" ? (index == 3 ? 4 : index) : 2);
        MeshData mesh = ClusterBuilder.ProcessRaw(vertices, attributes, indices,
            [new MeshMaterialSlot(Guid.Parse(instance.MaterialGuids![0]))], $"Tessellation factor geometry {index}");
        instance.MeshGuid = project.CreateAsset($"assets/Meshes/factor-{index}.mesh.asset", mesh.ToAsset()).ToString();
        instance.Position = new SceneVector3 { X = index % 2 == 0 ? -1.5f : 1.5f, Y = index / 2 == 0 ? -1.5f : 1.5f, Z = 12 };
        if (mode == "wpo-alpha" && index >= 4) instance.Position = new SceneVector3 { X = index % 2 == 0 ? -8 : 8, Y = (index - 4) / 2 == 0 ? -4 : 4, Z = 65 };
    }
}
if (mode is "wpo-alpha" or "displacement" or "materials")
{
    for (int i = 0; i < scene.MeshInstances.Count; i++)
    {
        scene.MeshInstances[i].MotionAmplitude = new SceneVector3 { X = .2f, Y = .1f, Z = .15f };
        scene.MeshInstances[i].MotionSeed = (uint)i + 1;
    }
    scene.PointLights = [new ScenePointLight { Position = new SceneVector3 { X = 3, Y = 2, Z = 7 },
        Color = new SceneVector3 { X = 1, Y = .6f, Z = .2f }, Range = 40, Intensity = 4 }];
}
if (mode == "displacement")
{
    float[] heights = Enumerable.Range(0, 64).Select(i => .5f + .5f * MathF.Sin((i % 8) * MathF.PI / 4) * MathF.Sin((i / 8) * MathF.PI / 4)).ToArray();
    Texture bounds = DisplacementBoundsTexture.Create(Guid.NewGuid(), "height bounds", 8, 8, heights, out _);
    project.CreateAsset("assets/Textures/scenario-bounds.texture.asset", bounds.ToAsset());
    Texture height = new() { AssetGuid = Guid.NewGuid().ToString(), Name = "heights", Dimension = TextureDimension.Texture2D,
        Width = 8, Height = 8, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
        Format = Format.R32Float, SampledFormat = Format.R32Float, SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        MipTiles = [new TextureMipTile { Width = 8, Height = 8, RowPitch = 32, SlicePitch = 256,
            Payload = MemoryMarshal.AsBytes(heights.AsSpan()).ToArray() }] };
    project.CreateAsset("assets/Textures/scenario-height.texture.asset", height.ToAsset());
    if (!storage.TryFind(Guid.Parse(scene.MeshInstances[0].MaterialGuids![0]), out AssetEntry materialEntry)) throw new InvalidOperationException();
    await using Asset<Material> materialFile = await loader.OpenAsync(new AssetId<Material>(materialEntry.AssetGuid));
    MaterialData material = materialFile.GetFieldsGenerated<MaterialData>();
    material.Set(new("AlbedoMap", MaterialParameterKind.Texture) { Texture = new(Guid.Parse(height.AssetGuid!)) });
    material.Set(new("NormalMap", MaterialParameterKind.Texture) { Texture = new(Guid.Parse(bounds.AssetGuid!)) });
    project.CreateAsset<Material>("assets/Materials/scenario.material.asset", material.ToAsset());
}
if (mode == "materials")
{
    if (!storage.TryFind(Guid.Parse(scene.MeshInstances[0].MaterialGuids![0]), out AssetEntry materialEntry)) throw new InvalidOperationException();
    await using Asset<Material> materialFile = await loader.OpenAsync(new AssetId<Material>(materialEntry.AssetGuid));
    MaterialData material = materialFile.GetFieldsGenerated<MaterialData>();
    for (int index = 0; index < scene.MeshInstances.Count; index++)
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            byte value = (x + y + index) % (index + 2) == 0 ? (byte)0 : (byte)255;
            for (int channel = 0; channel < 4; channel++) pixels[(y * 8 + x) * 4 + channel] = value;
        }
        Texture mask = new() { AssetGuid = Guid.NewGuid().ToString(), Name = "mask " + index, Dimension = TextureDimension.Texture2D,
            Width = 8, Height = 8, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8G8B8A8UNorm, SampledFormat = Format.R8G8B8A8UNorm, SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 8, Height = 8, RowPitch = 32, SlicePitch = 256, Payload = pixels }] };
        project.CreateAsset($"assets/Textures/scenario-mask-{index}.texture.asset", mask.ToAsset());
        material.AssetGuid = Guid.NewGuid().ToString();
        material.Name = "Phong masked " + index;
        material.Set(new("AlbedoMap", MaterialParameterKind.Texture) { Texture = new(Guid.Parse(mask.AssetGuid!)) });
        project.CreateAsset<Material>($"assets/Materials/scenario-{index}.material.asset", material.ToAsset());
        scene.MeshInstances[index].MaterialGuids = [material.AssetGuid];
    }
}
if (mode == "sparse-motion")
{
    for (int index = 0; index < scene.MeshInstances.Count; index++)
    {
        SceneMeshInstance instance = scene.MeshInstances[index];
        instance.Position = new SceneVector3 { X = (index % 16 - 7.5f) * 1.2f, Y = (index / 16 - 3.5f) * 1.2f, Z = 25 };
        instance.Scale = new SceneVector3 { X = .4f, Y = .4f, Z = .4f };
        instance.MotionAmplitude = new SceneVector3 { X = index == 0 ? .5f : 0 };
        instance.MotionSeed = 1;
        instance.BoundsExpansion = 0;
    }
}
project.CreateAsset("assets/Scenes/scenario.scene.asset", scene.ToAsset());
Console.WriteLine(destination);
