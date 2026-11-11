using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Render;
using SomeEngine.Render.Components;

namespace SomeEngine.Runtime.Tests;

public sealed class WorldAssetTests
{
    [Fact]
    public async Task SavedScenePreservesEntitiesComponentsAndMeshReferencesAfterClosingItsSource()
    {
        using var files = new WorldFiles();
        using var world = new World();
        Guid meshId = Guid.NewGuid();
        var transform = new TransformQvvs(new(2, 3, 4), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f))
        { Stretch = new(2, 3, 4), Scale = 0.75f };
        Entity entity = world.Create(new WorldTransform { Qvvs = transform });
        world.Set(entity, new MeshSource { Mesh = new(meshId), BoundsExpansion = 0.4f });
        world.Set(entity, new MeshMotion { Amplitude = new(1, 2, 3), Seed = 73 });
        var cameraValue = new SceneCamera
        {
            Position = new(1, 2, -3), Target = new(1, 2, 3), Up = Vector3.UnitY,
            VerticalFieldOfView = 0.8f, NearPlane = 0.1f, FarPlane = 500,
        };
        Entity camera = world.Create(cameraValue);
        var directionalValue = new DirectionalLight(Vector3.UnitY, new(1, 0.5f, 0.25f), 3, 7);
        var pointValue = new PointLight(new(4, 5, 6), 20, Vector3.One, 2, 13);
        var spotValue = new SpotLight(new(7, 8, 9), 30, Vector3.UnitZ, 0.9f, 0.8f, Vector3.One, 5, 19);
        Entity directional = world.Create(directionalValue);
        Entity point = world.Create(pointValue);
        Entity spot = world.Create(spotValue);
        Entity[] members = [entity, camera, directional, point, spot];
        using var asset = new EntityAsset(Guid.NewGuid()) { Name = "saved scene" };
        asset.GetBlock<EntityBlock>().WriteEntities(world, members, new(Codecs: SceneSerialization.Codecs));
        using EntityAsset changed = asset.CreateCopy();
        world.Set(entity, new MeshMotion { Seed = 99 });
        changed.GetBlock<EntityBlock>().WriteEntities(world, members, new(Codecs: SceneSerialization.Codecs));
        AssetEntry entry = await files.Save(asset);
        AssetEntry otherEntry = await files.Save(changed);
        Assert.Equal([meshId], entry.Dependencies);
        using (EntityAsset opened = await EntityAsset.OpenAsync(entry.Path))
        {
            Assert.Equal(asset.Id, opened.Id);
            Assert.Equal("saved scene", opened.Name);
            Assert.Equal(entry.Version, opened.SourceVersion);
        }
        AssetId<WorldAsset> directory = await files.SaveDirectory(new(new(entry.AssetGuid), new(0, 0, 0, 10, 10, 10)),
            new(new(otherEntry.AssetGuid), new(0, 0, 0, 10, 10, 10)));
        asset.Dispose();
        changed.Dispose();
        await using var loader = new AssetLoader(files.Storage());
        using var game = new GameWorld();
        var streaming = new WorldStreamSystem(game.World, loader, directory, new(), new(Codecs: SceneSerialization.Codecs));
        game.Systems.Add(streaming);
        game.Systems.Initialize();
        await streaming.Stream.ContentsReady;
        streaming.Stream.Request(0);
        streaming.Stream.Request(1);

        await Load(streaming.Stream, 0, 1);

        Assert.True(streaming.Stream.LoadedGroups.TryGetEntities(0, out ReadOnlyMemory<Entity> first));
        Assert.True(streaming.Stream.LoadedGroups.TryGetEntities(1, out ReadOnlyMemory<Entity> second));
        World loaded = game.World;
        TransformQvvs actual = loaded.ReadComponentData<WorldTransform>(first.Span[0]).Qvvs;
        Assert.Equal(transform.Position, actual.Position);
        Assert.Equal(transform.Rotation, actual.Rotation);
        Assert.Equal(transform.Stretch, actual.Stretch);
        Assert.Equal(transform.Scale, actual.Scale);
        Assert.Equal(meshId, loaded.ReadComponentData<MeshSource>(first.Span[0]).Mesh.Value);
        Assert.Equal(0.4f, loaded.ReadComponentData<MeshSource>(first.Span[0]).BoundsExpansion);
        Assert.Equal(73u, loaded.ReadComponentData<MeshMotion>(first.Span[0]).Seed);
        Assert.Equal(new Vector3(1, 2, 3), loaded.ReadComponentData<MeshMotion>(first.Span[0]).Amplitude);
        SceneCamera loadedCamera = loaded.ReadComponentData<SceneCamera>(first.Span[1]);
        Assert.Equal(cameraValue.Position, loadedCamera.Position);
        Assert.Equal(cameraValue.Target, loadedCamera.Target);
        Assert.Equal(cameraValue.Up, loadedCamera.Up);
        Assert.Equal(cameraValue.VerticalFieldOfView, loadedCamera.VerticalFieldOfView);
        Assert.Equal(cameraValue.NearPlane, loadedCamera.NearPlane);
        Assert.Equal(cameraValue.FarPlane, loadedCamera.FarPlane);
        Assert.Equal(directionalValue, loaded.ReadComponentData<DirectionalLight>(first.Span[2]));
        Assert.Equal(pointValue, loaded.ReadComponentData<PointLight>(first.Span[3]));
        Assert.Equal(spotValue, loaded.ReadComponentData<SpotLight>(first.Span[4]));
        Assert.Equal(99u, loaded.ReadComponentData<MeshMotion>(second.Span[0]).Seed);
    }

    [Fact]
    public async Task CanceledEntityReadLeavesTheAssetReadableAndStreamable()
    {
        using var files = new WorldFiles();
        using var world = new World();
        Entity entity = world.Create(new MeshMotion { Seed = 12 });
        using var asset = new EntityAsset(Guid.NewGuid());
        asset.GetBlock<EntityBlock>().WriteEntities(world, [entity], new(Codecs: SceneSerialization.Codecs));
        AssetEntry entry = await files.Save(asset);
        using EntityAsset opened = await EntityAsset.OpenAsync(entry.Path);
        EntityBlock block = await opened.GetBlockAsync<EntityBlock>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => block.ReadBytesAsync(EntityBlock.ContentKey, new(true)).AsTask());
        Assert.True((await block.ReadBytesAsync(EntityBlock.ContentKey)).Length > 0);
        AssetId<WorldAsset> directory = await files.SaveDirectory(new WorldContent(new(entry.AssetGuid), default));
        await using var loader = new AssetLoader(files.Storage());
        using var game = new GameWorld();
        var streaming = new WorldStreamSystem(game.World, loader, directory, new(), new(Codecs: SceneSerialization.Codecs));
        game.Systems.Add(streaming);
        game.Systems.Initialize();
        await streaming.Stream.ContentsReady;
        streaming.Stream.Request(0);

        await Load(streaming.Stream, 0);

        Assert.True(streaming.Stream.LoadedGroups.TryGetEntities(0, out ReadOnlyMemory<Entity> loaded));
        Assert.Equal(12u, game.World.ReadComponentData<MeshMotion>(loaded.Span[0]).Seed);
    }

    private static async Task Load(WorldStream stream, params int[] indices)
    {
        while (indices.Any(index => !stream.LoadedGroups.TryGetEntities(index, out _)))
        {
            stream.Update();
            await stream.CompleteReadsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class WorldFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-World-{Guid.NewGuid():N}");
        private readonly AssetManifest _manifest = new();
        internal WorldFiles() => Directory.CreateDirectory(_directory);
        internal async Task<AssetEntry> Save(EntityAsset asset)
        {
            AssetEntry entry = await asset.SaveAsync(Path.Combine(_directory, asset.Id + EntityAsset.PathSuffix));
            Add(entry);
            return entry;
        }
        internal async Task<AssetId<WorldAsset>> SaveDirectory(params WorldContent[] contents)
        {
            using var asset = new WorldAsset(Guid.NewGuid()) { Name = "saved scene" };
            asset.GetBlock<WorldBlock>().Contents = [.. contents];
            AssetEntry entry = await asset.SaveAsync(Path.Combine(_directory, asset.Id + WorldAsset.PathSuffix));
            Add(entry);
            return new(asset.Id);
        }
        private void Add(AssetEntry entry) => _manifest.AddAsset(entry.AssetGuid, entry.Name,
            Path.GetFileName(entry.Path), entry.AssetType, entry.SchemaFingerprint);
        internal IAssetStorage Storage() => new LooseAssetStorage(_directory, _manifest);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
