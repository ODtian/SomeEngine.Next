using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests;

public sealed partial class AssetFileOperationsTests
{
    [Fact]
    public async Task DiscoveryFindsUnknownBinaryIdentityAndIndependentShaderWithoutCompilingSlang()
    {
        using var directory = new TestDirectory();
        Guid unknownId = Guid.NewGuid();
        await WriteUnknownAsync(directory.File("unknown.extension.asset"), unknownId);
        using var shader = new ShaderAsset { Name = "editable shader" };
        await shader.SaveSourceAsync(directory.File("independent.shader"));
        File.WriteAllText(directory.File("independent.slang"), "This is not valid shader source and must not be compiled.");
        string source = directory.File("native.model");
        File.WriteAllText(source, "source");
        SourceMeta metadata = SourceMeta.GetOrCreate(source);
        Directory.CreateDirectory(directory.File("obj"));
        File.WriteAllText(directory.File("obj/cache.asset"), "not an asset");
        var project = new AssetProject(directory.Root, []);
        IReadOnlyList<AssetDiscoveredFile> found = await project.DiscoverAsync();
        Assert.Equal(3, found.Count);
        AssetDiscoveredFile unknown = Assert.Single(found, item => item.Kind == AssetFileKind.BinaryAsset);
        Assert.Equal(unknownId, unknown.Asset!.Value.AssetGuid);
        Assert.Equal("third-party custom content", unknown.Asset.Value.AssetType);
        Assert.Equal(metadata.SourceGuid, Assert.Single(found, item => item.Kind == AssetFileKind.ImportedSource).SourceGuid);
        Assert.Equal(shader.Id, Assert.Single(found, item => item.Kind == AssetFileKind.NativeSource).Asset!.Value.AssetGuid);
        Assert.False(File.Exists(directory.File("independent.shader.asset")));
        await project.RebuildAsync();
        Assert.Equal(unknownId, project.Resolve("unknown.extension.asset"));
        Assert.Contains(unknownId, AssetManifest.Load(directory.File("Library/AssetManifest")).Assets.Keys);
        Assert.Equal(shader.Id, project.Resolve("independent.shader"));
        Assert.True(project.Manifest.TryGetAsset(shader.Id, out AssetEntry unbuilt));
        Assert.Equal("independent.shader.asset", unbuilt.Path);
        Assert.Equal(Guid.Empty, unbuilt.Version);
        Assert.Equal("independent.shader", unbuilt.DefinitionPath);
    }

    [Fact]
    public async Task RebuildRejectsDuplicateIdentityAndKeepsThePreviousDirectory()
    {
        using var directory = new TestDirectory();
        Guid id = Guid.NewGuid();
        await WriteUnknownAsync(directory.File("first.asset"), id);
        var project = new AssetProject(directory.Root, []);
        await project.RebuildAsync();
        byte[] previous = File.ReadAllBytes(directory.File("Library/AssetManifest/catalog.json"));
        File.Copy(directory.File("first.asset"), directory.File("second.asset"));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => project.RebuildAsync().AsTask());

        Assert.Equal(previous, File.ReadAllBytes(directory.File("Library/AssetManifest/catalog.json")));
        Assert.Equal(id, project.Resolve("first.asset"));
    }

    [Fact]
    public async Task MoveRenamesSourceAndItsCompleteFilenameCompanionsTogether()
    {
        using var directory = new TestDirectory();
        string original = directory.File("part.model.gltf");
        File.WriteAllText(original, "source bytes");
        SourceMeta before = SourceMeta.GetOrCreate(original);
        Guid element = Guid.NewGuid();
        before.ElementIds["mesh/id"] = element;
        before.InputHash = "built input";
        SourceMeta.Save(original, before);
        var project = new AssetProject(directory.Root, []);
        await project.RebuildAsync();
        string destination = directory.File("renamed.model.gltf");
        await project.MoveAsync(original, destination);
        Assert.False(File.Exists(original));
        Assert.False(File.Exists(SourceMeta.GetMetaPath(original)));
        Assert.False(File.Exists(SourceMeta.GetBuildPath(original)));
        Assert.Equal("source bytes", File.ReadAllText(destination));
        SourceMeta after = SourceMeta.Load(destination);
        Assert.Equal(before.SourceGuid, after.SourceGuid);
        Assert.Equal(element, after.ElementIds["mesh/id"]);
        Assert.True(project.Manifest.TrySourceGuid("renamed.model.gltf", out SourceGuid sourceId));
        Assert.Equal(before.SourceGuid, sourceId);
        directory.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task SourceCopyGetsNewIdentitiesAndNoPreviousBuildResults()
    {
        using var directory = new TestDirectory();
        string original = directory.File("source.gltf");
        File.WriteAllText(original, "source bytes");
        SourceMeta before = SourceMeta.GetOrCreate(original);
        before.ElementIds["mesh/id"] = Guid.NewGuid();
        before.ImporterVersion = 31;
        before.InputHash = "previous build";
        SourceMeta.Save(original, before);
        var project = new AssetProject(directory.Root, []);
        SourceGuid copied = await project.CopySourceAsync(original, "copy.gltf");
        SourceMeta after = SourceMeta.Load(directory.File("copy.gltf"));
        Assert.Equal(copied, after.SourceGuid);
        Assert.NotEqual(before.SourceGuid, copied);
        Assert.NotEqual(before.ElementIds["mesh/id"], after.ElementIds["mesh/id"]);
        Assert.Empty(after.Outputs);
        Assert.Empty(after.InputHash);
        Assert.Equal(0u, after.ImporterVersion);
        Assert.Equal(before.SourceGuid, SourceMeta.Load(original).SourceGuid);
        directory.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task SourceCopyRejectsAnotherDirectoryBeforeChangingRelativeInputs()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.File("original"));
        Directory.CreateDirectory(directory.File("other"));
        string source = directory.File("original/source.gltf");
        string destination = directory.File("other/copy.gltf");
        File.WriteAllText(source, """{"asset":{"version":"2.0"},"buffers":[{"uri":"geometry.bin","byteLength":4}]}""");
        File.WriteAllBytes(directory.File("original/geometry.bin"), [1, 2, 3, 4]);
        File.WriteAllBytes(directory.File("other/geometry.bin"), [5, 6, 7, 8]);
        SourceMeta.GetOrCreate(source);
        var project = new AssetProject(directory.Root, []);
        await project.RebuildAsync();
        byte[] sourceBytes = File.ReadAllBytes(source);
        byte[] metadataBytes = File.ReadAllBytes(SourceMeta.GetMetaPath(source));
        string catalog = directory.File("Library/AssetManifest/catalog.json");
        byte[] catalogBytes = File.ReadAllBytes(catalog);

        await Assert.ThrowsAsync<NotSupportedException>(() => project.CopySourceAsync(source, destination).AsTask());

        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        Assert.Equal(metadataBytes, File.ReadAllBytes(SourceMeta.GetMetaPath(source)));
        Assert.Equal(catalogBytes, File.ReadAllBytes(catalog));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(directory.File("original/geometry.bin")));
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, File.ReadAllBytes(directory.File("other/geometry.bin")));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(SourceMeta.GetMetaPath(destination)));
        Assert.False(File.Exists(SourceMeta.GetBuildPath(destination)));
        Assert.False(project.Manifest.TrySourceGuid("other/copy.gltf", out _));
        directory.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task MoveConflictLeavesSourceCompanionsAndDirectoryUntouched()
    {
        using var directory = new TestDirectory();
        string source = directory.File("source.gltf");
        File.WriteAllText(source, "source bytes");
        SourceMeta.GetOrCreate(source);
        string destination = directory.File("target.gltf");
        File.WriteAllText(SourceMeta.GetMetaPath(destination), "existing companion");
        byte[] previous = File.ReadAllBytes(SourceMeta.GetMetaPath(source));
        var project = new AssetProject(directory.Root, []);
        await Assert.ThrowsAsync<IOException>(() => project.MoveAsync(source, destination).AsTask());
        Assert.True(File.Exists(source));
        Assert.Equal(previous, File.ReadAllBytes(SourceMeta.GetMetaPath(source)));
        Assert.Equal("existing companion", File.ReadAllText(SourceMeta.GetMetaPath(destination)));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task TypedAssetCopyUsesNewIdentityAndMoveKeepsIt()
    {
        using var directory = new TestDirectory();
        using var source = new ShaderAsset { Name = "copy this" };
        var project = new AssetProject(directory.Root, []);
        Guid copyId = await project.CopyAssetAsync(source, "first.shader.asset",
            static (asset, path, token) => asset.SaveAsync(path, token));
        Assert.NotEqual(source.Id, copyId);
        await project.MoveAsync("first.shader.asset", "second.shader.asset");
        Assert.Null(project.Resolve("first.shader.asset"));
        Assert.Equal(copyId, project.Resolve("second.shader.asset"));
        using ShaderAsset reopened = await ShaderAsset.OpenAsync(directory.File("second.shader.asset"));
        Assert.Equal(copyId, reopened.Id);
        Assert.Equal(source.Name, reopened.Name);
        directory.AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedSourceMoveKeepsExternalTextureBytesAndOtherBlockReferences(bool secondBlock)
    {
        using var directory = new TestDirectory();
        byte[] pixels = [10, 20, 30, 40];
        File.WriteAllBytes(directory.File("pixels.bin"), pixels);
        using var texture = new TextureAsset { Name = "external texture" };
        TextureBlock data = texture.GetBlock<TextureBlock>();
        data.Dimension = TextureDimension.Texture2D;
        data.Width = 4; data.Height = 1; data.Depth = 1;
        data.MipLevelCount = 1; data.ArrayLayerCount = 1;
        data.Format = Format.R8UNorm; data.SampledFormat = Format.R8UNorm;
        data.SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D;
        ulong key = TextureTiles.ChunkKey(0, 0, 0, 0, 0, 0);
        data.MipTiles.Add(new TextureMipTile { Width = 4, Height = 1, RowPitch = 4, SlicePitch = 4, ChunkKey = key, DecodedLength = 4 });
        data.SetBytes(key, pixels);
        Guid related = Guid.NewGuid();
        if (secondBlock)
        {
            using var notes = new FileOperationNotesBlock { RelatedAsset = new(related), RelativeFile = "notes.txt" };
            File.WriteAllText(directory.File("notes.txt"), "extension metadata file");
            texture.SetBlock(notes);
        }
        string sourcePath = directory.File("original.texture");
        await AssetText.SaveAsync(sourcePath, texture, (writer, asset) =>
        {
            writer.WriteBlock(asset.GetBlock<TextureBlock>(), new Dictionary<ulong, string> { [key] = "pixels.bin" });
            if (secondBlock) writer.WriteBlock(asset.GetBlock<FileOperationNotesBlock>());
        });
        var project = new AssetProject(directory.Root, []);
        string destination = directory.File("moved/texture.texture");
        await Assert.ThrowsAsync<NotSupportedException>(() => project.MoveAsync(sourcePath, destination).AsTask());
        var editors = new List<AssetBlockPathEditor>
        {
            AssetBlockPathEditor.Create<TextureBlock>(static (_, _, _) => { }),
        };
        if (secondBlock)
            editors.Add(AssetBlockPathEditor.Create<FileOperationNotesBlock>(static (block, oldDirectory, newDirectory) =>
                block.RelativeFile = Path.GetRelativePath(newDirectory, Path.Combine(oldDirectory, block.RelativeFile))));
        await project.MoveAsync(sourcePath, destination, editors);
        Assert.False(File.Exists(sourcePath));
        Assert.Equal(pixels, File.ReadAllBytes(directory.File("pixels.bin")));
        using AssetText.SourceReader moved = await AssetText.OpenAsync(destination);
        Assert.Equal(texture.Id, moved.Description.AssetGuid);
        using var reopened = new TextureBlock();
        await moved.ReadBlockIntoAsync(reopened);
        var bytes = await reopened.ReadBytesAsync(key);
        var read = bytes;
        Assert.Equal(pixels, read.ToArray());
        if (secondBlock)
        {
            using var notes = new FileOperationNotesBlock();
            await moved.ReadBlockIntoAsync(notes);
            Assert.Equal(new AssetId<TextureAsset>(related), notes.RelatedAsset);
            Assert.Equal("extension metadata file", File.ReadAllText(Path.Combine(Path.GetDirectoryName(destination)!, notes.RelativeFile)));
        }
        directory.AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task DuplicateSourceIdentityWithoutOutputsCannotReplaceThePreviousDirectory()
    {
        using var directory = new TestDirectory();
        string first = directory.File("first.gltf"), second = directory.File("second.gltf");
        File.WriteAllText(first, "source");
        SourceMeta meta = SourceMeta.GetOrCreate(first);
        var project = new AssetProject(directory.Root, []);
        await project.RebuildAsync();
        byte[] previous = File.ReadAllBytes(directory.File("Library/AssetManifest/catalog.json"));
        File.WriteAllText(second, "source");
        SourceMeta.Save(second, meta);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => project.RebuildAsync().AsTask());

        Assert.Equal(previous, File.ReadAllBytes(directory.File("Library/AssetManifest/catalog.json")));
    }

    [Fact]
    public void ManifestPathsUseTheOperatingSystemsPathComparison()
    {
        var manifest = new AssetManifest();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        manifest.AddAsset(first, "first", "Content/Texture.asset", "custom type", 1);
        Assert.True(manifest.TryAssetPath("Content/unused/.././Texture.asset", out AssetEntry alias));
        Assert.Equal(first, alias.AssetGuid);
        Assert.Throws<InvalidOperationException>(() => manifest.AddAsset(second, "second", "Content/../Content/Texture.asset", "custom type", 1));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(manifest.TryAssetPath("content/texture.asset", out AssetEntry entry));
            Assert.Equal(first, entry.AssetGuid);
            Assert.Throws<InvalidOperationException>(() => manifest.AddAsset(second, "second", "CONTENT/TEXTURE.ASSET", "custom type", 1));
        }
        else
        {
            Assert.False(manifest.TryAssetPath("content/texture.asset", out _));
            manifest.AddAsset(second, "second", "content/texture.asset", "custom type", 1);
            Assert.Equal(2, manifest.Assets.Count);
        }
    }

    private static async Task WriteUnknownAsync(string path, Guid id)
    {
        var document = BinaryDocumentWriter.Create(new UnknownContent(id, "external", "third-party custom content", 123));
        await using var stream = File.Create(path);
        await document.WriteAsync(stream);
    }

    [BinarySerializable, BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(Value))]
    internal readonly partial record struct UnknownContent(Guid Id, string Name, string AssetType, int Value);

    private sealed class TestDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SomeEngine-files-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Root);
        public string File(string relative) => Path.Combine(Root, relative);
        public void AssertNoTemporaryFiles() => Assert.DoesNotContain(Directory.GetFiles(Root, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains(".tmp.", StringComparison.Ordinal));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

[AssetBlock("SomeEngine.Assets.Tests.FileOperationNotesBlock"), BinarySerializable, TextSerializable]
[BinaryFields(nameof(RelatedAsset), nameof(RelativeFile))]
internal sealed partial class FileOperationNotesBlock : AssetBlock
{
    public partial AssetId<TextureAsset> RelatedAsset { get; set; }
    public partial string RelativeFile { get; set; }
}
