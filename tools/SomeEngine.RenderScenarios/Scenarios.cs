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
using SomeEngine.Resources;
using SomeEngine.Assets.Pipeline;
using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.Graphics;
using Texture = SomeEngine.Assets.Schema.TextureAsset;

if (args.Length != 3)
    throw new ArgumentException("Usage: SomeEngine.RenderScenarios <built-runtime-directory> <new-output-directory> <phong|factors|materials|wpo-alpha|displacement|sparse-motion|lighting|occlusion|lod-near|lod-far>");
string source = Path.GetFullPath(args[0]);
string destination = Path.GetFullPath(args[1]);
string mode = args[2];
if (mode is not ("phong" or "factors" or "materials" or "wpo-alpha" or "displacement" or "sparse-motion" or "lighting" or "occlusion" or "lod-near" or "lod-far"))
    throw new ArgumentException("Unknown scenario: " + mode);
if (!File.Exists(Path.Combine(source, "SomeEngine.Runtime.dll")) ||
    !File.Exists(Path.Combine(source, "Library", "AssetManifest", AssetManifest.CatalogFileName)))
    throw new ArgumentException("Source must contain a built application and its own content manifest.");
if (!File.Exists(Path.Combine(sourceShaders, "standard.shader")) || !Directory.Exists(engineShaders))
    throw new ArgumentException("Select the matching project's shader sources and engine include library.");
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
foreach (string file in Directory.EnumerateFiles(sourceShaders, "*", SearchOption.AllDirectories)
    .Where(path => path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slang", StringComparison.OrdinalIgnoreCase)))
{
    string output = Path.Combine(destination, "assets", "Shaders", Path.GetRelativePath(sourceShaders, file));
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.Copy(file, output, overwrite: true);
}
string shaderPath = Path.Combine(destination, "assets", "Shaders", "standard.slang");
string shader = File.ReadAllText(shaderPath);
if (mode is "phong" or "factors" or "materials")
    shader = shader.Replace("#include \"standard_material.slang\"", "#include \"standard_material.slang\"\n#include \"phong_tessellation.slang\"")
        .Replace("typedef LinearSurface DomainEvaluate;", "typedef PhongTessellation DomainEvaluate;") + "\n#include \"cluster_tessellation_entries.slang\"\n";
else if (mode == "wpo-alpha")
{
    string pixel = """
struct CheckerVarying
{
    float2 UV : TEXCOORD0;
};

struct CheckerVisibility : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = CheckerVarying;
    Texture2D<float4> AlbedoMap;
    SamplerState MaterialSampler;

    Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input)
    {
        Varying result;
        result.UV = vertex.uv0;
        return result;
    }

    Varying readLane(Varying value, uint lane)
    {
        Varying result;
        result.UV = WaveReadLaneAt(value.UV, lane);
        return result;
    }

    Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary)
    {
        Varying result;
        result.UV = a.UV * bary.x + b.UV * bary.y + c.UV * bary.z;
        return result;
    }

    Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input)
    {
        return interpolateVertex(a, b, c, input.PerspectiveBarycentric);
    }

    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        float alpha = AlbedoMap.SampleLevel(MaterialSampler, varying.UV, 0.0).a;
        uint2 checker = uint2(floor(frac(varying.UV) * 8.0));
        result.Visible = alpha > 0.5 && ((checker.x + checker.y) & 1u) == 0u ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
};
""";
    shader = shader.Replace("typedef StaticVertexEval VertexEvaluate;", pixel + "\ntypedef WaveVertexEval VertexEvaluate;")
        .Replace("typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;", "typedef CheckerVisibility VisibilityEvaluate;");
}
else if (mode == "displacement")
{
    string domain = """
#include "texture_displacement.slang"
struct SampledDisplacement : IDomainEvaluate<StandardVertex>
{
    typealias TriangleData = Triangle;
    typealias Surface = SurfaceEval;

    Texture2D<float> AlbedoMap;
    Texture2D<float2> NormalMap;
    SamplerState MaterialSampler;
    TextureDisplacement surface() { return MakeTextureDisplacement(AlbedoMap, NormalMap, MaterialSampler, float2(0.0, 1.0), 0.15, -0.075); }
    Triangle makeTriangle(StandardVertex a, StandardVertex b, StandardVertex c) { return MakeStandardTriangle(a,b,c); }
    float3 cornerPosition(Triangle t, uint c) { return c == 0u ? t.Position0 : (c == 1u ? t.Position1 : t.Position2); }
    float3 position(Triangle t, float3 b) { return surface().position(t,b); }
    SurfaceEval eval(Triangle t, float3 b) { return surface().eval(t,b); }
    SurfaceBarycentrics reconstruct(Triangle t, SurfaceReconstructionInput i) { return surface().reconstruct(t,i); }
    void bounds(Triangle t, out float3 lo, out float3 hi) { surface().bounds(t,lo,hi); }
    void patchBounds(Triangle t, float3 b0, float3 b1, float3 b2, out float3 lo, out float3 hi) { surface().patchBounds(t,b0,b1,b2,lo,hi); }
    DomainErrorBounds errorBounds(Triangle t, float3 b0, float3 b1, float3 b2, float3 p0, float3 p1, float3 p2) { return surface().errorBounds(t,b0,b1,b2,p0,p1,p2); }
};
""";
    shader = shader.Replace("typedef LinearSurface DomainEvaluate;", domain + "\ntypedef SampledDisplacement DomainEvaluate;") + "\n#include \"cluster_tessellation_entries.slang\"\n";
    // This scenario uses these two material textures as height and height bounds.
    // Its surface uses a white base and the displaced normal, not the bounds as a normal map.
    string surfacePath = Path.Combine(destination, "assets", "Shaders", "standard.slang");
    string surface = File.ReadAllText(surfacePath)
        .Replace("float4 albedoSample = AlbedoMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0);", "float4 albedoSample = 1.0.xxxx;")
        .Replace("float3 normalTS     = NormalMap.SampleLevel(MaterialSampler, ctx.uv0, 0.0).rgb * 2.0 - 1.0;", "float3 normalTS = float3(0.0, 0.0, 1.0);");
    File.WriteAllText(surfacePath, surface);
}

if (mode == "materials")
{
    const string pixel = """
struct MaskVarying
{
    float2 UV : TEXCOORD0;
};

struct MaskVisibility : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = MaskVarying;
    Texture2D<float4> AlbedoMap;
    SamplerState MaterialSampler;

    Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input)
    {
        Varying result;
        result.UV = vertex.uv0;
        return result;
    }

    Varying readLane(Varying value, uint lane)
    {
        Varying result;
        result.UV = WaveReadLaneAt(value.UV, lane);
        return result;
    }

    Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary)
    {
        Varying result;
        result.UV = a.UV * bary.x + b.UV * bary.y + c.UV * bary.z;
        return result;
    }

    Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input)
    {
        return interpolateVertex(a, b, c, input.PerspectiveBarycentric);
    }

    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        result.Visible = AlbedoMap.SampleLevel(MaterialSampler, varying.UV, 0.0).x > 0.5 ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
};
""";
    shader = shader.Replace(
        "typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;",
        pixel + "\ntypedef MaskVisibility VisibilityEvaluate;");
}
File.WriteAllText(shaderPath, shader);
AssetProject project = new AssetProject(destination, [Game.Importing.MaterialImport.CreateGltf()], sourceFormats: [new ShaderSourceFormat([engineShaders])]);
await project.BuildAsync(Path.ChangeExtension(shaderPath, ".shader"));
IAssetStorage storage = project.CreateStorage();
Guid sceneId = project.Resolve("assets/Scenes/default.scene.asset")!.Value;
if (!storage.TryFind(sceneId, out AssetEntry entry)) throw new InvalidOperationException();
await using var sceneDocument = await AssetProject.OpenAsync<RenderScene>(storage, entry);
RenderScene scene = sceneDocument.Root;
scene.Name = mode + " rendering scenario";
int instanceCount = mode switch
{
    "phong" or "lod-near" or "lod-far" => 1,
    "occlusion" or "sparse-motion" => 128,
    "wpo-alpha" => 8,
    _ => 4,
};
scene.MeshInstances = scene.MeshInstances!.Take(instanceCount).ToArray();
for (int index = 0; index < scene.MeshInstances.Count; index++)
{
    SceneMeshInstance instance = scene.MeshInstances[index];
    instance.Position = new SceneVector3 { X = 0, Y = 0, Z = 12 };
    instance.Scale = new SceneVector3 { X = 0.8f, Y = 0.8f, Z = 0.8f };
    instance.BoundsExpansion = mode == "wpo-alpha" ? .31f : 1;
    if (mode is "lod-near" or "lod-far")
    {
        instance.Position.Z = mode == "lod-near" ? 6 : 80;
        instance.BoundsExpansion = 0;
        instance.MotionAmplitude = new SceneVector3();
    }
}
if (mode is not ("phong" or "sparse-motion" or "occlusion" or "lod-near" or "lod-far"))
{
    for (int index = 0; index < instances.Length; index++)
    {
        Entity entity = instances[index];
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(mode == "factors" ? (index == 3 ? 4 : index) : 2);
        using MeshAsset mesh = ClusterBuilder.ProcessRaw(vertices, attributes, indices,
            [new MeshMaterialSlot(Materials(scene, entity)[0].Value)], $"Tessellation factor geometry {index}");
        project.RegisterAsset(await mesh.SaveAsync(Path.Combine(project.RootPath, $"assets/Meshes/factor-{index}.mesh.asset")));
        MeshSource meshSource = scene.Read<MeshSource>(entity);
        meshSource.Mesh = mesh.Reference;
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
    using var heightPixels = new TextureBlock { Dimension = TextureDimension.Texture2D,
        Width = 8, Height = 8, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
        Format = Format.R32Float, SampledFormat = Format.R32Float, SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        MipTiles = [new TextureMipTile { Width = 8, Height = 8, RowPitch = 32, SlicePitch = 256,
            ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 256 }] };
    heightPixels.Chunks.Set(heightPixels.MipTiles[0].ChunkKey, MemoryMarshal.AsBytes(heights.AsSpan()));
    using Texture height = new() { Name = "heights" };
    height.SetBlock(heightPixels);
    project.RegisterAsset(await height.SaveAsync(Path.Combine(project.RootPath, "assets/Textures/scenario-height.texture.asset")));
    using var material = await StandardMaterialAsset.OpenAsync(loader, Materials(scene, instances[0])[0]);
    var parameters = new StandardMaterialParameters(material.GetBlock<MaterialBlock>());
    parameters.AlbedoMap = height.Reference;
    parameters.NormalMap = bounds.Reference;
    project.RegisterAsset(await material.SaveAsync(Path.Combine(project.RootPath, project.Manifest.Assets[material.Id].Path)));
}
if (mode == "materials")
{
    using var template = await StandardMaterialAsset.OpenAsync(loader, Materials(scene, instances[0])[0]);
    for (int index = 0; index < instances.Length; index++)
    {
        byte[] pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            byte value = (x + y + index) % (index + 2) == 0 ? (byte)0 : (byte)255;
            for (int channel = 0; channel < 4; channel++) pixels[(y * 8 + x) * 4 + channel] = value;
        }
        using var maskPixels = new TextureBlock { Dimension = TextureDimension.Texture2D,
            Width = 8, Height = 8, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = Format.R8G8B8A8UNorm, SampledFormat = Format.R8G8B8A8UNorm, SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 8, Height = 8, RowPitch = 32, SlicePitch = 256, ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 256 }] };
        maskPixels.Chunks.Set(maskPixels.MipTiles[0].ChunkKey, pixels);
        using Texture mask = new() { Name = "mask " + index };
        mask.SetBlock(maskPixels);
        project.RegisterAsset(await mask.SaveAsync(Path.Combine(project.RootPath, $"assets/Textures/scenario-mask-{index}.texture.asset")));
        using var material = template.CreateCopy();
        material.Name = "Phong masked " + index;
        new StandardMaterialParameters(material.GetBlock<MaterialBlock>()).AlbedoMap = mask.Reference;
        project.RegisterAsset(await material.SaveAsync(Path.Combine(project.RootPath, $"assets/Materials/scenario-{index}.material.asset")));
        AssetId<MaterialAsset> id = material.Reference;
        scene.ExecuteBufferWrite<MaterialSource, AssetId<MaterialAsset>>(instances[index], ref id,
            static (DynamicBuffer<MaterialSource> buffer, ref AssetId<MaterialAsset> value) => { buffer.Clear(); buffer.Add(new(value)); });
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
if (mode == "lighting")
{
    scene.Camera!.Position = new SceneVector3 { Y = 4, Z = -3 };
    scene.Camera.Target = new SceneVector3 { Y = -.3f, Z = 9 };
    for (int index = 0; index < 3; index++)
    {
        scene.MeshInstances[index].Position = new SceneVector3 { X = (index - 1) * 2.3f, Z = 8 + index };
        scene.MeshInstances[index].MotionAmplitude = new SceneVector3 { X = index == 1 ? .8f : 0 };
        scene.MeshInstances[index].BoundsExpansion = 0;
    }
    SceneMeshInstance floor = scene.MeshInstances[3];
    Vector3[] corners = [new(-6, -.85f, 3), new(6, -.85f, 3), new(6, -.85f, 16), new(-6, -.85f, 16)];
    List<RawAttribute> attributes =
    [
        new("NORMAL", [0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0], 3, SomeEngine.Assets.Data.ValueType.Int8, 3, true),
        new("TANGENT", [1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1], 4, SomeEngine.Assets.Data.ValueType.Int8, 4, true),
        new("TEXCOORD_0", [0, 0, 1, 0, 1, 1, 0, 1], 2, SomeEngine.Assets.Data.ValueType.Float16, 2, false),
    ];
    Mesh floorMesh = ClusterBuilder.ProcessRaw(corners, attributes, [0, 2, 1, 0, 3, 2],
        [new MeshMaterialSlot(AssetGuid.Parse(floor.MaterialGuids![0]))], "Shadow receiver floor");
    floor.MeshGuid = project.CreateAsset("assets/Meshes/floor.mesh.asset", floorMesh).ToFlatString();
    floor.Position = new SceneVector3();
    floor.Scale = new SceneVector3 { X = 1, Y = 1, Z = 1 };
    floor.MotionAmplitude = new SceneVector3();
    floor.BoundsExpansion = 0;
    if (!storage.TryFind(AssetGuid.Parse(floor.MaterialGuids[0]), out AssetEntry materialEntry)) throw new InvalidOperationException();
    await using var materialDocument = await AssetProject.OpenAsync<Material>(storage, materialEntry);
    StandardMaterial material = (StandardMaterial)materialDocument.Root;
    Vector4[] colors = [new(.8f, .12f, .08f, 1), new(.12f, .65f, .2f, 1), new(.08f, .25f, .8f, 1), new(.5f, .5f, .5f, 1)];
    for (int index = 0; index < 4; index++)
    {
        material.AssetGuid = AssetGuid.New().ToFlatString();
        material.Name = "Lighting surface " + index;
        material.BaseColorTint = colors[index];
        material.Roughness = .6f;
        project.CreateAsset<Material>($"assets/Materials/lighting-{index}.material.asset", material);
        scene.MeshInstances[index].MaterialGuids = [material.AssetGuid];
    }
    scene.DirectionalLights = [new SceneDirectionalLight { Direction = new SceneVector3 { X = .4f, Y = -1, Z = .6f },
        Color = new SceneVector3 { X = 1, Y = 1, Z = 1 }, Intensity = 2 }];
    scene.PointLights = [new ScenePointLight { Position = new SceneVector3 { X = -3, Y = 2, Z = 6 },
        Color = new SceneVector3 { X = 1, Y = .25f, Z = .1f }, Range = 8, Intensity = 35 }];
    scene.SpotLights = [new SceneSpotLight { Position = new SceneVector3 { X = 3, Y = 4, Z = 8 },
        Direction = new SceneVector3 { X = -.25f, Y = -1, Z = .15f },
        Color = new SceneVector3 { X = .15f, Y = .4f, Z = 1 }, Range = 10, Intensity = 80,
        InnerConeCos = .95f, OuterConeCos = .8f }];
}
if (mode == "occlusion")
{
    foreach ((SceneMeshInstance instance, int index) in scene.MeshInstances.Select((value, index) => (value, index)))
    {
        instance.Position = new SceneVector3 { X = (index % 16 - 7.5f) * .8f, Y = (index / 16 - 3.5f) * .8f, Z = 18 };
        instance.Scale = new SceneVector3 { X = .3f, Y = .3f, Z = .3f };
        instance.MotionAmplitude = new SceneVector3();
        instance.BoundsExpansion = 0;
    }
    List<RawAttribute> attributes =
    [
        new("NORMAL", [0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1], 3, SomeEngine.Assets.Data.ValueType.Int8, 3, true),
        new("TEXCOORD_0", [0, 0, 1, 0, 1, 1, 0, 1], 2, SomeEngine.Assets.Data.ValueType.Float16, 2, false),
    ];
    string material = scene.MeshInstances[0].MaterialGuids![0];
    Mesh wall = ClusterBuilder.ProcessRaw([new(-2, -3.5f, 0), new(2, -3.5f, 0), new(2, 3.5f, 0), new(-2, 3.5f, 0)],
        attributes, [0, 2, 1, 0, 3, 2], [new MeshMaterialSlot(AssetGuid.Parse(material))], "Moving occluder");
    scene.MeshInstances = [.. scene.MeshInstances, new SceneMeshInstance
    {
        MeshGuid = project.CreateAsset("assets/Meshes/occluder.mesh.asset", wall).ToFlatString(),
        MaterialGuids = [material], Position = new SceneVector3 { Z = 10 }, Rotation = new SceneQuaternion(),
        Scale = new SceneVector3 { X = 1, Y = 1, Z = 1 },
        MotionAmplitude = new SceneVector3 { X = 4 }, MotionSeed = 1,
    }];
}
project.CreateAsset("assets/Scenes/scenario.scene.asset", scene);
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

static AssetId<MaterialAsset>[] Materials(World world, Entity entity)
{
    AssetId<MaterialAsset>[] ids = [];
    world.ExecuteBufferRead<MaterialSource, AssetId<MaterialAsset>[]>(entity, ref ids,
        static (BufferView<MaterialSource> buffer, ref AssetId<MaterialAsset>[] values) =>
        {
            values = new AssetId<MaterialAsset>[buffer.Count];
            for (int index = 0; index < values.Length; index++) values[index] = buffer[index].Material;
        });
    return ids;
}
