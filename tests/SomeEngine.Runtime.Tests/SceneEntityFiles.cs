using SomeEngine.Assets;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Render;

namespace SomeEngine.Runtime.Tests;

internal static class SceneEntityFiles
{
    internal static async Task SaveAsync(AssetProject project, World world, AssetEntry entry, string name = "Default")
    {
        await using var loader = new AssetLoader(project.CreateStorage());
        using WorldAsset scene = await WorldAsset.OpenAsync(loader, new(entry.AssetGuid));
        WorldBlock directory = scene.GetBlock<WorldBlock>();
        WorldContent content = directory.Contents.Single();
        AssetEntry entityEntry = project.Manifest.Assets[content.Asset.Value];
        var members = new List<Entity>();
        using (QueryScope scope = world.Query(new QueryDescription().Without<Destroyed>()).OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (Entity entity in chunk.Entities) members.Add(entity);
        using var entities = new EntityAsset(entityEntry.AssetGuid) { Name = name };
        entities.GetBlock<EntityBlock>().WriteEntities(world, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(members),
            new(Codecs: SceneSerialization.Codecs));
        project.RegisterAsset(await entities.SaveAsync(Path.Combine(project.RootPath, entityEntry.Path)));
        scene.Name = name;
        project.RegisterAsset(await scene.SaveAsync(Path.Combine(project.RootPath, entry.Path)));
    }
}
