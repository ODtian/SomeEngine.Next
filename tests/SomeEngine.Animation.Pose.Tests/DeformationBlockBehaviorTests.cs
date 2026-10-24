using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SomeEngine.Animation.Pose.Importers;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render;
using SomeEngine.Render.Components;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class DeformationBlockBehaviorTests
{
    [Fact]
    public async Task ImportedSkinBindingKeepsItsJointDataAfterTheAssetCloses()
    {
        using var files = new DeformationFiles(false);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        SkinBinding binding;
        using (SkeletonAsset asset = await OpenSkeleton(project, files, "first"))
            binding = (await asset.GetBlockAsync<SkinBindingBlock>()).CreateSkinBinding();

        Assert.Equal(2, binding.JointCount);
        Assert.Equal(new short[] { 2, 0 }, binding.JointBones.ToArray());
        Assert.Equal(new[] { DeformationFiles.FirstMatrix, DeformationFiles.SecondMatrix }, binding.InverseBindMatrices.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NeutralGroupsSaveSkinBindingsAndMorphDefaultsForSharedMeshNodes(bool binary)
    {
        using var files = new DeformationFiles(binary);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        using SkeletonAsset first = await OpenSkeleton(project, files, "first");
        using SkeletonAsset second = await OpenSkeleton(project, files, "second");
        SkinBindingBlock binding = await first.GetBlockAsync<SkinBindingBlock>();
        Assert.Equal(2, binding.JointCount);
        Assert.Equal(new short[] { 2, 0 }, binding.JointBones.ToArray());
        Assert.Equal(new[] { DeformationFiles.FirstMatrix, DeformationFiles.SecondMatrix }, binding.InverseBindMatrices.ToArray());
        Assert.Equal((await first.GetBlockAsync<SkeletonBlock>()).JointBones.ToArray(), binding.JointBones.ToArray());
        SkinBindingBlock other = await second.GetBlockAsync<SkinBindingBlock>();
        Assert.Equal(new short[] { 0, 2 }, other.JointBones.ToArray());
        Assert.Equal(new[] { Matrix4x4.Identity, Matrix4x4.Identity }, other.InverseBindMatrices.ToArray());

        using MeshAsset mesh = await OpenMesh(project, files, "shared");
        MorphTargetSetBlock targets = await mesh.GetBlockAsync<MorphTargetSetBlock>();
        Assert.Equal(new[] { "笑😊", "Blink" }, targets.Names);
        Assert.Equal(new[] { -0.25f, 1.5f }, targets.DefaultWeights);
        using MeshAsset staticMesh = await OpenMesh(project, files, "static");
        Assert.Null(await staticMesh.TryGetBlockAsync<SkinBindingBlock>());
        Assert.Null(await staticMesh.TryGetBlockAsync<MorphTargetSetBlock>());

        AssetEntry scene = Assert.Single(project.Manifest.Assets.Values, entry => entry.AssetType == WorldAsset.FileType);
        using WorldAsset worldAsset = await WorldAsset.OpenAsync(Path.Combine(files.Directory, scene.Path));
        using World world = await worldAsset.LoadAsync(SceneSerialization.CreateRegistry());
        Entity[] nodes = MeshNodes(world);
        Entity[] skinned = nodes.Where(node => world.Has<SkeletonSource>(node)).ToArray();
        Assert.Equal(2, skinned.Length);
        Assert.All(skinned, node => Assert.Equal(mesh.Reference, world.Read<MeshSource>(node).Mesh));
        Assert.Equal(new[] { first.Id, second.Id }.Order(), skinned.Select(node => world.Read<SkeletonSource>(node).Skeleton.Value).Order());
        Entity staticNode = Assert.Single(nodes, node => world.Read<MeshSource>(node).Mesh == staticMesh.Reference);
        Assert.False(world.Has<SkeletonSource>(staticNode));

        string saved = Path.Combine(files.Directory, "copy.skeleton.asset");
        await first.SaveAsync(saved);
        using SkeletonAsset reopened = await SkeletonAsset.OpenAsync(saved);
        AssertBinding(binding, await reopened.GetBlockAsync<SkinBindingBlock>());
        using SkeletonAsset copy = first.CreateCopy();
        AssertBinding(binding, await copy.GetBlockAsync<SkinBindingBlock>());
        string savedMesh = Path.Combine(files.Directory, "copy.mesh.asset");
        await mesh.SaveAsync(savedMesh);
        using MeshAsset reopenedMesh = await MeshAsset.OpenAsync(savedMesh);
        MorphTargetSetBlock savedTargets = await reopenedMesh.GetBlockAsync<MorphTargetSetBlock>();
        Assert.Equal(targets.Names, savedTargets.Names);
        Assert.Equal(targets.DefaultWeights, savedTargets.DefaultWeights);
    }

    [Fact]
    public async Task OmittedMorphNamesAndWeightsSupplyOneEmptyNameAndZeroPerTarget()
    {
        using var files = new DeformationFiles(false);
        files.Edit(source =>
        {
            source["meshes"]![0]!.AsObject().Remove("weights");
            source["meshes"]![0]!["extras"]!.AsObject().Remove("targetNames");
        });
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        using MeshAsset mesh = await OpenMesh(project, files, "shared");
        MorphTargetSetBlock targets = await mesh.GetBlockAsync<MorphTargetSetBlock>();
        Assert.Equal(new[] { "", "" }, targets.Names);
        Assert.Equal(new[] { 0f, 0f }, targets.DefaultWeights);
    }

    [Fact]
    public async Task BindingConsumesTheProjectSelectedSkeletonJointMapping()
    {
        using var files = new DeformationFiles(false);
        SourceMeta metadata = SourceMeta.Load(files.Source);
        metadata.Importers.Add(new(ProjectSkeletonImporter.Id, default));
        SourceMeta.Save(files.Source, metadata);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        using SkeletonAsset skeleton = await OpenSkeleton(project, files, "first");
        SkinBindingBlock binding = await skeleton.GetBlockAsync<SkinBindingBlock>();
        Assert.Equal(new short[] { 0, 0 }, binding.JointBones.ToArray());
        Assert.Equal(new[] { DeformationFiles.FirstMatrix, DeformationFiles.SecondMatrix }, binding.InverseBindMatrices.ToArray());
    }

    [Fact]
    public async Task MetadataCanDisableEachNeutralFeatureWhileKeepingMeshAndSkeletonContent()
    {
        using var files = new DeformationFiles(false);
        SourceMeta metadata = SourceMeta.Load(files.Source);
        metadata.Importers.Add(new(SkinBindingImporter.Id, default, Enabled: false));
        metadata.Importers.Add(new(MorphTargetSetImporter.Id, default, Enabled: false));
        SourceMeta.Save(files.Source, metadata);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        using SkeletonAsset skeleton = await OpenSkeleton(project, files, "first");
        Assert.Equal(new short[] { 2, 0 }, (await skeleton.GetBlockAsync<SkeletonBlock>()).JointBones.ToArray());
        Assert.Null(await skeleton.TryGetBlockAsync<SkinBindingBlock>());
        using MeshAsset mesh = await OpenMesh(project, files, "shared");
        Assert.Single((await mesh.GetBlockAsync<MeshBlock>()).Regions);
        Assert.Null(await mesh.TryGetBlockAsync<MorphTargetSetBlock>());
    }

    [Fact]
    public async Task ReimportKeepsAssetIdentitiesAndUpdatesOptionalContent()
    {
        using var files = new DeformationFiles(false);
        var importer = SourceImporter();
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, importer);
        Guid meshId;
        Guid skinId;
        using (MeshAsset mesh = await OpenMesh(project, files, "shared")) meshId = mesh.Id;
        using (SkeletonAsset skeleton = await OpenSkeleton(project, files, "first")) skinId = skeleton.Id;
        files.Edit(source =>
        {
            source["skins"]![0]!.AsObject().Remove("inverseBindMatrices");
            source["meshes"]![0]!["weights"] = new JsonArray(0.5, -0.75);
            source["meshes"]![0]!["extras"]!["targetNames"] = new JsonArray("Updated", "Blink");
        });
        await project.ImportAsync(files.Source, importer);
        using (SkeletonAsset skeleton = await OpenSkeleton(project, files, "first"))
        {
            Assert.Equal(skinId, skeleton.Id);
            Assert.Equal(new[] { Matrix4x4.Identity, Matrix4x4.Identity }, (await skeleton.GetBlockAsync<SkinBindingBlock>()).InverseBindMatrices.ToArray());
        }
        using (MeshAsset mesh = await OpenMesh(project, files, "shared"))
        {
            Assert.Equal(meshId, mesh.Id);
            MorphTargetSetBlock targets = await mesh.GetBlockAsync<MorphTargetSetBlock>();
            Assert.Equal(new[] { "Updated", "Blink" }, targets.Names);
            Assert.Equal(new[] { 0.5f, -0.75f }, targets.DefaultWeights);
        }
        files.Edit(source =>
        {
            source["meshes"]![0]!.AsObject().Remove("weights");
            source["meshes"]![0]!["extras"]!.AsObject().Remove("targetNames");
            source["meshes"]![0]!["primitives"]![0]!.AsObject().Remove("targets");
            source["nodes"]![3]!.AsObject().Remove("weights");
        });
        await project.ImportAsync(files.Source, importer);
        using MeshAsset result = await OpenMesh(project, files, "shared");
        Assert.Equal(meshId, result.Id);
        Assert.Null(await result.TryGetBlockAsync<MorphTargetSetBlock>());
    }

    [Theory]
    [InlineData("targetNames")]
    [InlineData("weights")]
    public async Task InconsistentTargetMetadataRejectsTheImport(string field)
    {
        using var files = new DeformationFiles(false);
        files.Edit(source =>
        {
            JsonNode mesh = source["meshes"]![0]!;
            if (field == "targetNames") mesh["extras"]![field] = new JsonArray("Single");
            else mesh[field] = new JsonArray(0.5);
        });
        var project = new AssetProject(files.Directory, []);
        await Assert.ThrowsAsync<InvalidDataException>(() => project.ImportAsync(files.Source, SourceImporter()).AsTask());
    }

    [Fact]
    public void BindingCopiesKeepTheDataSuppliedToThePublicConstructor()
    {
        Matrix4x4[] matrices = [DeformationFiles.FirstMatrix];
        short[] bones = [2];
        using var binding = new SkinBindingBlock(matrices, bones);
        matrices[0] = Matrix4x4.Identity;
        bones[0] = 0;
        using SkinBindingBlock copy = binding.Duplicate();
        binding.Dispose();
        Assert.Equal(DeformationFiles.FirstMatrix, copy.InverseBindMatrices[0]);
        Assert.Equal((short)2, copy.JointBones[0]);
    }

    private static GltfSourceImporter SourceImporter()
        => new(PoseTestAssets.AddAssetCapabilities(new Microsoft.Extensions.DependencyInjection.ServiceCollection()), PoseTestAssets.BuildVersion, PoseSceneImporter.Id);

    private static ValueTask<SkeletonAsset> OpenSkeleton(AssetProject project, DeformationFiles files, string id)
        => SkeletonAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets.Values.Single(entry => entry.SubAssetKey == "skin:id:" + id).Path));

    private static ValueTask<MeshAsset> OpenMesh(AssetProject project, DeformationFiles files, string id)
        => MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets.Values.Single(entry => entry.SubAssetKey == "mesh:id:" + id).Path));

    private static void AssertBinding(SkinBindingBlock expected, SkinBindingBlock actual)
    {
        Assert.Equal(expected.JointBones.ToArray(), actual.JointBones.ToArray());
        Assert.Equal(expected.InverseBindMatrices.ToArray(), actual.InverseBindMatrices.ToArray());
    }

    private static Entity[] MeshNodes(World world)
    {
        var nodes = new List<Entity>();
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<MeshSource>());
        try
        {
            world.ExecuteQuery(query, ref nodes, static (QueryCursor rows, ref List<Entity> values) =>
            {
                foreach (var row in rows.Rows) values.Add(row.Entity);
            });
        }
        finally { world.ReleaseQuery(query); }
        return nodes.ToArray();
    }

    private sealed class DeformationFiles : IDisposable
    {
        public static readonly Matrix4x4 FirstMatrix = new(2, 0.25f, 0.5f, 0, -0.75f, 3, 0.125f, 0, 0.375f, -0.5f, 4, 0, 5, -6, 7, 1);
        public static readonly Matrix4x4 SecondMatrix = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(-8, 9, -10);
        public string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "deformation-" + Guid.NewGuid().ToString("N"));
        public string Source { get; }
        private readonly bool _binary;
        private JsonNode _source;

        public DeformationFiles(bool binary)
        {
            _binary = binary;
            Source = Path.Combine(Directory, binary ? "mesh.glb" : "mesh.gltf");
            System.IO.Directory.CreateDirectory(Directory);
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
            {
                foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(value);
                writer.Write(new byte[12]);
                foreach (float value in new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }) writer.Write(value);
                foreach (float value in new float[] { 0, 0, 1, 0, 0, 2, 0, 0, 3 }) writer.Write(value);
                foreach (Matrix4x4 matrix in new[] { FirstMatrix, SecondMatrix })
                {
                    foreach (float value in new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14,
                        matrix.M21, matrix.M22, matrix.M23, matrix.M24, matrix.M31, matrix.M32, matrix.M33, matrix.M34,
                        matrix.M41, matrix.M42, matrix.M43, matrix.M44 }) writer.Write(value);
                }
            }
            string data = Convert.ToBase64String(bytes.ToArray());
            _source = JsonNode.Parse($$$$"""
                {"asset":{"version":"2.0"},"buffers":[{"byteLength":260,"uri":"data:application/octet-stream;base64,{{{{data}}}}"}],
                 "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":12},
                    {"buffer":0,"byteOffset":48,"byteLength":48},{"buffer":0,"byteOffset":96,"byteLength":36},
                    {"buffer":0,"byteOffset":132,"byteLength":128}],
                 "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},
                    {"bufferView":1,"componentType":5121,"count":3,"type":"VEC4"},
                    {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
                    {"bufferView":3,"componentType":5126,"count":3,"type":"VEC3"},
                    {"bufferView":4,"componentType":5126,"count":2,"type":"MAT4"}],
                 "meshes":[{"weights":[-0.25,1.5],"extras":{"someengine_id":"shared","targetNames":["笑😊","Blink"]},
                    "primitives":[{"attributes":{"POSITION":0,"JOINTS_0":1,"WEIGHTS_0":2},"targets":[{"POSITION":3},{"POSITION":3}]}]},
                    {"extras":{"someengine_id":"static"},"primitives":[{"attributes":{"POSITION":0}}]}],
                 "nodes":[{"name":"root","children":[1]},{"name":"bridge","children":[2]},{"name":"tip"},
                    {"mesh":0,"skin":0,"weights":[0.9,0.1]},{"mesh":0,"skin":1},{"mesh":1}],
                 "skins":[{"joints":[2,0],"inverseBindMatrices":4,"extras":{"someengine_id":"first"}},
                    {"joints":[0,2],"extras":{"someengine_id":"second"}}],
                 "scenes":[{"nodes":[0,3,4,5],"extras":{"someengine_id":"scene"}}],"scene":0}
                """)!;
            WriteSource();
            SourceMeta.Save(Source, new SourceMeta { SourceGuid = SourceGuid.New() });
        }

        public void Edit(Action<JsonNode> edit) { edit(_source); WriteSource(); }

        private void WriteSource()
        {
            byte[] json = Encoding.UTF8.GetBytes(_source.ToJsonString());
            if (!_binary) { File.WriteAllBytes(Source, json); return; }
            int length = (json.Length + 3) & ~3;
            byte[] glb = new byte[20 + length];
            BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546C67);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)length);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4E4F534A);
            glb.AsSpan(20).Fill(0x20);
            json.CopyTo(glb, 20);
            File.WriteAllBytes(Source, glb);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
