using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render;
using SomeEngine.Render.Components;
using SomeEngine.Serialization;
using GltfAnimation = SharpGLTF.Schema2.Animation;
using GltfMaterial = SharpGLTF.Schema2.Material;
using GltfSkin = SharpGLTF.Schema2.Skin;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class GltfAnimationElementBehaviorTests
{
    [Fact]
    public async Task SelectedImportersSaveEverySkinAndAnimationWithTypedIdentityAndContent()
    {
        using var files = new AnimationFiles();
        var project = new AssetProject(files.Directory, []);
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, CreateImporter());
        AssetEntry[] skeletons = Entries(project, SkeletonAsset.FileType);
        AssetEntry[] clips = Entries(project, AnimationClipAsset.FileType);
        Assert.Equal(3, skeletons.Length);
        Assert.Equal(2, clips.Length);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.NotEqual(skeletons[0].AssetGuid, clips[0].AssetGuid);

        await using var loader = new AssetLoader(project.CreateStorage());
        foreach (AssetEntry entry in skeletons)
        {
            Assert.EndsWith(SkeletonAsset.PathSuffix, entry.Path);
            using SkeletonAsset asset = await SkeletonAsset.OpenAsync(loader, new(entry.AssetGuid));
            Assert.Equal(entry.AssetGuid, asset.Reference.Value);
            SkinElementBlock block = await asset.GetBlockAsync<SkinElementBlock>();
            Assert.Equal("skin selection", block.Label);
            Assert.Equal(1, block.JointCount);
            Assert.Equal(block.SourceIndex == 2 ? "Skin_2" : "Rig", asset.Name);
        }
        foreach (AssetEntry entry in clips)
        {
            Assert.EndsWith(AnimationClipAsset.PathSuffix, entry.Path);
            using AnimationClipAsset asset = await AnimationClipAsset.OpenAsync(Path.Combine(files.Directory, entry.Path));
            Assert.Equal(entry.AssetGuid, asset.Reference.Value);
            AnimationElementBlock block = await asset.GetBlockAsync<AnimationElementBlock>();
            Assert.Equal("clip selection", block.Label);
            Assert.Equal(1, block.ChannelCount);
            Assert.Equal(block.SourceIndex == 0 ? "Walk" : "Animation_1", asset.Name);
            using AnimationClipAsset byId = await AnimationClipAsset.OpenAsync(loader, asset.Reference);
            Assert.Equal(asset.Name, byId.Name);
        }
    }

    [Fact]
    public async Task ReorderingAndRenamingPreservesElementIdentitiesAndOutputPaths()
    {
        using var files = new AnimationFiles();
        var importer = CreateImporter();
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, importer);
        AssetEntry[] first = Entries(project, SkeletonAsset.FileType).Concat(Entries(project, AnimationClipAsset.FileType))
            .OrderBy(entry => entry.SubAssetKey).ToArray();
        files.Edit(source =>
        {
            JsonArray skins = source["skins"]!.AsArray();
            JsonNode firstSkin = skins[0]!.DeepClone();
            skins[0] = skins[2]!.DeepClone();
            skins[2] = firstSkin;
            source["nodes"]![0]!["skin"] = 2;
            source["nodes"]![1]!["skin"] = 2;
            JsonArray animations = source["animations"]!.AsArray();
            JsonNode firstAnimation = animations[0]!.DeepClone();
            animations[0] = animations[1]!.DeepClone();
            animations[1] = firstAnimation;
            foreach (JsonNode? skin in skins) skin!["name"] = "Renamed rig";
            foreach (JsonNode? animation in animations) animation!["name"] = "Renamed clip";
        });
        var reopenedProject = new AssetProject(files.Directory, []);
        await reopenedProject.ImportAsync(files.Source, importer);
        AssetEntry[] second = Entries(reopenedProject, SkeletonAsset.FileType).Concat(Entries(reopenedProject, AnimationClipAsset.FileType))
            .OrderBy(entry => entry.SubAssetKey).ToArray();
        Assert.Equal(first.Select(entry => (entry.SubAssetKey, entry.AssetGuid, entry.Path)),
            second.Select(entry => (entry.SubAssetKey, entry.AssetGuid, entry.Path)));
        using SkeletonAsset skeleton = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory,
            second.Single(entry => entry.SubAssetKey == "skin:id:shared-A").Path));
        Assert.Equal(2, (await skeleton.GetBlockAsync<SkinElementBlock>()).SourceIndex);
        using AnimationClipAsset clip = await AnimationClipAsset.OpenAsync(Path.Combine(files.Directory,
            second.Single(entry => entry.SubAssetKey == "animation:id:shared-A").Path));
        Assert.Equal(1, (await clip.GetBlockAsync<AnimationElementBlock>()).SourceIndex);
        Assert.Equal("Renamed rig", skeleton.Name);
        Assert.Equal("Renamed clip", clip.Name);
        using SkeletonAsset branch = skeleton.Duplicate();
        using SkeletonAsset copy = skeleton.CreateCopy();
        Assert.Equal(skeleton.Reference, branch.Reference);
        Assert.NotEqual(skeleton.Reference, copy.Reference);
        using AnimationClipAsset clipBranch = clip.Duplicate();
        using AnimationClipAsset clipCopy = clip.CreateCopy();
        Assert.Equal(clip.Reference, clipBranch.Reference);
        Assert.NotEqual(clip.Reference, clipCopy.Reference);
    }

    [Fact]
    public async Task SkinnedNodesShareMeshAndReferenceTheirOwnSkinThroughWorldSerialization()
    {
        using var files = new AnimationFiles();
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, CreateImporter());
        AssetEntry scene = Assert.Single(Entries(project, WorldAsset.FileType));
        using WorldAsset asset = await WorldAsset.OpenAsync(Path.Combine(files.Directory, scene.Path));
        Guid firstSkin = Entries(project, SkeletonAsset.FileType).Single(entry => entry.SubAssetKey == "skin:id:shared-A").AssetGuid;
        Guid secondSkin = Entries(project, SkeletonAsset.FileType).Single(entry => entry.SubAssetKey == "skin:id:rig-B").AssetGuid;
        Guid mesh = Entries(project, MeshAsset.FileType).Single(entry => entry.SubAssetKey == "mesh:id:mesh-A").AssetGuid;
        Guid staticMesh = Entries(project, MeshAsset.FileType).Single(entry => entry.SubAssetKey == "mesh:id:mesh-B").AssetGuid;
        Assert.Equal(new[] { mesh, staticMesh, firstSkin, secondSkin }.Order(), asset.ExternalReferences.Select(id => id.Value).Order());
        Assert.Equal(asset.ExternalReferences.Select(id => id.Value).Order(), scene.Dependencies.Order());
        using World world = await asset.LoadAsync(SceneSerialization.CreateRegistry());
        Entity[] entities = MeshEntities(world);
        Assert.Equal(4, entities.Length);
        Entity[] skinned = entities.Where(entity => world.Has<SkeletonSource>(entity)).ToArray();
        Assert.Equal(3, skinned.Length);
        Assert.All(skinned, entity => Assert.Equal(mesh, world.Read<MeshSource>(entity).Mesh.Value));
        Assert.Equal(2, skinned.Count(entity => world.Read<SkeletonSource>(entity).Skeleton.Value == firstSkin));
        Assert.Single(skinned, entity => world.Read<SkeletonSource>(entity).Skeleton.Value == secondSkin);
        Entity first = entities.Single(entity => world.Read<WorldTransform>(entity).Qvvs.Position == new Vector3(1, 2, 3));
        Assert.Equal(firstSkin, world.Read<SkeletonSource>(first).Skeleton.Value);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task MetadataSelectionsControlSkeletonAndClipOutputs(bool skins, bool animations)
    {
        using var files = new AnimationFiles();
        var project = new AssetProject(files.Directory, []);
        var importer = CreateImporter();
        await project.ImportAsync(files.Source, importer);
        files.SelectImporters(skins, animations);
        await project.ImportAsync(files.Source, importer);
        Assert.Equal(skins ? 3 : 0, Entries(project, SkeletonAsset.FileType).Length);
        Assert.Equal(animations ? 2 : 0, Entries(project, AnimationClipAsset.FileType).Length);
        Assert.Equal(skins ? 3 : 0, Directory.GetFiles(files.Directory, "*" + SkeletonAsset.PathSuffix).Length);
        Assert.Equal(animations ? 2 : 0, Directory.GetFiles(files.Directory, "*" + AnimationClipAsset.PathSuffix).Length);
        AssetEntry scene = Assert.Single(Entries(project, WorldAsset.FileType));
        using WorldAsset asset = await WorldAsset.OpenAsync(Path.Combine(files.Directory, scene.Path));
        using World world = await asset.LoadAsync(SceneSerialization.CreateRegistry());
        Assert.Equal(skins ? 3 : 0, MeshEntities(world).Count(entity => world.Has<SkeletonSource>(entity)));
    }

    [Theory]
    [InlineData("skins")]
    [InlineData("animations")]
    public async Task ElementsRequirePersistentIdsByDefault(string collection)
    {
        using var files = new AnimationFiles();
        files.Edit(source => source[collection]![0]!.AsObject().Remove("extras"));
        var project = new AssetProject(files.Directory, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => project.ImportAsync(files.Source, CreateImporter()).AsTask());
    }

    [Fact]
    public async Task ExplicitOrdinalIdentityKeepsAssetIdsWhenReimportingEditedContent()
    {
        using var files = new AnimationFiles();
        files.Edit(source =>
        {
            foreach (JsonNode? skin in source["skins"]!.AsArray()) skin!.AsObject().Remove("extras");
            foreach (JsonNode? animation in source["animations"]!.AsArray()) animation!.AsObject().Remove("extras");
        });
        SourceMeta metadata = SourceMeta.Load(files.Source);
        metadata.Importers.Add(new(GltfSourceImporter.SourceImporterId,
            JsonSerializer.SerializeToElement(new GltfImporterSettings { RequirePersistentElementIds = false },
                GltfSettingsJson.Default.GltfImporterSettings)));
        SourceMeta.Save(files.Source, metadata);
        var project = new AssetProject(files.Directory, []);
        var importer = CreateImporter();
        await project.ImportAsync(files.Source, importer);
        AssetEntry[] first = Entries(project, SkeletonAsset.FileType).Concat(Entries(project, AnimationClipAsset.FileType))
            .OrderBy(entry => entry.SubAssetKey).ToArray();
        files.Edit(source => source["skins"]![0]!["name"] = "Edited rig");
        await project.ImportAsync(files.Source, importer);
        AssetEntry[] second = Entries(project, SkeletonAsset.FileType).Concat(Entries(project, AnimationClipAsset.FileType))
            .OrderBy(entry => entry.SubAssetKey).ToArray();
        Assert.Equal(first.Select(entry => (entry.SubAssetKey, entry.AssetGuid)), second.Select(entry => (entry.SubAssetKey, entry.AssetGuid)));
        Assert.Contains(second, entry => entry.SubAssetKey == "skin:index:0" && entry.Name == "Edited rig");
        Assert.Contains(second, entry => entry.SubAssetKey == "animation:index:1");
    }

    private static GltfSourceImporter CreateImporter()
        => new(services => ConsumerAssetCapabilities.AddAssetCapabilities(services), "animation-elements-1", ElementSceneImporter.Id);

    private static AssetEntry[] Entries(AssetProject project, string type)
        => project.Manifest.Assets.Values.Where(entry => entry.AssetType == type).OrderBy(entry => entry.SubAssetKey).ToArray();

    private static Entity[] MeshEntities(World world)
    {
        var entities = new List<Entity>();
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<MeshSource>());
        try
        {
            world.ExecuteQuery(query, ref entities, static (QueryCursor rows, ref List<Entity> values) =>
            {
                foreach (var row in rows.Rows) values.Add(row.Entity);
            });
        }
        finally { world.ReleaseQuery(query); }
        return entities.ToArray();
    }

    private sealed class AnimationFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "gltf-animation-elements-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Directory, "character.gltf");

        public AnimationFiles()
        {
            System.IO.Directory.CreateDirectory(Directory);
            byte[] bytes = new byte[136];
            float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
            for (int i = 0; i < positions.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), positions[i]);
            for (int i = 0; i < 3; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(48 + i * 16, 4), 1);
            for (ushort i = 0; i < 3; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(96 + i * 2, 2), i);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(108, 4), 1);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(128, 4), 1);
            File.WriteAllText(Source, $$$$"""
                {"asset":{"version":"2.0"},
                 "buffers":[{"uri":"data:application/octet-stream;base64,{{{{Convert.ToBase64String(bytes)}}}}","byteLength":136}],
                 "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36,"target":34962},
                    {"buffer":0,"byteOffset":36,"byteLength":12,"target":34962},
                    {"buffer":0,"byteOffset":48,"byteLength":48,"target":34962},
                    {"buffer":0,"byteOffset":96,"byteLength":6,"target":34963},
                    {"buffer":0,"byteOffset":104,"byteLength":8},{"buffer":0,"byteOffset":112,"byteLength":24}],
                 "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},
                    {"bufferView":1,"componentType":5121,"count":3,"type":"VEC4"},
                    {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
                    {"bufferView":3,"componentType":5123,"count":3,"type":"SCALAR"},
                    {"bufferView":4,"componentType":5126,"count":2,"type":"SCALAR","min":[0],"max":[1]},
                    {"bufferView":5,"componentType":5126,"count":2,"type":"VEC3"}],
                 "meshes":[{"extras":{"someengine_id":"mesh-A"},"primitives":[{"attributes":{"POSITION":0,"JOINTS_0":1,"WEIGHTS_0":2},"indices":3}]},
                    {"extras":{"someengine_id":"mesh-B"},"primitives":[{"attributes":{"POSITION":0},"indices":3}]}],
                 "nodes":[{"mesh":0,"skin":0,"translation":[1,2,3]},{"mesh":0,"skin":0},{"mesh":0,"skin":1},
                    {"name":"Joint A"},{"name":"Joint B"},{"mesh":1}],
                 "skins":[{"name":"Rig","joints":[3],"extras":{"someengine_id":"shared-A"}},
                    {"name":"Rig","joints":[4],"extras":{"someengine_id":"rig-B"}},
                    {"joints":[3],"extras":{"someengine_id":"rig-unused"}}],
                 "animations":[{"name":"Walk","extras":{"someengine_id":"shared-A"},"samplers":[{"input":4,"output":5}],
                    "channels":[{"sampler":0,"target":{"node":3,"path":"translation"}}]},
                    {"extras":{"someengine_id":"clip-B"},"samplers":[{"input":4,"output":5}],
                    "channels":[{"sampler":0,"target":{"node":4,"path":"translation"}}]}],
                 "scenes":[{"extras":{"someengine_id":"scene-A"},"nodes":[0,1,2,3,4,5]}],"scene":0}
                """);
            SelectImporters(true, true);
        }

        public void SelectImporters(bool skins, bool animations)
        {
            SourceMeta metadata = File.Exists(SourceMeta.GetMetaPath(Source)) ? SourceMeta.Load(Source)
                : new SourceMeta { SourceGuid = SourceGuid.New(), UseDefaultImporters = false };
            metadata.Importers.Clear();
            metadata.Importers.Add(new(ElementSceneImporter.Id, default));
            metadata.Importers.Add(new(SkinElementImporter.Id, JsonSerializer.SerializeToElement(new ElementSettings("skin selection"), ElementSettingsJson.Default.ElementSettings), skins));
            metadata.Importers.Add(new(AnimationElementImporter.Id, JsonSerializer.SerializeToElement(new ElementSettings("clip selection"), ElementSettingsJson.Default.ElementSettings), animations));
            SourceMeta.Save(Source, metadata);
        }

        public void Edit(Action<JsonNode> edit)
        {
            JsonNode source = JsonNode.Parse(File.ReadAllText(Source))!;
            edit(source);
            File.WriteAllText(Source, source.ToJsonString());
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

[AssetBlock("SomeEngine.Tests.SkinElement"), BinarySerializable, BinaryFields(nameof(SourceIndex), nameof(JointCount), nameof(Label))]
public sealed partial class SkinElementBlock : AssetBlock
{
    public partial int SourceIndex { get; set; }
    public partial int JointCount { get; set; }
    public partial string Label { get; set; }
}

[AssetBlock("SomeEngine.Tests.AnimationElement"), BinarySerializable, BinaryFields(nameof(SourceIndex), nameof(ChannelCount), nameof(Label))]
public sealed partial class AnimationElementBlock : AssetBlock
{
    public partial int SourceIndex { get; set; }
    public partial int ChannelCount { get; set; }
    public partial string Label { get; set; }
}

public sealed record ElementSettings(string Label);
[JsonSerializable(typeof(ElementSettings))]
public sealed partial class ElementSettingsJson : JsonSerializerContext;
public sealed class ElementSettingsProvider : IAssetImporterSettings<ElementSettings>
{
    public ElementSettings DefaultSettings => new("default");
    public JsonTypeInfo<ElementSettings> TypeInfo => ElementSettingsJson.Default.ElementSettings;
}

[AssetImporter("31579b59-ed68-4b0e-a5f8-8c94d3f4b850", typeof(SkinElementBlock), SettingsProvider = typeof(ElementSettingsProvider))]
public sealed class SkinElementImporter : IAssetImporter<GltfSkin, ElementSettings>
{
    public static readonly Guid Id = new("31579b59-ed68-4b0e-a5f8-8c94d3f4b850");
    public ValueTask ImportAsync(GltfSkin source, ElementSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new SkinElementBlock { SourceIndex = source.LogicalIndex, JointCount = source.JointsCount, Label = settings.Label });
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("ab606aa2-8aad-4f57-9611-e7eafcfe683d", typeof(AnimationElementBlock), SettingsProvider = typeof(ElementSettingsProvider))]
public sealed class AnimationElementImporter : IAssetImporter<GltfAnimation, ElementSettings>
{
    public static readonly Guid Id = new("ab606aa2-8aad-4f57-9611-e7eafcfe683d");
    public ValueTask ImportAsync(GltfAnimation source, ElementSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new AnimationElementBlock { SourceIndex = source.LogicalIndex, ChannelCount = source.Channels.Count, Label = settings.Label });
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("963b35fa-1831-45f7-9d80-83f826c132a8", typeof(WorldBlock), SettingsProvider = typeof(GltfSceneSettingsProvider))]
public sealed class ElementSceneImporter : GltfSceneImporter
{
    public static readonly Guid Id = new("963b35fa-1831-45f7-9d80-83f826c132a8");
    public override ValueTask ImportMaterialAsync(GltfMaterial source, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
