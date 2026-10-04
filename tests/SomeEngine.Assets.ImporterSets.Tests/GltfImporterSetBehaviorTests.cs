using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.ImporterSets.Tests;

public sealed class GltfImporterSetBehaviorTests
{
    [Fact]
    public async Task ProjectWithOnlyTestImportersUsesItsNeutralAndDefaultGroups()
    {
        using var files = new SetFiles();
        var project = new AssetProject(files.Directory, []);
        using MeshAsset mesh = await OpenMeshAsync(project, files.Source);
        Assert.Equal(31, (await mesh.GetBlockAsync<SetSharedBlock>()).Value);
        Assert.Equal(10, (await mesh.GetBlockAsync<SetFirstBlock>()).Value);
        Assert.False(mesh.ContainsBlock<SetSecondBlock>());
    }

    [Fact]
    public async Task SwitchingSavedGroupsChangesTheMeshBlocks()
    {
        using var files = new SetFiles();
        var project = new AssetProject(files.Directory, []);
        using (MeshAsset mesh = await OpenMeshAsync(project, files.Source))
            Assert.Equal(10, (await mesh.GetBlockAsync<SetFirstBlock>()).Value);
        files.SelectSets(SetGltfImporters.SecondId);
        using MeshAsset changed = await OpenMeshAsync(project, files.Source);
        Assert.Equal(20, (await changed.GetBlockAsync<SetSecondBlock>()).Value);
        Assert.Equal(31, (await changed.GetBlockAsync<SetSharedBlock>()).Value);
        Assert.False(changed.ContainsBlock<SetFirstBlock>());
    }

    [Fact]
    public async Task ReplacingOneMemberPreservesOtherMembersAndTheirSettings()
    {
        using var files = new SetFiles();
        var project = new AssetProject(files.Directory, []);
        using (MeshAsset mesh = await OpenMeshAsync(project, files.Source)) { }
        SourceMeta metadata = SourceMeta.Load(files.Source);
        int shared = metadata.Importers.FindIndex(value => value.ImporterId == SetSharedImporter.Id);
        metadata.Importers[shared] = metadata.Importers[shared] with { Settings = Settings(53) };
        int first = metadata.Importers.FindIndex(value => value.ImporterId == SetFirstImporter.Id);
        metadata.Importers[first] = metadata.Importers[first] with { ImporterId = SetReplacementImporter.Id, Settings = Settings(77) };
        SourceMeta.Save(files.Source, metadata);
        using (MeshAsset changed = await OpenMeshAsync(project, files.Source))
        {
            Assert.Equal(77, (await changed.GetBlockAsync<SetFirstBlock>()).Value);
            Assert.Equal(53, (await changed.GetBlockAsync<SetSharedBlock>()).Value);
        }
        files.SelectSets(SetGltfImporters.SecondId);
        using MeshAsset second = await OpenMeshAsync(project, files.Source);
        Assert.Equal(53, (await second.GetBlockAsync<SetSharedBlock>()).Value);
        Assert.Equal(20, (await second.GetBlockAsync<SetSecondBlock>()).Value);
        Assert.False(second.ContainsBlock<SetFirstBlock>());
    }

    [Fact]
    public async Task TwoPipelineGroupsProduceTheirOwnBlocksAndShareCommonData()
    {
        using var files = new SetFiles();
        files.SelectSets(SetGltfImporters.FirstId, SetGltfImporters.SecondId);
        var project = new AssetProject(files.Directory, []);
        using MeshAsset mesh = await OpenMeshAsync(project, files.Source);
        Assert.Equal(10, (await mesh.GetBlockAsync<SetFirstBlock>()).Value);
        Assert.Equal(20, (await mesh.GetBlockAsync<SetSecondBlock>()).Value);
        Assert.Equal(31, (await mesh.GetBlockAsync<SetSharedBlock>()).Value);
    }

    [Fact]
    public async Task EmptyGroupSelectionStillImportsNeutralData()
    {
        using var files = new SetFiles();
        files.SelectSets();
        var project = new AssetProject(files.Directory, []);
        using MeshAsset mesh = await OpenMeshAsync(project, files.Source);
        Assert.Equal(31, (await mesh.GetBlockAsync<SetSharedBlock>()).Value);
        Assert.False(mesh.ContainsBlock<SetFirstBlock>());
        Assert.False(mesh.ContainsBlock<SetSecondBlock>());
    }

    [Fact]
    public async Task OneTimeImporterChoicesKeepNeutralMembersAndPreserveSavedPipelineChoices()
    {
        using var files = new SetFiles();
        var project = new AssetProject(files.Directory, []);
        var overrides = new AssetImportSettings<GltfImportOverrides>(new(Importers: [new(SetSecondImporter.Id, default)]));
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, CreateImporter(), overrides);
        Guid meshId = Assert.Single(ids, id => project.Manifest.Assets[id].AssetType == MeshAsset.FileType);
        using (MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[meshId].Path)))
        {
            Assert.Equal(31, (await mesh.GetBlockAsync<SetSharedBlock>()).Value);
            Assert.Equal(20, (await mesh.GetBlockAsync<SetSecondBlock>()).Value);
            Assert.False(mesh.ContainsBlock<SetFirstBlock>());
        }
        using MeshAsset saved = await OpenMeshAsync(project, files.Source);
        Assert.Equal(10, (await saved.GetBlockAsync<SetFirstBlock>()).Value);
        Assert.False(saved.ContainsBlock<SetSecondBlock>());
    }

    [Fact]
    public async Task MembersAreExpandedForTheirOwnSourceTypes()
    {
        using var files = new SetFiles();
        JsonNode source = JsonNode.Parse(File.ReadAllText(files.Source))!;
        source["materials"] = JsonNode.Parse("""[{"extras":{"someengine_id":"material-A"}}]""");
        source["meshes"]![0]!["primitives"]![0]!["material"] = 0;
        File.WriteAllText(files.Source, source.ToJsonString());
        var project = new AssetProject(files.Directory, []);
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, CreateImporter());
        Guid materialId = Assert.Single(ids, id => project.Manifest.Assets[id].AssetType == MaterialAsset.FileType);
        using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[materialId].Path));
        Assert.Equal(99, (await material.GetBlockAsync<SetFirstBlock>()).Value);
        Assert.Equal(SetSceneImporter.ShaderId, (await material.GetBlockAsync<MaterialBlock>()).Shader.Value);
        Guid meshId = Assert.Single(ids, id => project.Manifest.Assets[id].AssetType == MeshAsset.FileType);
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[meshId].Path));
        Assert.Equal(10, (await mesh.GetBlockAsync<SetFirstBlock>()).Value);
    }

    [Fact]
    public async Task UnknownSavedGroupFailsWithItsIdentityAndPreservesThePreviousMesh()
    {
        using var files = new SetFiles();
        var project = new AssetProject(files.Directory, []);
        string meshPath;
        using (MeshAsset mesh = await OpenMeshAsync(project, files.Source))
            meshPath = Path.Combine(files.Directory, project.Manifest.Assets[mesh.Id].Path);
        Guid missing = Guid.NewGuid();
        files.SelectSets(missing.ToString());
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => project.ImportAsync(files.Source, CreateImporter()).AsTask());
        Assert.Contains(missing.ToString(), error.Message);
        using MeshAsset previous = await MeshAsset.OpenAsync(meshPath);
        Assert.Equal(10, (await previous.GetBlockAsync<SetFirstBlock>()).Value);
    }

    [Fact]
    public async Task GroupsWithCompetingBlockProducersFailThroughThePublicImport()
    {
        using var files = new SetFiles();
        files.SelectSets(SetGltfImporters.FirstId, SetGltfImporters.ConflictId);
        var project = new AssetProject(files.Directory, []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.ImportAsync(files.Source, CreateImporter()).AsTask());
    }

    [Fact]
    public async Task CopyingASourcePreservesItsGroupChoicesAndMemberReplacement()
    {
        using var files = new SetFiles();
        var project = new AssetProject(files.Directory, []);
        using (MeshAsset mesh = await OpenMeshAsync(project, files.Source)) { }
        SourceMeta metadata = SourceMeta.Load(files.Source);
        int first = metadata.Importers.FindIndex(value => value.ImporterId == SetFirstImporter.Id);
        metadata.Importers[first] = metadata.Importers[first] with { ImporterId = SetReplacementImporter.Id, Settings = Settings(64) };
        SourceMeta.Save(files.Source, metadata);
        string copy = Path.Combine(files.Directory, "copy.gltf");
        await project.CopySourceAsync(files.Source, copy);
        using MeshAsset copied = await OpenMeshAsync(project, copy);
        Assert.Equal(64, (await copied.GetBlockAsync<SetFirstBlock>()).Value);
        Assert.Equal(31, (await copied.GetBlockAsync<SetSharedBlock>()).Value);
    }

    private static JsonElement Settings(int value)
        => JsonSerializer.SerializeToElement(new SetGltfSettings(value), SetGltfJson.Default.SetGltfSettings);

    private static async Task<MeshAsset> OpenMeshAsync(AssetProject project, string source)
    {
        IReadOnlyList<Guid> ids = await project.ImportAsync(source, CreateImporter());
        Guid mesh = Assert.Single(ids, id => project.Manifest.Assets[id].AssetType == MeshAsset.FileType);
        return await MeshAsset.OpenAsync(Path.Combine(project.RootPath, project.Manifest.Assets[mesh].Path));
    }

    private static GltfSourceImporter CreateImporter() => new(services =>
    {
        // This project's registry contains only test importers; all descriptions and settings binders come from the generator.
        services.AddScoped<SetSharedImporter>();
        services.AddScoped<SetFirstImporter>();
        services.AddScoped<SetSecondImporter>();
        services.AddScoped<SetReplacementImporter>();
        services.AddScoped<SetSceneImporter>();
        services.AddScoped<SetMaterialImporter>();
        services.AddScoped<SetOtherImporter>();
        Type[] types = [typeof(SetSharedImporter), typeof(SetFirstImporter), typeof(SetSecondImporter), typeof(SetReplacementImporter),
            typeof(SetSceneImporter), typeof(SetMaterialImporter), typeof(SetOtherImporter)];
        foreach (AssetImporterCapability importer in SetProjectCapabilities.ImporterCapabilities.Where(value => types.Contains(value.ImporterType)))
            services.AddSingleton(importer);
        Guid[] sets = [new(SetGltfImporters.NeutralId), new(SetGltfImporters.FirstId), new(SetGltfImporters.SecondId), new(SetGltfImporters.ConflictId)];
        foreach (AssetImporterSetCapability set in SetProjectCapabilities.ImporterSetCapabilities.Where(value => sets.Contains(value.Id)))
            services.AddSingleton(set);
    }, SetProjectCapabilities.BuildVersion);

    private sealed class SetFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "gltf-importer-sets-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Directory, "mesh.gltf");

        public SetFiles()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Source, """
                {"asset":{"version":"2.0"},
                "buffers":[{"uri":"data:application/octet-stream;base64,AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8AAAAA","byteLength":36}],
                "bufferViews":[{"buffer":0,"byteLength":36}],
                "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]}],
                "meshes":[{"extras":{"someengine_id":"mesh-A"},"primitives":[{"attributes":{"POSITION":0}}]}],
                "nodes":[{"mesh":0}],"scenes":[{"extras":{"someengine_id":"scene-A"},"nodes":[0]}],"scene":0}
                """);
        }

        public void SelectSets(params string[] ids)
        {
            SourceMeta metadata = SourceMeta.GetOrCreate(Source);
            metadata.ImporterSets = ids.Select(Guid.Parse).ToList();
            SourceMeta.Save(Source, metadata);
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}

[AssetCapabilities(SetGltfImporters.FirstId)]
public static partial class SetProjectCapabilities;

[AssetImporterSet(NeutralId, "Test shared data", typeof(SetSharedImporter), typeof(SetMaterialImporter), typeof(SetOtherImporter), Neutral = true)]
[AssetImporterSet(FirstId, "Test first pipeline", typeof(SetFirstImporter), typeof(SetSharedImporter))]
[AssetImporterSet(SecondId, "Test second pipeline", typeof(SetSecondImporter), typeof(SetSharedImporter))]
[AssetImporterSet(ConflictId, "Test competing pipeline", typeof(SetReplacementImporter))]
public static class SetGltfImporters
{
    public const string NeutralId = "ce2b3042-6540-42b1-a731-464d90443909";
    public const string FirstId = "cae785b8-c551-44c2-988a-729344478aa0";
    public const string SecondId = "6d6a410a-eec4-41e3-a35e-c30d67dd6f7b";
    public const string ConflictId = "a0a47a5b-59d3-431e-a890-e918d718bd43";
}

[AssetBlock("test.set-shared"), BinarySerializable, BinaryFields(nameof(Value))]
public sealed partial class SetSharedBlock : AssetBlock
{
    public partial int Value { get; set; }
}
[AssetBlock("test.set-first"), BinarySerializable, BinaryFields(nameof(Value))]
public sealed partial class SetFirstBlock : AssetBlock
{
    public partial int Value { get; set; }
}
[AssetBlock("test.set-second"), BinarySerializable, BinaryFields(nameof(Value))]
public sealed partial class SetSecondBlock : AssetBlock
{
    public partial int Value { get; set; }
}

public sealed record SetGltfSettings(int? Value = null);
[JsonSerializable(typeof(SetGltfSettings))]
public sealed partial class SetGltfJson : JsonSerializerContext;
public sealed class SetGltfSettingsProvider : IAssetImporterSettings<SetGltfSettings>
{
    public SetGltfSettings DefaultSettings => new();
    public JsonTypeInfo<SetGltfSettings> TypeInfo => SetGltfJson.Default.SetGltfSettings;
}

[AssetImporter("f9ca4892-bd34-4d24-a506-fd506cba5d12", typeof(SetSharedBlock), SettingsProvider = typeof(SetGltfSettingsProvider))]
public sealed class SetSharedImporter : IAssetImporter<GltfMesh, SetGltfSettings>
{
    public static readonly Guid Id = new("f9ca4892-bd34-4d24-a506-fd506cba5d12");
    public ValueTask ImportAsync(GltfMesh source, SetGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new SetSharedBlock { Value = settings.Value ?? 31 });
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("a3c002ad-847a-4825-bbaf-7a4ad4bf7073", typeof(SetFirstBlock), SettingsProvider = typeof(SetGltfSettingsProvider))]
public sealed class SetFirstImporter : IAssetImporter<GltfMesh, SetGltfSettings>
{
    public static readonly Guid Id = new("a3c002ad-847a-4825-bbaf-7a4ad4bf7073");
    public ValueTask ImportAsync(GltfMesh source, SetGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new SetFirstBlock { Value = settings.Value ?? 10 });
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("a0c2f665-06b0-4ac3-b99c-9bb6f1781d12", typeof(SetSecondBlock), SettingsProvider = typeof(SetGltfSettingsProvider))]
public sealed class SetSecondImporter : IAssetImporter<GltfMesh, SetGltfSettings>
{
    public static readonly Guid Id = new("a0c2f665-06b0-4ac3-b99c-9bb6f1781d12");
    public ValueTask ImportAsync(GltfMesh source, SetGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new SetSecondBlock { Value = settings.Value ?? 20 });
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("ab9e6fd7-a764-44e1-8ee0-e51d977d9f7e", typeof(SetFirstBlock), SettingsProvider = typeof(SetGltfSettingsProvider))]
public sealed class SetMaterialImporter : IAssetImporter<Material, SetGltfSettings>
{
    public ValueTask ImportAsync(Material source, SetGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new SetFirstBlock { Value = settings.Value ?? 99 });
        return ValueTask.CompletedTask;
    }
}

public sealed class SetOtherSource;
[AssetImporter("50e7dbd7-2f27-4be0-b454-554e836864f0", typeof(SetFirstBlock))]
public sealed class SetOtherImporter : IAssetImporter<SetOtherSource, int>
{
    public ValueTask ImportAsync(SetOtherSource source, int settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new SetFirstBlock { Value = settings });
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("9f170fbb-6903-4c4a-acbc-8bcd2437c161", typeof(SetFirstBlock), SettingsProvider = typeof(SetGltfSettingsProvider))]
public sealed class SetReplacementImporter : IAssetImporter<GltfMesh, SetGltfSettings>
{
    public static readonly Guid Id = new("9f170fbb-6903-4c4a-acbc-8bcd2437c161");
    public ValueTask ImportAsync(GltfMesh source, SetGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new SetFirstBlock { Value = settings.Value ?? 77 });
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("d41a09fd-a105-44c6-b5fa-8d6b14e61ea2", typeof(WorldBlock), SettingsProvider = typeof(GltfSceneSettingsProvider))]
public sealed class SetSceneImporter : GltfSceneImporter
{
    public static readonly Guid ShaderId = new("0c1d0f85-e510-48c4-ab9c-cdc1a3501c48");
    public override ValueTask ImportMaterialAsync(Material source, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new MaterialBlock { Shader = new(ShaderId) });
        return ValueTask.CompletedTask;
    }
}
