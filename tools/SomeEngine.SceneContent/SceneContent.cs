using System.Numerics;
using DefaultGame.Content;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
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
        await WriteSpheresAsync(destination, width, height).ConfigureAwait(false);
    }

    public static async Task GenerateSpheresFromCookedAsync(string destination, string cookedContent,
        int width = 64, int height = 64)
    {
        destination = Path.GetFullPath(destination);
        cookedContent = Path.GetFullPath(cookedContent);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new ArgumentException("Use an empty content directory.", nameof(destination));
        foreach (string relative in new[] { "assets", Path.Combine("Library", "AssetManifest") })
        {
            string source = Path.Combine(cookedContent, relative);
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                Copy(file, Path.Combine(destination, relative, Path.GetRelativePath(source, file)));
        }
        await WriteSpheresAsync(destination, width, height).ConfigureAwait(false);
    }

    private static async Task WriteSpheresAsync(string destination, int width, int height)
    {
        var project = new AssetProject(destination, []);
        var manifest = project.Manifest;
        await using var loader = new AssetLoader(new LooseAssetStorage(destination, manifest));
        AssetEntry applicationEntry = manifest.List(ApplicationSettingsAsset.FileType).Single();
        using var application = await ApplicationSettingsAsset.OpenAsync(loader, new(applicationEntry.AssetGuid));
        var settings = await application.GetBlockAsync<ApplicationSettingsBlock>();
        using var original = await WorldAsset.OpenAsync(loader, settings.Scene);
        using var world = new World();
        await using WorldStream stream = await OpenSceneAsync(world, loader, settings.Scene);
        AssetId<MeshAsset> meshId = default;
        Query query = world.Query(new QueryDescription().With<MeshSource>());
        using (QueryScope source = query.OpenRead())
            foreach (QueryChunk chunk in source)
            {
                meshId = chunk.Read<MeshSource>()[0].Mesh;
                break;
            }
        AssetEntry unlitEntry = manifest.Assets.Values.Single(entry => entry.Path.EndsWith("TestUnlit_1.material.asset", StringComparison.Ordinal));
        using var template = await UnlitMaterialAsset.OpenAsync(loader, new(unlitEntry.AssetGuid));
        using var sourceMesh = await MeshAsset.OpenAsync(loader, meshId);
        world.Destroy(ActiveEntities(world));
        for (int index = 0; index < 2; index++)
        {
            using var material = template.CreateCopy();
            material.GetBlock<MaterialBlock>().Parent = template.Reference;
            material.Name = index == 0 ? "red sphere" : "green sphere";
            new UnlitMaterialParameters(material.GetBlock<MaterialBlock>()).BaseColorTint = index == 0 ? new(1, 0, 0, 1) : new(0, 1, 0, 1);
            project.RegisterAsset(await material.SaveAsync(Path.Combine(destination, $"assets/Materials/sphere-{index}.material.asset")));
            using var mesh = sourceMesh.CreateCopy();
            mesh.Name = material.Name;
            mesh.GetBlock<MeshMaterialsBlock>().Materials = [material.Reference];
            project.RegisterAsset(await mesh.SaveAsync(Path.Combine(destination, $"assets/Meshes/sphere-{index}.mesh.asset")));
            var entity = world.Create(new WorldTransform { Qvvs = new(new Vector3(index == 0 ? -1 : 1, 0, 6), Quaternion.Identity) });
            world.Set(entity, new MeshSource { Mesh = mesh.Reference, DepthCacheClass = DepthCacheClass.Static });
        }
        world.Create(new SceneCamera { Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
            VerticalFieldOfView = MathF.PI / 3, NearPlane = 0.1f, FarPlane = 100 });
        await SaveSceneAsync(project, original, world);
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

    internal static async Task<WorldStream> OpenSceneAsync(World world, AssetLoader loader, AssetId<WorldAsset> source)
    {
        var stream = new WorldStream(world, loader, source, new WorldStreamOptions { CommitBudget = TimeSpan.MaxValue },
            new SerializationReadOptions(Codecs: SceneSerialization.Codecs));
        try
        {
            await stream.ContentsReady.ConfigureAwait(false);
            int count = stream.Contents.Length;
            for (int index = 0; index < count; index++) stream.Request(index);
            stream.Update();
            for (;;)
            {
                bool complete = true;
                for (int index = 0; index < count; index++)
                    if (!stream.LoadedGroups.TryGetEntities(index, out _)) { complete = false; break; }
                if (complete) return stream;
                await stream.CompleteReadsAsync().ConfigureAwait(false);
                stream.Update();
            }
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static async Task SaveSceneAsync(AssetProject project, WorldAsset scene, World world)
    {
        WorldBlock directory = scene.GetBlock<WorldBlock>();
        WorldContent content = directory.Contents.Single();
        await using var loader = new AssetLoader(project.CreateStorage());
        using EntityAsset entities = await EntityAsset.OpenAsync(loader, new(content.Asset.Value));
        entities.GetBlock<EntityBlock>().WriteEntities(world, ActiveEntities(world), new(Codecs: SceneSerialization.Codecs));
        project.RegisterAsset(await entities.SaveAsync(Path.Combine(project.RootPath, project.Manifest.Assets[entities.Id].Path)));
        directory.Contents = [new(new AssetId<Asset>(entities.Id), content.Bounds)];
        project.RegisterAsset(await scene.SaveAsync(Path.Combine(project.RootPath, project.Manifest.Assets[scene.Id].Path)));
    }

    private static Entity[] ActiveEntities(World world)
    {
        Query query = world.Query(new QueryDescription().Without<Destroyed>());
        var entities = new Entity[query.Count];
        int offset = 0;
        using QueryScope scope = query.OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            chunk.Entities.CopyTo(entities.AsSpan(offset));
            offset += chunk.Count;
        }
        return entities;
    }
}
