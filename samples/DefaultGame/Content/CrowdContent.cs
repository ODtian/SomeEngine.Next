using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace DefaultGame.Content;

/// <summary>Builds the procedural crowd example with two material implementations of the same deformation.</summary>
public static class CrowdContent
{
    public static async Task BuildMaterialsAsync(string projectRoot, string engineRoot)
    {
        string template = await File.ReadAllTextAsync(Path.Combine(projectRoot, "assets/Shaders/standard.slang"));
        AssetManifest common = AssetManifest.Load(Path.Combine(projectRoot, "Library/AssetManifest"));
        foreach ((string directory, bool cached) in new[] { ("CrowdInline", false), ("CrowdCache", true) })
        {
            var project = new AssetProject(Path.Combine(projectRoot, directory), [],
                shaderIncludeDirectories: [Path.Combine(engineRoot, "assets/Shaders")]);
            foreach (AssetEntry entry in common.Assets.Values.Where(entry => entry.AssetType == ShaderAsset.FileType
                && entry.Path != "assets/Shaders/standard.shader.asset"))
            {
                string destination = Path.Combine(project.RootPath, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(projectRoot, entry.Path), destination, overwrite: true);
                project.RegisterAsset(entry);
            }
            await WriteShaderAsync(project, template, cached);
        }
    }

    public static async Task BuildAsync(string projectRoot, string engineRoot)
    {
        string inlineRoot = Path.Combine(projectRoot, "CrowdInline");
        string cacheRoot = Path.Combine(projectRoot, "CrowdCache");
        CopyContent(projectRoot, inlineRoot);
        var example = new CrowdExample();
        var project = new AssetProject(inlineRoot, [], shaderIncludeDirectories: [Path.Combine(engineRoot, "assets/Shaders")]);
        string template = await File.ReadAllTextAsync(Path.Combine(projectRoot, "assets/Shaders/standard.slang"));
        await WriteShaderAsync(project, template, cached: false);
        string meshPath = Path.Combine(inlineRoot, "assets/Models/crowd.mesh.asset");
        Guid meshId = Guid.Empty;
        if (File.Exists(meshPath))
        {
            using MeshAsset previous = await MeshAsset.OpenAsync(meshPath);
            meshId = previous.Id;
        }
        using MeshAsset mesh = await ImportCharacterAsync(project, example, meshId);
        project.RegisterAsset(await mesh.SaveAsync(meshPath));
        await WriteSceneAsync(project, mesh.Id);
        await SetResolutionAsync(project);
        CopyContent(inlineRoot, cacheRoot);
        var cached = new AssetProject(cacheRoot, [], shaderIncludeDirectories: [Path.Combine(engineRoot, "assets/Shaders")]);
        await WriteShaderAsync(cached, template, cached: true);
        ClusterGeometryBlock geometry = await mesh.GetBlockAsync<ClusterGeometryBlock>();
        Console.WriteLine($"Crowd example: {CrowdExample.CharacterCount} instances, {CrowdExample.BoneCount} bones each, "
            + $"{example.Positions.Length} source vertices/{example.Indices.Length / 3} triangles per LOD0, 4 positive weights, "
            + $"{geometry.Clusters.Count} stored clusters, levels={string.Join(',', geometry.Clusters.Select(c => c.Level).Distinct().Order())}; "
            + "idle/walk/run/turn, 60 Hz clips, inline and 48-byte current/previous cache materials.");
    }

    private static void CopyContent(string source, string destination)
    {
        string shaders = Path.Combine(destination, "assets/Shaders");
        if (Directory.Exists(shaders))
            foreach (string file in Directory.EnumerateFiles(shaders, "*.slang", SearchOption.AllDirectories))
                if (!File.Exists(Path.Combine(source, Path.GetRelativePath(destination, file)))) File.Delete(file);
        foreach (string folder in new[] { "assets", "Library/AssetManifest" })
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, folder), "*", SearchOption.AllDirectories))
        {
            string output = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file, output, overwrite: true);
        }
    }

    private static async Task WriteShaderAsync(AssetProject project, string template, bool cached)
    {
        string evaluate = SkinEvaluator;
        if (cached) evaluate = "#include \"vertex_deform_cache.slang\"\n" + evaluate
            .Replace("struct CrowdSkin : IVertexEvaluate", "struct CrowdSkin : IVertexDeformCache")
            .Replace("precise float3 getPosition(Vertex value)", CacheEncoding + "\nprecise float3 getPosition(Vertex value)");
        string shader = $"#define CLUSTER_VERTEX_ENCODING \"someengine.cluster.positions.uint16.v1+{CrowdExample.VertexEncoding}\"\n"
            + (cached ? "#define CLUSTER_DEFORM_VERTEX_ENCODING CLUSTER_VERTEX_ENCODING\n" : "")
            + template.Replace("typedef StaticVertexEval VertexEvaluate;", evaluate + (cached
                ? "\ntypedef CrowdSkin DeformEvaluate;\ntypedef CachedVertexEval<CrowdSkin> VertexEvaluate;"
                : "\ntypedef CrowdSkin VertexEvaluate;"))
            .Replace("typedef StaticPositionEval RasterVertexEvaluate;", "typedef VertexEvaluate RasterVertexEvaluate;");
        if (cached) shader += "\n#include \"cluster_deform.slang\"\n";
        await File.WriteAllTextAsync(Path.Combine(project.RootPath, "assets/Shaders/standard.slang"), shader);
        string definitionPath = Path.Combine(project.RootPath, "assets/Shaders/standard.shader");
        using (ShaderAsset definition = await ShaderAsset.OpenSourceAsync(definitionPath))
        {
            ShaderSourceBlock inputs = await definition.GetBlockAsync<ShaderSourceBlock>();
            inputs.Specializations = SampleContent.CreateMaterialSpecializations(0);
            await definition.SaveSourceAsync(definitionPath);
        }
        await project.BuildAsync(definitionPath);
    }

    private static async Task<MeshAsset> ImportCharacterAsync(AssetProject project, CrowdExample example, Guid meshId)
    {
        double[] joints = example.VertexParts.SelectMany(part => new double[] { part * 4, part * 4 + 1, part * 4 + 2, part * 4 + 3 }).ToArray();
        double[] weights = example.VertexParts.SelectMany(_ => new double[] { 102.0 / 255, 77.0 / 255, 51.0 / 255, 25.0 / 255 }).ToArray();
        var attributes = new Dictionary<string, VertexStream>
        {
            ["POSITION"] = new(3, example.Positions.SelectMany(v => new double[] { v.X, v.Y, v.Z }).ToArray()),
            ["NORMAL"] = new(3, example.Normals.SelectMany(v => new double[] { v.X, v.Y, v.Z }).ToArray()),
            ["TANGENT"] = new(4, example.Tangents.SelectMany(v => new double[] { v.X, v.Y, v.Z, v.W }).ToArray()),
            ["JOINTS_0"] = new(4, joints), ["WEIGHTS_0"] = new(4, weights),
        };
        var primitive = new VertexPrimitive(0, 0, example.Indices,
            Enumerable.Range(0, example.Positions.Length).Select(v => (uint)v).ToArray(), attributes, []);
        await using var context = new ImportContext(project.RootPath, "crowd-example", new SourceMeta { SourceGuid = SourceGuid.New() });
        context.EnableImporter<GltfMesh, MeshAsset>(new CrowdStreams(primitive));
        var services = new ServiceCollection();
        SampleAssetCapabilities.AddAssetCapabilities(services);
        ServiceProvider provider = services.BuildServiceProvider();
        context.Hold(provider);
        AssetImporterSetCapability set = provider.GetServices<AssetImporterSetCapability>()
            .Single(value => value.Id == new Guid(GltfImporterSets.ClusterId));
        foreach (AssetImporterCapability capability in provider.GetServices<AssetImporterCapability>()
            .Where(value => set.Importers.Contains(value.Id) || value.Id == new Guid("334ba003-337f-4298-ae72-1a5ce4861591")))
            context.EnableImporter<GltfMesh, MeshAsset>(capability.Bind<GltfMesh>(provider));
        GltfMesh source = ModelRoot.CreateModel().CreateMesh("Crowd example");
        source.CreatePrimitive();
        context.AddAsset(source, new MeshAsset(meshId == Guid.Empty ? Guid.NewGuid() : meshId) { Name = "Crowd example" },
            "mesh:index:0", "assets/Models/crowd.mesh.asset");
        MeshAsset imported = await context.GetAssetAsync<GltfMesh, MeshAsset>(source);
        MeshAsset mesh = imported.Duplicate();
        ClusterVertexAttributesBlock surface = await mesh.GetBlockAsync<ClusterVertexAttributesBlock>();
        ClusterSkinInfluencesBlock skin = await mesh.GetBlockAsync<ClusterSkinInfluencesBlock>();
        mesh.SetBlock(new ClusterVertexAttributesBlock
        {
            VertexIdentity = surface.VertexIdentity, Encoding = CrowdExample.VertexEncoding,
            Clusters = surface.Clusters.Select((value, index) => value with { Data = Encode(value, skin.Clusters[index]).ToImmutableArray() }).ToArray(),
        });
        mesh.SetBlock(new MeshMaterialsBlock { Materials = [new(project.Manifest.List(MaterialAsset.FileType).Single(value => value.Name == "Standard").AssetGuid)] });
        return mesh;
    }

    private sealed class CrowdStreams(VertexPrimitive primitive) : IAssetImporter<GltfMesh>
    {
        public Guid Id { get; } = Guid.NewGuid();
        public IReadOnlyList<Guid> ProducedBlocks { get; } = [VertexStreamsBlock.BlockTypeId];
        public ValueTask ImportAsync(GltfMesh source, ImportContext context, ImportOutput output, CancellationToken cancellationToken = default)
        {
            output.SetBlock(new VertexStreamsBlock([primitive], []));
            return ValueTask.CompletedTask;
        }
    }

    private static byte[] Encode(ClusterVertexAttributes surface, ClusterVertexAttributes skin)
    {
        ClusterVertexAttribute joints = skin.Attributes.Single(value => value.Name == "JOINTS_0");
        ClusterVertexAttribute weights = skin.Attributes.Single(value => value.Name == "WEIGHTS_0");
        var bytes = new byte[surface.Data.Length + joints.VertexCount * 12 + 4];
        surface.Data.AsSpan().CopyTo(bytes);
        for (int vertex = 0; vertex < joints.VertexCount; vertex++)
        {
            Span<byte> row = bytes.AsSpan(surface.Data.Length + vertex * 12);
            for (int influence = 0; influence < 4; influence++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(row[(influence * 2)..], (ushort)joints.ReadComponent(vertex, influence));
                row[8 + influence] = (byte)Math.Round(weights.ReadComponent(vertex, influence) * 255);
            }
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), (uint)surface.Data.Length);
        return bytes;
    }

    private static async Task WriteSceneAsync(AssetProject project, Guid mesh)
    {
        using var world = new World(initialEntityCapacity: 2048);
        for (int index = 0; index < CrowdExample.CharacterCount; index++)
        {
            var entity = world.CreateEntity(new WorldTransform
            {
                Qvvs = new(new Vector3((index % 40 - 19.5f) * 1.45f, 0, index / 40 * 1.7f), Quaternion.Identity),
            });
            world.Add(entity, new MeshSource { Mesh = new(mesh) });
        }
        world.CreateEntity(new SceneCamera
        {
            Position = new(0, 30, -35), Target = new(0, .8f, 20), Up = Vector3.UnitY,
            VerticalFieldOfView = .8f, NearPlane = 40, FarPlane = 90,
        });
        world.CreateEntity(new DirectionalLight(new(.35f, -.85f, .4f), new(1, .96f, .9f), 3));
        AssetEntry entry = project.Manifest.List(WorldAsset.FileType).Single(value => value.Name == "Default");
        using var scene = new WorldAsset(entry.AssetGuid) { Name = "1000 GPU characters" };
        scene.WriteWorld(world, SceneSerialization.CreateRegistry());
        project.RegisterAsset(await scene.SaveAsync(Path.Combine(project.RootPath, entry.Path)));
    }

    private static async Task SetResolutionAsync(AssetProject project)
    {
        AssetEntry entry = project.Manifest.List(ApplicationSettingsAsset.FileType).Single();
        string path = Path.Combine(project.RootPath, entry.Path);
        using ApplicationSettingsAsset application = await ApplicationSettingsAsset.OpenAsync(path);
        ApplicationSettingsBlock settings = await application.GetBlockAsync<ApplicationSettingsBlock>();
        settings.Width = 1920; settings.Height = 1080;
        project.RegisterAsset(await application.SaveAsync(path));
    }

    private const string SkinEvaluator = """
        #include "skin_joint_transforms.slang"
        struct CrowdSkin : IVertexEvaluate
        {
            typedef StandardVertex Vertex;
            void setSurface(inout Vertex result, float3 position, float3 normal)
            {
                result.position = position;
                result.basePosition = position;
                result.worldNormal = normal;
                float3 tangent = normalize(abs(normal.y) < .9 ? cross(float3(0,1,0), normal) : cross(float3(1,0,0), normal));
                result.worldTangent = float4(tangent, 1);
            }
            precise Vertex evaluate(VertexEvalContext ctx)
            {
                uint footer = ctx.pageHeap.Load(ctx.attributeBlobAddress + ctx.attributeBlobSize - 4u);
                uint address = ctx.attributeBlobAddress + footer + ctx.localVertexIndex * 12u;
                uint3 packed = ctx.pageHeap.Load3(address);
                uint4 joints = uint4(packed.x & 65535u, packed.x >> 16u, packed.y & 65535u, packed.y >> 16u);
                float4 weights = float4(packed.z & 255u, (packed.z >> 8u) & 255u, (packed.z >> 16u) & 255u, packed.z >> 24u) / 255.0;
                uint2 binding = LoadSkinBinding(ctx.instanceID);
                precise float3 position = EvalObjectPosition(ctx);
                uint normalStream = ctx.pageHeap.Load(ctx.attributeBlobAddress + 4u);
                precise float3 normal = float3(ReadStandardComponent(ctx, normalStream, 0u, 0),
                    ReadStandardComponent(ctx, normalStream, 1u, 1), ReadStandardComponent(ctx, normalStream, 2u, 0));
                precise float3 localPosition = 0;
                precise float3 localNormal = 0;
                [unroll] for (uint influence = 0; influence < 4u; influence++)
                {
                    precise float4x4 transform = LoadSkinJointTransform(binding, joints[influence], ctx.IsPreviousFrame != 0u);
                    precise float3 transformedPosition = mul(float4(position, 1), transform).xyz;
                    precise float3 transformedNormal = mul(float4(normal, 0), transform).xyz;
                    localPosition += transformedPosition * weights[influence];
                    localNormal += transformedNormal * weights[influence];
                }
                StaticVertexEval source;
                precise Vertex result = source.evaluate(ctx);
                precise float3 worldPosition = TransformInstanceLocalPosition(localPosition, ctx.transform);
                precise float3 worldNormal = normalize(RotateByTransform(localNormal, ctx.transform));
                setSurface(result, worldPosition, worldNormal);
                return result;
            }
            precise float3 getPosition(Vertex value) { return value.position; }
            precise Vertex readLane(Vertex value, uint lane) { StaticVertexEval source; return source.readLane(value, lane); }
        };
        """;

    // This example cache stores current/previous world position and normal in 48 bytes per selected vertex.
    private const string CacheEncoding = """
        uint getCacheByteSize(uint count) { return count * 48u; }
        uint getCacheByteAlignment() { return 16u; }
        void writeCache(RWByteAddressBuffer cache, uint address, uint count, uint index, Vertex current, Vertex previous)
        {
            address += index * 48u;
            cache.Store3(address, asuint(current.position));
            cache.Store3(address + 12u, asuint(current.worldNormal));
            cache.Store3(address + 24u, asuint(previous.position));
            cache.Store3(address + 36u, asuint(previous.worldNormal));
        }
        precise Vertex readCache(ByteAddressBuffer cache, uint address, uint count, VertexEvalContext context)
        {
            StaticVertexEval source;
            precise Vertex result = source.evaluate(context);
            address += context.localVertexIndex * 48u + context.IsPreviousFrame * 24u;
            setSurface(result, asfloat(cache.Load3(address)), asfloat(cache.Load3(address + 12u)));
            return result;
        }
        """;
}
