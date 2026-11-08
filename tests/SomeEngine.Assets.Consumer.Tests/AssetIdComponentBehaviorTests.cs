using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.Render;
using SomeEngine.Render.Components;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class AssetIdComponentBehaviorTests
{
    [Fact]
    public async Task SavedWorldPreservesTypedAssetIdsAndMeshInstanceSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-asset-ids-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            AssetId<MeshAsset> mesh = new(Guid.NewGuid());
            AssetId<SkeletonAsset> skeleton = new(Guid.NewGuid());
            var settings = new MeshInstanceSettings { BoundsExpansion = .4f, DepthCacheClass = DepthCacheClass.Dynamic,
                FirstPerson = new(FirstPersonMeshKind.FirstPerson, 7, MeshPlayerVisibility.PlayerOnly) };
            using var world = new World();
            var entity = world.CreateEntity(mesh);
            world.Add(entity, skeleton);
            world.Add(entity, settings);
            var registry = SceneSerialization.CreateRegistry();
            using var asset = new WorldAsset();
            asset.WriteWorld(world, registry);
            AssetEntry entry = await asset.SaveAsync(Path.Combine(directory, "asset-ids.scene.asset"));
            Assert.Equal(new[] { mesh.Value, skeleton.Value }.Order(), entry.Dependencies.Order());
            using WorldAsset opened = await WorldAsset.OpenAsync(entry.Path);
            using World loaded = await opened.LoadAsync(registry);
            opened.Dispose();
            Assert.Equal(mesh, loaded.Read<AssetId<MeshAsset>>(entity));
            Assert.Equal(skeleton, loaded.Read<AssetId<SkeletonAsset>>(entity));
            MeshInstanceSettings actual = loaded.Read<MeshInstanceSettings>(entity);
            Assert.Equal(settings.BoundsExpansion, actual.BoundsExpansion);
            Assert.Equal(settings.DepthCacheClass, actual.DepthCacheClass);
            Assert.Equal(settings.FirstPerson, actual.FirstPerson);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
