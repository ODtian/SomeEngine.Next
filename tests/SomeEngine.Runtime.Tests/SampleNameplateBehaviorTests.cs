using System.Numerics;
using System.Text.Json;
using DefaultGame;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Elements.Render;
using SomeEngine.Layout;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Views;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Scene content")]
public sealed class SampleNameplateBehaviorTests(SceneContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task TheSphereSceneNumbersTheOriginalEightSelectedMovingEntities(string backend)
    {
        await using var files = await NameplateSceneFiles.CreateAsync(content.DirectoryPath);
        using var source = new World();
        var spheres = new Entity[1024];
        for (int index = 0; index < spheres.Length; index++)
            spheres[index] = AddMesh(source, files.RigidMesh, index, new(1, .25f, .125f));
        AddCamera(source);
        await files.WriteAsync(source);
        string[] args = ["--graphics-backend", backend, "--hidden-window", "--synchronous-frames",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "1"];
        var platform = new SceneTestPlatform(
            [new TestScene("Selected sphere names", files.DirectoryPath)
                { AssetPacks = files.Packs, ApplicationAsset = files.ApplicationId }],
            RuntimeStartupOptions.Parse(args));
        platform.SampleOptions = platform.SampleOptions with
            { SurfacePanel = false, HudOverlay = false, WorldNameplates = true, DynamicScene = false };
        ApplicationScene application = platform.Application;
        bool observed = false;
        Entity[] expected = [spheres[992], spheres[996], spheres[1000], spheres[1004],
            spheres[1008], spheres[1012], spheres[1016], spheres[1020]];
        application.Initialize += frame => Assert.Equal(spheres, frame.Scene.MeshEntities);
        application.FrameCompleted += frame =>
        {
            AssertNumberedTargets(frame.World, expected);
            Assert.Empty(Entities(frame.World, new QueryDefinitionBuilder().Read<TextAtlas>()));
            observed = true;
        };

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(observed);
        Assert.Equal(1, result.CompletedFrames);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task MixedGeometryKeepsTheMovingEntityOrderAndItsNumberedTargets(string backend)
    {
        await using var files = await NameplateSceneFiles.CreateAsync(content.DirectoryPath);
        using var source = new World();
        // This first zero-motion skin-bounds entity must not decide which moving group is first.
        Entity zeroSkin = AddMesh(source, files.SkinBoundsMesh, -1, Vector3.Zero);
        Entity unsupported = AddMesh(source, files.UnsupportedMesh, -2, Vector3.One);
        Entity zeroRigid = AddMesh(source, files.RigidMesh, -3, Vector3.Zero);
        var rigid = new Entity[1000];
        var skinned = new Entity[24];
        for (int index = 0; index < rigid.Length; index++)
        {
            rigid[index] = AddMesh(source, files.RigidMesh, index, new(1, .25f, .125f));
            if (index < skinned.Length)
                skinned[index] = AddMesh(source, files.SkinBoundsMesh, 2000 + index, new(1, .25f, .125f));
        }
        Entity noMotion = source.CreateEntity(new WorldTransform { Qvvs = new(new(0, 0, 24), Quaternion.Identity) });
        source.Add(noMotion, new MeshSource { Mesh = files.RigidMesh });
        Entity noTransform = source.CreateEntity(new MeshSource { Mesh = files.RigidMesh });
        source.Add(noTransform, new MeshMotion { Amplitude = Vector3.One, Seed = 73 });
        AddCamera(source);
        await files.WriteAsync(source);
        string[] args = ["--graphics-backend", backend, "--hidden-window", "--synchronous-frames",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "1"];
        var platform = new SceneTestPlatform(
            [new TestScene("Mixed moving names", files.DirectoryPath)
                { AssetPacks = files.Packs, ApplicationAsset = files.ApplicationId }],
            RuntimeStartupOptions.Parse(args));
        platform.SampleOptions = platform.SampleOptions with
            { SurfacePanel = false, HudOverlay = false, WorldNameplates = true, DynamicScene = false };
        ApplicationScene application = platform.Application;
        Entity[] moving = [.. rigid, .. skinned];
        var movingSet = moving.ToHashSet();
        Entity[] expectedTargets = [rigid[992], rigid[996], skinned[0], skinned[4],
            skinned[8], skinned[12], skinned[16], skinned[20]];
        bool observed = false;
        // SceneEdit is available only during its public action window.
        application.Initialize += frame => Assert.Equal(moving, frame.Scene.MeshEntities.Where(movingSet.Contains));
        application.FrameCompleted += frame =>
        {
            Assert.All(rigid, entity => Assert.False(frame.World.Has<MeshSkinBounds>(entity)));
            Assert.All(skinned, entity => Assert.True(frame.World.Has<MeshSkinBounds>(entity)));
            Assert.All(moving.Concat([zeroSkin, zeroRigid, noMotion]), entity =>
            {
                Assert.True(frame.World.Has<MeshInstance>(entity));
                Assert.False(frame.World.Has<MeshSource>(entity));
                Assert.False(frame.World.Has<MeshMotion>(entity));
            });
            Assert.True(frame.World.Has<MeshSource>(unsupported));
            Assert.True(frame.World.Has<MeshMotion>(unsupported));
            Assert.False(frame.World.Has<MeshInstance>(unsupported));
            Assert.True(frame.World.Has<MeshSource>(noTransform));
            Assert.True(frame.World.Has<MeshMotion>(noTransform));
            Assert.False(frame.World.Has<MeshInstance>(noTransform));
            AssertNumberedTargets(frame.World, expectedTargets);
            observed = true;
        };

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(observed);
        Assert.Equal(1, result.CompletedFrames);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task WorldNameplatesFollowGameTransformChangesWhenViewTimeDoesNotAdvance(string backend)
    {
        await using var files = await NameplateSceneFiles.CreateAsync(content.DirectoryPath);
        using var source = new World();
        var spheres = new Entity[1024];
        for (int index = 0; index < spheres.Length; index++)
            spheres[index] = AddMesh(source, files.RigidMesh, index, Vector3.One);
        AddCamera(source);
        await files.WriteAsync(source);
        string[] args = ["--graphics-backend", backend, "--hidden-window", "--synchronous-frames",
            "--no-debug-ui", "--no-vsync", "--no-taa", "--no-vsm", "--frames", "1"];
        var platform = new SceneTestPlatform(
            [new TestScene("Following sphere names", files.DirectoryPath)
                { AssetPacks = files.Packs, ApplicationAsset = files.ApplicationId }],
            RuntimeStartupOptions.Parse(args));
        platform.SampleOptions = platform.SampleOptions with
            { SurfacePanel = false, HudOverlay = false, WorldNameplates = true, DynamicScene = false };
        ApplicationScene application = platform.Application;
        Entity[] expectedTargets = [spheres[992], spheres[996], spheres[1000], spheres[1004],
            spheres[1008], spheres[1012], spheres[1016], spheres[1020]];
        int moves = 0;
        bool observed = false;
        application.Initialize += frame => frame.Systems.AddGameSystem(new GameAction(() =>
        {
            moves++;
            frame.World.Replace(spheres[992], new WorldTransform
            {
                Qvvs = new(new Vector3(moves, 2, 20), Quaternion.CreateFromYawPitchRoll(.3f * moves, -.2f, .1f), .5f)
                    { Stretch = new(2, 3, 4) },
            });
        }));
        application.FrameCompleted += frame =>
        {
            Assert.Equal(2, moves); // Existing zero-delta bootstrap and the rendered game update.
            AssertNumberedTargets(frame.World, expectedTargets);
            frame.Systems.UpdateViews(0);
            Assert.Equal(3, moves);
            AssertNumberedTargets(frame.World, expectedTargets);
            frame.Systems.UpdateViews(0);
            Assert.Equal(4, moves);
            AssertNumberedTargets(frame.World, expectedTargets);
            observed = true;
        };

        ApplicationResult result = await Application.RunAsync(args, application);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(observed);
        Assert.Equal(1, result.CompletedFrames);
    }

    private static Entity AddMesh(World world, AssetId<MeshAsset> mesh, int identity, Vector3 amplitude)
    {
        var transform = new TransformQvvs(new((identity % 32 - 15.5f) * .25f,
            (identity / 32 - 15.5f) * .25f, 20 + identity * .001f), Quaternion.Identity, .25f);
        Entity entity = world.CreateEntity(new WorldTransform { Qvvs = transform });
        world.Add(entity, new MeshSource { Mesh = mesh });
        world.Add(entity, new MeshMotion { Amplitude = amplitude, Seed = 73 });
        return entity;
    }

    private static void AddCamera(World world) => world.CreateEntity(new SceneCamera
    {
        Position = Vector3.Zero, Target = Vector3.UnitZ, Up = Vector3.UnitY,
        VerticalFieldOfView = MathF.PI / 3, NearPlane = .1f, FarPlane = 100,
    });

    private static void AssertNumberedTargets(World world, IReadOnlyList<Entity> targets)
    {
        Entity[] canvases = Entities(world, new QueryDefinitionBuilder().Read<Canvas>().Read<WorldTransform>())
            .Where(entity => world.Read<Canvas>(entity).Target.Equals(CanvasTarget.WorldSpace(entity))).ToArray();
        var actual = new List<(string Number, Entity Target)>();
        foreach (Entity canvas in canvases)
        {
            TransformQvvs value = world.Read<WorldTransform>(canvas).Qvvs;
            Entity target = Assert.Single(targets, entity => PlateTransform(world.Read<WorldTransform>(entity).Qvvs).Equals(value));
            actual.Add((ReadNumber(world, canvas), target));
        }
        (string Number, Entity Target)[] expected = targets
            .Select((target, index) => ((index + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture), target)).ToArray();
        Assert.Equal(expected, actual.OrderBy(value => value.Number, StringComparer.Ordinal));
    }

    private static TransformQvvs PlateTransform(TransformQvvs transform) => TransformQvvs.Combine(transform,
        new(new(2.4f, 2.5f, -.2f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI), .03f));

    private static string ReadNumber(World world, Entity canvas)
    {
        var rectangles = new List<Rect>();
        Read(canvas);
        Rect[] segments = [new(0, 0, 14, 3), new(12, 1, 3, 12), new(12, 12, 3, 12),
            new(0, 23, 14, 3), new(0, 12, 3, 12), new(0, 1, 3, 12), new(0, 11, 14, 3)];
        int[] masks = [0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f];
        var digits = new char[3];
        for (int digit = 0; digit < digits.Length; digit++)
        {
            int mask = 0;
            for (int segment = 0; segment < segments.Length; segment++)
            {
                Rect rectangle = segments[segment];
                var expected = new Rect(43 + digit * 24 + rectangle.X, 9 + rectangle.Y, rectangle.Width, rectangle.Height);
                if (rectangles.Remove(expected)) mask |= 1 << segment;
            }
            int number = Array.IndexOf(masks, mask);
            Assert.InRange(number, 0, 9);
            digits[digit] = (char)('0' + number);
        }
        Assert.Empty(rectangles);
        return new string(digits);

        void Read(Entity parent)
        {
            foreach (Entity child in Hierarchy<ElementTree>.GetChildren(world, parent))
            {
                if (world.Has<Surface>(child) && world.Read<Surface>(child).Fill.Equals(new Color(.85f, .95f, 1)))
                    rectangles.Add(world.Read<LayoutRect>(child).Value);
                Read(child);
            }
        }
    }

    private static Entity[] Entities(World world, QueryDefinitionBuilder definition)
    {
        QueryHandle query = world.Query(definition);
        var entities = new List<Entity>();
        try
        {
            world.ExecuteQuery(query, ref entities, static (rows, ref values) =>
            {
                foreach (var row in rows.Rows) values.Add(row.Entity);
            });
            return entities.ToArray();
        }
        finally { world.ReleaseQuery(query); }
    }

    private sealed class GameAction(Action action) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) => action();
    }

    // Only asset setup: tests above own every entity, action and expected result.
    private sealed class NameplateSceneFiles : IAsyncDisposable
    {
        private readonly string _sourceDirectory;
        private readonly AssetProject _project;
        private readonly ApplicationSettingsAsset _settings;
        private NameplateSceneFiles(string source, string directory, AssetProject project,
            ApplicationSettingsAsset settings, AssetId<MeshAsset> rigid, AssetId<MeshAsset> skinBounds, AssetId<MeshAsset> unsupported)
            => (_sourceDirectory, DirectoryPath, _project, _settings, RigidMesh, SkinBoundsMesh, UnsupportedMesh)
                = (source, directory, project, settings, rigid, skinBounds, unsupported);
        internal string DirectoryPath { get; }
        internal AssetId<MeshAsset> RigidMesh { get; }
        internal AssetId<MeshAsset> SkinBoundsMesh { get; }
        internal AssetId<MeshAsset> UnsupportedMesh { get; }
        internal string[] Packs { get; private set; } = [];
        internal Guid ApplicationId => _settings.Id;

        internal static async Task<NameplateSceneFiles> CreateAsync(string sourceDirectory)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Nameplates-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            ApplicationSettingsAsset? settings = null;
            try
            {
                var project = new AssetProject(directory, []);
                await using AssetContent source = await AssetContent.OpenAsync(sourceDirectory);
                AssetEntry application = Assert.Single(source.Manifest.List(ApplicationSettingsAsset.FileType));
                using ApplicationSettingsAsset original = await ApplicationSettingsAsset.OpenAsync(source.Loader, new(application.AssetGuid));
                using WorldAsset scene = await WorldAsset.OpenAsync(source.Loader, original.GetBlock<ApplicationSettingsBlock>().Scene);
                using World sourceWorld = await scene.LoadAsync(SampleSceneSerialization.CreateRegistry());
                Entity first = Entities(sourceWorld, new QueryDefinitionBuilder().Read<MeshSource>()).First();
                using MeshAsset template = await MeshAsset.OpenAsync(source.Loader, sourceWorld.Read<MeshSource>(first).Mesh);
                AssetId<MaterialAsset> material = template.GetBlock<MeshMaterialsBlock>().Materials[0];
                var (vertices, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(0);
                using MeshAsset rigid = ClusterBuilder.ProcessRaw(vertices, attributes, indices, [new(material.Value)], "nameplate rigid");
                project.RegisterAsset(await rigid.SaveAsync(Path.Combine(directory, "rigid.mesh.asset")));
                using MeshAsset skinBounds = rigid.CreateCopy();
                ClusterGeometryBlock geometry = rigid.GetBlock<ClusterGeometryBlock>();
                using var bounds = new ClusterSkinBoundsBlock
                {
                    VertexIdentity = geometry.VertexIdentity,
                    Clusters = geometry.Clusters.Select(cluster => new ClusterSkinBoxSet
                    {
                        Joints = [new(0,
                            new() { X = cluster.BoundMin.X, Y = cluster.BoundMin.Y, Z = cluster.BoundMin.Z },
                            new() { X = cluster.BoundMax.X, Y = cluster.BoundMax.Y, Z = cluster.BoundMax.Z })],
                        Group = cluster.Group, ParentGroup = cluster.ParentGroup, Level = cluster.Level,
                        Error = cluster.Error, PositionError = cluster.PositionStep,
                    }).ToList(),
                };
                Assert.Empty(skinBounds.SetBlock(bounds));
                project.RegisterAsset(await skinBounds.SaveAsync(Path.Combine(directory, "skin-bounds.mesh.asset")));
                using MeshAsset unsupported = rigid.CreateCopy();
                Assert.Empty(unsupported.RemoveBlock<ClusterGeometryBlock>());
                Assert.Empty(unsupported.RemoveBlock<ClusterMeshBlock>());
                project.RegisterAsset(await unsupported.SaveAsync(Path.Combine(directory, "unsupported.mesh.asset")));
                settings = original.CreateCopy();
                return new(sourceDirectory, directory, project, settings, rigid.Reference, skinBounds.Reference, unsupported.Reference);
            }
            catch
            {
                settings?.Dispose();
                Directory.Delete(directory, recursive: true);
                throw;
            }
        }

        internal async Task WriteAsync(World world)
        {
            using var scene = new WorldAsset { Name = "nameplate behavior" };
            scene.WriteWorld(world, SampleSceneSerialization.CreateRegistry());
            _project.RegisterAsset(await scene.SaveAsync(Path.Combine(DirectoryPath, "names.scene.asset")));
            ApplicationSettingsBlock settings = _settings.GetBlock<ApplicationSettingsBlock>();
            settings.Scene = scene.Reference;
            settings.Width = settings.Height = 64;
            _project.RegisterAsset(await _settings.SaveAsync(Path.Combine(DirectoryPath, "names.runtime.asset")));
            string sourceRoot = Path.GetFullPath(_sourceDirectory);
            string[] shared = [];
            if (File.Exists(sourceRoot)) shared = [sourceRoot];
            else if (File.Exists(Path.Combine(sourceRoot, AssetContent.PackPathsFile)))
            {
                string[] paths = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(
                    Path.Combine(sourceRoot, AssetContent.PackPathsFile)))!;
                shared = paths.Select(path => Path.GetFullPath(path, sourceRoot)).ToArray();
            }
            else
            {
                // Shared cooked bytes remain in their original files. Link only the selected dependencies.
                var source = new AssetProject(sourceRoot, []);
                var selected = new HashSet<Guid>();
                var linkedPacks = new Dictionary<string, string>(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                var required = new Stack<Guid>(_project.Manifest.Assets.Values.SelectMany(entry => entry.Dependencies));
                string sharedDirectory = Path.Combine(DirectoryPath, "shared");
                Directory.CreateDirectory(sharedDirectory);
                while (required.TryPop(out Guid id))
                {
                    if (_project.Manifest.Assets.ContainsKey(id) || !selected.Add(id)) continue;
                    AssetEntry entry = source.Manifest.Assets[id];
                    foreach (Guid dependency in entry.Dependencies) required.Push(dependency);
                    string local = Path.Combine(sharedDirectory, $"{id:N}-{Path.GetFileName(entry.Path)}");
                    string pack = "";
                    if (entry.PackPath.Length == 0)
                        File.CreateSymbolicLink(local, Path.GetFullPath(entry.Path, sourceRoot));
                    else
                    {
                        string originalPack = Path.GetFullPath(entry.PackPath, sourceRoot);
                        if (!linkedPacks.TryGetValue(originalPack, out string? linked))
                        {
                            linked = Path.Combine(sharedDirectory, $"pack-{linkedPacks.Count}.assetpack");
                            File.CreateSymbolicLink(linked, originalPack);
                            linkedPacks.Add(originalPack, linked);
                        }
                        pack = Path.GetRelativePath(DirectoryPath, linked);
                    }
                    _project.RegisterAsset(entry with
                    {
                        Path = local, PackPath = pack, DefinitionPath = "", SourceGuid = default, SubAssetKey = "",
                    });
                }
                // Do not copy or repackage on link failure; the exception identifies the unavailable fixture setup.
                return;
            }
            string overlay = Path.Combine(DirectoryPath, "names.assetpack");
            await _project.BuildPackAsync(overlay, _project.Manifest.Assets.Keys);
            Packs = [overlay, .. shared];
        }

        public async ValueTask DisposeAsync()
        {
            try { await _settings.DisposeAsync(); }
            finally { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
        }
    }
}
