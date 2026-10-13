using System.Numerics;
using DefaultGame.Content;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Resources;

namespace SomeEngine.SceneContent;

/// <summary>Builds self-contained scene content from the rendering sample's sources.</summary>
public static class SceneContent
{
    public static async Task GenerateDefaultAsync(string destination, string engineRoot)
    {
        destination = Path.GetFullPath(destination);
        engineRoot = Path.GetFullPath(engineRoot);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new ArgumentException("Use an empty content directory.", nameof(destination));
        string source = Path.Combine(engineRoot, "samples", "DefaultGame");

        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "assets", "Shaders"), "*", SearchOption.AllDirectories))
            if (Path.GetExtension(file) is ".shader" or ".slang" or ".meta")
                Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "assets", "Models"), "ico_sphere.glb*"))
            if (Path.GetExtension(file) != ".json") Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        await SampleContent.BuildAsync(destination, engineRoot).ConfigureAwait(false);
    }

    /// <summary>Two colored spheres with fixed transforms and camera, suitable for equivalent arrival comparisons.</summary>
    public static async Task GenerateSpheresAsync(string destination, string engineRoot, int width = 64, int height = 64)
    {
        await GenerateDefaultAsync(destination, engineRoot).ConfigureAwait(false);
        var project = new AssetProject(destination, []);
        var manifest = project.Manifest;
        await using var loader = new AssetLoader(new LooseAssetStorage(destination, manifest));
        AssetEntry applicationEntry = manifest.List(ApplicationSettingsAsset.FileType).Single();
        using var application = await ApplicationSettingsAsset.OpenAsync(loader, new(applicationEntry.AssetGuid));
        var settings = await application.GetBlockAsync<ApplicationSettingsBlock>();
        using var original = await WorldAsset.OpenAsync(loader, settings.Scene);
        using var originalWorld = await original.LoadAsync(SceneSerialization.CreateRegistry());
        AssetId<MeshAsset> meshId = default;
        var query = originalWorld.Query(new QueryDefinitionBuilder().Read<MeshSource>());
        try
        {
            originalWorld.ExecuteQuery(query, rows =>
            {
                foreach (var row in rows.Rows) { meshId = row.Read<MeshSource>().Mesh; break; }
            });
        }
        finally { originalWorld.ReleaseQuery(query); }
        AssetEntry unlitEntry = manifest.Assets.Values.Single(entry => entry.Path.EndsWith("TestUnlit_1.material.asset", StringComparison.Ordinal));
        using var template = await UnlitMaterialAsset.OpenAsync(loader, new(unlitEntry.AssetGuid));
        using var sourceMesh = await MeshAsset.OpenAsync(loader, meshId);
        using var world = new World();
        for (int index = 0; index < 2; index++)
        {
            using var material = template.CreateCopy();
            material.Name = index == 0 ? "red sphere" : "green sphere";
            new UnlitMaterialParameters(material.GetBlock<MaterialBlock>()).BaseColorTint = index == 0 ? new(1, 0, 0, 1) : new(0, 1, 0, 1);
            project.RegisterAsset(await material.SaveAsync(Path.Combine(destination, $"assets/Materials/sphere-{index}.material.asset")));
            using var mesh = sourceMesh.CreateCopy();
            mesh.Name = material.Name;
            mesh.GetBlock<MeshMaterialsBlock>().Materials = [material.Reference];
            project.RegisterAsset(await mesh.SaveAsync(Path.Combine(destination, $"assets/Meshes/sphere-{index}.mesh.asset")));
            var entity = world.CreateEntity(new WorldTransform { Qvvs = new(new Vector3(index == 0 ? -1 : 1, 0, 6), Quaternion.Identity) });
            world.Add(entity, new MeshSource { Mesh = mesh.Reference });
        }
        world.CreateEntity(new SceneCamera { Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
            VerticalFieldOfView = MathF.PI / 3, NearPlane = 0.1f, FarPlane = 100 });
        original.WriteWorld(world, SceneSerialization.CreateRegistry());
        project.RegisterAsset(await original.SaveAsync(Path.Combine(destination, manifest.Assets[original.Id].Path)));
        settings.Width = width;
        settings.Height = height;
        project.RegisterAsset(await application.SaveAsync(Path.Combine(destination, applicationEntry.Path)));
    }

    public static async Task SetOutputSizeAsync(string directory, int width, int height)
    {
        var project = new AssetProject(directory, []);
        var manifest = project.Manifest;
        await using var loader = new AssetLoader(new LooseAssetStorage(directory, manifest));
        AssetEntry entry = manifest.List(ApplicationSettingsAsset.FileType).Single();
        using var application = await ApplicationSettingsAsset.OpenAsync(loader, new(entry.AssetGuid));
        var settings = await application.GetBlockAsync<ApplicationSettingsBlock>();
        settings.Width = width;
        settings.Height = height;
        project.RegisterAsset(await application.SaveAsync(Path.Combine(directory, entry.Path)));
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }
}
