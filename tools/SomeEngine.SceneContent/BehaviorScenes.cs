using System.Numerics;
using System.Text.Json.Nodes;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render;
using SomeEngine.Resources;
using SomeEngine.Render.Components;

namespace SomeEngine.SceneContent;

public static partial class BehaviorScenes
{
    public static async Task CreateTessellatedSpheresAsync(string source, string destination, string engineRoot)
    {
        CopyContent(source, destination);
        string shader = ReadUnlitSource(engineRoot)
            .Replace("import triangle_surface;", "import triangle_surface;\nimport phong_tessellation;")
            .Replace("typedef LinearSurface Domain;", "typedef PhongTessellation Domain;");
        await SetGreenShaderAsync(destination, engineRoot, "tessellated-unlit", shader);
    }

    public static async Task CreateDeformedPhongSpheresAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = SomeEngine.Assets.Importers.EngineShaderContent.OpenFiles(project.RootPath, project.Manifest);
        AssetEntry entry = FindContentAsset(project, "green sphere", MaterialAsset.FileType);
        using var material = await UnlitMaterialAsset.OpenAsync(loader, new(entry.AssetGuid));
        material.GetBlock<MaterialBlock>().Shader = new(FindContentAsset(project, "Deformed Phong", ShaderAsset.FileType).AssetGuid);
        _ = new UnlitMaterialParameters(material.GetBlock<MaterialBlock>())
        {
            AlbedoMap = new(FindContentAsset(project, "Blend checker", TextureAsset.FileType).AssetGuid),
        };
        project.RegisterAsset(await material.SaveAsync(Path.Combine(destination, entry.Path)));
        using var red = await MeshAsset.OpenAsync(loader,
            new(FindContentAsset(project, "red sphere", MeshAsset.FileType).AssetGuid));
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(0);
        using MeshAsset green = ClusterBuilder.ProcessRaw(vertices, attributes, indices, [new(material.Id)], "green Phong sphere");
        project.RegisterAsset(await green.SaveAsync(Path.Combine(destination, "assets/Meshes/green-phong.mesh.asset")));
        using var world = new World();
        Entity redEntity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(-1, 0, 6), Quaternion.Identity) });
        world.Add(redEntity, new MeshSource { Mesh = red.Reference });
        Entity greenEntity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(1, 0, 6), Quaternion.Identity) });
        world.Add(greenEntity, new MeshSource { Mesh = green.Reference, BoundsExpansion = 1.31f });
        await WriteSceneAsync(project, world);
        await SceneContent.SetOutputSizeAsync(destination, 128, 128);
    }

    /// <summary>Sixteen nearby Wave/Phong parts at the default gallery's window resolution.</summary>
    public static async Task CreateDeformedPhongPartsAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = SomeEngine.Assets.Importers.EngineShaderContent.OpenFiles(project.RootPath, project.Manifest);
        using var part = await MeshAsset.OpenAsync(loader,
            new(FindContentAsset(project, "Blend part", MeshAsset.FileType).AssetGuid));
        AssetEntry materialEntry = FindContentAsset(project, "Deformed Phong", MaterialAsset.FileType);
        using (var material = await MaterialAsset.OpenAsync(loader, new(materialEntry.AssetGuid)))
        {
            material.GetBlock<MaterialBlock>().Shader = new(FindContentAsset(project, "Deformed Phong both faces", ShaderAsset.FileType).AssetGuid);
            project.RegisterAsset(await material.SaveAsync(Path.Combine(destination, materialEntry.Path)));
        }
        using var curved = part.CreateCopy();
        curved.GetBlock<MeshMaterialsBlock>().Materials =
            [new(materialEntry.AssetGuid)];
        project.RegisterAsset(await curved.SaveAsync(Path.Combine(destination, "assets/Meshes/phong-part.mesh.asset")));
        using var world = new World();
        for (int group = 0; group < 4; group++)
        for (int layer = 0; layer < 4; layer++)
        {
            int index = group * 4 + layer;
            Vector3 position = new((group % 2 - .5f) * .9f + MathF.Sin(index * .3f) * .15f,
                (group / 2 - .5f) * .7f, 2.4f + layer * .2f);
            Entity entity = world.CreateEntity(new WorldTransform
            {
                Qvvs = new(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, index * .2f), .35f),
            });
            world.Add(entity, new MeshSource { Mesh = curved.Reference, BoundsExpansion = .66f });
        }
        world.CreateEntity(new PointLight(new(0, 2, 0), 20, Vector3.One, 30));
        await WriteSceneAsync(project, world);
        await SceneContent.SetOutputSizeAsync(destination, 1280, 720);
    }

    public static async Task CreateShaderReplacementSpheresAsync(string source, string destination, string engineRoot)
    {
        CopyContent(source, destination);
        string material = File.ReadAllText(Path.Combine(engineRoot, "assets/Shaders/unlit_material.slang"))
            .Replace("albedo.rgb * scalars.BaseColorTint.rgb", "albedo.rgb * scalars.BaseColorTint.bgr");
        string shader = ReadUnlitSource(engineRoot)
            .Replace("import unlit_material;", material);
        await SetGreenShaderAsync(destination, engineRoot, "swapped-unlit", shader);
    }

    public static async Task CreateShadowedPhongPartsAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = SomeEngine.Assets.Importers.EngineShaderContent.OpenFiles(project.RootPath, project.Manifest);
        using var receiver = await MeshAsset.OpenAsync(loader,
            new(FindContentAsset(project, "Blend panel", MeshAsset.FileType).AssetGuid));
        using var application = await ApplicationSettingsAsset.OpenAsync(loader,
            new(project.Manifest.List(ApplicationSettingsAsset.FileType).Single().AssetGuid));
        using var scene = await WorldAsset.OpenAsync(loader, application.GetBlock<ApplicationSettingsBlock>().Scene);
        using var world = await scene.LoadAsync(SceneSerialization.CreateRegistry());
        Entity entity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity, 3) });
        world.Add(entity, new MeshSource { Mesh = receiver.Reference });
        var cameras = world.Query(new QueryDefinitionBuilder().ReadWrite<SceneCamera>());
        world.ExecuteQuery(cameras, rows =>
        {
            foreach (var row in rows.Rows) row.ReadWrite<SceneCamera>().VerticalFieldOfView = MathF.PI / 4;
        });
        world.ReleaseQuery(cameras);
        scene.WriteWorld(world, SceneSerialization.CreateRegistry());
        project.RegisterAsset(await scene.SaveAsync(Path.Combine(destination, project.Manifest.Assets[scene.Id].Path)));
    }

    public static async Task CreateStreamingSphereAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = SomeEngine.Assets.Importers.EngineShaderContent.OpenFiles(project.RootPath, project.Manifest);
        using var material = await UnlitMaterialAsset.OpenAsync(loader,
            new(FindContentAsset(project, "red sphere", MaterialAsset.FileType).AssetGuid));
        var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(5);
        using MeshAsset mesh = ClusterBuilder.ProcessRaw(vertices, attributes, indices,
            [new(material.Id)], "streaming sphere");
        project.RegisterAsset(await mesh.SaveAsync(Path.Combine(destination, "assets/Meshes/streaming.mesh.asset")));
        using var world = new World();
        Entity entity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity, 2) });
        world.Add(entity, new MeshSource { Mesh = mesh.Reference });
        await WriteSceneAsync(project, world);
    }

    public static async Task CreateShadowReceiverAsync(string source, string destination)
    {
        CopyContent(source, destination);
        var project = new AssetProject(destination, []);
        await using var loader = SomeEngine.Assets.Importers.EngineShaderContent.OpenFiles(project.RootPath, project.Manifest);
        using var original = await MeshAsset.OpenAsync(loader,
            new(FindContentAsset(project, "red sphere", MeshAsset.FileType).AssetGuid));
        using var material = await StandardMaterialAsset.OpenAsync(loader, MaterialTemplates.Standard);
        using var mesh = original.CreateCopy();
        mesh.GetBlock<MeshMaterialsBlock>().Materials = [material.Reference];
        project.RegisterAsset(await mesh.SaveAsync(Path.Combine(destination, "assets/Meshes/receiver.mesh.asset")));
        using var world = new World();
        Entity receiver = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 8), Quaternion.Identity) { Stretch = new(4, 4, .1f) } });
        world.Add(receiver, new MeshSource { Mesh = mesh.Reference });
        Entity caster = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(0, 0, 6), Quaternion.Identity, .6f) });
        world.Add(caster, new MeshSource { Mesh = mesh.Reference });
        world.CreateEntity(new PointLight(new(4, 2, 3), 20, Vector3.One, 30));
        await WriteSceneAsync(project, world);
        await SceneContent.SetOutputSizeAsync(destination, 128, 128);
    }

    private static string ReadUnlitSource(string engineRoot) =>
        File.ReadAllText(Path.Combine(engineRoot, "samples", "DefaultGame", "assets", "Shaders", "unlit.slang"));

    private static async Task SetGreenShaderAsync(string directory, string engineRoot, string name, string source)
    {
        string shaderDirectory = Path.Combine(directory, "assets/Shaders");
        string sampleShaders = Path.Combine(engineRoot, "samples", "DefaultGame", "assets", "Shaders");
        Directory.CreateDirectory(shaderDirectory);
        File.WriteAllText(Path.Combine(shaderDirectory, name + ".slang"), source);
        using ShaderAsset template = await ShaderAsset.OpenSourceAsync(Path.Combine(sampleShaders, "unlit.shader"));
        using ShaderAsset definition = template.CreateCopy();
        definition.Name = name;
        definition.GetBlock<ShaderSourceBlock>().SourceNames = [name + ".slang"];
        string path = Path.Combine(shaderDirectory, name + ".shader");
        await definition.SaveSourceAsync(path);
        var project = new AssetProject(directory, [],
            shaderIncludeDirectories: [sampleShaders, Path.Combine(engineRoot, "assets/Shaders")],
            shaderTargets: [SlangCompileTarget.Dxil]);
        await project.BuildAsync(path);
        await using var loader = SomeEngine.Assets.Importers.EngineShaderContent.OpenFiles(project.RootPath, project.Manifest);
        AssetEntry entry = FindContentAsset(project, "green sphere", MaterialAsset.FileType);
        using var material = await UnlitMaterialAsset.OpenAsync(loader, new(entry.AssetGuid));
        material.GetBlock<MaterialBlock>().Shader = new(project.Resolve(path)!.Value);
        project.RegisterAsset(await material.SaveAsync(Path.Combine(directory, entry.Path)));
    }

    private static async Task WriteSceneAsync(AssetProject project, World world)
    {
        await using var loader = SomeEngine.Assets.Importers.EngineShaderContent.OpenFiles(project.RootPath, project.Manifest);
        AssetEntry entry = project.Manifest.List(ApplicationSettingsAsset.FileType).Single();
        using var application = await ApplicationSettingsAsset.OpenAsync(loader, new(entry.AssetGuid));
        using var scene = await WorldAsset.OpenAsync(loader, application.GetBlock<ApplicationSettingsBlock>().Scene);
        world.CreateEntity(new SceneCamera { Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
            VerticalFieldOfView = MathF.PI / 3, NearPlane = .1f, FarPlane = 100 });
        scene.WriteWorld(world, SceneSerialization.CreateRegistry());
        project.RegisterAsset(await scene.SaveAsync(Path.Combine(project.RootPath, project.Manifest.Assets[scene.Id].Path)));
    }

    private static AssetEntry FindContentAsset(AssetProject project, string name, string assetType) =>
        ContentAssets(project).DistinctBy(entry => entry.AssetGuid)
            .Single(entry => entry.Name == name && entry.AssetType == assetType);

    private static IEnumerable<AssetEntry> ContentAssets(AssetProject project)
    {
        foreach (AssetEntry entry in project.Manifest.Assets.Values) yield return entry;
        string references = Path.Combine(project.RootPath, "Library", "AssetManifest", EngineShaderContent.ContentDirectoriesFileName);
        foreach (JsonNode? directory in JsonNode.Parse(File.ReadAllText(references))!.AsArray())
        {
            string root = Path.GetFullPath(directory!.GetValue<string>(), project.RootPath);
            AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest"));
            foreach (AssetEntry entry in manifest.Assets.Values) yield return entry;
        }
    }

    private static void CopyContent(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string path = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(file, path);
        }
        string references = Path.Combine(source, "Library", "AssetManifest", EngineShaderContent.ContentDirectoriesFileName);
        string[] directories = JsonNode.Parse(File.ReadAllText(references))!.AsArray()
            .Select(directory => Path.GetFullPath(directory!.GetValue<string>(), source)).ToArray();
        EngineShaderContent.SetContentDirectories(new AssetProject(destination, []), directories);
    }
}
