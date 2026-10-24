using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using SharpGLTF.Schema2;
using SomeEngine.Animation.Pose.Importers;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class SkeletonBehaviorTests
{
    [Fact]
    public async Task ImportedAndCreatedSkeletonsKeepTheirContentAfterAssetClosure()
    {
        using var files = new SkeletonFiles(false);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry entry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        string saved = Path.Combine(files.Directory, "readable.skeleton.asset");
        using (SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, entry.Path)))
            await asset.SaveAsync(saved);
        Skeleton imported;
        using (SkeletonAsset asset = await SkeletonAsset.OpenAsync(saved))
            imported = (await asset.GetBlockAsync<SkeletonBlock>()).CreateSkeleton();

        float halfSqrt = MathF.Sqrt(0.5f);
        var created = new Skeleton([-1, 0, 1, 0],
            [new() { Translation = new(1, 2, 3), Rotation = new(0, 0, 1, 0), Scale = new(2, 3, 4) },
             new() { Translation = new(4, 5, 6), Rotation = new(0, 0, halfSqrt, halfSqrt), Scale = new(2, 3, 4) },
             new() { Translation = new(7, 8, 9), Rotation = Quaternion.Identity, Scale = Vector3.One },
             new() { Rotation = Quaternion.Identity, Scale = Vector3.One }],
            [BoneNames.Hash("root"), BoneNames.Hash("桥🌉"), BoneNames.Hash("tip"), 0], [2, 0, 1, 3]);

        Assert.Equal(4, imported.BoneCount);
        Assert.Equal(created.BoneCount, imported.BoneCount);
        Assert.Equal(created.Parents.ToArray(), imported.Parents.ToArray());
        Assert.Equal(created.JointBones.ToArray(), imported.JointBones.ToArray());
        Assert.Equal(created.NameHashes.ToArray(), imported.NameHashes.ToArray());
        for (int bone = 0; bone < created.BoneCount; bone++)
        {
            BoneTransform expected = created.ReferencePose[bone];
            AssertTransform(imported.ReferencePose[bone], expected.Translation, expected.Rotation, expected.Scale);
        }
    }

    [Fact]
    public async Task ReferencedNeutralGroupImportsSkeletonsFromTheProjectsDefaultChoices()
    {
        using var files = new SkeletonFiles(false);
        SourceMeta metadata = SourceMeta.Load(files.Source);
        metadata.Importers.RemoveAll(value => value.ImporterId == GltfSkeletonImporter.Id);
        SourceMeta.Save(files.Source, metadata);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry entry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        using SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, entry.Path));
        SkeletonBlock skeleton = await asset.GetBlockAsync<SkeletonBlock>();
        Assert.Equal(4, skeleton.BoneCount);
        Assert.Equal(new short[] { 2, 0, 1, 3 }, skeleton.JointBones.ToArray());
        Assert.Equal(new Vector3(7, 8, 9), skeleton.ReferencePose[skeleton.JointBones[0]].Translation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlbSkinSavesParentOrderedLocalTransformsAndSourceJointCorrespondence(bool intermediateNodes)
    {
        using var files = new SkeletonFiles(intermediateNodes);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry entry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        using SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, entry.Path));
        SkeletonBlock skeleton = await asset.GetBlockAsync<SkeletonBlock>();

        Assert.Equal(4, skeleton.BoneCount);
        Assert.Equal(new short[] { -1, 0, 1, 0 }, skeleton.Parents.ToArray());
        Assert.Equal(intermediateNodes ? new short[] { 2, 3 } : new short[] { 2, 0, 1, 3 }, skeleton.JointBones.ToArray());
        Assert.Equal(new uint[] { BoneNames.Hash("root"), BoneNames.Hash("桥🌉"), BoneNames.Hash("tip"), 0 }, skeleton.NameHashes.ToArray());
        BoneTransform[] pose = skeleton.ReferencePose.ToArray();
        AssertTransform(pose[0], new(1, 2, 3), new(0, 0, 1, 0), new(2, 3, 4));
        float halfSqrt = MathF.Sqrt(0.5f);
        AssertTransform(pose[1], new(4, 5, 6), new(0, 0, halfSqrt, halfSqrt), new(2, 3, 4));
        AssertTransform(pose[2], new(7, 8, 9), Quaternion.Identity, Vector3.One);
        AssertTransform(pose[3], Vector3.Zero, Quaternion.Identity, Vector3.One);

        // Check model-space propagation through the public parent and reference-pose data.
        Matrix4x4[] matrices = ModelMatrices(skeleton);
        Matrix4x4 root = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateFromQuaternion(new(0, 0, 1, 0)) * Matrix4x4.CreateTranslation(1, 2, 3);
        Matrix4x4 bridge = new(0, 2, 0, 0, -3, 0, 0, 0, 0, 0, 4, 0, 4, 5, 6, 1);
        Vector3 expected = Vector3.Transform(Vector3.Zero, Matrix4x4.CreateTranslation(7, 8, 9) * bridge * root);
        Assert.True(Vector3.Distance(expected, matrices[skeleton.JointBones[0]].Translation) < 0.0001f);

        string saved = Path.Combine(files.Directory, "saved.skeleton.asset");
        await asset.SaveAsync(saved);
        using SkeletonAsset reopened = await SkeletonAsset.OpenAsync(saved);
        AssertContent(skeleton, await reopened.GetBlockAsync<SkeletonBlock>());
        using SkeletonAsset copy = asset.CreateCopy();
        AssertContent(skeleton, await copy.GetBlockAsync<SkeletonBlock>());
    }

    [Fact]
    public async Task SelectedSkeletonImporterCanBeConsumedByAnotherImporterThroughPublicBlockDependency()
    {
        using var files = new SkeletonFiles(false);
        files.SelectImporters(GltfSkeletonImporter.Id, JointNamesImporter.Id);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry entry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        using SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, entry.Path));
        JointNamesBlock names = await asset.GetBlockAsync<JointNamesBlock>();
        Assert.Equal(new uint[] { BoneNames.Hash("tip"), BoneNames.Hash("root"), BoneNames.Hash("桥🌉"), 0 }, names.Names);
    }

    [Fact]
    public async Task AncestorsAboveSkinRootKeepTheirTransformsAndOtherBranchesStayInTheScene()
    {
        using var files = new SkeletonFiles(false);
        files.WriteSource(false, sourceJoints: "2", skinRoot: 2);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry entry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        using SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, entry.Path));
        SkeletonBlock skeleton = await asset.GetBlockAsync<SkeletonBlock>();
        Assert.Equal(3, skeleton.BoneCount);
        Assert.Equal(new short[] { -1, 0, 1 }, skeleton.Parents.ToArray());
        Assert.Equal(new short[] { 2 }, skeleton.JointBones.ToArray());
        Assert.Equal(new Vector3(1, 2, 3), skeleton.ReferencePose[0].Translation);
        Assert.Equal(new Vector3(4, 5, 6), skeleton.ReferencePose[1].Translation);
    }

    [Fact]
    public async Task ProjectImporterUsesTheSameSkeletonBlockApiWhenSelectedInSourceMetadata()
    {
        using var files = new SkeletonFiles(false);
        files.SelectImporters(ProjectSkeletonImporter.Id);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry entry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        using SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, entry.Path));
        SkeletonBlock skeleton = await asset.GetBlockAsync<SkeletonBlock>();
        Assert.Equal(1, skeleton.BoneCount);
        Assert.Equal(new short[] { -1 }, skeleton.Parents.ToArray());
        Assert.Equal(new Vector3(11, 12, 13), skeleton.ReferencePose[0].Translation);
        Assert.Equal(BoneNames.Hash("project bone"), skeleton.NameHashes[0]);
        Assert.Equal(new short[] { 0, 0, 0, 0 }, skeleton.JointBones.ToArray());
    }

    [Fact]
    public async Task ReimportKeepsSkinIdentityAndUpdatesReferencePose()
    {
        using var files = new SkeletonFiles(false);
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry first = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        files.WriteSource(false, tipTranslation: 10);
        await project.ImportAsync(files.Source, SourceImporter());
        AssetEntry second = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == SkeletonAsset.FileType);
        Assert.Equal(first.AssetGuid, second.AssetGuid);
        using SkeletonAsset asset = await SkeletonAsset.OpenAsync(Path.Combine(files.Directory, second.Path));
        SkeletonBlock skeleton = await asset.GetBlockAsync<SkeletonBlock>();
        Assert.Equal(new Vector3(10, 8, 9), skeleton.ReferencePose[skeleton.JointBones[0]].Translation);
    }

    [Fact]
    public void ConstructorRetainsItsOwnDataAndCopiesKeepTheirContent()
    {
        short[] parents = [-1, -1];
        BoneTransform[] pose = [new() { Rotation = Quaternion.Identity, Scale = Vector3.One },
            new() { Translation = Vector3.UnitX, Rotation = Quaternion.Identity, Scale = Vector3.One }];
        uint[] names = [BoneNames.Hash("root"), 0];
        short[] joints = [1, 0];
        using var skeleton = new SkeletonBlock(parents, pose, names, joints);
        parents[0] = 1;
        pose[1].Translation = Vector3.UnitY;
        names[0] = 0;
        joints[0] = 0;
        using SkeletonBlock copy = skeleton.Duplicate();
        skeleton.Dispose();
        Assert.Equal(new short[] { -1, -1 }, copy.Parents.ToArray());
        Assert.Equal(Vector3.UnitX, copy.ReferencePose[1].Translation);
        Assert.Equal(BoneNames.Hash("root"), copy.NameHashes[0]);
        Assert.Equal(new short[] { 1, 0 }, copy.JointBones.ToArray());
    }

    [Theory]
    [InlineData("", 0u)]
    [InlineData("a", 0xe40c292cu)]
    [InlineData("hello", 0x4f9f2cabu)]
    [InlineData("root", 0x20fd0e45u)]
    public void NameHashesFollowTheDocumentedFnv1aEncoding(string name, uint expected)
        => Assert.Equal(expected, BoneNames.Hash(name));

    [Fact]
    public void UnicodeNameHashesUseUtf8AndPreserveCase()
    {
        uint expected = 2166136261;
        foreach (byte value in Encoding.UTF8.GetBytes("桥🌉")) expected = unchecked((expected ^ value) * 16777619);
        Assert.Equal(expected, BoneNames.Hash("桥🌉"));
        Assert.NotEqual(BoneNames.Hash("Root"), BoneNames.Hash("root"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32768)]
    public async Task EmptySkeletonAndLargestBoneIndexRoundTripThroughAssetPersistence(int count)
    {
        string directory = Path.Combine(Path.GetTempPath(), "pose-skeleton-size-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var parents = new short[count];
            var pose = new BoneTransform[count];
            var names = new uint[count];
            for (int bone = 0; bone < count; bone++)
            {
                parents[bone] = (short)(bone - 1);
                pose[bone] = new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
                names[bone] = (uint)bone;
            }
            short[] joints = count == 0 ? [] : [(short)(count - 1)];
            using var skeleton = new SkeletonBlock(parents, pose, names, joints);
            using var asset = new SkeletonAsset(Guid.NewGuid());
            asset.SetBlock(skeleton);
            string path = Path.Combine(directory, "size.skeleton.asset");
            await asset.SaveAsync(path);
            using SkeletonAsset reopened = await SkeletonAsset.OpenAsync(path);
            AssertContent(skeleton, await reopened.GetBlockAsync<SkeletonBlock>());
        }
        finally { System.IO.Directory.Delete(directory, recursive: true); }
    }

#if DEBUG
    [Fact]
    public void DevelopmentChecksReportInvalidBoneData()
    {
        BoneTransform[] pose = [new() { Rotation = Quaternion.Identity, Scale = Vector3.One }];
        Assert.Throws<InvalidOperationException>(() => new SkeletonBlock([0], pose, [0]));
        Assert.Throws<InvalidOperationException>(() => new SkeletonBlock([-1], [], [0]));
        Assert.Throws<InvalidOperationException>(() => new SkeletonBlock([-1], pose, [0], [1]));
    }
#endif

    private static GltfSourceImporter SourceImporter()
        => new(services => PoseTestAssets.AddAssetCapabilities(services), PoseTestAssets.BuildVersion, PoseSceneImporter.Id);

    private static void AssertTransform(BoneTransform transform, Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        Assert.True(Vector3.Distance(translation, transform.Translation) < 0.0001f);
        Assert.True(MathF.Abs(Quaternion.Dot(rotation, transform.Rotation)) > 0.9999f);
        Assert.True(Vector3.Distance(scale, transform.Scale) < 0.0001f);
    }

    private static void AssertContent(SkeletonBlock expected, SkeletonBlock actual)
    {
        Assert.Equal(expected.BoneCount, actual.BoneCount);
        Assert.Equal(expected.Parents.ToArray(), actual.Parents.ToArray());
        Assert.Equal(expected.ReferencePose.ToArray(), actual.ReferencePose.ToArray());
        Assert.Equal(expected.NameHashes.ToArray(), actual.NameHashes.ToArray());
        Assert.Equal(expected.JointBones.ToArray(), actual.JointBones.ToArray());
    }

    private static Matrix4x4[] ModelMatrices(SkeletonBlock skeleton)
    {
        var result = new Matrix4x4[skeleton.BoneCount];
        for (int bone = 0; bone < result.Length; bone++)
        {
            BoneTransform transform = skeleton.ReferencePose[bone];
            Matrix4x4 local = Matrix4x4.CreateScale(transform.Scale) * Matrix4x4.CreateFromQuaternion(transform.Rotation)
                * Matrix4x4.CreateTranslation(transform.Translation);
            result[bone] = skeleton.Parents[bone] >= 0 ? local * result[skeleton.Parents[bone]] : local;
        }
        return result;
    }

    private sealed class SkeletonFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "pose-skeleton-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Directory, "rig.glb");

        public SkeletonFiles(bool intermediateNodes)
        {
            System.IO.Directory.CreateDirectory(Directory);
            WriteSource(intermediateNodes);
            SelectImporters(GltfSkeletonImporter.Id);
        }

        public void SelectImporters(params Guid[] ids)
        {
            var metadata = new SourceMeta { SourceGuid = SourceGuid.New(), UseDefaultImporters = false };
            metadata.Importers.Add(new(PoseSceneImporter.Id, default));
            foreach (Guid id in ids) metadata.Importers.Add(new(id, default));
            SourceMeta.Save(Source, metadata);
        }

        public void WriteSource(bool intermediateNodes, int tipTranslation = 7, string? sourceJoints = null, int skinRoot = 0)
        {
            string joints = sourceJoints ?? (intermediateNodes ? "2,3" : "2,0,1,3");
            byte[] json = Encoding.UTF8.GetBytes($$$$"""
                {"asset":{"version":"2.0"},
                 "nodes":[{"name":"root","children":[1,3],"translation":[1,2,3],"rotation":[0,0,1,0],"scale":[2,3,4]},
                    {"name":"桥🌉","children":[2],"matrix":[0,2,0,0,-3,0,0,0,0,0,4,0,4,5,6,1]},
                    {"name":"tip","translation":[{{{{tipTranslation}}}},8,9]},{}],
                 "skins":[{"name":"Rig","joints":[{{{{joints}}}}],"skeleton":{{{{skinRoot}}}},"extras":{"someengine_id":"rig"}}],
                 "scenes":[{"nodes":[0],"extras":{"someengine_id":"scene"}}],"scene":0}
                """);
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

[AssetCapabilities]
public static partial class PoseTestAssets;

[AssetImporter("b3220a1c-e36c-45bc-a053-c98c3cc4ab51", typeof(WorldBlock), SettingsProvider = typeof(GltfSceneSettingsProvider))]
public sealed class PoseSceneImporter : GltfSceneImporter
{
    public static readonly Guid Id = new("b3220a1c-e36c-45bc-a053-c98c3cc4ab51");
    public override ValueTask ImportMaterialAsync(Material source, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}

[AssetBlock("SomeEngine.Animation.Pose.Tests.JointNames"), SomeEngine.Serialization.BinarySerializable,
 SomeEngine.Serialization.BinaryFields(nameof(Names))]
public sealed partial class JointNamesBlock : AssetBlock
{
    public partial IList<uint> Names { get; set; }
}

[AssetImporter("a7b1e9dc-5b8c-49f3-b5c0-bf8a712fc822", typeof(JointNamesBlock), SettingsProvider = typeof(GltfSkeletonSettingsProvider))]
public sealed class JointNamesImporter : IAssetImporter<Skin, GltfSkeletonSettings>
{
    public static readonly Guid Id = new("a7b1e9dc-5b8c-49f3-b5c0-bf8a712fc822");
    public async ValueTask ImportAsync(Skin source, GltfSkeletonSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        SkeletonBlock skeleton = await context.GetBlockAsync<SkeletonBlock>(output.Target, cancellationToken);
        var names = new List<uint>();
        foreach (short bone in skeleton.JointBones) names.Add(skeleton.NameHashes[bone]);
        var block = new JointNamesBlock { Names = names };
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
    }
}

[AssetImporter("1bccead5-9145-4142-ab13-9495e016a053", typeof(SkeletonBlock), SettingsProvider = typeof(GltfSkeletonSettingsProvider))]
public sealed class ProjectSkeletonImporter : IAssetImporter<Skin, GltfSkeletonSettings>
{
    public static readonly Guid Id = new("1bccead5-9145-4142-ab13-9495e016a053");
    public ValueTask ImportAsync(Skin source, GltfSkeletonSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken)
    {
        BoneTransform[] pose = [new() { Translation = new(11, 12, 13), Rotation = Quaternion.Identity, Scale = Vector3.One }];
        var skeleton = new SkeletonBlock([-1], pose, [BoneNames.Hash("project bone")], new short[source.JointsCount]);
        try { output.SetBlock(skeleton); }
        catch { skeleton.Dispose(); throw; }
        return ValueTask.CompletedTask;
    }
}
