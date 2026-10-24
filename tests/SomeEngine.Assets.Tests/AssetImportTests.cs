using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Importers;
using SomeEngine.Serialization.Containers;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests;

public sealed class AssetImportTests
{
    [Fact]
    public async Task RebuildingWithoutTheBuildRecordRestoresSourceElementIdentities()
    {
        using var files = new TestFiles();
        var importer = new SourceImporter();
        var project = new AssetProject(files.Root, [importer]);
        IReadOnlyList<Guid> first = await project.ImportAsync(files.Source, importer);
        SourceMeta metadata = SourceMeta.Load(files.Source);
        File.Delete(SourceMeta.GetBuildPath(files.Source));
        await project.RebuildAsync();
        Assert.All(first, id => Assert.Equal(metadata.SourceGuid, project.Manifest.Assets[id].SourceGuid));
        Assert.Equal(first, await project.ImportAsync(files.Source, importer));
    }

    [Fact]
    public async Task DiscoveryFindsARegisteredSourceBeforeItHasMetadata()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, [new SourceImporter()]);
        AssetDiscoveredFile found = Assert.Single(await project.DiscoverAsync());
        Assert.Equal(AssetFileKind.ImportedSource, found.Kind);
        Assert.Equal(files.Source, found.Path);
        Assert.True(found.SourceGuid.IsEmpty);
        Assert.False(File.Exists(SourceMeta.GetMetaPath(files.Source)));
    }

    [Fact]
    public async Task RealMeshAndShaderSaveTogetherAndReopenWithPersistentIdentities()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter { IncludeMesh = true };
        IReadOnlyList<Guid> first = await project.ImportAsync(files.Source, importer);
        Assert.Equal(2, first.Count);
        using MeshAsset mesh = await MeshAsset.OpenAsync(files.Path("mesh.mesh.asset"));
        using ShaderAsset shader = await ShaderAsset.OpenAsync(files.Path("shader0.shader.asset"));
        Assert.Contains(mesh.Id, first);
        Assert.Contains(shader.Id, first);
        Assert.NotEmpty((await mesh.GetBlockAsync<ClusterGeometryBlock>()).Clusters);
        Assert.NotEmpty((await mesh.GetBlockAsync<ClusterVertexAttributesBlock>()).Clusters);
        Assert.All(project.List(), entry => Assert.NotEqual(Guid.Empty, entry.Version));
        File.WriteAllText(files.Source, "renamed source content");
        Assert.Equal(first, await project.ImportAsync(files.Source, importer));
        Assert.Equal(2, importer.Reads);
        Assert.Equal(first.Order(), AssetManifest.Load(files.Catalog).Assets.Keys.Order());
        files.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task UnchangedInputsUseCacheButEditedOrCorruptedOutputsAreRebuilt()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter();
        Guid id = Assert.Single(await project.ImportAsync(files.Source, importer));
        Assert.Equal(id, Assert.Single(await project.ImportAsync(files.Source, importer)));
        Assert.Equal(1, importer.Reads);
        using (ShaderAsset edited = await ShaderAsset.OpenAsync(files.Path("shader0.shader.asset")))
        {
            edited.Name = "manually edited derived output";
            await edited.SaveAsync(files.Path("shader0.shader.asset"));
        }
        Assert.Equal(id, Assert.Single(await project.ImportAsync(files.Source, importer)));
        Assert.Equal(2, importer.Reads);
        File.WriteAllBytes(files.Path("shader0.shader.asset"), [1, 2, 3]);
        Assert.Equal(id, Assert.Single(await project.ImportAsync(files.Source, importer)));
        Assert.Equal(3, importer.Reads);
        using ShaderAsset recovered = await ShaderAsset.OpenAsync(files.Path("shader0.shader.asset"));
        Assert.Equal("source:0", recovered.Name);
        files.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FailureAfterSavingDraftsDoesNotChangeFilesMetadataOrDirectory()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter { ShaderCount = 2 };
        IReadOnlyList<Guid> first = await project.ImportAsync(files.Source, importer);
        Dictionary<string, byte[]> before = files.SavedFiles();
        File.WriteAllText(files.Source, "changed source");
        importer.FailAfterSave = 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => project.ImportAsync(files.Source, importer).AsTask());
        files.AssertSavedFiles(before);
        Assert.Equal(first.Order(), project.List().Select(static entry => entry.AssetGuid).Order());
        files.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task FailedReplacementRestoresAnAlreadyReplacedOutput()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter();
        await project.ImportAsync(files.Source, importer);
        Dictionary<string, byte[]> before = files.SavedFiles();
        File.WriteAllText(files.Source, "changed source");
        importer.ShaderCount = 2;
        Directory.CreateDirectory(files.Path("shader1.shader.asset"));
        await Assert.ThrowsAnyAsync<IOException>(() => project.ImportAsync(files.Source, importer).AsTask());
        files.AssertSavedFiles(before);
        Assert.Single(project.List());
        files.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task CancellationAfterDraftSavePreservesLastSuccessfulImport()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter();
        await project.ImportAsync(files.Source, importer);
        Dictionary<string, byte[]> before = files.SavedFiles();
        File.WriteAllText(files.Source, "changed source");
        using var cancellation = new CancellationTokenSource();
        importer.CancelAfterSave = cancellation;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => project.ImportAsync(files.Source, importer, cancellation.Token).AsTask());
        files.AssertSavedFiles(before);
        files.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task RemovedOutputsLeaveDirectoryAndDiskButRetainTheirElementIdentity()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter { ShaderCount = 2 };
        IReadOnlyList<Guid> first = await project.ImportAsync(files.Source, importer);
        importer.ShaderCount = 1;
        Assert.Equal(first[0], Assert.Single(await project.ImportAsync(files.Source, importer)));
        Assert.False(File.Exists(files.Path("shader1.shader.asset")));
        Assert.False(project.Manifest.Assets.ContainsKey(first[1]));
        Assert.Equal(first[1], SourceMeta.Load(files.Source).ElementIds["shader/1"]);
        importer.ShaderCount = 2;
        Assert.Equal(first, await project.ImportAsync(files.Source, importer));
    }

    [Fact]
    public async Task PathConflictDoesNotReplaceAnotherSourcesOutput()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter();
        Guid first = Assert.Single(await project.ImportAsync(files.Source, importer));
        byte[] previous = File.ReadAllBytes(files.Path("shader0.shader.asset"));
        string second = files.Path("other.input");
        File.WriteAllText(second, "another source");
        await Assert.ThrowsAsync<InvalidDataException>(() => project.ImportAsync(second, importer).AsTask());
        Assert.Equal(previous, File.ReadAllBytes(files.Path("shader0.shader.asset")));
        Assert.Equal(first, Assert.Single(project.List()).AssetGuid);
        Assert.False(File.Exists(SourceMeta.GetMetaPath(second)));
        files.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task PersistentSettingsAndPerCallOverridesUseSeparateScopes()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter();
        var metadata = new SourceMeta
        {
            SourceGuid = SourceGuid.New(), Importers = [new(importer.Id, JsonSerializer.SerializeToElement(2, ImportTestJsonContext.Default.Int32))],
        };
        SourceMeta.Save(files.Source, metadata);
        await project.ImportAsync(files.Source, importer);
        using (ShaderAsset shader = await ShaderAsset.OpenAsync(files.Path("shader0.shader.asset"))) Assert.Equal("source:2", shader.Name);
        await project.ImportAsync(files.Source, importer, new AssetImportSettings<int>(9));
        using (ShaderAsset shader = await ShaderAsset.OpenAsync(files.Path("shader0.shader.asset"))) Assert.Equal("source:9", shader.Name);
        Assert.Equal(2, SourceMeta.Load(files.Source).GetSettings(importer.Id, 0, ImportTestJsonContext.Default.Int32));
        await project.ImportAsync(files.Source, importer);
        using (ShaderAsset shader = await ShaderAsset.OpenAsync(files.Path("shader0.shader.asset"))) Assert.Equal("source:2", shader.Name);
        Assert.Equal(3, importer.Reads);
        using JsonDocument settings = JsonDocument.Parse(File.ReadAllBytes(SourceMeta.GetMetaPath(files.Source)));
        Assert.True(settings.RootElement.TryGetProperty("importers", out _));
        Assert.False(settings.RootElement.TryGetProperty("input_hash", out _));
        using JsonDocument build = JsonDocument.Parse(File.ReadAllBytes(SourceMeta.GetBuildPath(files.Source)));
        Assert.True(build.RootElement.TryGetProperty("input_hash", out _));
        Assert.False(build.RootElement.TryGetProperty("importers", out _));
    }

    [Fact]
    public async Task CatalogIsAuthoritativeAndOldStorageCannotSilentlyChangeVersions()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter { ShaderCount = 2 };
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, importer);
        LooseAssetStorage previous = project.CreateStorage();
        Assert.True(previous.TryFind(ids[0], out AssetEntry selected));
        await using var oldBytes = await previous.OpenAsync(selected);
        File.WriteAllText(files.Source, "new content");
        await project.ImportAsync(files.Source, importer);
        await Assert.ThrowsAsync<IOException>(async () => { await using var bytes = await previous.OpenAsync(selected); });
        Assert.Equal(selected.Version, await BinaryDocument.ReadVersionAsync(oldBytes));
        Assert.True(previous.TryFind(ids[0], out AssetEntry updated));
        Assert.NotEqual(selected.Version, updated.Version);
        await using (var bytes = await previous.OpenAsync(updated))
            Assert.Equal(updated.Version, await BinaryDocument.ReadVersionAsync(bytes));
        File.WriteAllText(System.IO.Path.Combine(files.Catalog, "asset_index.json"), "An obsolete file must never participate in catalog reads.");
        AssetManifest catalog = AssetManifest.Load(files.Catalog);
        var current = new LooseAssetStorage(files.Root, catalog);
        foreach (Guid id in ids)
        {
            Assert.True(current.TryFind(id, out AssetEntry entry));
            await using var bytes = await current.OpenAsync(entry);
            Assert.Equal(entry.Version, await BinaryDocument.ReadVersionAsync(bytes));
        }
    }

    [Fact]
    public async Task DeleteSourceRemovesEveryOutputCompanionAndDirectoryEntry()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter { ShaderCount = 2 };
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, importer);
        await project.DeleteSourceAsync(files.Source);
        Assert.False(File.Exists(files.Source));
        Assert.False(File.Exists(SourceMeta.GetMetaPath(files.Source)));
        Assert.False(File.Exists(SourceMeta.GetBuildPath(files.Source)));
        Assert.False(File.Exists(files.Path("shader0.shader.asset")));
        Assert.False(File.Exists(files.Path("shader1.shader.asset")));
        Assert.Empty(project.List());
        Assert.Empty(project.Manifest.Sources);
        Assert.Empty(AssetManifest.Load(files.Catalog).Assets);
        Assert.All(ids, id => Assert.False(project.Manifest.Assets.ContainsKey(id)));
        files.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task DeleteImportedAssetKeepsItsSourceIdentityForReimport()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter { ShaderCount = 2 };
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, importer);
        await project.DeleteAssetAsync(ids[0]);
        Assert.True(File.Exists(files.Source));
        Assert.False(File.Exists(files.Path("shader0.shader.asset")));
        Assert.True(File.Exists(files.Path("shader1.shader.asset")));
        SourceMeta metadata = SourceMeta.Load(files.Source);
        Assert.Equal(ids[0], metadata.ElementIds["shader/0"]);
        Assert.DoesNotContain(metadata.Outputs, output => output.AssetGuid == ids[0]);
        Assert.Equal(ids, await project.ImportAsync(files.Source, importer));
    }

    [Fact]
    public async Task DeleteRefusesOutsideReferencesUnlessCallerExplicitlyAllowsDanglingReferences()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter { ShaderCount = 2 };
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, importer);
        using var application = new ApplicationSettingsAsset();
        application.GetBlock<ApplicationSettingsBlock>().UiShader = new(ids[0]);
        project.RegisterAsset(await application.SaveAsync(files.Path("referencer.runtime.asset")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => project.DeleteSourceAsync(files.Source).AsTask());
        Assert.True(File.Exists(files.Source));
        await project.DeleteSourceAsync(files.Source, allowDanglingReferences: true);
        Assert.True(File.Exists(files.Path("referencer.runtime.asset")));
        Assert.Equal(application.Id, Assert.Single(project.List()).AssetGuid);
        Assert.Contains(ids[0], project.GetDependencies(application.Id));
    }

    [Fact]
    public async Task DeleteCannotFollowAMetadataOutputIntoAnotherSourcesAsset()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter();
        await project.ImportAsync(files.Source, importer);
        using var foreign = new ShaderAsset { Name = "independent" };
        string foreignPath = files.Path("foreign.shader.asset");
        project.RegisterAsset(await foreign.SaveAsync(foreignPath));
        SourceMeta metadata = SourceMeta.Load(files.Source);
        metadata.Outputs.Add(new SourceOutput
        {
            AssetGuid = foreign.Id, SubAssetKey = "foreign", AssetPath = foreignPath, InputHash = "", Dependencies = [],
        });
        SourceMeta.Save(files.Source, metadata);
        byte[] bytes = File.ReadAllBytes(foreignPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => project.DeleteSourceAsync(files.Source).AsTask());
        Assert.Equal(bytes, File.ReadAllBytes(foreignPath));
        Assert.True(File.Exists(files.Source));
        Assert.Equal(2, project.List().Count);
    }

    [Fact]
    public async Task FailedOrCanceledDeletePreservesTheLastSuccessfulGroup()
    {
        using var files = new TestFiles();
        var project = new AssetProject(files.Root, []);
        var importer = new SourceImporter { ShaderCount = 2 };
        IReadOnlyList<Guid> ids = await project.ImportAsync(files.Source, importer);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => project.DeleteSourceAsync(files.Source, cancellationToken: cancellation.Token).AsTask());
        Assert.True(File.Exists(files.Source));
        string catalogPath = System.IO.Path.Combine(files.Catalog, AssetManifest.CatalogFileName);
        byte[] catalogBytes = File.ReadAllBytes(catalogPath);
        File.Delete(catalogPath);
        Directory.CreateDirectory(catalogPath);
        Dictionary<string, byte[]> before = files.SavedFiles();
        await Assert.ThrowsAnyAsync<IOException>(() => project.DeleteSourceAsync(files.Source).AsTask());
        files.AssertSavedFiles(before);
        Assert.True(File.Exists(files.Source));
        Assert.Equal(ids.Order(), project.List().Select(static entry => entry.AssetGuid).Order());
        Directory.Delete(catalogPath);
        File.WriteAllBytes(catalogPath, catalogBytes);
        Assert.Equal(ids.Order(), AssetManifest.Load(files.Catalog).Assets.Keys.Order());
        files.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ProjectEditsRejectOutsideOutputsAndCompanionsBeforeCreatingTemporaryFiles()
    {
        string boundary = System.IO.Path.Combine(SomeEngine.Tests.TestProjectPaths.ProjectRoot(), "obj", "asset-write-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(boundary);
        try
        {
            using var files = new TestFiles(System.IO.Path.Combine(boundary, "project"));
            string outside = System.IO.Path.Combine(boundary, "outside.shader.asset");
            byte[] previous = [10, 20, 30];
            File.WriteAllBytes(outside, previous);
            var project = new AssetProject(files.Root, []);
            await Assert.ThrowsAsync<InvalidDataException>(() => project.ImportAsync(files.Source, new OutsideOutputImporter(outside)).AsTask());
            Assert.Equal(previous, File.ReadAllBytes(outside));
            Assert.False(File.Exists(SourceMeta.GetMetaPath(files.Source)));
            SourceMeta.GetOrCreate(files.Source);
            await Assert.ThrowsAsync<InvalidDataException>(() => project.MoveAsync(files.Source, System.IO.Path.Combine(boundary, "outside.input")).AsTask());
            await Assert.ThrowsAsync<InvalidDataException>(() => project.CopySourceAsync(files.Source, System.IO.Path.Combine(boundary, "copy.input")).AsTask());
            using var asset = new ShaderAsset();
            await Assert.ThrowsAsync<InvalidDataException>(() => project.CopyAssetAsync(asset, outside).AsTask());
            string externalSource = System.IO.Path.Combine(boundary, "external.input");
            File.WriteAllText(externalSource, "external source");
            var reader = new SourceImporter();
            await Assert.ThrowsAsync<InvalidDataException>(() => project.ImportAsync(externalSource, reader).AsTask());
            Assert.Equal(0, reader.Reads);
            Assert.False(File.Exists(SourceMeta.GetMetaPath(externalSource)));
            SourceMeta metadata = SourceMeta.Load(files.Source);
            metadata.Outputs.Add(new SourceOutput { AssetGuid = Guid.NewGuid(), SubAssetKey = "outside", AssetPath = outside, InputHash = "", Dependencies = [] });
            SourceMeta.Save(files.Source, metadata);
            await Assert.ThrowsAsync<InvalidDataException>(() => project.DeleteSourceAsync(files.Source).AsTask());
            Assert.Equal(previous, File.ReadAllBytes(outside));
            Assert.True(File.Exists(files.Source));
            Assert.DoesNotContain(Directory.GetFiles(boundary, "*", SearchOption.AllDirectories),
                path => System.IO.Path.GetFileName(path).Contains(".tmp.", StringComparison.Ordinal));
        }
        finally { Directory.Delete(boundary, recursive: true); }
    }

    private sealed class OutsideOutputImporter(string outputPath) : IAssetSourceImporter<string>
    {
        public Guid Id => new("3d040d83-953c-42a8-9785-6878a353a859");
        public ValueTask<string> ReadAsync(string path, CancellationToken token) => ValueTask.FromResult(File.ReadAllText(path));
        public async ValueTask ImportAsync(string source, ImportContext context, CancellationToken token)
        {
            context.ConfigureAssets<ShaderPart, ShaderAsset>(new OutsideShaders(outputPath));
            context.EnableImporter<ShaderPart, ShaderAsset>(new AssetImporter<ShaderPart, int>(Id,
                [ShaderBlock.BlockTypeId, ShaderSourceBlock.BlockTypeId], new ShaderConversion(), 0));
            await context.GetAssetAsync<ShaderPart, ShaderAsset>(new(0, source), token);
        }
    }

    private sealed class SourceImporter : IAssetSourceImporter<string, int>
    {
        public IReadOnlyList<string> SourceExtensions => [".input"];
        public Guid Id => new("e2eaad2c-7ee5-4bf3-85a2-6314b61d8c9c");
        public int Reads { get; private set; }
        public int ShaderCount { get; set; } = 1;
        public bool IncludeMesh { get; set; }
        public int FailAfterSave { get; set; } = -1;
        public CancellationTokenSource? CancelAfterSave { get; set; }
        public async ValueTask<string> ReadAsync(string sourcePath, CancellationToken cancellationToken)
        { Reads++; return await File.ReadAllTextAsync(sourcePath, cancellationToken); }
        public ValueTask<ImportInputs?> GetInputsAsync(string projectRoot, string sourcePath, SourceMeta metadata,
            AssetImportSettings<int>? overrides, CancellationToken cancellationToken)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{File.ReadAllText(sourcePath)}|{ShaderCount}|{IncludeMesh}|{overrides?.Value}")));
            return ValueTask.FromResult<ImportInputs?>(new ImportInputs { InputHash = hash, ImporterVersion = 1, Dependencies = [] });
        }
        public async ValueTask ImportAsync(string source, ImportContext context, AssetImportSettings<int>? overrides, CancellationToken token)
        {
            int settings = context.GetSettings(Id, 0, ImportTestJsonContext.Default.Int32, overrides);
            context.ConfigureAssets<ShaderPart, ShaderAsset>(new ShaderSources(this, settings));
            context.EnableImporter<ShaderPart, ShaderAsset>(new AssetImporter<ShaderPart, int>(Id,
                [ShaderBlock.BlockTypeId, ShaderSourceBlock.BlockTypeId], new ShaderConversion(), settings));
            if (IncludeMesh)
            {
                context.ConfigureAssets<string, MeshAsset>(new MeshSources());
                context.EnableImporter<string, MeshAsset>(new AssetImporter<string, int>(
                    new Guid("c698a681-8c3a-4e20-965e-f8d321f6bda9"),
                    [MeshBlock.BlockTypeId, ClusterGeometryBlock.BlockTypeId, ClusterVertexAttributesBlock.BlockTypeId, MeshMaterialsBlock.BlockTypeId], new MeshConversion(), 0));
            }
            for (int index = 0; index < ShaderCount; index++)
                await context.GetAssetAsync<ShaderPart, ShaderAsset>(new(index, source), token);
            if (IncludeMesh) await context.GetAssetAsync<string, MeshAsset>(source, token);
        }
    }

    private sealed record ShaderPart(int Index, string Text);
    private sealed class OutsideShaders(string path) : AssetSource<ShaderPart, ShaderAsset>
    {
        public override string GetElementKey(ShaderPart source) => "outside";
        public override ShaderAsset CreateAsset(ShaderPart source, Guid id) => new(id);
        public override string GetOutputPath(ShaderPart source, Guid id) => path;
    }
    private sealed class ShaderSources(SourceImporter importer, int settings) : AssetSource<ShaderPart, ShaderAsset>
    {
        private int _saved;
        public override string GetElementKey(ShaderPart source) => $"shader/{source.Index}";
        public override ShaderAsset CreateAsset(ShaderPart source, Guid id) => new(id) { Name = $"{source.Text}:{settings}" };
        public override string GetOutputPath(ShaderPart source, Guid id) => $"shader{source.Index}.shader.asset";
        public override async ValueTask<AssetEntry> SaveAsync(ShaderAsset asset, string path, CancellationToken cancellationToken)
        {
            AssetEntry entry = await asset.SaveAsync(path, cancellationToken);
            if (_saved++ == importer.FailAfterSave) throw new InvalidDataException("Import failed after saving an output.");
            importer.CancelAfterSave?.Cancel();
            return entry;
        }
    }
    private sealed class MeshSources : AssetSource<string, MeshAsset>
    {
        public override string GetElementKey(string source) => "mesh/0";
        public override MeshAsset CreateAsset(string source, Guid id) => new(id);
        public override string GetOutputPath(string source, Guid id) => "mesh.mesh.asset";
    }
    private sealed class ShaderConversion : IAssetImporter<ShaderPart, int>
    {
        public ValueTask ImportAsync(ShaderPart source, int settings, ImportContext context, ImportOutput output, CancellationToken token)
        {
            output.SetBlock(new ShaderSourceBlock());
            output.SetBlock(new ShaderBlock());
            return ValueTask.CompletedTask;
        }
    }
    private sealed class MeshConversion : IAssetImporter<string, int>
    {
        public ValueTask ImportAsync(string source, int settings, ImportContext context, ImportOutput output, CancellationToken token)
        {
            var (positions, indices, attributes) = PrimitiveMeshGenerator.CreateIcoSphere(0);
            using MeshAsset mesh = ClusterBuilder.ProcessRaw(positions, attributes, indices, [new MeshMaterialSlot(Guid.Empty)], source);
            output.SetBlock(mesh.GetBlock<MeshBlock>().Duplicate());
            output.SetBlock(mesh.GetBlock<ClusterGeometryBlock>().Duplicate());
            output.SetBlock(mesh.GetBlock<ClusterVertexAttributesBlock>().Duplicate());
            output.SetBlock(mesh.GetBlock<MeshMaterialsBlock>().Duplicate());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestFiles : IDisposable
    {
        public string Root { get; }
        public string Source => Path("source.input");
        public string Catalog => Path("Library/AssetManifest");
        public TestFiles(string? root = null)
        {
            Root = root ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SomeEngine-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(Source, "source");
        }
        public string Path(string relative) => System.IO.Path.Combine(Root, relative);
        public Dictionary<string, byte[]> SavedFiles() => Directory.GetFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => path != Source).ToDictionary(path => path, File.ReadAllBytes);
        public void AssertSavedFiles(Dictionary<string, byte[]> expected)
        { foreach ((string path, byte[] bytes) in expected) Assert.Equal(bytes, File.ReadAllBytes(path)); }
        public void AssertNoTemporaryFiles() => Assert.DoesNotContain(Directory.GetFiles(Root, "*", SearchOption.AllDirectories),
            path => System.IO.Path.GetFileName(path).Contains(".tmp.", StringComparison.Ordinal));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

[JsonSerializable(typeof(int))]
internal sealed partial class ImportTestJsonContext : JsonSerializerContext;
