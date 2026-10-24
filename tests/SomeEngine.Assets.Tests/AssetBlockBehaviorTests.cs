using System.Collections.Immutable;
using SomeEngine.Assets.Importing;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Tests;

public sealed class AssetBlockBehaviorTests
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ProcessOnlyBlocksDoNotAddReferencesToSavedContent()
    {
        using var files = new BlockFiles();
        Guid materialId = Guid.NewGuid();
        Guid sessionReference = Guid.NewGuid();
        using var asset = CreateContent();
        using var materials = new SomeEngine.Assets.Schema.MeshMaterialsBlock
        {
            Materials = [new(materialId)],
        };
        using var session = new BlockTestSessionData(sessionReference);
        asset.SetBlock(materials);
        asset.SetBlock(session);
        using var branch = asset.Duplicate();

        AssetEntry saved = await asset.SaveAsync(files.First);
        Assert.Equal(new[] { materialId }, saved.Dependencies);
        Assert.Equal(sessionReference, asset.GetBlock<BlockTestSessionData>().Reference.Value);
        Assert.Equal(sessionReference, branch.GetBlock<BlockTestSessionData>().Reference.Value);
        Assert.False(session.Disposed);

        using AssetFile file = await AssetFile.OpenAsync(files.First, BlockTestAsset.WireType);
        Assert.Equal(saved.Dependencies, file.Describe().Dependencies);
        using var reopened = await BlockTestAsset.OpenAsync(files.First);
        Assert.False(reopened.ContainsBlock<BlockTestSessionData>());
        Assert.True(reopened.ContainsBlock<SomeEngine.Assets.Schema.MeshMaterialsBlock>());
        AssetEntry copied = await reopened.SaveAsync(files.Second);
        Assert.Equal(saved.Dependencies, copied.Dependencies);

        BlockTestSessionData attached = asset.GetBlock<BlockTestSessionData>();
        asset.Dispose();
        Assert.True(attached.Disposed);
        Assert.False(branch.GetBlock<BlockTestSessionData>().Disposed);
        Assert.Equal(sessionReference, branch.GetBlock<BlockTestSessionData>().Reference.Value);
    }

    [Fact]
    public async Task DifferentBlocksCanPersistTheSameLocalChunkKey()
    {
        using var files = new BlockFiles();
        using (var asset = CreateContent()) await asset.SaveAsync(files.First);
        using var opened = await BlockTestAsset.OpenAsync(files.First);
        Assert.True(opened.ContainsBlock<BlockTestNumbers>());
        Assert.True(opened.ContainsBlock<BlockTestText>());
        BlockTestNumbers numbers = await opened.GetBlockAsync<BlockTestNumbers>();
        BlockTestText text = await opened.GetBlockAsync<BlockTestText>();
        ReadOnlyMemory<byte> numberBytes = await numbers.ReadBytesAsync(1);
        ReadOnlyMemory<byte> textBytes = await text.ReadBytesAsync(1);
        Assert.Equal(new byte[] { 1, 2, 3 }, Bytes(numberBytes));
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, Bytes(textBytes));
        Assert.Equal(31, numbers.Value);
        Assert.Equal("custom text metadata", text.Text);
    }

    [Fact]
    public async Task ExtensionDefinedTextMetadataUsesItsOwnEncodingDescriptor()
    {
        using var files = new BlockFiles();
        using (var asset = CreateContent()) await asset.SaveAsync(files.First);
        using var opened = await BlockTestAsset.OpenAsync(files.First);
        AssetBlockDescription description = Assert.Single(opened.BlockDescriptions, block => block.BlockTypeId == BlockTestText.BlockTypeId);
        Assert.Equal(BlockTestText.TextEncoding.TypeId, description.EncodingTypeId);
        Assert.NotEqual(description.BlockTypeId, description.EncodingTypeId);
        Assert.Equal(BlockTestText.TextEncoding.SchemaFingerprint, description.SchemaHash);
        Assert.Equal(BlockTestText.TextEncoding.SchemaEpoch, description.Epoch);
        Assert.Equal(BlockTestText.TextEncoding.Compatibility, description.EncodingCompatibility);
        Assert.Equal("custom text metadata", (await opened.GetBlockAsync<BlockTestText>()).Text);
    }

    [Fact]
    public async Task SavingAnUnloadedBlockPreservesItsEncodingAndDoesNotInvokeItsCodec()
    {
        using var files = new BlockFiles();
        using (var asset = CreateContent()) await asset.SaveAsync(files.First);
        using var opened = await BlockTestAsset.OpenAsync(files.First);
        AssetBlockDescription original = Assert.Single(opened.BlockDescriptions, block => block.BlockTypeId == BlockTestText.BlockTypeId);
        opened.Name = "renamed while extension is unopened";
        await opened.SaveAsync(files.Second);
        using var saved = await BlockTestAsset.OpenAsync(files.Second);
        AssetBlockDescription result = Assert.Single(saved.BlockDescriptions, block => block.BlockTypeId == BlockTestText.BlockTypeId);
        Assert.Equal(original.EncodingTypeId, result.EncodingTypeId);
        Assert.Equal(original.SchemaHash, result.SchemaHash);
        Assert.Equal(original.Epoch, result.Epoch);
        Assert.Equal(original.EncodingCompatibility, result.EncodingCompatibility);
        Assert.Equal(original.ChunkKeys.ToArray(), result.ChunkKeys.ToArray());
        Assert.Equal("renamed while extension is unopened", saved.Name);
        Assert.Throws<InvalidOperationException>(() => saved.GetBlock<BlockTestText>());
        BlockTestText text = await saved.GetBlockAsync<BlockTestText>();
        Assert.Equal("custom text metadata", text.Text);
        ReadOnlyMemory<byte> bytes = await text.ReadBytesAsync(1);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, Bytes(bytes));
    }

    [Fact]
    public void ReplacingImmutableBytesKeepsOtherBranchesAndExistingViewsUnchanged()
    {
        using var asset = CreateContent();
        using var duplicate = asset.Duplicate();
        BlockTestNumbers numbers = asset.GetBlock<BlockTestNumbers>();
        BlockTestText text = asset.GetBlock<BlockTestText>();
        ReadOnlyMemory<byte> original = numbers.Chunks[1];
        Assert.True(original.Equals(duplicate.GetBlock<BlockTestNumbers>().Chunks[1]));
        byte[] edited = original.ToArray();
        edited[0] = 44;
        numbers.SetBytes(1, edited);
        duplicate.Name = "independent";
        duplicate.GetBlock<BlockTestText>().SetBytes(1, new byte[] { 55, 8, 7, 6 });
        Assert.Equal("content", asset.Name);
        Assert.Equal("independent", duplicate.Name);
        Assert.Equal(new byte[] { 44, 2, 3 }, numbers.Chunks[1].ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, original.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, duplicate.GetBlock<BlockTestNumbers>().Chunks[1].ToArray());
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, text.Chunks[1].ToArray());
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelTheSharedBlockRead()
    {
        using var files = new BlockFiles();
        using (var asset = CreateContent()) await asset.SaveAsync(files.First);
        var read = new BlockReadControl();
        try
        {
            await using var opened = await files.OpenControlledAsync(read);
            using var cancel = new CancellationTokenSource();
            Task<BlockTestText> first = opened.GetBlockAsync<BlockTestText>(cancel.Token).AsTask();
            await read.Started.Task.WaitAsync(CompletionTimeout);
            Task<BlockTestText> second = opened.GetBlockAsync<BlockTestText>().AsTask();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(CompletionTimeout));
            Assert.False(read.ReadCancellation.IsCancellationRequested);
            Assert.False(second.IsCompleted);
            read.Continue.TrySetResult();
            BlockTestText content = await second.WaitAsync(CompletionTimeout);
            Assert.Equal("custom text metadata", content.Text);
            Assert.Same(content, await opened.GetBlockAsync<BlockTestText>());
            Assert.Equal(1, read.Calls);
        }
        finally { read.Continue.TrySetResult(); }
    }

    [Fact]
    public async Task ClosingTheAssetCancelsTheCodecAndCompletesEveryWaiter()
    {
        using var files = new BlockFiles();
        using (var asset = CreateContent()) await asset.SaveAsync(files.First);
        var read = new BlockReadControl();
        try
        {
            await using var opened = await files.OpenControlledAsync(read);
            Task<BlockTestText> first = opened.GetBlockAsync<BlockTestText>().AsTask();
            await read.Started.Task.WaitAsync(CompletionTimeout);
            Task<BlockTestText> second = opened.GetBlockAsync<BlockTestText>().AsTask();
            await opened.DisposeAsync().AsTask().WaitAsync(CompletionTimeout);
            Assert.True(read.ReadCancellation.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(CompletionTimeout));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(CompletionTimeout));
            Assert.Equal(1, read.Calls);
        }
        finally { read.Continue.TrySetResult(); }
    }

    [Fact]
    public async Task EveryCloseWaitsForTheCodecToFinishItsActualRead()
    {
        using var files = new BlockFiles();
        using (var asset = CreateContent()) await asset.SaveAsync(files.First);
        var read = new BlockReadControl { FinishBeforeCancellation = true };
        try
        {
            await using var opened = await files.OpenControlledAsync(read);
            Task<BlockTestText> acquisition = opened.GetBlockAsync<BlockTestText>().AsTask();
            await read.Started.Task.WaitAsync(CompletionTimeout);
            var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = read.ReadCancellation.Register(() => cancellationObserved.TrySetResult());
            Task firstClose = opened.DisposeAsync().AsTask();
            Task secondClose = opened.DisposeAsync().AsTask();
            await cancellationObserved.Task.WaitAsync(CompletionTimeout);
            Assert.False(firstClose.IsCompleted);
            Assert.False(secondClose.IsCompleted);
            read.Continue.TrySetResult();
            await Task.WhenAll(firstClose, secondClose).WaitAsync(CompletionTimeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        }
        finally { read.Continue.TrySetResult(); }
    }

    [Fact]
    public async Task FailedBlockReadCompletesEveryWaiterAndAllowsRetry()
    {
        using var files = new BlockFiles();
        using (var asset = CreateContent()) await asset.SaveAsync(files.First);
        var read = new BlockReadControl { RejectRead = true };
        try
        {
            await using var opened = await files.OpenControlledAsync(read);
            Task<BlockTestText> first = opened.GetBlockAsync<BlockTestText>().AsTask();
            await read.Started.Task.WaitAsync(CompletionTimeout);
            Task<BlockTestText> second = opened.GetBlockAsync<BlockTestText>().AsTask();
            read.Continue.TrySetResult();
            IOException firstError = await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(CompletionTimeout));
            IOException secondError = await Assert.ThrowsAsync<IOException>(() => second.WaitAsync(CompletionTimeout));

            Assert.Equal(1, read.Calls);
            read.RejectRead = false;
            Assert.Equal("custom text metadata", (await opened.GetBlockAsync<BlockTestText>()).Text);
        }
        finally { read.Continue.TrySetResult(); }
    }

    [Fact]
    public async Task ImportGroupIsFullyVisibleEvenWhenRetiredBlockCleanupThrows()
    {
        await using var context = new ImportContext();
        BlockTestAsset? target = null;
        context.ConfigureAssets<object, BlockTestAsset>(_ => "group", (_, id) =>
        {
            target = CreateContent(id);
            target.GetBlock<BlockTestText>().ThrowOnRelease = true;
            return target;
        }, (_, _) => "group.block-test.asset", static (asset, path, token) => asset.SaveAsync(path, token));
        context.EnableImporter<object, BlockTestAsset>(new AssetImporter<object, int>(
            new Guid("eef1c633-39c2-4556-8f6a-4e31f98e526c"),
            [BlockTestNumbers.BlockTypeId, BlockTestText.BlockTypeId], new GroupImporter(), 72));
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => context.GetAssetAsync<object, BlockTestAsset>(new()).AsTask());
        Assert.IsType<IOException>(Assert.Single(error.InnerExceptions));
        Assert.Equal(72, target!.GetBlock<BlockTestNumbers>().Value);
        Assert.Equal("replacement", target.GetBlock<BlockTestText>().Text);
        Assert.Empty(context.Assets);
    }

    [Fact]
    public async Task RemovingAnUnloadedBlockDoesNotDecodeItAndRemovesItFromTheFile()
    {
        using var files = new BlockFiles();
        using (var original = CreateContent()) await original.SaveAsync(files.First);
        using var asset = await BlockTestAsset.OpenAsync(files.First);
        Assert.Empty(asset.RemoveBlock<BlockTestText>());
        Assert.False(asset.ContainsBlock<BlockTestText>());
        Assert.Throws<KeyNotFoundException>(() => asset.GetBlock<BlockTestText>());
        await asset.SaveAsync(files.Second);
        using var reopened = await BlockTestAsset.OpenAsync(files.Second);
        Assert.False(reopened.ContainsBlock<BlockTestText>());
        Assert.Equal(31, (await reopened.GetBlockAsync<BlockTestNumbers>()).Value);
    }

    [Fact]
    public void RemovingAndReplacingBlocksDoesNotChangeAnIndependentAsset()
    {
        using var asset = CreateContent();
        using var copy = asset.Duplicate();
        Assert.Empty(asset.RemoveBlock<BlockTestNumbers>());
        Assert.False(asset.ContainsBlock<BlockTestNumbers>());
        Assert.Equal(31, copy.GetBlock<BlockTestNumbers>().Value);
        using var replacement = new BlockTestNumbers { Value = 67 };
        Assert.Empty(asset.SetBlock(replacement));
        Assert.Equal(67, asset.GetBlock<BlockTestNumbers>().Value);
        Assert.Equal(31, copy.GetBlock<BlockTestNumbers>().Value);
    }

    private static BlockTestAsset CreateContent(Guid id = default)
    {
        var asset = new BlockTestAsset(id) { Name = "content" };
        try
        {
            using var numbers = new BlockTestNumbers { Value = 31 };
            numbers.SetBytes(1, new byte[] { 1, 2, 3 });
            using var text = new BlockTestText { Text = "custom text metadata" };
            text.SetBytes(1, new byte[] { 9, 8, 7, 6 });
            Assert.Empty(asset.SetBlock(numbers));
            Assert.Empty(asset.SetBlock(text));
            return asset;
        }
        catch { asset.Dispose(); throw; }
    }

    private static byte[] Bytes(ReadOnlyMemory<byte> bytes) => bytes.ToArray();

    private sealed class GroupImporter : IAssetImporter<object, int>
    {
        public ValueTask ImportAsync(object source, int settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
        {
            output.SetBlock(new BlockTestText { Text = "replacement" });
            output.SetBlock(new BlockTestNumbers { Value = settings });
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "asset-block-behavior-" + Guid.NewGuid().ToString("N"));
        public string First => Path.Combine(_directory, "first.block-test.asset");
        public string Second => Path.Combine(_directory, "second.block-test.asset");
        internal async ValueTask<BlockTestAsset> OpenControlledAsync(BlockReadControl read)
        {
            using AssetFile file = await AssetFile.OpenAsync(First, BlockTestAsset.WireType);
            using BlockTestAsset asset = BlockTestAsset.Open(file);
            AssetBlockDescription description = Assert.Single(asset.BlockDescriptions,
                block => block.BlockTypeId == BlockTestText.BlockTypeId);
            BinaryChunkEntry metadata = await file.DescribeChunkAsync(description.MetadataKey);
            var storage = new BlockStorage(file.Describe(), File.ReadAllBytes(First), metadata, read);
            await using var loader = new AssetLoader(storage);
            using AssetFile loaded = await loader.OpenFileAsync(new AssetId<BlockTestAsset>(asset.Id),
                BlockTestAsset.FileType, BlockTestAsset.WireType);
            BlockTestAsset opened = BlockTestAsset.Open(loaded);
            storage.ControlReads = true;
            return opened;
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }
}

[BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
internal sealed partial class BlockTestAsset : Asset
{
    public const string FileType = "SomeEngine.Tests.BlockAsset";
    public static BinaryWireTypeDescriptor WireType => AssetHeader.WireType;
    public new ImmutableArray<AssetBlockDescription> BlockDescriptions { get => base.BlockDescriptions; private set => base.BlockDescriptions = value; }

    public BlockTestAsset() : this(Guid.Empty) { }
    public BlockTestAsset(Guid id = default) : base(id) => AssetType = FileType;
    private BlockTestAsset(BlockTestAsset source) : base(source) { }
    public override BlockTestAsset Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public async ValueTask<AssetEntry> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        using BlockTestAsset copy = Duplicate();
        return await copy.SaveBlockContentAsync(path, ".block-test.asset", cancellationToken);
    }
    public static async ValueTask<BlockTestAsset> OpenAsync(string path)
    {
        using AssetFile file = await AssetFile.OpenAsync(path, WireType);
        var asset = new BlockTestAsset(file.Describe().AssetGuid);
        try { asset.ReadBlockContent(file); asset.AttachFile(file); return asset; }
        catch { asset.Dispose(); throw; }
    }
    internal static BlockTestAsset Open(AssetFile file)
    {
        var asset = new BlockTestAsset(file.Describe().AssetGuid);
        try { asset.ReadBlockContent(file); asset.AttachFile(file); return asset; }
        catch { asset.Dispose(); throw; }
    }

}

[AssetBlock("SomeEngine.Assets.Tests.BlockTestNumbers"), BinarySerializable, BinaryFields(nameof(Value))]
internal sealed partial class BlockTestNumbers : AssetBlock
{
    public partial int Value { get; set; }
}

[AssetBlock("SomeEngine.Tests.SessionData")]
internal sealed partial class BlockTestSessionData : AssetBlock
{
    public AssetId<Asset> Reference { get; }
    public bool Disposed { get; private set; }

    public BlockTestSessionData(Guid reference) => Reference = new(reference);
    private BlockTestSessionData(BlockTestSessionData source, object? syncRoot = null)
        : base(source, syncRoot) => Reference = source.Reference;
    public override AssetBlock Duplicate() => new BlockTestSessionData(this);
    public override AssetBlock Duplicate(object syncRoot) => new BlockTestSessionData(this, syncRoot);
    public override IEnumerable<Guid> References() => [Reference.Value];
    protected override void ReleaseContent() => Disposed = true;
}

internal sealed class BlockReadControl
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken ReadCancellation { get; set; }
    public int Calls;
    public bool RejectRead { get; set; }
    public bool FinishBeforeCancellation { get; init; }
}

/// <summary>A genuine extension codec: UTF-8 metadata, with no BinarySerializable fields.</summary>
[AssetBlock("SomeEngine.Tests.TextData")]
internal sealed partial class BlockTestText : AssetBlock, IAssetBlockWriter
{
    public static BinaryWireTypeDescriptor TextEncoding => new(new("e9a6b638-a773-47bd-a61c-2f644aa8119e"), 0x74212b09, BinaryCompatibility.ExactSchema, 3);
    public bool ThrowOnRelease { get; set; }
    private string _text = "";
    public string Text
    {
        get { lock (SyncRoot) { RequireIdle(); return _text; } }
        set { lock (SyncRoot) { RequireIdle(); _text = value; } }
    }
    public BlockTestText() { }
    private BlockTestText(BlockTestText source, object? syncRoot = null) : base(source, syncRoot)
    { _text = source._text; ThrowOnRelease = source.ThrowOnRelease; }
    public override BlockTestText Duplicate() { lock (SyncRoot) { RequireIdle(); return new(this); } }
    public override BlockTestText Duplicate(object syncRoot) { lock (SyncRoot) { RequireIdle(); return new(this, syncRoot); } }
    public static async ValueTask<BlockTestText> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
    {
        if (source.Description.EncodingTypeId != TextEncoding.TypeId || source.Description.SchemaHash != TextEncoding.SchemaFingerprint)
            throw new InvalidDataException("Unsupported text encoding.");
        ReadOnlyMemory<byte> bytes = await source.ReadMetadataAsync(cancellationToken);
        ReadOnlyMemory<byte> read = bytes;
        var block = new BlockTestText() { Text = System.Text.Encoding.UTF8.GetString(read.ToArray()) };
        block.AttachSource(source);
        return block;
    }
    public async ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken)
    {
        byte[] text = System.Text.Encoding.UTF8.GetBytes(Text);
        writer.WriteMetadata(BlockTypeId, TextEncoding, text);
        await WriteChunksAsync(writer, cancellationToken);
    }
    protected override void ReleaseContent()
    {
        try { if (ThrowOnRelease) throw new IOException("Extension content cleanup failed."); }
        finally { base.ReleaseContent(); }
    }
}

internal sealed class BlockStorage(AssetEntry entry, byte[] bytes, BinaryChunkEntry metadata, BlockReadControl read) : IAssetStorage
{
    internal bool ControlReads { get; set; }
    public bool TryFind(Guid id, out AssetEntry result) { result = entry; return id == entry.AssetGuid; }
    public ValueTask<IRangeSource> OpenAsync(AssetEntry requested, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IRangeSource>(new BlockRange(this, bytes, metadata, read));

    private sealed class BlockRange(BlockStorage storage, byte[] bytes, BinaryChunkEntry metadata, BlockReadControl read) : IRangeSource
    {
        private readonly MemoryRangeSource _bytes = new(bytes);
        public long Length => _bytes.Length;
        public string Generation => _bytes.Generation;
        public bool LeasesAreImmutable => true;
        public bool RetainsResidentBacking => true;
        public async ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
        {
            if (storage.ControlReads && offset < metadata.EndOffset && offset + length > metadata.Offset)
            {
                Interlocked.Increment(ref read.Calls);
                read.ReadCancellation = cancellationToken;
                read.Started.TrySetResult();
                if (read.FinishBeforeCancellation) await read.Continue.Task;
                else await read.Continue.Task.WaitAsync(cancellationToken);
                if (read.RejectRead) throw new IOException("The metadata read failed.");
            }
            return await _bytes.AcquireAsync(offset, length, cancellationToken);
        }
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            using RangeLease range = await AcquireAsync(offset, destination.Length, cancellationToken);
            range.Memory.CopyTo(destination);
        }
        public ValueTask DisposeAsync() => _bytes.DisposeAsync();
    }
}
