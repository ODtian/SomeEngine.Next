using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;
using SomeEngine.Render;
using SomeEngine.Render.Components;

namespace SomeEngine.Assets.Scenes.Tests;

// The scene importer writes entity blocks with SceneSerialization's registry and scene streaming reads them back with
// it. A scene component that the registry leaves out is silently not saved, so every one of them is checked here.
public sealed class SceneEntityBlockTests
{
    [Fact]
    public void Every_scene_component_and_the_hierarchy_survive_an_entity_block_round_trip()
    {
        using var source = new World();
        Entity root = source.Create(new WorldTransform
        {
            Qvvs = new TransformQvvs(new Vector3(1, 2, 3), Quaternion.CreateFromYawPitchRoll(0.5f, 0, 0), 2) { Stretch = new Vector3(1, 2, 1) },
        });
        source.Add(root, new WorldOrigin(4, 5, 6));
        source.Add(root, new SceneCamera { Position = new(0, 1, 2), Target = Vector3.Zero, Up = Vector3.UnitY, VerticalFieldOfView = 1, NearPlane = 0.1f, FarPlane = 100 });
        Entity sun = source.Create(new DirectionalLight(new Vector3(0, -1, 0), new Vector3(1, 0.9f, 0.8f), 3));
        Entity lamp = source.Create(new PointLight(new Vector3(1, 1, 1), 5, Vector3.One, 2));
        Entity spot = source.Create(new SpotLight(new Vector3(2, 2, 2), 6, Vector3.UnitZ, 0.2f, 0.4f, Vector3.One, 1));
        var mesh = new AssetId<MeshAsset>(Guid.Parse("4c3b2a19-0f1e-4d2c-8b7a-6958473a2b1c"));
        Entity body = source.Create(new MeshSource { Mesh = mesh, BoundsExpansion = 0.5f });
        source.Add(body, new MeshMotion { Amplitude = new(1, 0, 0), Seed = 7 });
        source.Add(body, new Xray(new XrayBlend(new Vector3(1, 0, 0), 0.5f), new XrayBlend(0.25f), 0.1f, 0.9f));
        var skeleton = new AssetId<SkeletonAsset>(Guid.Parse("5d4c3b2a-1f0e-4e3d-9c8b-7a6958473a2b"));
        source.Add(body, new SkeletonSource { Skeleton = skeleton });
        Hierarchy<DefaultHierarchyDomain>.SetParent(source, body, root);
        using var bytes = new MemoryStream();
        WorldSerializer.WriteEntities(bytes, source, [root, sun, lamp, spot, body], SceneSerialization.CreateRegistry(),
            new SerializeOptions(Purpose: SavePurpose.DurableSave));
        using var target = new World();

        Entity[] loaded = WorldSerializer.ReadEntities(bytes.ToArray().AsMemory(), target, SceneSerialization.CreateRegistry());

        Assert.Equal(source.Read<WorldTransform>(root).Qvvs.Position, target.Read<WorldTransform>(loaded[0]).Qvvs.Position);
        Assert.Equal(source.Read<WorldTransform>(root).Qvvs.Rotation, target.Read<WorldTransform>(loaded[0]).Qvvs.Rotation);
        Assert.Equal(source.Read<WorldTransform>(root).Qvvs.Stretch, target.Read<WorldTransform>(loaded[0]).Qvvs.Stretch);
        Assert.Equal(2, target.Read<WorldTransform>(loaded[0]).Qvvs.Scale);
        Assert.Equal(new WorldOrigin(4, 5, 6), target.Read<WorldOrigin>(loaded[0]));
        Assert.Equal(100, target.Read<SceneCamera>(loaded[0]).FarPlane);
        Assert.Equal(source.Read<DirectionalLight>(sun), target.Read<DirectionalLight>(loaded[1]));
        Assert.Equal(source.Read<PointLight>(lamp), target.Read<PointLight>(loaded[2]));
        Assert.Equal(source.Read<SpotLight>(spot), target.Read<SpotLight>(loaded[3]));
        Assert.Equal(mesh, target.Read<MeshSource>(loaded[4]).Mesh);
        Assert.Equal(7u, target.Read<MeshMotion>(loaded[4]).Seed);
        Assert.Equal(source.Read<Xray>(body), target.Read<Xray>(loaded[4]));
        Assert.Equal(skeleton, target.Read<SkeletonSource>(loaded[4]).Skeleton);
        Assert.Equal(loaded[0], Hierarchy<DefaultHierarchyDomain>.GetParent(target, loaded[4]));
    }
}
