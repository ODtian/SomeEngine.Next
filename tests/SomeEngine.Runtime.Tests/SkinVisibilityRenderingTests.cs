using System.Collections.Immutable;
using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Resources;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task SkinnedMainAndShadowSilhouettesFollowTheSamePose()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        await content.WriteShadowSceneAsync();
        Skeleton skeleton = VisibilitySkeleton();
        var storage = new PoseStorage();
        using SkeletonInstance animated = storage.CreateInstance(skeleton), identity = storage.CreateInstance(skeleton);
        BoneTransform first = new() { Translation = new(-1.2f, 0, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -.3f), Scale = new(1.4f, 1.8f, 1) };
        BoneTransform second = new() { Translation = new(1.2f, .3f, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .6f), Scale = new(1.8f, 1.2f, 1) };
        animated.Update([first, first], 0);
        var actual = new List<SceneImage>();
        var expected = new List<SceneImage>();
        var application = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame => AttachLayeredPanels(frame, animated, identity, 8),
            UpdateFrame = frame => { if (frame.Index == 4) animated.Update([second, second], .5); },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(application, 8,
            [new(3, edit => edit.CaptureImage(actual.Add)), new(7, edit => edit.CaptureImage(actual.Add))]),
            "--no-taa", "--fixed-step", "0.125");

        Entity actor = Entity.Null;
        var reference = new ApplicationScene(content.Project.RootPath)
        {
            Initialize = frame =>
            {
                actor = AttachLayeredPanels(frame, identity, identity, 8);
                PlacePanel(frame.World, actor, first);
            },
            UpdateFrame = frame => { if (frame.Index == 4) PlacePanel(frame.World, actor, second); },
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(reference, 8,
            [new(3, edit => edit.CaptureImage(expected.Add)), new(7, edit => edit.CaptureImage(expected.Add))]),
            "--no-taa", "--fixed-step", "0.125");
        Assert.Equal(2, actual.Count);
        Assert.Equal(2, expected.Count);
        for (int frame = 0; frame < expected.Count; frame++)
        {
            int bluePixels = 0;
            for (int pixel = 0; pixel < expected[frame].Pixels.Length; pixel += 4)
                if (expected[frame].Pixels.Span[pixel + 2] - expected[frame].Pixels.Span[pixel] > 30) bluePixels++;
            Assert.True(bluePixels > 1000, $"Visible skinned silhouette pixels: {bluePixels}.");
            SceneImages.AssertWithinPixelError(expected[frame], actual[frame], pixelRadius: 1, colorTolerance: 4);
        }

        SceneImage? unshadowed = null;
        await SceneImages.RunAsync("d3d12", new SceneScript(reference, 8,
            [new(7, edit => edit.CaptureImage(image => unshadowed = image))]),
            "--no-taa", "--no-vsm", "--fixed-step", "0.125");
        SceneImage light = Assert.IsType<SceneImage>(unshadowed);
        int shadowPixels = 0;
        for (int pixel = 0; pixel < light.Pixels.Length; pixel += 4)
            if (light.Pixels.Span[pixel] - actual[1].Pixels.Span[pixel] > 20) shadowPixels++;
        Assert.True(shadowPixels > 100, $"Visible shadow pixels: {shadowPixels}.");
    }

    private static Skeleton VisibilitySkeleton() => new([-1, -1],
        [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
         new() { Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2]);

    private static Entity AttachLayeredPanels(ApplicationFrame frame, SkeletonInstance actorPose,
        SkeletonInstance backgroundPose, float backgroundScale)
    {
        Entity actor = Entity.Null;
        var binding = new SkinBinding([Matrix4x4.Identity, Matrix4x4.Identity], [0, 1]);
        foreach (Entity entity in frame.Scene.MeshEntities)
        {
            WorldTransform transform = frame.World.Read<WorldTransform>(entity);
            if (transform.Qvvs.Position.Z == 6)
            {
                actor = entity;
                frame.World.Add(entity, new MeshSkin(actorPose, binding));
            }
            else
            {
                frame.World.Add(entity, new MeshSkin(backgroundPose, binding));
                transform.Qvvs.Scale = backgroundScale;
                frame.World.Replace(entity, transform);
            }
        }
        return actor;
    }

    private static void PlacePanel(World world, Entity actor, BoneTransform transform) =>
        world.Replace(actor, new WorldTransform
        {
            Qvvs = new(new Vector3(0, 0, 6) + transform.Translation, transform.Rotation) { Stretch = transform.Scale },
        });

    private sealed partial class SkinContent
    {
        public async Task WriteShadowSceneAsync()
        {
            using MeshAsset mesh = await ImportPanelAsync(21, Positions);
            ClusterVertexAttributesBlock attributes = await mesh.GetBlockAsync<ClusterVertexAttributesBlock>();
            ClusterSkinInfluencesBlock skin = await mesh.GetBlockAsync<ClusterSkinInfluencesBlock>();
            mesh.SetBlock(new ClusterVertexAttributesBlock
            {
                VertexIdentity = attributes.VertexIdentity, Encoding = "someengine.tests.skin.float.v1",
                Clusters = attributes.Clusters.Select((item, cluster) => item with
                    { Data = EncodeFloatInfluences(item, skin.Clusters[cluster], previous: false).ToImmutableArray() }).ToArray(),
            });
            using MeshAsset receiver = mesh.CreateCopy();
            Project.RegisterAsset(await receiver.SaveAsync(Path.Combine(Project.RootPath, "assets/Models/shadow-receiver.mesh.asset")));
            AssetEntry standardEntry = Project.Manifest.List(MaterialAsset.FileType).Single(item => item.Name == "Standard");
            using MaterialAsset standard = await MaterialAsset.OpenAsync(Path.Combine(Project.RootPath, standardEntry.Path));
            using MaterialAsset blue = standard.CreateCopy();
            blue.Name = "Skin blue";
            MaterialBlock parameters = await blue.GetBlockAsync<MaterialBlock>();
            parameters.SetParameter(new("BaseColorTint", MaterialParameterKind.Vector4) { Vector4 = new(.2f, .5f, 1, 1) });
            Project.RegisterAsset(await blue.SaveAsync(Path.Combine(Project.RootPath, "assets/Materials/skin-blue.material.asset")));
            mesh.SetBlock(new MeshMaterialsBlock { Materials = [new(blue.Id)] });
            Project.RegisterAsset(await mesh.SaveAsync(Path.Combine(Project.RootPath, "assets/Models/shadow-panel.mesh.asset")));
            using var world = new World();
            foreach (Vector3 position in new Vector3[] { new(0, 0, 6), new(0, 0, 8) })
            {
                Entity entity = world.CreateEntity(new WorldTransform { Qvvs = new(position, Quaternion.Identity) });
                world.Add(entity, new MeshSource { Mesh = new(position.Z == 6 ? mesh.Id : receiver.Id) });
            }
            world.CreateEntity(new SceneCamera { Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
                VerticalFieldOfView = 1.1f, NearPlane = .1f, FarPlane = 100 });
            world.CreateEntity(new DirectionalLight(new(.6f, .2f, 1), Vector3.One, 3));
            AssetEntry entry = Project.Manifest.List(WorldAsset.FileType).Single(item => item.Name == "Default");
            using var scene = new WorldAsset(entry.AssetGuid) { Name = "Default" };
            scene.WriteWorld(world, SceneSerialization.CreateRegistry());
            Project.RegisterAsset(await scene.SaveAsync(Path.Combine(Project.RootPath, entry.Path)));
        }
    }
}
