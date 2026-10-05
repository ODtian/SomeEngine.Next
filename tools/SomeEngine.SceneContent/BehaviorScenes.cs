using System.Numerics;
using System.Text.Json.Nodes;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Render;
using SomeEngine.Resources;
using SomeEngine.Render.Components;

namespace SomeEngine.SceneContent;

public static class BehaviorScenes
{
    public static async Task CreateTessellatedSpheresAsync(string source, string destination, string engineRoot)
    {
        CopyContent(source, destination);
        string shader = File.ReadAllText(Path.Combine(destination, "assets/Shaders/unlit.slang"))
            .Replace("typedef LinearSurface DomainEvaluate;", "#include \"phong_tessellation.slang\"\ntypedef PhongTessellation DomainEvaluate;")
            + "\n#include \"cluster_tessellation_entries.slang\"\n";
        await SetGreenShaderAsync(destination, engineRoot, "tessellated-unlit", shader);
    }

    public static async Task CreateShaderReplacementSpheresAsync(string source, string destination, string engineRoot)
    {
        CopyContent(source, destination);
        string material = File.ReadAllText(Path.Combine(destination, "assets/Shaders/unlit_material.slang"))
            .Replace("albedoSample.rgb * scalars.BaseColorTint.rgb", "albedoSample.rgb * scalars.BaseColorTint.bgr");
        string shader = File.ReadAllText(Path.Combine(destination, "assets/Shaders/unlit.slang"))
            .Replace("#include \"unlit_material.slang\"", material);
        await SetGreenShaderAsync(destination, engineRoot, "swapped-unlit", shader);
    }

    public static async Task CreateStreamingSphereAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = new AssetLoader(project.CreateStorage());
        using var material = await UnlitMaterialAsset.OpenAsync(loader,
            new(project.Manifest.Assets.Values.Single(entry => entry.Name == "red sphere" && entry.AssetType == MaterialAsset.FileType).AssetGuid));
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        using MeshAsset mesh = ClusterBuilder.ProcessRaw(vertices, attributes, indices,
            [new(material.Id)], "streaming sphere");
        project.RegisterAsset(await mesh.SaveAsync(Path.Combine(destination, "assets/Meshes/streaming.mesh.asset")));
        await WriteSceneAsync(project, world =>
        {
            Entity entity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity, 2) });
            world.Add(entity, new MeshSource { Mesh = mesh.Reference });
        });
    }

    public static async Task CreateShadowReceiverAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = new AssetLoader(project.CreateStorage());
        using var original = await MeshAsset.OpenAsync(loader,
            new(project.Manifest.Assets.Values.Single(entry => entry.Name == "red sphere" && entry.AssetType == MeshAsset.FileType).AssetGuid));
        using var material = await StandardMaterialAsset.OpenAsync(loader,
            new(project.Manifest.Assets.Values.Single(entry => entry.Name == "Standard" && entry.AssetType == MaterialAsset.FileType).AssetGuid));
        using var mesh = original.CreateCopy();
        mesh.GetBlock<MeshMaterialsBlock>().Materials = [material.Reference];
        project.RegisterAsset(await mesh.SaveAsync(Path.Combine(destination, "assets/Meshes/receiver.mesh.asset")));
        await WriteSceneAsync(project, world =>
        {
            Entity receiver = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 8), Quaternion.Identity) { Stretch = new(4, 4, .1f) } });
            world.Add(receiver, new MeshSource { Mesh = mesh.Reference });
            Entity caster = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity, .6f) });
            world.Add(caster, new MeshSource { Mesh = mesh.Reference });
            world.CreateEntity(new PointLight(new(4, 2, 3), 20, Vector3.One, 30));
        });
        await SceneContent.SetOutputSizeAsync(destination, 128, 128);
    }

    public static async Task CreateOffCameraShadowCasterAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = new AssetLoader(project.CreateStorage());
        using var receiverMesh = await MeshAsset.OpenAsync(loader,
            new(project.Resolve("assets/Meshes/receiver.mesh.asset")!.Value));
        using var material = await StandardMaterialAsset.OpenAsync(loader,
            new(project.Manifest.Assets.Values.Single(entry => entry.Name == "Standard" && entry.AssetType == MaterialAsset.FileType).AssetGuid));
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        using MeshAsset casterMesh = ClusterBuilder.ProcessRaw(vertices, attributes, indices,
            [new(material.Id)], "off-camera caster");
        project.RegisterAsset(await casterMesh.SaveAsync(Path.Combine(destination, "assets/Meshes/caster.mesh.asset")));
        await WriteSceneAsync(project, world =>
        {
            Entity receiver = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 8), Quaternion.Identity) { Stretch = new(4, 4, .1f) } });
            world.Add(receiver, new MeshSource { Mesh = receiverMesh.Reference });
            // Outside the camera frustum, but on the light-to-receiver-center segment.
            Entity caster = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(3.4f, 1.7f, 3.75f), Quaternion.Identity, .3f) });
            world.Add(caster, new MeshSource { Mesh = casterMesh.Reference });
            world.CreateEntity(new PointLight(new(4, 2, 3), 20, Vector3.One, 30));
        });
    }

    private static async Task SetGreenShaderAsync(string directory, string engineRoot, string name, string source)
    {
        string shaderDirectory = Path.Combine(directory, "assets/Shaders");
        File.WriteAllText(Path.Combine(shaderDirectory, name + ".slang"), source);
        JsonNode document = JsonNode.Parse(File.ReadAllText(Path.Combine(shaderDirectory, "unlit.shader")))!;
        document["Fields"]!["Id"] = Guid.NewGuid().ToString();
        document["Fields"]!["Name"] = name;
        document["Blocks"]![0]!["Fields"]!["SourceNames"] = new JsonArray(name + ".slang");
        string path = Path.Combine(shaderDirectory, name + ".shader");
        File.WriteAllText(path, document.ToJsonString());
        var project = new AssetProject(directory, [], sourceFormats: [new ShaderSourceFormat([Path.Combine(engineRoot, "assets/Shaders")])]);
        await project.BuildAsync(path);
        await using var loader = new AssetLoader(project.CreateStorage());
        AssetEntry entry = project.Manifest.Assets.Values.Single(entry => entry.Name == "green sphere" && entry.AssetType == MaterialAsset.FileType);
        using var material = await UnlitMaterialAsset.OpenAsync(loader, new(entry.AssetGuid));
        material.GetBlock<MaterialBlock>().Shader = new(project.Resolve(path)!.Value);
        project.RegisterAsset(await material.SaveAsync(Path.Combine(directory, entry.Path)));
    }

    private static async Task WriteSceneAsync(AssetProject project, Action<World> create)
    {
        await using var loader = new AssetLoader(project.CreateStorage());
        AssetEntry entry = project.Manifest.List(ApplicationSettingsAsset.FileType).Single();
        using var application = await ApplicationSettingsAsset.OpenAsync(loader, new(entry.AssetGuid));
        using var scene = await WorldAsset.OpenAsync(loader, application.GetBlock<ApplicationSettingsBlock>().Scene);
        using var world = new World();
        create(world);
        world.CreateEntity(new SceneCamera { Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
            VerticalFieldOfView = MathF.PI / 3, NearPlane = .1f, FarPlane = 100 });
        scene.WriteWorld(world, SceneSerialization.CreateRegistry());
        project.RegisterAsset(await scene.SaveAsync(Path.Combine(project.RootPath, project.Manifest.Assets[scene.Id].Path)));
    }

    private static void CopyContent(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string path = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(file, path);
        }
    }
}
