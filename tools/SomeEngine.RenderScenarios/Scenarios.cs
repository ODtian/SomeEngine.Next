using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Pipeline;
using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.Graphics;
using Texture = SomeEngine.Assets.Schema.Texture.Asset;

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
using var sceneFile = await WorldAsset.OpenAsync(loader, new AssetId<World>(sceneId));
var worldTypes = SceneSerialization.CreateRegistry();
using World scene = await sceneFile.LoadAsync(worldTypes);
sceneFile.Name = mode + " rendering scenario";
Entity[] allInstances = Entities(scene, new QueryDefinitionBuilder().Read<MeshSource>().Read<WorldTransform>());
Entity[] instances = allInstances.Take(mode == "phong" ? 1 : mode == "wpo-alpha" ? 8 : mode == "sparse-motion" ? 128 : 4).ToArray();
foreach (Entity entity in allInstances.Skip(instances.Length)) scene.DestroyEntity(entity);
for (int index = 0; index < instances.Length; index++)
{
    Entity entity = instances[index];
    WorldTransform transform = scene.Read<WorldTransform>(entity);
    transform.Qvvs.Position = new(0, 0, 12);
    transform.Qvvs.Stretch = new(0.8f);
    scene.Replace(entity, transform);
    MeshSource mesh = scene.Read<MeshSource>(entity);
    mesh.BoundsExpansion = mode == "wpo-alpha" ? .31f : 1;
    scene.Replace(entity, mesh);
}
if (mode is not ("phong" or "sparse-motion"))
{
    for (int index = 0; index < instances.Length; index++)
    {
        Entity entity = instances[index];
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(mode == "factors" ? (index == 3 ? 4 : index) : 2);
        using Mesh.Asset mesh = ClusterBuilder.ProcessRaw(vertices, attributes, indices,
            [new MeshMaterialSlot(Materials(scene, entity)[0].Value)], $"Tessellation factor geometry {index}");
        project.RegisterAsset(await mesh.SaveAsync(Path.Combine(project.RootPath, $"assets/Meshes/factor-{index}.mesh.asset")));
        MeshSource meshSource = scene.Read<MeshSource>(entity);
        meshSource.Mesh = mesh.Id;
        scene.Replace(entity, meshSource);
        WorldTransform transform = scene.Read<WorldTransform>(entity);
        transform.Qvvs.Position = new(index % 2 == 0 ? -1.5f : 1.5f, index / 2 == 0 ? -1.5f : 1.5f, 12);
        if (mode == "wpo-alpha" && index >= 4) transform.Qvvs.Position = new(index % 2 == 0 ? -8 : 8, (index - 4) / 2 == 0 ? -4 : 4, 65);
        scene.Replace(entity, transform);
    }
}
if (mode is "wpo-alpha" or "displacement" or "materials")
{
    for (int index = 0; index < instances.Length; index++)
        scene.Replace(instances[index], new MeshMotion { Amplitude = new(.2f, .1f, .15f), Seed = (uint)index + 1 });
    foreach (Entity entity in Entities(scene, new QueryDefinitionBuilder().Read<PointLight>())) scene.DestroyEntity(entity);
    scene.CreateEntity(new PointLight(new(3, 2, 7), 40, new(1, .6f, .2f), 4));
}
if (mode == "displacement")
{
    float[] heights = Enumerable.Range(0, 64).Select(i => .5f + .5f * MathF.Sin((i % 8) * MathF.PI / 4) * MathF.Sin((i / 8) * MathF.PI / 4)).ToArray();
    using Texture bounds = DisplacementBoundsTexture.Create(Guid.NewGuid(), "height bounds", 8, 8, heights, out _);
    project.RegisterAsset(await bounds.SaveAsync(Path.Combine(project.RootPath, "assets/Textures/scenario-bounds.texture.asset")));
    using Texture height = new() { Name = "heights", Dimension = TextureDimension.Texture2D,
        Width = 8, Height = 8, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
        Format = Format.R32Float, SampledFormat = Format.R32Float, SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        MipTiles = [new TextureMipTile { Width = 8, Height = 8, RowPitch = 32, SlicePitch = 256,
            Payload = MemoryMarshal.AsBytes(heights.AsSpan()).ToArray() }] };
    project.RegisterAsset(await height.SaveAsync(Path.Combine(project.RootPath, "assets/Textures/scenario-height.texture.asset")));
    using var material = await StandardMaterial.Asset.OpenAsync(loader, Materials(scene, instances[0])[0]);
    material.AlbedoMap = height.Id;
    material.NormalMap = bounds.Id;
    project.RegisterAsset(await material.SaveAsync(Path.Combine(project.RootPath, "assets/Materials/scenario.material.asset")));
}
if (mode == "materials")
{
    using var template = await StandardMaterial.Asset.OpenAsync(loader, Materials(scene, instances[0])[0]);
    for (int index = 0; index < instances.Length; index++)
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            byte value = (x + y + index) % (index + 2) == 0 ? (byte)0 : (byte)255;
            for (int channel = 0; channel < 4; channel++) pixels[(y * 8 + x) * 4 + channel] = value;
        }
        using Texture mask = new() { Name = "mask " + index, Dimension = TextureDimension.Texture2D,
            Width = 8, Height = 8, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8G8B8A8UNorm, SampledFormat = Format.R8G8B8A8UNorm, SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 8, Height = 8, RowPitch = 32, SlicePitch = 256, Payload = pixels }] };
        project.RegisterAsset(await mask.SaveAsync(Path.Combine(project.RootPath, $"assets/Textures/scenario-mask-{index}.texture.asset")));
        using var material = template.Duplicate(new(Guid.NewGuid()));
        material.Name = "Phong masked " + index;
        material.AlbedoMap = mask.Id;
        project.RegisterAsset(await material.SaveAsync(Path.Combine(project.RootPath, $"assets/Materials/scenario-{index}.material.asset")));
        AssetId<Material> id = material.Id;
        scene.ExecuteBufferWrite<MaterialSource, AssetId<Material>>(instances[index], ref id,
            static (DynamicBuffer<MaterialSource> buffer, ref AssetId<Material> value) => { buffer.Clear(); buffer.Add(new(value)); });
    }
}
if (mode == "sparse-motion")
{
    for (int index = 0; index < instances.Length; index++)
    {
        Entity entity = instances[index];
        WorldTransform transform = scene.Read<WorldTransform>(entity);
        transform.Qvvs.Position = new((index % 16 - 7.5f) * 1.2f, (index / 16 - 3.5f) * 1.2f, 25);
        transform.Qvvs.Stretch = new(.4f);
        scene.Replace(entity, transform);
        scene.Replace(entity, new MeshMotion { Amplitude = new(index == 0 ? .5f : 0, 0, 0), Seed = 1 });
        MeshSource mesh = scene.Read<MeshSource>(entity);
        mesh.BoundsExpansion = 0;
        scene.Replace(entity, mesh);
    }
}
sceneFile.WriteWorld(scene, worldTypes);
project.RegisterAsset(await sceneFile.SaveAsync(Path.Combine(project.RootPath, "assets/Scenes/scenario.scene.asset")));
Console.WriteLine(destination);

static Entity[] Entities(World world, QueryDefinitionBuilder definition)
{
    QueryHandle query = world.Query(definition);
    var entities = new List<Entity>();
    try
    {
        world.ExecuteQuery(query, ref entities, static (QueryCursor rows, ref List<Entity> values) =>
        {
            foreach (var row in rows.Rows) values.Add(row.Entity);
        });
        return [.. entities];
    }
    finally { world.ReleaseQuery(query); }
}

static AssetId<Material>[] Materials(World world, Entity entity)
{
    AssetId<Material>[] ids = [];
    world.ExecuteBufferRead<MaterialSource, AssetId<Material>[]>(entity, ref ids,
        static (BufferView<MaterialSource> buffer, ref AssetId<Material>[] values) =>
        {
            values = new AssetId<Material>[buffer.Count];
            for (int index = 0; index < values.Length; index++) values[index] = buffer[index].Material;
        });
    return ids;
}
