using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.Render;
using SomeEngine.Render.Components;

namespace SomeEngine.Runtime.Tests;

public sealed class SceneContentSerializationTests
{
    [Fact]
    public async Task SphereContentEditingReusesSceneAndEntityAssetsAndSavesItsCurrentMembers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-scene-content-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(directory, "source"), destination = Path.Combine(directory, "edited");
        Directory.CreateDirectory(source);
        try
        {
            var project = new AssetProject(source, []);
            using var material = new UnlitMaterialAsset(Guid.NewGuid()) { Name = "source unlit" };
            project.RegisterAsset(await material.SaveAsync(Path.Combine(source, "assets/Materials/TestUnlit_1.material.asset")));
            using var mesh = new MeshAsset(Guid.NewGuid()) { Name = "source mesh" };
            using (var geometry = new MeshBlock { Bounds = new() { Radius = 1 } }) mesh.SetBlock(geometry);
            using (var materials = new MeshMaterialsBlock { Materials = [material.Reference] }) mesh.SetBlock(materials);
            project.RegisterAsset(await mesh.SaveAsync(Path.Combine(source, "assets/Meshes/source.mesh.asset")));
            using var original = new World();
            Entity first = original.Create(new WorldTransform { Qvvs = new(Vector3.Zero, Quaternion.Identity) });
            original.Set(first, new MeshSource { Mesh = mesh.Reference });
            using var entities = new EntityAsset(Guid.NewGuid()) { Name = "source entities" };
            entities.GetBlock<EntityBlock>().WriteEntities(original, [first], new(Codecs: SceneSerialization.Codecs));
            project.RegisterAsset(await entities.SaveAsync(Path.Combine(source, "assets/Scenes/source.entities.asset")));
            using var scene = new WorldAsset(Guid.NewGuid()) { Name = "source scene" };
            scene.GetBlock<WorldBlock>().Contents = [new(new AssetId<Asset>(entities.Id), new(-64, -64, -64, 64, 64, 128))];
            project.RegisterAsset(await scene.SaveAsync(Path.Combine(source, "assets/Scenes/source.scene.asset")));
            using var application = new ApplicationSettingsAsset(Guid.NewGuid());
            application.GetBlock<ApplicationSettingsBlock>().Scene = scene.Reference;
            project.RegisterAsset(await application.SaveAsync(Path.Combine(source, "assets/Runtime/default.runtime.asset")));

            await SomeEngine.SceneContent.SceneContent.GenerateSpheresFromCookedAsync(destination, source, 80, 48);

            var edited = new AssetProject(destination, []);
            await using var loader = new AssetLoader(edited.CreateStorage());
            using WorldAsset savedScene = await WorldAsset.OpenAsync(loader, scene.Reference);
            WorldContent content = Assert.Single(savedScene.GetBlock<WorldBlock>().Contents);
            Assert.Equal(entities.Id, content.Asset.Value);
            using EntityAsset savedEntities = await EntityAsset.OpenAsync(loader, new(content.Asset.Value));
            EntityBlock block = await savedEntities.GetBlockAsync<EntityBlock>();
            using var world = new World();
            Entity business = world.Create(new SceneCamera { NearPlane = 42 });
            Entity[] members = WorldSerializer.ReadEntities(await block.ReadBytesAsync(EntityBlock.ContentKey), world,
                new(Codecs: SceneSerialization.Codecs));
            Assert.Equal(3, members.Length);
            Assert.Equal(42, world.Read<SceneCamera>(business).NearPlane);
            var positions = new List<Vector3>();
            using (QueryScope scope = world.Query(new QueryDescription().With<MeshSource>().With<WorldTransform>()).OpenRead())
                foreach (QueryChunk chunk in scope)
                    foreach (WorldTransform transform in chunk.Read<WorldTransform>()) positions.Add(transform.Qvvs.Position);
            Assert.Equal(new HashSet<Vector3> { new(-1, 0, 6), new(1, 0, 6) }, positions.ToHashSet());
            using ApplicationSettingsAsset savedApplication = await ApplicationSettingsAsset.OpenAsync(loader, application.Reference);
            ApplicationSettingsBlock settings = savedApplication.GetBlock<ApplicationSettingsBlock>();
            Assert.Equal(scene.Reference, settings.Scene);
            Assert.Equal(80, settings.Width);
            Assert.Equal(48, settings.Height);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
