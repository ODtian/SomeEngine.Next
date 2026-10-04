using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Game.Importing;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using GltfMesh = SharpGLTF.Schema2.Mesh;

namespace SomeEngine.Assets.Consumer.Tests;

public sealed class GltfExtensionBehaviorTests
{
    [Fact]
    public async Task GeneratedCapabilitiesImportClustersWithTheImportersOwnEncoding()
    {
        var services = ProjectAssetCapabilities.AddAssetCapabilities(new ServiceCollection());
        using var provider = services.BuildServiceProvider();
        Assert.Empty(provider.GetServices<IClusterVertexEncoder>());
        using var scope = provider.CreateScope();
        Assert.IsType<ClusterGeometryImporter>(scope.ServiceProvider.GetRequiredService<ClusterGeometryImporter>());
        Assert.IsType<ClusterVertexAttributesImporter>(scope.ServiceProvider.GetRequiredService<ClusterVertexAttributesImporter>());

        using var files = new GltfFiles();
        await files.WriteDefaultTemplatesAsync();
        var importer = StandardGltfSceneImporter.CreateSourceImporter();
        var project = new AssetProject(files.Directory, [importer]);
        Guid id = MeshId(project, await project.ImportAsync(files.Source));
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        ClusterGeometryBlock clusters = await mesh.GetBlockAsync<ClusterGeometryBlock>();
        Assert.Equal(ClusterGeometryBlock.EncodingCompatibility, clusters.PositionEncoding);
        Assert.NotEmpty(clusters.Clusters);
    }

    [Theory]
    [InlineData("TEXCOORD_1")]
    [InlineData("_CUSTOM")]
    public async Task StandardClusterImportPreservesAdditionalSourceAttributes(string attribute)
    {
        using var files = new GltfFiles();
        files.AddVertexAttribute(attribute);
        await files.WriteDefaultTemplatesAsync();
        using var diagnostics = new ImportDiagnostics();
        Trace.Listeners.Add(diagnostics);
        try
        {
            var importer = StandardGltfSceneImporter.CreateSourceImporter();
            var project = new AssetProject(files.Directory, [importer]);
            Guid id = MeshId(project, await project.ImportAsync(files.Source));
            using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
            ClusterGeometryBlock clusters = await mesh.GetBlockAsync<ClusterGeometryBlock>();
            Assert.Equal(ClusterGeometryBlock.EncodingCompatibility, clusters.PositionEncoding);
            Assert.NotEmpty(clusters.Clusters);
            ClusterVertexAttributesBlock values = await mesh.GetBlockAsync<ClusterVertexAttributesBlock>();
            Assert.Contains(values.Clusters.SelectMany(cluster => cluster.Attributes), stream => stream.Name == attribute);
        }
        finally { Trace.Listeners.Remove(diagnostics); }
    }

    private sealed class ImportDiagnostics : TraceListener
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public override void Write(string? message) { if (message is not null) Messages.Enqueue(message); }
        public override void WriteLine(string? message) => Write(message);
    }

    [Fact]
    public async Task ExplicitlyDisabledTextureProducerPreservesThePreviouslyBuiltAssets()
    {
        using var files = new GltfFiles();
        await files.WriteDefaultTemplatesAsync();
        files.AddTexturedMaterial();
        files.WriteSelections(new SourceImporterSettings(ImporterId<GltfMeshImporter>(),
            JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)));
        var importer = CreateImporter();
        var project = new AssetProject(files.Directory, [importer]);
        Guid id = MeshId(project, await project.ImportAsync(files.Source, importer));
        string path = Path.Combine(files.Directory, project.Manifest.Assets[id].Path);
        byte[] successful = await File.ReadAllBytesAsync(path);
        SourceMeta metadata = SourceMeta.Load(files.Source);
        int texture = metadata.Importers.FindIndex(selection => selection.ImporterId == ImporterId<GltfTextureImporter>());
        metadata.Importers[texture] = metadata.Importers[texture] with { Enabled = false };
        SourceMeta.Save(files.Source, metadata);
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.ImportAsync(files.Source, importer).AsTask());
        Assert.Equal(successful, await File.ReadAllBytesAsync(path));
        Assert.False(SourceMeta.Load(files.Source).Importers.Single(selection => selection.ImporterId == ImporterId<GltfTextureImporter>()).Enabled);
    }

    [Fact]
    public async Task SourceWithoutPersistentElementIdsRequiresAnExplicitIdentitySetting()
    {
        using var files = new GltfFiles();
        await files.WriteDefaultTemplatesAsync();
        JsonNode source = JsonNode.Parse(File.ReadAllText(files.Source))!;
        source["scenes"]![0]!["extras"] = null;
        File.WriteAllText(files.Source, source.ToJsonString());
        var importer = CreateImporter();
        var project = new AssetProject(files.Directory, [importer]);
        await Assert.ThrowsAsync<InvalidDataException>(() => project.ImportAsync(files.Source, importer).AsTask());
        SourceMeta metadata = SourceMeta.GetOrCreate(files.Source);
        metadata.Importers.Add(new(GltfSourceImporter.SourceImporterId,
            JsonSerializer.SerializeToElement(new GltfImporterSettings { RequirePersistentElementIds = false },
                GltfSettingsJson.Default.GltfImporterSettings)));
        SourceMeta.Save(files.Source, metadata);
        Assert.NotEmpty(await project.ImportAsync(files.Source, importer));
        Assert.Contains("scene:index:0", SourceMeta.Load(files.Source).ElementIds.Keys);
    }

    [Fact]
    public async Task LibrarySourceImporterUsesGeneratedProjectServicesWithoutProjectTraversalCode()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(),
            JsonSerializer.SerializeToElement(new ExtensionGltfSettings(29, "library source"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var importer = StandardGltfSceneImporter.CreateSourceImporter(
            services => ConsumerAssetCapabilities.AddAssetCapabilities(services), ConsumerAssetCapabilities.BuildVersion);
        var project = new AssetProject(files.Directory, []);
        Guid id = MeshId(project, await project.ImportAsync(files.Source, importer));
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        ExtensionGltfBlock block = await mesh.GetBlockAsync<ExtensionGltfBlock>();
        Assert.Equal("library source", block.Label);
        Assert.False(mesh.ContainsBlock<ClusterGeometryBlock>());
    }

    [Fact]
    public async Task AddingCapabilitiesPreservesTheSavedDefaultMeshRepresentation()
    {
        using var files = new GltfFiles();
        await files.WriteDefaultTemplatesAsync();
        var project = new AssetProject(files.Directory, []);
        var original = StandardGltfSceneImporter.CreateSourceImporter(_ => { }, "project-services-1");
        Guid id = MeshId(project, await project.ImportAsync(files.Source, original));
        SourceMeta metadata = SourceMeta.Load(files.Source);
        Assert.Equal(new[] { ImporterId<GltfMeshImporter>(), ImporterId<ClusterGeometryImporter>(), ImporterId<ClusterVertexAttributesImporter>(), ImporterId<GltfVertexStreamsImporter>(), ImporterId<GltfTextureImporter>(),
            MorphTargetSetImporter.Id, StandardGltfSceneImporter.ImporterId }.Order(),
            metadata.Importers.Select(value => value.ImporterId).Order());
        var extended = StandardGltfSceneImporter.CreateSourceImporter(services => ConsumerAssetCapabilities.AddAssetCapabilities(services), "project-services-1");
        Assert.Equal(id, MeshId(project, await project.ImportAsync(files.Source, extended)));
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.True(mesh.ContainsBlock<ClusterGeometryBlock>());
        Assert.False(mesh.ContainsBlock<ExtensionGltfBlock>());
    }

    [Fact]
    public async Task ReportedExtensionInputRebuildsOnlyWhenThatInputChanges()
    {
        using var files = new GltfFiles();
        string input = Path.Combine(files.Directory, "extension.data");
        await File.WriteAllTextAsync(input, "first data");
        files.WriteSelections(new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(),
            JsonSerializer.SerializeToElement(new ExtensionGltfSettings(13, "", input), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var project = new AssetProject(files.Directory, []);
        var importer = CreateImporter();
        Guid id = MeshId(project, await project.ImportAsync(files.Source, importer));
        Guid version = project.Manifest.Assets[id].Version;
        await File.WriteAllTextAsync(Path.Combine(files.Directory, "unrelated.txt"), "does not affect conversion");
        Assert.Equal(id, MeshId(project, await project.ImportAsync(files.Source, importer)));
        Assert.Equal(version, project.Manifest.Assets[id].Version);
        await File.WriteAllTextAsync(input, "second data");
        Assert.Equal(id, MeshId(project, await project.ImportAsync(files.Source, importer)));
        Assert.NotEqual(version, project.Manifest.Assets[id].Version);
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.Equal("second data", (await mesh.GetBlockAsync<ExtensionGltfBlock>()).Label);
    }

    [Fact]
    public async Task ImporterResourcesRemainAliveUntilBlockSavingCompletes()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<ImporterEncodedGltfImporter>(),
            JsonSerializer.SerializeToElement(new ExtensionGltfSettings(1, "saved using importer codec"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var importer = CreateImporter();
        var project = new AssetProject(files.Directory, []);
        Guid id = MeshId(project, await project.ImportAsync(files.Source, importer));
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.Equal("saved using importer codec", (await mesh.GetBlockAsync<ImporterEncodedGltfBlock>()).Text);
    }

    [Fact]
    public async Task DisabledImporterAllowsTheSelectedProducerToSaveItsOutput()
    {
        using var files = new GltfFiles();
        files.WriteSelections(
            new(ImporterId<DisabledGltfImporter>(), default, Enabled: false),
            new(ImporterId<ExtensionGltfImporter>(), JsonSerializer.SerializeToElement(
                new ExtensionGltfSettings(37, "selected"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var project = new AssetProject(files.Directory, []);
        Guid id = MeshId(project, await project.ImportAsync(files.Source, CreateImporter()));
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.Equal("selected", (await mesh.GetBlockAsync<ExtensionGltfBlock>()).Label);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SceneImporterReadsSourceMaterialSettingsFromTheContext(bool unlit, bool useOverrides)
    {
        using var files = new GltfFiles();
        await files.WriteDefaultTemplatesAsync();
        files.AddTexturedMaterial();
        if (unlit)
        {
            JsonNode source = JsonNode.Parse(File.ReadAllText(files.Source))!;
            source["extensionsUsed"] = new JsonArray("KHR_materials_unlit");
            source["materials"]![0]!["extensions"] = JsonNode.Parse("""{"KHR_materials_unlit":{}}""");
            File.WriteAllText(files.Source, source.ToJsonString());
        }
        string templatePath = unlit ? StandardGltfSceneImporter.UnlitTemplate : StandardGltfSceneImporter.LitTemplate;
        string savedPath = "assets/Materials/source.material.asset";
        string overridePath = "assets/Materials/override.material.asset";
        Guid savedShader = Guid.NewGuid(), overrideShader = Guid.NewGuid();
        using (MaterialAsset template = await MaterialAsset.OpenAsync(Path.Combine(files.Directory, templatePath)))
        {
            template.GetBlock<MaterialBlock>().Shader = new(savedShader);
            await template.SaveAsync(Path.Combine(files.Directory, savedPath));
            template.GetBlock<MaterialBlock>().Shader = new(overrideShader);
            await template.SaveAsync(Path.Combine(files.Directory, overridePath));
        }
        var savedSettings = new GltfImporterSettings { LitMaterialTemplate = savedPath, UnlitMaterialTemplate = savedPath };
        files.WriteSelections(
            new(ImporterId<GltfMeshImporter>(), JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)),
            new(GltfSourceImporter.SourceImporterId, JsonSerializer.SerializeToElement(savedSettings, GltfSettingsJson.Default.GltfImporterSettings)));
        var importer = CreateImporter();
        var project = new AssetProject(files.Directory, []);
        AssetImportSettings<GltfImportOverrides>? overrides = useOverrides ? new(new(savedSettings with
            { LitMaterialTemplate = overridePath, UnlitMaterialTemplate = overridePath })) : null;
        await project.ImportAsync(files.Source, importer, overrides);
        AssetEntry entry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == MaterialAsset.FileType);
        using (MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(files.Directory, entry.Path)))
            Assert.Equal(useOverrides ? overrideShader : savedShader, material.GetBlock<MaterialBlock>().Shader.Value);
        Assert.Equal(savedSettings, SourceMeta.Load(files.Source).GetSettings(GltfSourceImporter.SourceImporterId,
            new GltfImporterSettings(), GltfSettingsJson.Default.GltfImporterSettings));
        if (useOverrides)
        {
            await project.ImportAsync(files.Source, importer);
            using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(files.Directory, entry.Path));
            Assert.Equal(savedShader, material.GetBlock<MaterialBlock>().Shader.Value);
        }
    }

    [Fact]
    public async Task ASelectedExternalTextureProducerSuppliesTheProjectMaterialDependency()
    {
        using var files = new GltfFiles();
        await files.WriteDefaultTemplatesAsync();
        files.AddTexturedMaterial();
        files.WriteSelections(
            new SourceImporterSettings(ImporterId<GltfMeshImporter>(), JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)),
            new SourceImporterSettings(ImporterId<PaintGltfTextureImporter>(), JsonSerializer.SerializeToElement(new PaintGltfTextureSettings(69), ExtensionGltfSettingsJson.Default.PaintGltfTextureSettings)));
        var project = new AssetProject(files.Directory, []);
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, CreateImporter());
        Assert.Equal(4, ids.Count);
        AssetEntry meshEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == MeshAsset.FileType);
        AssetEntry materialEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == MaterialAsset.FileType);
        AssetEntry textureEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == TextureAsset.FileType);
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, meshEntry.Path));
        Assert.Equal(new AssetId<MaterialAsset>(materialEntry.AssetGuid), Assert.Single(mesh.GetBlock<MeshMaterialsBlock>().Materials));
        using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(files.Directory, materialEntry.Path));
        Assert.Equal(textureEntry.AssetGuid, Assert.Single(material.GetBlock<MaterialBlock>().Parameters, value => value.Name == "AlbedoMap").Texture.Value);
        using TextureAsset texture = await TextureAsset.OpenAsync(Path.Combine(files.Directory, textureEntry.Path));
        TextureBlock content = await texture.GetBlockAsync<TextureBlock>();
        ReadOnlyMemory<byte> pixels = await content.ReadBytesAsync(Assert.Single(content.MipTiles).ChunkKey);
        ReadOnlyMemory<byte> read = pixels;
        Assert.Equal(new byte[] { 69, 0, 0, 255 }, read.ToArray());
    }

    [Fact]
    public async Task ReferencedProducerPersistsAlongsideGeometryAndUsesItsOwnSettingsCodec()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<GltfMeshImporter>(), JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)),
            new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(), JsonSerializer.SerializeToElement(new ExtensionGltfSettings(31, "persistent"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var importer = CreateImporter();
        var project = new AssetProject(files.Directory, []);
        Guid id = MeshId(project, await project.ImportAsync(files.Source, importer));
        string output = Path.Combine(files.Directory, project.Manifest.Assets[id].Path);
        using (MeshAsset mesh = await MeshAsset.OpenAsync(output))
        {
            Assert.True(mesh.ContainsBlock<MeshBlock>());
            Assert.False(mesh.ContainsBlock<ClusterGeometryBlock>());
            Assert.Single(mesh.GetBlock<MeshMaterialsBlock>().Materials);
            Assert.Single(mesh.GetBlock<MeshBlock>().Regions);
            ExtensionGltfBlock extension = await mesh.GetBlockAsync<ExtensionGltfBlock>();
            Assert.Equal(31, extension.Number);
            Assert.Equal("persistent", extension.Label);
        }
        string input = await File.ReadAllTextAsync(files.Source);
        await File.WriteAllTextAsync(files.Source, input.Replace("Triangle", "Renamed", StringComparison.Ordinal));
        Assert.Equal(id, MeshId(project, await project.ImportAsync(files.Source, importer)));
        using MeshAsset renamed = await MeshAsset.OpenAsync(output);
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal(31, (await renamed.GetBlockAsync<ExtensionGltfBlock>()).Number);
    }

    [Fact]
    public async Task CallOverridesSelectAnAlternativeAndDisableGeometryWhilePreservingSavedChoices()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<GltfMeshImporter>(), JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)),
            new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(), JsonSerializer.SerializeToElement(new ExtensionGltfSettings(31, "persistent"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var importer = CreateImporter();
        var project = new AssetProject(files.Directory, []);
        var overrides = new AssetImportSettings<GltfImportOverrides>(new(Importers:
        [new SourceImporterSettings(ImporterId<AlternativeGltfImporter>(),
            JsonSerializer.SerializeToElement(new AlternativeGltfSettings("chosen", 72), ExtensionGltfSettingsJson.Default.AlternativeGltfSettings)),
         new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(), default, Enabled: false),
         new SourceImporterSettings(ImporterId<GltfMeshImporter>(), default, Enabled: false)]));
        Guid id = MeshId(project, await project.ImportAsync(files.Source, importer, overrides));
        string output = Path.Combine(files.Directory, project.Manifest.Assets[id].Path);
        using (MeshAsset mesh = await MeshAsset.OpenAsync(output))
        {
            Assert.False(mesh.ContainsBlock<MeshBlock>());
            ExtensionGltfBlock extension = await mesh.GetBlockAsync<ExtensionGltfBlock>();
            Assert.Equal(72, extension.Number);
            Assert.Equal("chosen", extension.Label);
        }
        SourceMeta persistent = SourceMeta.Load(files.Source);
        Assert.Contains(persistent.Importers, value => value.ImporterId == ImporterId<ExtensionGltfImporter>());
        Assert.DoesNotContain(persistent.Importers, value => value.ImporterId == ImporterId<AlternativeGltfImporter>());
        Assert.Equal(new ExtensionGltfSettings(31, "persistent"), persistent.GetSettings(ImporterId<ExtensionGltfImporter>(),
            new ExtensionGltfSettings(0, ""), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings));
        Assert.Equal(id, MeshId(project, await project.ImportAsync(files.Source, importer)));
        using MeshAsset restored = await MeshAsset.OpenAsync(output);
        Assert.True(restored.ContainsBlock<MeshBlock>());
        Assert.Equal(31, (await restored.GetBlockAsync<ExtensionGltfBlock>()).Number);
    }

    [Fact]
    public async Task SelectingCompetingProducersLeavesPreviouslyCommittedOutputUntouched()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(),
            JsonSerializer.SerializeToElement(new ExtensionGltfSettings(31, "first"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var importer = CreateImporter();
        var project = new AssetProject(files.Directory, []);
        Guid id = MeshId(project, await project.ImportAsync(files.Source, importer));
        string output = Path.Combine(files.Directory, project.Manifest.Assets[id].Path);
        byte[] successful = await File.ReadAllBytesAsync(output);
        var conflicting = new AssetImportSettings<GltfImportOverrides>(new(Importers:
        [new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(), JsonSerializer.SerializeToElement(new ExtensionGltfSettings(31, "first"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)),
         new SourceImporterSettings(ImporterId<AlternativeGltfImporter>(), JsonSerializer.SerializeToElement(new AlternativeGltfSettings("second", 72), ExtensionGltfSettingsJson.Default.AlternativeGltfSettings))]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.ImportAsync(files.Source, importer, conflicting).AsTask());
        Assert.Equal(successful, await File.ReadAllBytesAsync(output));
        Assert.Contains(id, project.Manifest.Assets.Keys);
    }

    private static Guid ImporterId<T>() => ConsumerAssetCapabilities.ImporterCapabilities.Single(value => value.ImporterType == typeof(T)).Id;

    private static Guid MeshId(AssetProject project, IReadOnlyList<Guid> ids)
        => Assert.Single(ids, id => project.Manifest.Assets[id].AssetType == MeshAsset.FileType);

    private static GltfSourceImporter CreateImporter() => StandardGltfSceneImporter.CreateSourceImporter(services => ConsumerAssetCapabilities.AddAssetCapabilities(services), "external-glTF-behavior-1");

    private sealed class GltfFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "gltf-extension-behavior-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Directory, "mesh.gltf");
        public GltfFiles()
        {
            System.IO.Directory.CreateDirectory(Directory);
            byte[] bytes = new byte[42];
            float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
            for (int index = 0; index < positions.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4, 4), positions[index]);
            for (ushort index = 0; index < 3; index++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(36 + index * 2, 2), index);
            File.WriteAllText(Source, $$"""
                {"asset":{"version":"2.0"},"buffers":[{"uri":"data:application/octet-stream;base64,{{Convert.ToBase64String(bytes)}}","byteLength":42}],
                "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36,"target":34962},{"buffer":0,"byteOffset":36,"byteLength":6,"target":34963}],
                "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},{"bufferView":1,"componentType":5123,"count":3,"type":"SCALAR"}],
                "meshes":[{"name":"Triangle","extras":{"someengine_id":"mesh-A"},"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],
                "nodes":[{"mesh":0}],"scenes":[{"extras":{"someengine_id":"scene-A"},"nodes":[0]}],"scene":0}
                """);
        }
        public void WriteSelections(params SourceImporterSettings[] selections)
        {
            var metadata = new SourceMeta { SourceGuid = SourceGuid.New(), UseDefaultImporters = false, Importers =
                [.. selections, new(StandardGltfSceneImporter.ImporterId,
                    JsonSerializer.SerializeToElement(new GltfSceneSettings(), GltfSceneSettingsJson.Default.GltfSceneSettings))] };
            if (!selections.Any(selection => ConsumerAssetCapabilities.ImporterCapabilities.Any(capability =>
                capability.Id == selection.ImporterId && capability.SourceType == typeof(SharpGLTF.Schema2.Texture))))
                metadata.Importers.Add(new(ImporterId<GltfTextureImporter>(),
                    JsonSerializer.SerializeToElement(new GltfTextureSettings(), GltfSettingsJson.Default.GltfTextureSettings)));
            SourceMeta.Save(Source, metadata);
        }
        public async Task WriteDefaultTemplatesAsync()
        {
            Game.Assets.MaterialTypes.Register();
            string lit = Path.Combine(Directory, StandardGltfSceneImporter.LitTemplate), unlit = Path.Combine(Directory, StandardGltfSceneImporter.UnlitTemplate);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(lit)!);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(unlit)!);
            using var standard = new Game.Assets.StandardMaterialAsset { Name = "default" };
            using var unlitMaterial = new Game.Assets.UnlitMaterialAsset { Name = "unlit" };
            standard.GetBlock<MaterialBlock>().Shader = new(Guid.NewGuid());
            unlitMaterial.GetBlock<MaterialBlock>().Shader = new(Guid.NewGuid());
            await standard.SaveAsync(lit);
            await unlitMaterial.SaveAsync(unlit);
        }
        public void AddTexturedMaterial()
        {
            JsonNode source = JsonNode.Parse(File.ReadAllText(Source))!;
            source["materials"] = JsonNode.Parse("""[{"name":"Material","extras":{"someengine_id":"material-A"},"pbrMetallicRoughness":{"baseColorTexture":{"index":0}}}]""");
            source["textures"] = JsonNode.Parse("""[{"source":0,"extras":{"someengine_id":"texture-A"}}]""");
            source["images"] = JsonNode.Parse("""[{"uri":"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6Z0ioAAAAASUVORK5CYII="}]""");
            source["meshes"]![0]!["primitives"]![0]!["material"] = 0;
            File.WriteAllText(Source, source.ToJsonString());
        }
        public void AddVertexAttribute(string name)
        {
            JsonNode source = JsonNode.Parse(File.ReadAllText(Source))!;
            byte[] bytes = new byte[68];
            string uri = source["buffers"]![0]!["uri"]!.GetValue<string>();
            Convert.FromBase64String(uri[(uri.IndexOf(',') + 1)..]).CopyTo(bytes, 0);
            float[] values = [0, 0, 1, 0, 0, 1];
            for (int index = 0; index < values.Length; index++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(44 + index * 4, 4), values[index]);
            source["buffers"]![0]!["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes);
            source["buffers"]![0]!["byteLength"] = bytes.Length;
            source["bufferViews"]!.AsArray().Add(JsonNode.Parse("""{"buffer":0,"byteOffset":44,"byteLength":24,"target":34962}"""));
            source["accessors"]!.AsArray().Add(JsonNode.Parse("""{"bufferView":2,"componentType":5126,"count":3,"type":"VEC2"}"""));
            source["meshes"]![0]!["primitives"]![0]!["attributes"]![name] = 2;
            File.WriteAllText(Source, source.ToJsonString());
        }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }
}

[AssetBlock("SomeEngine.Assets.Consumer.Tests.ExtensionGltfBlock"), BinarySerializable, BinaryFields(nameof(Number), nameof(Label))]
public sealed partial class ExtensionGltfBlock : AssetBlock
{
    public partial int Number { get; set; }
    public partial string Label { get; set; }
}

public sealed record ExtensionGltfSettings(int Number, string Label, string? InputPath = null);
public sealed record AlternativeGltfSettings(string Label, int Amount);
public sealed record PaintGltfTextureSettings(byte Red);
[JsonSerializable(typeof(ExtensionGltfSettings)), JsonSerializable(typeof(AlternativeGltfSettings)), JsonSerializable(typeof(PaintGltfTextureSettings))]
public sealed partial class ExtensionGltfSettingsJson : JsonSerializerContext;
public sealed class ExtensionGltfSettingsProvider : IAssetImporterSettings<ExtensionGltfSettings>
{
    public ExtensionGltfSettings DefaultSettings => new(1, "default");
    public JsonTypeInfo<ExtensionGltfSettings> TypeInfo => ExtensionGltfSettingsJson.Default.ExtensionGltfSettings;
}
public sealed class AlternativeGltfSettingsProvider : IAssetImporterSettings<AlternativeGltfSettings>
{
    public AlternativeGltfSettings DefaultSettings => new("alternative", 2);
    public JsonTypeInfo<AlternativeGltfSettings> TypeInfo => ExtensionGltfSettingsJson.Default.AlternativeGltfSettings;
}
public sealed class PaintGltfTextureSettingsProvider : IAssetImporterSettings<PaintGltfTextureSettings>
{
    public PaintGltfTextureSettings DefaultSettings => new(255);
    public JsonTypeInfo<PaintGltfTextureSettings> TypeInfo => ExtensionGltfSettingsJson.Default.PaintGltfTextureSettings;
}
[AssetImporter("1835566e-9f72-4241-a010-f5db8276a3ee", typeof(ExtensionGltfBlock), SettingsProvider = typeof(ExtensionGltfSettingsProvider))]
public sealed class ExtensionGltfImporter : IAssetImporter<GltfMesh, ExtensionGltfSettings>
{
    public async ValueTask ImportAsync(GltfMesh source, ExtensionGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        string label = settings.Label;
        if (settings.InputPath is { } path)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            context.ReportInput(path, Convert.ToHexString(SHA256.HashData(bytes)));
            label = System.Text.Encoding.UTF8.GetString(bytes);
        }
        output.SetBlock(new ExtensionGltfBlock { Number = settings.Number, Label = label });
    }
}
[AssetImporter("10847b92-e6f3-4eb5-8a80-03a19f2a5c0b", typeof(ExtensionGltfBlock), SettingsProvider = typeof(AlternativeGltfSettingsProvider))]
public sealed class AlternativeGltfImporter : IAssetImporter<GltfMesh, AlternativeGltfSettings>
{
    public ValueTask ImportAsync(GltfMesh source, AlternativeGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new ExtensionGltfBlock { Number = settings.Amount, Label = settings.Label });
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("cd465aaf-f8d2-4864-be65-fae01a8a0029", typeof(ExtensionGltfBlock), SettingsProvider = typeof(AlternativeGltfSettingsProvider))]
public sealed class DisabledGltfImporter : IAssetImporter<GltfMesh, AlternativeGltfSettings>
{
    public DisabledGltfImporter() => throw new InvalidOperationException("Constructed the disabled importer.");
    public ValueTask ImportAsync(GltfMesh source, AlternativeGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Executed the disabled importer.");
    }
}

public sealed class ExtensionTextEncoder : IDisposable
{
    private readonly MemoryStream _bytes = new();
    public byte[] Encode(string text)
    {
        _bytes.SetLength(0);
        _bytes.Write(System.Text.Encoding.UTF8.GetBytes(text));
        return _bytes.ToArray();
    }
    public void Dispose() => _bytes.Dispose();
}

[AssetBlock("SomeEngine.Tests.ServiceEncodedGltf")]
public sealed partial class ImporterEncodedGltfBlock : AssetBlock, IAssetBlockWriter
{
    private readonly ExtensionTextEncoder? _encoding;
    public string Text { get; }
    public ImporterEncodedGltfBlock(string text, ExtensionTextEncoder? encoding = null) { Text = text; _encoding = encoding; }
    private ImporterEncodedGltfBlock(ImporterEncodedGltfBlock source, object? syncRoot = null) : base(source, syncRoot)
    { Text = source.Text; _encoding = source._encoding; }
    public override ImporterEncodedGltfBlock Duplicate() => new(this);
    public override ImporterEncodedGltfBlock Duplicate(object syncRoot) => new(this, syncRoot);
    public static async ValueTask<ImporterEncodedGltfBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> metadata = await source.ReadMetadataAsync(cancellationToken);
        ReadOnlyMemory<byte> read = metadata;
        return new(System.Text.Encoding.UTF8.GetString(read.ToArray()));
    }
    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        byte[] bytes = _encoding?.Encode(Text) ?? System.Text.Encoding.UTF8.GetBytes(Text);
        writer.WriteMetadata(BlockTypeId, new(new Guid("ff1721e2-0e1a-4f31-bab3-98e334e465ea"), 0x1132, BinaryCompatibility.ExactSchema, 1), bytes);
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("a966f4ee-305e-4995-b448-f00a4c5b471b", typeof(ImporterEncodedGltfBlock), SettingsProvider = typeof(ExtensionGltfSettingsProvider))]
public sealed class ImporterEncodedGltfImporter : IAssetImporter<GltfMesh, ExtensionGltfSettings>, IDisposable
{
    private readonly ExtensionTextEncoder _encoding = new();
    public ValueTask ImportAsync(GltfMesh source, ExtensionGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new ImporterEncodedGltfBlock(settings.Label, _encoding));
        return ValueTask.CompletedTask;
    }
    public void Dispose() => _encoding.Dispose();
}

[AssetImporter("e71bf7ec-9d45-4a83-a2b0-2441ac18c06c", typeof(TextureBlock), SettingsProvider = typeof(PaintGltfTextureSettingsProvider))]
public sealed class PaintGltfTextureImporter : IAssetImporter<SharpGLTF.Schema2.Texture, PaintGltfTextureSettings>
{
    public ValueTask ImportAsync(SharpGLTF.Schema2.Texture source, PaintGltfTextureSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        var block = new TextureBlock()
        {
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D, Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm, SampledFormat = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        };
        block.MipTiles.Add(new SomeEngine.Resources.TextureMipTile { MipLevel = 0, TileX = 0, TileY = 0, Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4, ChunkKey = 1, DecodedLength = 4 });
        block.SetBytes(1, new byte[] { settings.Red, 0, 0, 255 });
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
        return ValueTask.CompletedTask;
    }
}
