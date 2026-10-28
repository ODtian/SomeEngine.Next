using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task SelectedImporterSetBuildsAndPersistsTheExtensionBlock()
    {
        using var files = new GltfFiles();
        SourceMeta.Save(files.Source, new SourceMeta
        {
            SourceGuid = SourceGuid.New(),
            ImporterSets = [new Guid("4a62c958-14cf-48d9-9c7a-a21c8b166d40")],
        });
        var project = new AssetProject(files.Directory, []);
        Guid id = MeshId(project, await project.ImportAsync(files.Source, CreateImporter()));
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.Equal("default", (await mesh.GetBlockAsync<ExtensionGltfBlock>()).Label);
        Assert.Contains(SourceMeta.Load(files.Source).Importers, value => value.ImporterId == ImporterId<ExtensionGltfImporter>());
    }

    [Fact]
    public async Task ExplicitlyDisabledTextureProducerPreservesThePreviouslyBuiltAssets()
    {
        using var files = new GltfFiles();
        await files.WriteProjectShadersAsync();
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
        await files.WriteProjectShadersAsync();
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
    public async Task ImportPreservesTheSceneWhenUnusedSourceElementsHaveNoPersistentIds()
    {
        using var files = new GltfFiles();
        JsonNode source = JsonNode.Parse(File.ReadAllText(files.Source))!;
        JsonNode unusedMesh = source["meshes"]![0]!.DeepClone();
        unusedMesh.AsObject().Remove("extras");
        source["meshes"]!.AsArray().Add(unusedMesh);
        source["materials"] = JsonNode.Parse("""[{"name":"Unused"}]""");
        source["textures"] = JsonNode.Parse("""[{"source":0}]""");
        source["images"] = JsonNode.Parse("""[{"uri":"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO6Z0ioAAAAASUVORK5CYII="}]""");
        File.WriteAllText(files.Source, source.ToJsonString());
        var project = new AssetProject(files.Directory, []);

        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, CreateImporter());

        Assert.Equal(2, ids.Count);
        Assert.Single(project.List(MeshAsset.FileType));
        Assert.Single(project.List(WorldAsset.FileType));
        Assert.Equal(new[] { "mesh:id:mesh-A", "scene:id:scene-A" },
            SourceMeta.Load(files.Source).ElementIds.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ReferencedMaterialsRequirePersistentIds()
    {
        using var files = new GltfFiles();
        await files.WriteProjectShadersAsync();
        JsonNode source = JsonNode.Parse(File.ReadAllText(files.Source))!;
        source["materials"] = JsonNode.Parse("""[{"name":"Referenced"}]""");
        source["meshes"]![0]!["primitives"]![0]!["material"] = 0;
        File.WriteAllText(files.Source, source.ToJsonString());
        var project = new AssetProject(files.Directory, []);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            project.ImportAsync(files.Source, CreateImporter()).AsTask());
    }

    [Fact]
    public async Task AssetCommandUsesGeneratedProjectImporter()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(),
            JsonSerializer.SerializeToElement(new ExtensionGltfSettings(29, "library source"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        AssetProject project = AssetProjects.Create(files.Directory,
            importers: ConsumerAssetCapabilities.AddAssetCapabilities(new ServiceCollection()));
        using var output = new StringWriter();
        Assert.Equal(0, await AssetCommands.RunAsync(project, ["import", files.Source], output));
        AssetEntry meshEntry = Assert.Single(project.List(MeshAsset.FileType));
        Guid id = meshEntry.AssetGuid;
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        ExtensionGltfBlock block = await mesh.GetBlockAsync<ExtensionGltfBlock>();
        Assert.Equal("library source", block.Label);
        Assert.False(mesh.ContainsBlock<ClusterGeometryBlock>());
        Assert.False(mesh.ContainsBlock<ClusterVertexAttributesBlock>());
    }

    [Fact]
    public async Task RepeatedAssetCommandReusesGeneratedProjectImporterOutputs()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(),
            JsonSerializer.SerializeToElement(new ExtensionGltfSettings(29, "library source"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        AssetProject project = AssetProjects.Create(files.Directory,
            importers: ConsumerAssetCapabilities.AddAssetCapabilities(new ServiceCollection()));
        using var output = new StringWriter();
        await AssetCommands.RunAsync(project, ["import", files.Source], output);
        Guid version = Assert.Single(project.List(MeshAsset.FileType)).Version;

        await AssetCommands.RunAsync(project, ["import", files.Source], output);

        Assert.Equal(version, Assert.Single(project.List(MeshAsset.FileType)).Version);
    }

    [Fact]
    public async Task DefaultEngineAndProjectRegistrationsUseTheSameSelectedInputVersions()
    {
        using var files = new GltfFiles();
        SourceMeta metadata = SourceMeta.GetOrCreate(files.Source);
        var engine = StandardGltfSceneImporter.CreateSourceImporter();
        var project = StandardGltfSceneImporter.CreateSourceImporter(
            ConsumerAssetCapabilities.AddAssetCapabilities(new ServiceCollection()));

        ImportInputs? engineInputs = await engine.GetInputsAsync(files.Directory, files.Source, metadata,
            null, CancellationToken.None);
        ImportInputs? projectInputs = await project.GetInputsAsync(files.Directory, files.Source, metadata,
            null, CancellationToken.None);

        Assert.NotNull(engineInputs);
        Assert.NotNull(projectInputs);
        Assert.Equal(engineInputs.InputHash, projectInputs.InputHash);
    }

    [Fact]
    public async Task ExplicitProjectConversionVersionRemainsAnImportInput()
    {
        using var files = new GltfFiles();
        SourceMeta metadata = SourceMeta.GetOrCreate(files.Source);
        var original = StandardGltfSceneImporter.CreateSourceImporter(capabilitiesVersion: "project-conversion-1");
        var changed = StandardGltfSceneImporter.CreateSourceImporter(capabilitiesVersion: "project-conversion-2");

        ImportInputs? originalInputs = await original.GetInputsAsync(files.Directory, files.Source, metadata,
            null, CancellationToken.None);
        ImportInputs? changedInputs = await changed.GetInputsAsync(files.Directory, files.Source, metadata,
            null, CancellationToken.None);

        Assert.NotNull(originalInputs);
        Assert.NotNull(changedInputs);
        Assert.NotEqual(originalInputs.InputHash, changedInputs.InputHash);
    }

    [Fact]
    public async Task AddingUnselectedCapabilitiesPreservesSavedDefaultsAndHitsTheCache()
    {
        using var files = new GltfFiles();
        await files.WriteProjectShadersAsync();
        var project = new AssetProject(files.Directory, []);
        var original = StandardGltfSceneImporter.CreateSourceImporter(new ServiceCollection(), "project-services-1");
        Guid id = MeshId(project, await project.ImportAsync(files.Source, original));
        Guid version = project.Manifest.Assets[id].Version;
        SourceMeta metadata = SourceMeta.Load(files.Source);
        Guid[] expectedImporters =
        [
            ImporterId<GltfMeshImporter>(), ImporterId<GltfTextureImporter>(),
            ImporterId<GltfVertexStreamsImporter>(), ImporterId<MorphTargetSetImporter>(),
            ImporterId<ClusterGeometryImporter>(), ImporterId<ClusterVertexAttributesImporter>(),
            ImporterId<ClusterSkinInfluencesImporter>(), ImporterId<ClusterSkinBoundsImporter>(),
            ImporterId<ClusterMorphDeltasImporter>(), StandardGltfSceneImporter.ImporterId,
        ];
        Assert.Equal(new[] { new Guid(GltfImporterSets.ClusterId) }, metadata.ImporterSets);
        Assert.Equal(expectedImporters.Order(), metadata.Importers.Select(value => value.ImporterId).Order());
        var extended = StandardGltfSceneImporter.CreateSourceImporter(ConsumerAssetCapabilities.AddAssetCapabilities(new ServiceCollection()), "project-services-1");
        Assert.Equal(id, MeshId(project, await project.ImportAsync(files.Source, extended)));
        Assert.Equal(version, project.Manifest.Assets[id].Version);
        SourceMeta extendedMetadata = SourceMeta.Load(files.Source);
        Assert.Equal(metadata.ImporterSets, extendedMetadata.ImporterSets);
        Assert.Equal(expectedImporters.Order(), extendedMetadata.Importers.Select(value => value.ImporterId).Order());
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.True(mesh.ContainsBlock<ClusterGeometryBlock>());
        Assert.True(mesh.ContainsBlock<ClusterVertexAttributesBlock>());
        ClusterGeometryBlock geometry = await mesh.GetBlockAsync<ClusterGeometryBlock>();
        ClusterVertexAttributesBlock attributes = await mesh.GetBlockAsync<ClusterVertexAttributesBlock>();
        Assert.NotEmpty(geometry.Clusters);
        Assert.Equal(geometry.VertexIdentity, attributes.VertexIdentity);
        Assert.Equal(geometry.Clusters.Count, attributes.Clusters.Count);
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
    public async Task ImporterHeldEncodingWritesTheSavedBlock()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<ServiceEncodedGltfImporter>(),
            JsonSerializer.SerializeToElement(new ExtensionGltfSettings(1, "saved using scoped codec"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var importer = StandardGltfSceneImporter.CreateSourceImporter(ConsumerAssetCapabilities.AddAssetCapabilities(new ServiceCollection()), "scoped-codec-test-1");
        var project = new AssetProject(files.Directory, []);
        Guid id = MeshId(project, await project.ImportAsync(files.Source, importer));
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.Equal("saved using scoped codec", (await mesh.GetBlockAsync<ServiceEncodedGltfBlock>()).Text);
    }

    [Fact]
    public async Task ASelectedExternalTextureProducerSuppliesTheProjectMaterialDependency()
    {
        using var files = new GltfFiles();
        await files.WriteProjectShadersAsync();
        files.AddTexturedMaterial();
        files.WriteSelections(
            new SourceImporterSettings(ImporterId<GltfMeshImporter>(), JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)),
            new SourceImporterSettings(ImporterId<PaintGltfTextureImporter>(), JsonSerializer.SerializeToElement(new PaintGltfTextureSettings(69), ExtensionGltfSettingsJson.Default.PaintGltfTextureSettings)));
        var project = new AssetProject(files.Directory, []);
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, CreateImporter());
        Assert.Equal(4, ids.Count);
        AssetEntry[] importedEntries = ids.Select(id => project.Manifest.Assets[id]).ToArray();
        AssetEntry meshEntry = Assert.Single(importedEntries, value => value.AssetType == MeshAsset.FileType);
        AssetEntry materialEntry = Assert.Single(importedEntries, value => value.AssetType == MaterialAsset.FileType);
        AssetEntry textureEntry = Assert.Single(importedEntries, value => value.AssetType == TextureAsset.FileType);
        Assert.Single(importedEntries, value => value.AssetType == WorldAsset.FileType);
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
            Assert.False(mesh.ContainsBlock<ClusterVertexAttributesBlock>());
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
    public async Task CallOverridesSelectAnAlternativeAndOmitGeometryWithoutChangingPersistentSelections()
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
         // Neutral shared-data producers are retained unless explicitly disabled.
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
        Assert.True(persistent.Importers.Single(value => value.ImporterId == ImporterId<GltfMeshImporter>()).Enabled);
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

    [Fact]
    public void StandardMaterialDefaultsPreserveLinearColorAndArmOcclusion()
    {
        using var block = new MaterialBlock();
        var parameters = new StandardMaterialParameters(block);
        Assert.Equal(1f, parameters.NormalScale);
        Assert.Equal(1f, parameters.OcclusionStrength);
        Assert.False(parameters.UseOcclusionMap);
        Assert.False(parameters.UseEmissiveMap);
        Assert.False(parameters.DecodeAlbedoSrgb);
        Assert.False(parameters.DecodeEmissiveSrgb);
    }

    [Fact]
    public async Task StandardGltfImportsIndependentPbrChannelsWithoutUsingAoAsMetallicRoughness()
    {
        using var files = new GltfFiles();
        await files.WriteProjectShadersAsync();
        files.AddTexturedMaterial();
        JsonNode source = JsonNode.Parse(File.ReadAllText(files.Source))!;
        source["extensionsUsed"] = JsonNode.Parse("""["KHR_materials_emissive_strength"]""");
        source["textures"] = JsonNode.Parse("""
            [
            {"source":0,"name":"Albedo","extras":{"someengine_id":"albedo"}},
            {"source":0,"name":"AO","extras":{"someengine_id":"ao"}},
            {"source":0,"name":"Emission","extras":{"someengine_id":"emission"}},
            {"source":0,"name":"Normal","extras":{"someengine_id":"normal"}}]
            """);
        source["materials"] = JsonNode.Parse("""
            [{
            "name":"Material","extras":{"someengine_id":"material-A"},
            "pbrMetallicRoughness":{"baseColorTexture":{"index":0},"metallicFactor":0.8,"roughnessFactor":0.25},
            "normalTexture":{"index":3,"scale":0.5},"occlusionTexture":{"index":1,"strength":0.3},
            "emissiveTexture":{"index":2},"emissiveFactor":[0.1,0.2,0.3],
            "extensions":{"KHR_materials_emissive_strength":{"emissiveStrength":4}}}]
            """);
        File.WriteAllText(files.Source, source.ToJsonString());
        files.WriteSelections(new SourceImporterSettings(ImporterId<GltfMeshImporter>(),
            JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)));
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, CreateImporter());
        SourceMeta metadata = SourceMeta.Load(files.Source);
        Guid Id(string key) => metadata.Outputs.Single(value => value.SubAssetKey == key).AssetGuid;
        AssetEntry materialEntry = project.Manifest.Assets[Id("material:id:material-A")];
        using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(files.Directory, materialEntry.Path));
        await using var loader = new AssetLoader(project.CreateStorage());
        using MaterialBlock resolved = await material.ResolveAsync(loader);
        var values = new StandardMaterialParameters(resolved);
        Assert.False(material.GetBlock<MaterialBlock>().HasOverride("ARMMap"));
        Assert.Equal(MaterialTemplates.White, values.ARMMap);
        Assert.Equal(Id("texture:id:ao"), values.OcclusionMap.Value);
        Assert.Equal(Id("texture:id:emission"), values.EmissiveMap.Value);
        Assert.Equal(Id("texture:id:normal"), values.NormalMap.Value);
        Assert.Equal(0.5f, values.NormalScale);
        Assert.Equal(0.3f, values.OcclusionStrength);
        Assert.Equal(0.8f, values.MetallicFactor);
        Assert.Equal(0.25f, values.Roughness);
        Assert.Equal(new System.Numerics.Vector3(0.4f, 0.8f, 1.2f), values.EmissiveFactor);
        Assert.True(values.UseOcclusionMap);
        Assert.True(values.UseEmissiveMap);
        Assert.True(values.DecodeAlbedoSrgb);
        Assert.True(values.DecodeEmissiveSrgb);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardGltfDecodesColorOnceForSelectedTextureProducers(bool srgbView)
    {
        using var files = new GltfFiles();
        await files.WriteProjectShadersAsync();
        files.AddTexturedMaterial();
        JsonNode source = JsonNode.Parse(File.ReadAllText(files.Source))!;
        source["materials"]![0]!["emissiveTexture"] = JsonNode.Parse("""{"index":0}""");
        source["materials"]![0]!["emissiveFactor"] = JsonNode.Parse("""[1,1,1]""");
        File.WriteAllText(files.Source, source.ToJsonString());
        files.WriteSelections(
            new SourceImporterSettings(ImporterId<GltfMeshImporter>(),
                JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)),
            new SourceImporterSettings(ImporterId<PaintGltfTextureImporter>(),
                JsonSerializer.SerializeToElement(new PaintGltfTextureSettings(69, srgbView), ExtensionGltfSettingsJson.Default.PaintGltfTextureSettings)));
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, CreateImporter());
        Guid materialId = SourceMeta.Load(files.Source).Outputs.Single(value => value.SubAssetKey == "material:id:material-A").AssetGuid;
        using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[materialId].Path));
        var values = new StandardMaterialParameters(material.GetBlock<MaterialBlock>());
        Assert.Equal(!srgbView, values.DecodeAlbedoSrgb);
        Assert.Equal(!srgbView, values.DecodeEmissiveSrgb);
        Assert.Equal(values.AlbedoMap, values.EmissiveMap);
        Assert.Equal(MaterialTemplates.White, values.OcclusionMap);
    }

    [Fact]
    public async Task StandardGltfWithMetallicRoughnessButNoAoDoesNotUseArmRed()
    {
        using var files = new GltfFiles();
        await files.WriteProjectShadersAsync();
        files.AddTexturedMaterial();
        JsonNode source = JsonNode.Parse(File.ReadAllText(files.Source))!;
        source["materials"]![0]!["pbrMetallicRoughness"]!["metallicRoughnessTexture"] = JsonNode.Parse("""{"index":0}""");
        File.WriteAllText(files.Source, source.ToJsonString());
        files.WriteSelections(new SourceImporterSettings(ImporterId<GltfMeshImporter>(),
            JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)));
        var project = new AssetProject(files.Directory, []);
        await project.ImportAsync(files.Source, CreateImporter());
        Guid materialId = SourceMeta.Load(files.Source).Outputs.Single(value => value.SubAssetKey == "material:id:material-A").AssetGuid;
        using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[materialId].Path));
        var values = new StandardMaterialParameters(material.GetBlock<MaterialBlock>());
        Assert.Equal(values.AlbedoMap, values.ARMMap);
        Assert.Equal(MaterialTemplates.White, values.OcclusionMap);
        Assert.True(values.UseOcclusionMap);
    }

    [Fact]
    public void GenericMaskCutoffAccountsForBaseColorAlphaWithoutChangingVariantPolicy()
    {
        using var files = new GltfFiles();
        files.AddTexturedMaterial();
        JsonNode source = JsonNode.Parse(File.ReadAllText(files.Source))!;
        source["materials"]![0]!["alphaMode"] = "MASK";
        source["materials"]![0]!["alphaCutoff"] = 0.5;
        source["materials"]![0]!["pbrMetallicRoughness"]!["baseColorFactor"] = JsonNode.Parse("""[1,1,1,0.25]""");
        File.WriteAllText(files.Source, source.ToJsonString());
        var material = SharpGLTF.Schema2.ModelRoot.Load(files.Source).LogicalMaterials[0];
        Assert.Equal(2f, StandardGltfSceneImporter.GetAlphaMaskCutoff(material));
        source["materials"]![0]!["alphaMode"] = "OPAQUE";
        File.WriteAllText(files.Source, source.ToJsonString());
        Assert.Equal(-1f, StandardGltfSceneImporter.GetAlphaMaskCutoff(
            SharpGLTF.Schema2.ModelRoot.Load(files.Source).LogicalMaterials[0]));
    }

    private static GltfSourceImporter CreateImporter() => StandardGltfSceneImporter.CreateSourceImporter(ConsumerAssetCapabilities.AddAssetCapabilities(new ServiceCollection()), "external-glTF-behavior-1");

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
        public async Task WriteProjectShadersAsync()
        {
            string shaderDirectory = Path.Combine(Directory, "assets", "Shaders");
            System.IO.Directory.CreateDirectory(shaderDirectory);
            using var standard = new ShaderAsset { Name = "standard" };
            using var unlit = new ShaderAsset { Name = "unlit" };
            await standard.SaveSourceAsync(Path.Combine(shaderDirectory, "standard.shader"));
            await unlit.SaveSourceAsync(Path.Combine(shaderDirectory, "unlit.shader"));
            await MaterialTemplates.SaveAsync(new AssetProject(Directory, []), standard.Reference, unlit.Reference);
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
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }
}

[AssetImporterSet("4a62c958-14cf-48d9-9c7a-a21c8b166d40", "Extension glTF importers", typeof(ExtensionGltfImporter), typeof(StandardGltfSceneImporter))]
public static class ExtensionGltfImporters;

[AssetBlock("SomeEngine.Assets.Consumer.Tests.ExtensionGltfBlock"), BinarySerializable, BinaryFields(nameof(Number), nameof(Label))]
public sealed partial class ExtensionGltfBlock : AssetBlock
{
    public partial int Number { get; set; }
    public partial string Label { get; set; }
}

public sealed record ExtensionGltfSettings(int Number, string Label, string? InputPath = null);
public sealed record AlternativeGltfSettings(string Label, int Amount);
public sealed record PaintGltfTextureSettings(byte Red, bool SrgbView = false);
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
    public ValueTask ImportAsync(GltfMesh source, AlternativeGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("An unselected capability must not be constructed or run.");
    }
}

public sealed class ExtensionEncodingService : IDisposable
{
    public bool Disposed { get; private set; }
    public byte[] Encode(string text)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return System.Text.Encoding.UTF8.GetBytes(text);
    }
    public void Dispose() => Disposed = true;
}

[AssetBlock("SomeEngine.Tests.ServiceEncodedGltf")]
public sealed partial class ServiceEncodedGltfBlock : AssetBlock, IAssetBlockWriter
{
    private readonly ExtensionEncodingService? _encoding;
    public string Text { get; }
    public ServiceEncodedGltfBlock(string text, ExtensionEncodingService? encoding = null) { Text = text; _encoding = encoding; }
    private ServiceEncodedGltfBlock(ServiceEncodedGltfBlock source, object? syncRoot = null) : base(source, syncRoot)
    { Text = source.Text; _encoding = source._encoding; }
    public override ServiceEncodedGltfBlock Duplicate() => new(this);
    public override ServiceEncodedGltfBlock Duplicate(object syncRoot) => new(this, syncRoot);
    public static async ValueTask<ServiceEncodedGltfBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
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

[AssetImporter("a966f4ee-305e-4995-b448-f00a4c5b471b", typeof(ServiceEncodedGltfBlock), SettingsProvider = typeof(ExtensionGltfSettingsProvider))]
public sealed class ServiceEncodedGltfImporter : IAssetImporter<GltfMesh, ExtensionGltfSettings>, IDisposable
{
    private readonly ExtensionEncodingService _encoding = new();
    public ValueTask ImportAsync(GltfMesh source, ExtensionGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new ServiceEncodedGltfBlock(settings.Label, _encoding));
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
            Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledFormat = settings.SrgbView ? SomeEngine.Graphics.Format.R8G8B8A8UNormSrgb : SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        };
        block.MipTiles.Add(new SomeEngine.Resources.TextureMipTile { MipLevel = 0, TileX = 0, TileY = 0, Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4, ChunkKey = 1, DecodedLength = 4 });
        block.SetBytes(1, new byte[] { settings.Red, 0, 0, 255 });
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
        return ValueTask.CompletedTask;
    }
}
