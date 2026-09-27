using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
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
    public async Task LibrarySourceImporterUsesGeneratedProjectServicesWithoutProjectTraversalCode()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(),
            JsonSerializer.SerializeToElement(new ExtensionGltfSettings(29, "library source"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var importer = new GltfSourceImporter(services => ConsumerAssetCapabilities.AddAssetCapabilities(services),
            ConsumerAssetCapabilities.BuildVersion);
        var project = new AssetProject(files.Directory, []);
        Guid id = Assert.Single(await project.ImportAsync(files.Source, importer));
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        ExtensionGltfBlock block = await mesh.GetBlockAsync<ExtensionGltfBlock>();
        Assert.Equal("library source", block.Label);
        Assert.False(mesh.ContainsBlock<ClusterMeshBlock>());
    }

    [Fact]
    public async Task AddingUnselectedCapabilitiesPreservesSavedDefaultsAndHitsTheCache()
    {
        using var files = new GltfFiles();
        await files.WriteDefaultTemplatesAsync();
        var project = new AssetProject(files.Directory, []);
        var original = MaterialImport.CreateGltf(_ => { }, "project-services-1");
        Guid id = Assert.Single(await project.ImportAsync(files.Source, original));
        Guid version = project.Manifest.Assets[id].Version;
        SourceMeta metadata = SourceMeta.Load(files.Source);
        Assert.Equal(new[] { ImporterId<GltfMeshImporter>(), ImporterId<ClusterGltfMeshImporter>(), ImporterId<GltfMeshMaterialsImporter>(),
                ImporterId<GltfMaterialImporter>(), ImporterId<GltfTextureImporter>() }.Order(),
            metadata.Importers.Select(value => value.ImporterId).Order());
        var extended = MaterialImport.CreateGltf(services => ConsumerAssetCapabilities.AddAssetCapabilities(services), "project-services-1");
        Assert.Equal(id, Assert.Single(await project.ImportAsync(files.Source, extended)));
        Assert.Equal(version, project.Manifest.Assets[id].Version);
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.True(mesh.ContainsBlock<ClusterMeshBlock>());
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
        Guid id = Assert.Single(await project.ImportAsync(files.Source, importer));
        Guid version = project.Manifest.Assets[id].Version;
        await File.WriteAllTextAsync(Path.Combine(files.Directory, "unrelated.txt"), "does not affect conversion");
        Assert.Equal(id, Assert.Single(await project.ImportAsync(files.Source, importer)));
        Assert.Equal(version, project.Manifest.Assets[id].Version);
        await File.WriteAllTextAsync(input, "second data");
        Assert.Equal(id, Assert.Single(await project.ImportAsync(files.Source, importer)));
        Assert.NotEqual(version, project.Manifest.Assets[id].Version);
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.Equal("second data", (await mesh.GetBlockAsync<ExtensionGltfBlock>()).Label);
    }

    [Fact]
    public async Task ScopedCodecServicesRemainAliveUntilBlockSavingCompletes()
    {
        using var files = new GltfFiles();
        files.WriteSelections(new SourceImporterSettings(ImporterId<ServiceEncodedGltfImporter>(),
            JsonSerializer.SerializeToElement(new ExtensionGltfSettings(1, "saved using scoped codec"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)));
        var encoding = new ExtensionEncodingService();
        var importer = MaterialImport.CreateGltf(services =>
        {
            services.AddScoped(_ => encoding);
            ConsumerAssetCapabilities.AddAssetCapabilities(services);
        }, "scoped-codec-test-1");
        var project = new AssetProject(files.Directory, []);
        Guid id = Assert.Single(await project.ImportAsync(files.Source, importer));
        Assert.True(encoding.WriteCount > 0);
        Assert.True(encoding.Disposed);
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, project.Manifest.Assets[id].Path));
        Assert.Equal("saved using scoped codec", (await mesh.GetBlockAsync<ServiceEncodedGltfBlock>()).Text);
    }

    [Fact]
    public async Task ASelectedExternalTextureProducerSuppliesTheProjectMaterialDependency()
    {
        using var files = new GltfFiles();
        await files.WriteDefaultTemplatesAsync();
        files.AddTexturedMaterial();
        files.WriteSelections(
            new SourceImporterSettings(ImporterId<GltfMeshImporter>(), JsonSerializer.SerializeToElement(new GltfMeshSettings(), GltfMeshSettingsJson.Default.GltfMeshSettings)),
            new SourceImporterSettings(ImporterId<GltfMeshMaterialsImporter>(), JsonSerializer.SerializeToElement(new GltfMeshMaterialSettings(), GltfSettingsJson.Default.GltfMeshMaterialSettings)),
            new SourceImporterSettings(ImporterId<GltfMaterialImporter>(), JsonSerializer.SerializeToElement(new GltfMaterialSettings(), GltfSettingsJson.Default.GltfMaterialSettings)),
            new SourceImporterSettings(ImporterId<PaintGltfTextureImporter>(), JsonSerializer.SerializeToElement(new PaintGltfTextureSettings(69), ExtensionGltfSettingsJson.Default.PaintGltfTextureSettings)));
        var project = new AssetProject(files.Directory, []);
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, CreateImporter());
        Assert.Equal(3, ids.Count);
        AssetEntry meshEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == MeshAsset.FileType);
        AssetEntry materialEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == MaterialAsset.FileType);
        AssetEntry textureEntry = Assert.Single(project.Manifest.Assets.Values, value => value.AssetType == TextureAsset.FileType);
        using MeshAsset mesh = await MeshAsset.OpenAsync(Path.Combine(files.Directory, meshEntry.Path));
        Assert.Equal(new AssetId<MaterialAsset>(materialEntry.AssetGuid), Assert.Single(mesh.GetBlock<MeshMaterialsBlock>().Materials));
        using MaterialAsset material = await MaterialAsset.OpenAsync(Path.Combine(files.Directory, materialEntry.Path));
        Assert.Equal(textureEntry.AssetGuid, Assert.Single(material.GetBlock<MaterialBlock>().Parameters, value => value.Name == "AlbedoMap").Texture.Value);
        using TextureAsset texture = await TextureAsset.OpenAsync(Path.Combine(files.Directory, textureEntry.Path));
        TextureBlock content = await texture.GetBlockAsync<TextureBlock>();
        using PageBuffer pixels = await content.AcquireBufferAsync(Assert.Single(content.MipTiles).ChunkKey);
        using PageRange<byte> read = pixels.Retain();
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
        Guid id = Assert.Single(await project.ImportAsync(files.Source, importer));
        string output = Path.Combine(files.Directory, project.Manifest.Assets[id].Path);
        using (MeshAsset mesh = await MeshAsset.OpenAsync(output))
        {
            Assert.True(mesh.ContainsBlock<MeshBlock>());
            Assert.False(mesh.ContainsBlock<ClusterMeshBlock>());
            Assert.False(mesh.ContainsBlock<MeshMaterialsBlock>());
            Assert.Single(mesh.GetBlock<MeshBlock>().Regions);
            ExtensionGltfBlock extension = await mesh.GetBlockAsync<ExtensionGltfBlock>();
            Assert.Equal(31, extension.Number);
            Assert.Equal("persistent", extension.Label);
        }
        string input = await File.ReadAllTextAsync(files.Source);
        await File.WriteAllTextAsync(files.Source, input.Replace("Triangle", "Renamed", StringComparison.Ordinal));
        Assert.Equal(id, Assert.Single(await project.ImportAsync(files.Source, importer)));
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
         new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(), default, Enabled: false)]));
        Guid id = Assert.Single(await project.ImportAsync(files.Source, importer, overrides));
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
        Assert.Equal(id, Assert.Single(await project.ImportAsync(files.Source, importer)));
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
        Guid id = Assert.Single(await project.ImportAsync(files.Source, importer));
        string output = Path.Combine(files.Directory, project.Manifest.Assets[id].Path);
        byte[] successful = await File.ReadAllBytesAsync(output);
        var conflicting = new AssetImportSettings<GltfImportOverrides>(new(Importers:
        [new SourceImporterSettings(ImporterId<ExtensionGltfImporter>(), JsonSerializer.SerializeToElement(new ExtensionGltfSettings(31, "first"), ExtensionGltfSettingsJson.Default.ExtensionGltfSettings)),
         new SourceImporterSettings(ImporterId<AlternativeGltfImporter>(), JsonSerializer.SerializeToElement(new AlternativeGltfSettings("second", 72), ExtensionGltfSettingsJson.Default.AlternativeGltfSettings))]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.ImportAsync(files.Source, importer, conflicting).AsTask());
        Assert.Equal(successful, await File.ReadAllBytesAsync(output));
        Assert.Equal(id, Assert.Single(project.Manifest.Assets.Keys));
    }

    private static Guid ImporterId<T>() => ConsumerAssetCapabilities.ImporterCapabilities.Single(value => value.ImporterType == typeof(T)).Id;

    private static GltfSourceImporter CreateImporter() => MaterialImport.CreateGltf(services => ConsumerAssetCapabilities.AddAssetCapabilities(services), "external-glTF-behavior-1");

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
                "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0}
                """);
        }
        public void WriteSelections(params SourceImporterSettings[] selections) => SourceMeta.Save(Source, new SourceMeta
        { SourceGuid = SourceGuid.New(), UseDefaultImporters = false, Importers = selections.ToList() });
        public async Task WriteDefaultTemplatesAsync()
        {
            Game.Assets.MaterialTypes.Register();
            string lit = Path.Combine(Directory, MaterialImport.LitTemplate), unlit = Path.Combine(Directory, MaterialImport.UnlitTemplate);
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
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
    }
}

[AssetBlock, BinarySerializable, BinaryFields(nameof(Number), nameof(Label))]
public sealed partial class ExtensionGltfBlock : AssetPageBlock
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

public sealed class UnregisteredGltfDependency;
[AssetImporter("cd465aaf-f8d2-4864-be65-fae01a8a0029", typeof(ExtensionGltfBlock), SettingsProvider = typeof(AlternativeGltfSettingsProvider))]
public sealed class DisabledGltfImporter(UnregisteredGltfDependency dependency) : IAssetImporter<GltfMesh, AlternativeGltfSettings>
{
    public ValueTask ImportAsync(GltfMesh source, AlternativeGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        _ = dependency;
        throw new InvalidOperationException("An unselected capability must not be constructed or run.");
    }
}

public sealed class ExtensionEncodingService : IDisposable
{
    public bool Disposed { get; private set; }
    public int WriteCount { get; private set; }
    public byte[] Encode(string text)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        WriteCount++;
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
    private ServiceEncodedGltfBlock(ServiceEncodedGltfBlock source, PageAccess? access = null) : base(source, access)
    { Text = source.Text; _encoding = source._encoding; }
    public override ServiceEncodedGltfBlock Duplicate() => new(this);
    public override ServiceEncodedGltfBlock Duplicate(PageAccess access) => new(this, access);
    public static async ValueTask<ServiceEncodedGltfBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        using PageBuffer metadata = await source.AcquireMetadataAsync(cancellationToken);
        using PageRange<byte> read = metadata.Retain();
        return new(System.Text.Encoding.UTF8.GetString(read.ToArray()));
    }
    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        byte[] bytes = _encoding?.Encode(Text) ?? System.Text.Encoding.UTF8.GetBytes(Text);
        using var metadata = new PageBuffer(PageArena.Shared, bytes.Length);
        metadata.Write(0, bytes);
        writer.WriteMetadata(BlockTypeId, new(new Guid("ff1721e2-0e1a-4f31-bab3-98e334e465ea"), 0x1132, BinaryCompatibility.ExactSchema, 1), metadata);
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("a966f4ee-305e-4995-b448-f00a4c5b471b", typeof(ServiceEncodedGltfBlock), SettingsProvider = typeof(ExtensionGltfSettingsProvider))]
public sealed class ServiceEncodedGltfImporter(ExtensionEncodingService encoding) : IAssetImporter<GltfMesh, ExtensionGltfSettings>
{
    public ValueTask ImportAsync(GltfMesh source, ExtensionGltfSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        output.SetBlock(new ServiceEncodedGltfBlock(settings.Label, encoding));
        return ValueTask.CompletedTask;
    }
}

[AssetImporter("e71bf7ec-9d45-4a83-a2b0-2441ac18c06c", typeof(TextureBlock), SettingsProvider = typeof(PaintGltfTextureSettingsProvider))]
public sealed class PaintGltfTextureImporter : IAssetImporter<SharpGLTF.Schema2.Texture, PaintGltfTextureSettings>
{
    public ValueTask ImportAsync(SharpGLTF.Schema2.Texture source, PaintGltfTextureSettings settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
    {
        var block = new TextureBlock(output.Target.Pages)
        {
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D, Width = 1, Height = 1, Depth = 1, MipLevelCount = 1, ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm, SampledFormat = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        };
        block.MipTiles.Add(new SomeEngine.Resources.TextureMipTile { MipLevel = 0, TileX = 0, TileY = 0, Width = 1, Height = 1, RowPitch = 4, SlicePitch = 4, ChunkKey = 1, DecodedLength = 4 });
        block.Chunks.Set(1, new byte[] { settings.Red, 0, 0, 255 });
        try { output.SetBlock(block); }
        catch { block.Dispose(); throw; }
        return ValueTask.CompletedTask;
    }
}
