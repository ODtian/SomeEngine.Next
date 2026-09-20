using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.Render;
using SomeEngine.Render.Components;

namespace SomeEngine.Runtime.Tests;

public sealed class WorldAssetTests
{
    [Fact]
    public async Task SavedScenePreservesEntitiesComponentsBuffersAndReferencesAfterClosingItsSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-World-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var registry = SceneSerialization.CreateRegistry();
            using var world = new World();
            Guid meshId = Guid.NewGuid(), materialId = Guid.NewGuid();
            var transform = new TransformQvvs(new(2, 3, 4), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f))
            { Stretch = new(2, 3, 4), Scale = 0.75f };
            var entity = world.CreateEntity(new WorldTransform { Qvvs = transform });
            world.Add(entity, new MeshSource { Mesh = new(meshId), BoundsExpansion = 0.4f });
            world.Add(entity, new MeshMotion { Amplitude = new(1, 2, 3), Seed = 73 });
            world.AddBuffer<MaterialSource>(entity);
            world.ExecuteBufferWrite<MaterialSource, Guid>(entity, ref materialId,
                static (DynamicBuffer<MaterialSource> buffer, ref Guid id) =>
                {
                    buffer.Add(new(new(id)));
                    buffer.Add(new(new(id)));
                    buffer.Add(default);
                });
            var cameraValue = new SceneCamera
            {
                Position = new(1, 2, -3), Target = new(1, 2, 3), Up = Vector3.UnitY,
                VerticalFieldOfView = 0.8f, NearPlane = 0.1f, FarPlane = 500,
            };
            var camera = world.CreateEntity(cameraValue);
            var directionalValue = new DirectionalLight(Vector3.UnitY, new(1, 0.5f, 0.25f), 3, 7);
            var pointValue = new PointLight(new(4, 5, 6), 20, Vector3.One, 2, 13);
            var spotValue = new SpotLight(new(7, 8, 9), 30, Vector3.UnitZ, 0.9f, 0.8f, Vector3.One, 5, 19);
            var directional = world.CreateEntity(directionalValue);
            var point = world.CreateEntity(pointValue);
            var spot = world.CreateEntity(spotValue);
            using var asset = new WorldAsset { Name = "saved scene" };
            asset.WriteWorld(world, registry);
            using var changed = asset.Duplicate();
            world.Replace(entity, new MeshMotion { Seed = 99 });
            changed.WriteWorld(world, registry);
            AssetEntry entry = await asset.SaveAsync(Path.Combine(directory, "scene.scene.asset"));
            Assert.Equal(new[] { meshId, materialId }.Order(), entry.Dependencies);
            using WorldAsset opened = await WorldAsset.OpenAsync(entry.Path);
            Assert.Equal(asset.Id, opened.Id);
            Assert.Equal("saved scene", opened.Name);
            Assert.Equal(entry.Version, opened.SourceVersion);
            using World loaded = await opened.LoadAsync(registry);
            opened.Dispose();
            asset.Dispose();
            Assert.True(loaded.IsAlive(entity));
            TransformQvvs actual = loaded.Read<WorldTransform>(entity).Qvvs;
            Assert.Equal(transform.Position, actual.Position);
            Assert.Equal(transform.Rotation, actual.Rotation);
            Assert.Equal(transform.Stretch, actual.Stretch);
            Assert.Equal(transform.Scale, actual.Scale);
            Assert.Equal(meshId, loaded.Read<MeshSource>(entity).Mesh.Value);
            Assert.Equal(0.4f, loaded.Read<MeshSource>(entity).BoundsExpansion);
            Assert.Equal(73u, loaded.Read<MeshMotion>(entity).Seed);
            Assert.Equal(new Vector3(1, 2, 3), loaded.Read<MeshMotion>(entity).Amplitude);
            MaterialSource[] materials = [];
            loaded.ExecuteBufferRead<MaterialSource, MaterialSource[]>(entity, ref materials,
                static (BufferView<MaterialSource> buffer, ref MaterialSource[] values) => values = buffer.AsSpan().ToArray());
            Assert.Equal(3, materials.Length);
            Assert.Equal(materialId, materials[0].Material.Value);
            Assert.Equal(materialId, materials[1].Material.Value);
            Assert.True(materials[2].Material.IsEmpty);
            Assert.Equal(cameraValue.Position, loaded.Read<SceneCamera>(camera).Position);
            Assert.Equal(cameraValue.Target, loaded.Read<SceneCamera>(camera).Target);
            Assert.Equal(cameraValue.Up, loaded.Read<SceneCamera>(camera).Up);
            Assert.Equal(cameraValue.VerticalFieldOfView, loaded.Read<SceneCamera>(camera).VerticalFieldOfView);
            Assert.Equal(cameraValue.NearPlane, loaded.Read<SceneCamera>(camera).NearPlane);
            Assert.Equal(cameraValue.FarPlane, loaded.Read<SceneCamera>(camera).FarPlane);
            Assert.Equal(directionalValue, loaded.Read<DirectionalLight>(directional));
            Assert.Equal(pointValue, loaded.Read<PointLight>(point));
            Assert.Equal(spotValue, loaded.Read<SpotLight>(spot));
            using World other = await changed.LoadAsync(registry);
            Assert.Equal(99u, other.Read<MeshMotion>(entity).Seed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CanceledLoadLeavesTheAssetReadable()
    {
        var registry = SceneSerialization.CreateRegistry();
        using var world = new World();
        var entity = world.CreateEntity(new MeshMotion { Seed = 12 });
        using var asset = new WorldAsset();
        asset.WriteWorld(world, registry);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => asset.LoadAsync(registry, cancellationToken: new(true)).AsTask());
        using World loaded = await asset.LoadAsync(registry);
        Assert.Equal(12u, loaded.Read<MeshMotion>(entity).Seed);
    }
}
