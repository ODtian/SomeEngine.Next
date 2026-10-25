using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Render;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Render.Instances;
using SomeEngine.Resources;
using ClusterGeometry = SomeEngine.Assets.Schema.ClusterGeometry;
using ValueType = SomeEngine.Resources.Geometry.ValueType;

namespace SomeEngine.Animation.Checks.PoseExample;

/// <summary>Rebuildable example resources using public assets, geometry conversion and scene serialization.</summary>
internal static class PoseConsumerContent
{
    // This example matches the shared two-influence fixture. Materials choose the vertex encoding.
    internal const string Encoding = "someengine.tests.skin.float.v1";

    internal static async Task PrepareAsync(string commonDirectory, string engineShaders, string directory, bool reuseShader)
    {
        directory = Path.GetFullPath(directory);
        foreach (string part in new[] { "assets", "Library/AssetManifest" })
            foreach (string input in Directory.EnumerateFiles(Path.Combine(commonDirectory, part), "*", SearchOption.AllDirectories))
            {
                string output = Path.Combine(directory, Path.GetRelativePath(commonDirectory, input));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(input, output, overwrite: true);
            }
        string shaders = Path.Combine(directory, "assets/Shaders");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "PoseExample/standard.slang"), Path.Combine(shaders, "standard.slang"), true);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "PoseExample/standard.shader"), Path.Combine(shaders, "standard.shader"), true);
        RenderInstancePropertyLayout layout = RenderInstancePropertyLayout.Compose(
            ClusterRenderFeature.InstanceLayout, SkinInstanceProducer.Layout);
        int bindingWord = layout.Resolve<RenderSkinBinding>(SkinInstanceProducer.BindingKey).Descriptor.MetadataWordOffset;
        string layoutSource = ClusterShaderSources.BuildInstanceLayout(layout).Replace("#endif",
            $"static const uint SKIN_BINDING_METADATA_WORD = {bindingWord}u;\n\n#endif", StringComparison.Ordinal);
        Directory.CreateDirectory(Path.Combine(shaders, "generated"));
        await File.WriteAllTextAsync(Path.Combine(shaders, "generated/render_instance_layout.slang"), layoutSource);
        var project = new AssetProject(directory, [], shaderIncludeDirectories: [Path.GetFullPath(engineShaders)]);
        if (!reuseShader) await project.BuildAsync(Path.Combine(shaders, "standard.shader"));
        await WriteSceneAsync(project);
        Console.WriteLine($"POSE_CONSUMER_CONTENT encoding={Encoding} shader={(reuseShader ? "reused" : "compiled")} directory={directory}");
    }

    private static async Task WriteSceneAsync(AssetProject project)
    {
        Guid material = project.Manifest.List(MaterialAsset.FileType).Single(value => value.Name == "Standard").AssetGuid;
        Vector3[] positions = [new(-.5f, -.5f, 0), new(.5f, -.5f, 0), new(-.5f, .5f, 0), new(.5f, .5f, 0)];
        List<RawAttribute> raw =
        [
            new("NORMAL", [0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1], 3, ValueType.Float32, 3, false),
            new("TANGENT", [1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1], 4, ValueType.Float32, 4, false),
            new("TEXCOORD_0", [0, 0, 1, 0, 0, 1, 1, 1], 2, ValueType.Float32, 2, false),
        ];
        using MeshAsset converted = ClusterBuilder.ProcessRaw(positions, raw, [0, 2, 1, 1, 2, 3], [new(material)], "Example panel");
        ClusterGeometryBlock geometry = await converted.GetBlockAsync<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock surface = await converted.GetBlockAsync<ClusterVertexAttributesBlock>();
        var attributes = new ClusterVertexAttributes[surface.Clusters.Count];
        for (int cluster = 0; cluster < attributes.Length; cluster++)
        {
            ClusterGeometry shape = geometry.Clusters[cluster];
            ClusterVertexAttributes item = surface.Clusters[cluster];
            byte[] data = new byte[item.Data.Length + shape.Vertices.Length * 16 + 8];
            item.Data.AsSpan().CopyTo(data);
            for (int vertex = 0; vertex < shape.Vertices.Length; vertex++)
            {
                uint source = geometry.Vertices[(int)shape.Vertices[vertex]].SourceVertex;
                float weight = source < 2 ? .75f : .25f;
                Span<byte> row = data.AsSpan(item.Data.Length + vertex * 16);
                BinaryPrimitives.WriteUInt32LittleEndian(row, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(row[4..], 1);
                BinaryPrimitives.WriteSingleLittleEndian(row[8..], weight);
                BinaryPrimitives.WriteSingleLittleEndian(row[12..], 1 - weight);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(data.Length - 8), (uint)item.Data.Length);
            attributes[cluster] = item with { Data = data.ToImmutableArray() };
            geometry.Clusters[cluster] = shape with { Deform = ClusterDeform.Skin };
        }
        using var mesh = new MeshAsset(new Guid("3a5d9c09-dd62-4e4a-9fb3-9a46001e9c27")) { Name = "Example panel" };
        mesh.SetBlock(await converted.GetBlockAsync<MeshBlock>());
        mesh.SetBlock(await converted.GetBlockAsync<MeshMaterialsBlock>());
        mesh.SetBlock(geometry);
        mesh.SetBlock(new ClusterVertexAttributesBlock { VertexIdentity = geometry.VertexIdentity, Encoding = Encoding, Clusters = attributes });
        string meshPath = Path.Combine(project.RootPath, "assets/Models/pose-consumer.mesh.asset");
        project.RegisterAsset(await mesh.SaveAsync(meshPath));
        using var world = new World();
        foreach (float x in new[] { -1.1f, 1.1f })
        {
            Entity entity = world.CreateEntity(new WorldTransform { Qvvs = new(new(x, 0, 5), Quaternion.Identity) });
            world.Add(entity, new MeshSource { Mesh = new(mesh.Id), BoundsExpansion = 3 });
        }
        world.CreateEntity(new SceneCamera { Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
            VerticalFieldOfView = 1.1f, NearPlane = .1f, FarPlane = 100 });
        world.CreateEntity(new DirectionalLight(new(.35f, -.85f, .4f), Vector3.One, 3));
        AssetEntry sceneEntry = project.Manifest.List(WorldAsset.FileType).Single(value => value.Name == "Default");
        using var scene = new WorldAsset(sceneEntry.AssetGuid) { Name = "Default" };
        scene.WriteWorld(world, SceneSerialization.CreateRegistry());
        project.RegisterAsset(await scene.SaveAsync(Path.Combine(project.RootPath, sceneEntry.Path)));
        AssetEntry settingsEntry = project.Manifest.List(ApplicationSettingsAsset.FileType).Single();
        using ApplicationSettingsAsset settings = await ApplicationSettingsAsset.OpenAsync(Path.Combine(project.RootPath, settingsEntry.Path));
        ApplicationSettingsBlock values = await settings.GetBlockAsync<ApplicationSettingsBlock>();
        values.Width = 640; values.Height = 480;
        project.RegisterAsset(await settings.SaveAsync(Path.Combine(project.RootPath, settingsEntry.Path)));
    }
}
