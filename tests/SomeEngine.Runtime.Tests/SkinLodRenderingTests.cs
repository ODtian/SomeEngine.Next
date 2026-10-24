using System.Collections.Immutable;
using System.Numerics;
using Game.Importing;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Animation;
using SomeEngine.Animation.Pose;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Render;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Skinning;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Runtime.Tests;

public sealed partial class SkinningRenderingTests
{
    [Fact]
    public async Task SkinnedLodKeepsTheSelectedPixelErrorAcrossNearAndFarPoses()
    {
        using SkinContent content = await SkinContent.CreateAsync();
        float minimumLodError = await content.WriteLodSceneAsync();
        var skeleton = new Skeleton([-1, 0],
            [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Rotation = Quaternion.Identity, Scale = Vector3.One }], [1, 2]);
        var clip = new AnimationClip(skeleton, 2, ClipSampler.Create<StretchedClip, StretchedSampler>(new()), [], -1);
        var storage = new PoseStorage();
        using SkeletonInstance selectedPose = storage.CreateInstance(skeleton), referencePose = storage.CreateInstance(skeleton);
        var selectedMetrics = new List<ClusterFrameMetrics>();
        var referenceMetrics = new List<ClusterFrameMetrics>();
        var selectedImages = new List<SceneImage>();
        var referenceImages = new List<SceneImage>();
        var selected = new ApplicationScene(content.Project.RootPath)
        {
            LodThreshold = .75f,
            FrameRendered = selectedMetrics.Add,
            Initialize = frame => AttachLodAnimation(frame, selectedPose, clip),
            UpdateFrame = MoveLodCamera,
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(selected, 12,
            [new(5, scene => scene.CaptureImage(selectedImages.Add)), new(11, scene => scene.CaptureImage(selectedImages.Add))]),
            "--no-taa", "--no-vsm", "--fixed-step", "0.0625");
        var reference = new ApplicationScene(content.Project.RootPath)
        {
            LodThreshold = 0,
            FrameRendered = referenceMetrics.Add,
            Initialize = frame => AttachLodAnimation(frame, referencePose, clip),
            UpdateFrame = MoveLodCamera,
        };
        await SceneImages.RunAsync("d3d12", new SceneScript(reference, 12,
            [new(5, scene => scene.CaptureImage(referenceImages.Add)), new(11, scene => scene.CaptureImage(referenceImages.Add))]),
            "--no-taa", "--no-vsm", "--fixed-step", "0.0625");

        Assert.Equal(12, selectedMetrics.Count);
        Assert.Equal(12, referenceMetrics.Count);
        Assert.True(DrawnClusters(selectedMetrics[11]) < DrawnClusters(referenceMetrics[11]),
            $"Selected/reference drawn clusters: near {DrawnClusters(selectedMetrics[5])}/{DrawnClusters(referenceMetrics[5])}, " +
            $"far {DrawnClusters(selectedMetrics[11])}/{DrawnClusters(referenceMetrics[11])}; " +
            $"smallest source LOD error {minimumLodError}.");
        for (int view = 0; view < referenceImages.Count; view++)
        {
            SceneImage expected = referenceImages[view];
            int visible = 0;
            for (int pixel = 0; pixel < expected.Pixels.Length; pixel += 4)
                if (expected.Pixels.Span[pixel] > 32) visible++;
            Assert.True(visible > 200);
            SceneImages.AssertWithinPixelError(expected, selectedImages[view], pixelRadius: 1, colorTolerance: 4);
        }
    }

    private static uint DrawnClusters(ClusterFrameMetrics metrics) => metrics.PhaseOneSoftwareClusters
        + metrics.PhaseOneHardwareClusters + metrics.PhaseTwoSoftwareClusters + metrics.PhaseTwoHardwareClusters;

    private static void MoveLodCamera(ApplicationFrame frame)
    {
        if (frame.Index == 6) frame.Scene.SetCameraTransform(new(0, 0, -36), Quaternion.Identity);
    }

    private static void AttachLodAnimation(ApplicationFrame frame, SkeletonInstance pose, AnimationClip clip)
    {
        PoseAnimation.AddSystems(frame.Systems.Animation);
        Entity target = PoseAnimation.CreateTarget(frame.World, pose);
        using var builder = new AnimationBuilder(frame.World);
        builder.Leaf<ClipParameters, ClipLeaf>(target, new(clip, new(0)));
        builder.Play();
        Matrix4x4 inverseBind = Matrix4x4.CreateScale(1.5f, .8f, 1.2f) * Matrix4x4.CreateRotationY(.25f);
        frame.World.Add(frame.Scene.MeshEntities[0], new MeshSkin(pose, new SkinBinding([inverseBind, Matrix4x4.Identity], [1, 1])));
    }

    private sealed class StretchedClip;
    private readonly struct StretchedSampler : IClipSampler<StretchedClip>
    {
        public static void SampleLocal(StretchedClip data, float time, Span<BoneTransform> destination)
        {
            destination[0] = new() { Rotation = Quaternion.Identity, Scale = new(2 + time, 2, 1) };
            destination[1] = new() { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f + .3f * time), Scale = Vector3.One };
        }
    }

    private sealed partial class SkinContent
    {
        public async Task<float> WriteLodSceneAsync()
        {
            using MeshAsset mesh = await ImportSurfaceAsync(10, LodSphere());
            ClusterGeometryBlock geometry = await mesh.GetBlockAsync<ClusterGeometryBlock>();
            Assert.Contains(geometry.Clusters, cluster => cluster.Level > 0);
            ClusterVertexAttributesBlock attributes = await mesh.GetBlockAsync<ClusterVertexAttributesBlock>();
            ClusterSkinInfluencesBlock skin = await mesh.GetBlockAsync<ClusterSkinInfluencesBlock>();
            mesh.SetBlock(new ClusterVertexAttributesBlock
            {
                VertexIdentity = attributes.VertexIdentity, Encoding = "someengine.tests.skin.float.v1",
                Clusters = attributes.Clusters.Select((item, cluster) => item with
                    { Data = EncodeFloatInfluences(item, skin.Clusters[cluster], previous: false).ToImmutableArray() }).ToArray(),
            });
            Project.RegisterAsset(await mesh.SaveAsync(Path.Combine(Project.RootPath, "assets/Models/lod-sphere.mesh.asset")));
            using var world = new World();
            Entity entity = world.CreateEntity(new WorldTransform { Qvvs = new(new(0, 0, 15), Quaternion.Identity) });
            world.Add(entity, new MeshSource { Mesh = new(mesh.Id) });
            world.CreateEntity(new SceneCamera { Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
                VerticalFieldOfView = 1.1f, NearPlane = .1f, FarPlane = 100 });
            world.CreateEntity(new DirectionalLight(new(.35f, -.85f, .4f), Vector3.One, 3));
            AssetEntry entry = Project.Manifest.List(WorldAsset.FileType).Single(item => item.Name == "Default");
            using var scene = new WorldAsset(entry.AssetGuid) { Name = "Default" };
            scene.WriteWorld(world, SceneSerialization.CreateRegistry());
            Project.RegisterAsset(await scene.SaveAsync(Path.Combine(Project.RootPath, entry.Path)));
            return geometry.Clusters.Where(cluster => cluster.Level > 0).Min(cluster => cluster.Error);
        }

        private async Task<MeshAsset> ImportSurfaceAsync(int index, VertexPrimitive primitive)
        {
            await using var context = new ImportContext(Project.RootPath, "skin-lod", _source);
            context.ConfigureAssets<GltfMesh, MeshAsset>(_ => $"surface-{index}", (_, id) => new MeshAsset(id),
                (_, id) => id + ".mesh.asset", static (asset, path, token) => asset.SaveAsync(path, token));
            context.EnableImporter<GltfMesh, MeshAsset>(Guid.NewGuid(), [VertexStreamsBlock.BlockTypeId],
                (_, _, asset, _) => { asset.SetBlock(new VertexStreamsBlock([primitive], [])); return ValueTask.CompletedTask; });
            var services = new ServiceCollection();
            ProjectAssetCapabilities.AddAssetCapabilities(services);
            ServiceProvider provider = services.BuildServiceProvider();
            context.Hold(provider);
            var set = provider.GetServices<AssetImporterSetCapability>().Single(value => value.Id == new Guid(GltfImporterSets.ClusterId));
            foreach (var capability in provider.GetServices<AssetImporterCapability>().Where(value => set.Importers.Contains(value.Id)
                || value.Id == new Guid("334ba003-337f-4298-ae72-1a5ce4861591")))
                context.EnableImporter<GltfMesh, MeshAsset>(capability.Bind<GltfMesh>(provider));
            GltfMesh source = ModelRoot.CreateModel().CreateMesh($"surface-{index}");
            source.CreatePrimitive();
            MeshAsset mesh = await context.GetAssetAsync<GltfMesh, MeshAsset>(source);
            mesh.SetBlock(new MeshMaterialsBlock { Materials = [new(Project.Manifest.List(MaterialAsset.FileType).Single(value => value.Name == "Standard").AssetGuid)] });
            return mesh.Duplicate();
        }
    }

    private static VertexPrimitive LodSphere()
    {
        const int rings = 32, segments = 64;
        var positions = new List<Vector3> { new(0, .5f, 0) };
        for (int ring = 1; ring < rings; ring++)
        for (int segment = 0; segment < segments; segment++)
        {
            float latitude = MathF.PI * ring / rings, longitude = MathF.Tau * segment / segments;
            positions.Add(new Vector3(MathF.Sin(latitude) * MathF.Cos(longitude), MathF.Cos(latitude),
                MathF.Sin(latitude) * MathF.Sin(longitude)) * .5f);
        }
        uint bottom = (uint)positions.Count;
        positions.Add(new(0, -.5f, 0));
        var indices = new List<uint>();
        for (uint segment = 0; segment < segments; segment++)
        {
            uint next = (segment + 1) % segments;
            indices.AddRange([0, 1 + next, 1 + segment]);
            for (uint ring = 0; ring < rings - 2; ring++)
            {
                uint first = 1 + ring * segments;
                indices.AddRange([first + segment, first + next, first + segments + segment,
                    first + next, first + segments + next, first + segments + segment]);
            }
            uint last = 1 + (rings - 2) * segments;
            indices.AddRange([last + segment, last + next, bottom]);
        }
        // The comparison asset uses one normal so pixel differences measure its geometric silhouette.
        var attributes = new Dictionary<string, VertexStream>
        {
            ["POSITION"] = new(3, positions.SelectMany(value => new double[] { value.X, value.Y, value.Z }).ToArray()),
            ["NORMAL"] = new(3, positions.SelectMany(_ => new double[] { 0, 0, -1 }).ToArray()),
            ["TANGENT"] = new(4, positions.SelectMany(_ => new double[] { 1, 0, 0, 1 }).ToArray()),
            ["JOINTS_0"] = new(2, positions.SelectMany(_ => new double[] { 0, 1 }).ToArray()),
            ["WEIGHTS_0"] = new(2, positions.SelectMany(_ => new double[] { 1, 0 }).ToArray()),
        };
        return new(0, 0, indices.ToArray(), Enumerable.Range(0, positions.Count).Select(value => (uint)value).ToArray(), attributes, []);
    }
}
