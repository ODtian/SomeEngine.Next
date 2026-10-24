using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization;

namespace SomeEngine.Assets.Tests;

public sealed class ImportBlockBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclaredOptionalOutputCanBeReadByAnotherImporter(bool produce)
    {
        await using var context = CreateContext();
        context.EnableImporter<string, BlockTestAsset>(Guid.NewGuid(), [ContextTextBlock.BlockTypeId],
            async (_, imports, output, token) =>
            {
                ContextNumberBlock? number = await imports.TryGetBlockAsync<ContextNumberBlock>(output.Target, token);
                output.SetBlock(new ContextTextBlock(number?.Value.ToString() ?? "absent"));
            });
        context.EnableImporter<string, BlockTestAsset>(Guid.NewGuid(), [ContextNumberBlock.BlockTypeId],
            (_, _, output, _) =>
            {
                if (produce) output.SetBlock(new ContextNumberBlock(42));
                return ValueTask.CompletedTask;
            });

        BlockTestAsset asset = await GetAsset(context, "mesh");
        Assert.Equal(produce ? "42" : "absent", asset.GetBlock<ContextTextBlock>().Text);
        Assert.Equal(produce ? 42 : (int?)null, (await asset.TryGetBlockAsync<ContextNumberBlock>())?.Value);
        Assert.Equal(produce ? 42 : (int?)null, (await context.TryGetBlockAsync<ContextNumberBlock>(asset))?.Value);
        Assert.Null(await context.TryGetBlockAsync<BlockTestNumbers>(asset));
        Assert.Null(await asset.TryGetBlockAsync<BlockTestNumbers>());
        Assert.Same(asset, Assert.Single(context.Assets));
        if (!produce)
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => asset.GetBlockAsync<ContextNumberBlock>().AsTask());
            await Assert.ThrowsAsync<KeyNotFoundException>(() => context.GetBlockAsync<ContextNumberBlock>(asset).AsTask());
        }
    }

    [Fact]
    public async Task OptionalReadPropagatesProducerFailure()
    {
        await using var context = CreateContext();
        Asset? target = null;
        context.EnableImporter<string, BlockTestAsset>(Guid.NewGuid(), [ContextNumberBlock.BlockTypeId],
            (_, _, output, _) =>
            {
                target = output.Target;
                throw new KeyNotFoundException("source element missing");
            });

        await Assert.ThrowsAsync<KeyNotFoundException>(() => GetAsset(context, "mesh").AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            context.TryGetBlockAsync<ContextNumberBlock>(target!).AsTask());
    }

    [Fact]
    public async Task OptionalReadCancellationKeepsOtherReadersUsable()
    {
        await using var context = CreateContext();
        var target = new TaskCompletionSource<Asset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.EnableImporter<string, BlockTestAsset>(Guid.NewGuid(), [ContextNumberBlock.BlockTypeId],
            async (_, _, output, token) =>
            {
                target.SetResult(output.Target);
                await finish.Task.WaitAsync(token);
                output.SetBlock(new ContextNumberBlock(42));
            });
        Task<BlockTestAsset> import = GetAsset(context, "mesh").AsTask();
        Asset asset = await target.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        Task<ContextNumberBlock?> canceled = context.TryGetBlockAsync<ContextNumberBlock>(asset, cancellation.Token).AsTask();
        Task<ContextNumberBlock?> reader = context.TryGetBlockAsync<ContextNumberBlock>(asset).AsTask();
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        }
        finally { finish.TrySetResult(); }

        Assert.Equal(42, (await reader.WaitAsync(TimeSpan.FromSeconds(10)))!.Value);
        Assert.Equal(42, (await import.WaitAsync(TimeSpan.FromSeconds(10))).GetBlock<ContextNumberBlock>().Value);
    }

    [Fact]
    public async Task OptionalReadObservesCancellationAndAssetLifetime()
    {
        using var asset = new BlockTestAsset();
        await using var context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asset.TryGetBlockAsync<ContextNumberBlock>(cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.TryGetBlockAsync<ContextNumberBlock>(asset, cancellation.Token).AsTask());
        asset.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => asset.TryGetBlockAsync<ContextNumberBlock>().AsTask());
    }

    [Fact]
    public async Task ImportOnlyBlockFeedsOtherImportersAndSavesPersistentContent()
    {
        using var files = new ImportFiles();
        var project = new AssetProject(files.Root, []);
        Guid id = Assert.Single(await project.ImportAsync(files.Source, new SourceImporter()));

        using var asset = await BlockTestAsset.OpenAsync(files.Output);
        Assert.Equal(id, asset.Id);
        Assert.Equal(42, (await asset.TryGetBlockAsync<BlockTestNumbers>())!.Value);
        Assert.Equal("21", (await asset.TryGetBlockAsync<ImportTextBlock>())!.Value);
        Assert.Null(await asset.TryGetBlockAsync<ImportNumberBlock>());
        Assert.Null(await asset.TryGetBlockAsync<ContextNumberBlock>());
        Assert.DoesNotContain(asset.BlockDescriptions, block => block.BlockTypeId == ImportNumberBlock.BlockTypeId);
        Assert.Empty(project.Manifest.Assets[id].Dependencies);
        Assert.Empty(AssetManifest.Load(files.CatalogDirectory).Assets[id].Dependencies);
        Assert.Single(Directory.GetFiles(files.Root, "*.asset", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ImportOnlyDeclarationAppliesToAssignedAndCopiedBlocks()
    {
        using var files = new ImportFiles();
        using var asset = new BlockTestAsset();
        using var block = new ImportNumberBlock { Value = 21, Reference = new(Guid.NewGuid()) };
        block.SetBytes(1, new byte[] { 1, 2, 3 });
        asset.SetBlock(block);
        using var numbers = new BlockTestNumbers { Value = 42 };
        asset.SetBlock(numbers);
        using var branch = asset.Duplicate();
        using var copy = asset.CreateCopy();

        int index = 0;
        foreach (BlockTestAsset value in new[] { asset, branch, (BlockTestAsset)copy })
        {
            ImportNumberBlock number = (await value.TryGetBlockAsync<ImportNumberBlock>())!;
            Assert.Equal(21, number.Value);
            Assert.Equal(block.Reference, number.Reference);
            Assert.Equal(new byte[] { 1, 2, 3 }, (await number.ReadBytesAsync(1)).ToArray());
            string path = Path.Combine(files.Root, $"{index++}.block-test.asset");
            AssetEntry saved = await value.SaveAsync(path);
            Assert.Empty(saved.Dependencies);
            using var reopened = await BlockTestAsset.OpenAsync(path);
            Assert.Equal(42, (await reopened.TryGetBlockAsync<BlockTestNumbers>())!.Value);
            Assert.Null(await reopened.TryGetBlockAsync<ImportNumberBlock>());
            Assert.DoesNotContain(reopened.BlockDescriptions, entry => entry.BlockTypeId == ImportNumberBlock.BlockTypeId);
        }
    }

    [Fact]
    public void AssetDescriptionIncludesReferencesFromPersistentBlocks()
    {
        using var asset = new MeshAsset();
        Guid materialId = Guid.NewGuid();
        using var materials = new MeshMaterialsBlock { Materials = [new(materialId)] };
        using var number = new ImportNumberBlock { Reference = new(Guid.NewGuid()) };
        asset.SetBlock(materials);
        asset.SetBlock(number);
        using var branch = asset.Duplicate();
        Assert.Equal(new[] { materialId }, asset.References());
        Assert.Equal(new[] { materialId }, asset.Describe().Dependencies);
        Assert.Equal(new[] { materialId }, branch.Describe().Dependencies);
    }

    [Fact]
    public async Task OptionalReadPropagatesBlockReaderFailure()
    {
        using var files = new ImportFiles();
        using (var asset = new BlockTestAsset())
        using (var block = new FailedReadBlock())
        {
            asset.SetBlock(block);
            await asset.SaveAsync(files.Output);
        }
        using var reopened = await BlockTestAsset.OpenAsync(files.Output);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            reopened.TryGetBlockAsync<FailedReadBlock>().AsTask());
    }

    private static ImportContext CreateContext() => new();

    private static ValueTask<BlockTestAsset> GetAsset(ImportContext context, string source, CancellationToken cancellationToken = default)
    {
        Guid id = context.GetAssetId(source);
        context.AddAsset(source, new BlockTestAsset(id), source, "result.block-test.asset");
        return context.GetAssetAsync<string, BlockTestAsset>(source, cancellationToken);
    }

    private sealed class SourceImporter : IAssetSourceImporter<string>
    {
        public Guid Id { get; } = new("7b8e1fd3-16c2-4f26-aa5e-4b7969787ccc");
        public async ValueTask<string> ReadAsync(string sourcePath, CancellationToken cancellationToken)
            => await File.ReadAllTextAsync(sourcePath, cancellationToken);

        public async ValueTask ImportAsync(string source, ImportContext context, CancellationToken cancellationToken)
        {
            context.EnableImporter<string, BlockTestAsset>(Guid.NewGuid(), [BlockTestNumbers.BlockTypeId],
                async (_, imports, output, token) =>
                {
                    ImportNumberBlock number = await imports.GetBlockAsync<ImportNumberBlock>(output.Target, token);
                    Assert.Null(await imports.TryGetBlockAsync<ContextNumberBlock>(output.Target, token));
                    output.SetBlock(new BlockTestNumbers { Value = number.Value * 2 });
                });
            context.EnableImporter<string, BlockTestAsset>(Guid.NewGuid(), [ImportTextBlock.BlockTypeId],
                async (_, imports, output, token) =>
                {
                    ImportNumberBlock? number = await imports.TryGetBlockAsync<ImportNumberBlock>(output.Target, token);
                    output.SetBlock(new ImportTextBlock { Value = number!.Value.ToString() });
                });
            context.EnableImporter<string, BlockTestAsset>(Guid.NewGuid(), [ImportNumberBlock.BlockTypeId],
                (value, _, output, _) =>
                {
                    var number = new ImportNumberBlock { Value = int.Parse(value), Reference = new(Guid.NewGuid()) };
                    number.SetBytes(1, new byte[] { 4, 5, 6 });
                    output.SetBlock(number);
                    return ValueTask.CompletedTask;
                });
            context.EnableImporter<string, BlockTestAsset>(Guid.NewGuid(), [ContextNumberBlock.BlockTypeId],
                static (_, _, _, _) => ValueTask.CompletedTask);
            BlockTestAsset asset = await GetAsset(context, source, cancellationToken);
            Assert.Equal(21, (await asset.TryGetBlockAsync<ImportNumberBlock>(cancellationToken))!.Value);
        }
    }

    private sealed class ImportFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "import-block-behavior-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "mesh.input");
        public string Output => Path.Combine(Root, "result.block-test.asset");
        public string CatalogDirectory => Path.Combine(Root, "Library", "AssetManifest");

        public ImportFiles()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Source, "21");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

[AssetBlock("SomeEngine.Tests.ImportNumber"), BinarySerializable, BinaryFields(nameof(Value), nameof(Reference))]
internal sealed partial class ImportNumberBlock : AssetBlock
{
    public static bool Persistent => false;
    public partial int Value { get; set; }
    public partial AssetId<Asset> Reference { get; set; }
}

[AssetBlock("SomeEngine.Tests.ImportText"), BinarySerializable, BinaryFields(nameof(Value))]
internal sealed partial class ImportTextBlock : AssetBlock
{
    public partial string Value { get; set; }
}

[AssetBlock("SomeEngine.Tests.FailedRead")]
internal sealed partial class FailedReadBlock : AssetBlock, IAssetBlockWriter
{
    public override AssetBlock Duplicate() => new FailedReadBlock();
    public override AssetBlock Duplicate(object syncRoot) => new FailedReadBlock();
    public static ValueTask<FailedReadBlock> ReadAsync(AssetBlockSource source, CancellationToken cancellationToken = default)
        => throw new KeyNotFoundException("block source missing");
    public ValueTask WriteAsync(AssetBlockWriter writer, CancellationToken cancellationToken = default)
    {
        writer.WriteMetadata(BlockTypeId, new BlockTestNumbers());
        return ValueTask.CompletedTask;
    }
}
