using SomeEngine.Assets.Importing;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Tests;

public sealed class ImportContextTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ConfiguredSettingsApplyToOneImportAndFreezeAtTheFirstAssetRequest()
    {
        Guid importerId = Guid.NewGuid();
        var defaults = new GltfImporterSettings();
        var saved = defaults with { BuildVersion = "saved" };
        var configured = defaults with { BuildVersion = "configured" };
        var explicitSettings = defaults with { BuildVersion = "explicit" };
        var typeInfo = GltfSettingsJson.Default.GltfImporterSettings;
        var metadata = new SourceMeta
        {
            SourceGuid = SourceGuid.New(),
            Importers = [new(importerId, System.Text.Json.JsonSerializer.SerializeToElement(saved, typeInfo))],
        };
        await using (var context = Context(metadata))
        {
            Assert.Equal(saved, context.GetSettings(importerId, defaults, typeInfo));
            context.ConfigureSettings(importerId, configured);
            Assert.Equal(configured, context.GetSettings(importerId, defaults, typeInfo));
            Assert.Equal(explicitSettings, context.GetSettings(importerId, defaults, typeInfo, new(explicitSettings)));
            Select(context, [ContextTextBlock.BlockTypeId], (_, imports, output, _) =>
            {
                output.SetBlock(new ContextTextBlock(imports.GetSettings(importerId, defaults, typeInfo).BuildVersion));
                return ValueTask.CompletedTask;
            });
            TestAsset asset = await context.GetAssetAsync<Source, TestAsset>(new("mesh"));
            Assert.Equal("configured", asset.GetBlock<ContextTextBlock>().Text);
            Assert.Throws<InvalidOperationException>(() => context.ConfigureSettings(importerId, saved));
        }
        await using var next = Context(metadata);
        Assert.Equal(saved, next.GetSettings(importerId, defaults, typeInfo));
        Assert.Equal(defaults, next.GetSettings(Guid.NewGuid(), defaults, typeInfo));
    }

    [Fact]
    public async Task ConcurrentAndLaterRequestsReturnOneCompletedAssetAndOneConversion()
    {
        await using var context = Context();
        var entered = Signal();
        var release = Signal();
        int calls = 0;
        Select(context, [ContextNumberBlock.BlockTypeId], async (_, _, output, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            output.SetBlock(new ContextNumberBlock(42));
        });
        var source = new Source("mesh");
        using var cancellation = new CancellationTokenSource();
        Task<TestAsset> canceled = context.GetAssetAsync<Source, TestAsset>(source, cancellation.Token).AsTask();
        await entered.Task.WaitAsync(Timeout);
        Task<TestAsset>[] readers = Enumerable.Range(0, 12)
            .Select(_ => context.GetAssetAsync<Source, TestAsset>(source).AsTask()).ToArray();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(1, calls);
        Assert.Empty(context.Assets);
        release.TrySetResult();
        TestAsset[] values = await Task.WhenAll(readers).WaitAsync(Timeout);
        Assert.All(values, value => Assert.Same(values[0], value));
        Assert.Equal(42, values[0].GetBlock<ContextNumberBlock>().Value);
        Assert.Same(values[0], await context.GetAssetAsync<Source, TestAsset>(new Source("mesh")));
        Assert.Same(values[0], Assert.Single(context.Assets));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DifferentSourceElementsCreateDifferentAssetsWhileTheirMetadataIdsSurviveAnotherImport()
    {
        var metadata = new SourceMeta { SourceGuid = SourceGuid.New() };
        Guid first;
        await using (var context = Context(metadata))
        {
            Select(context, [ContextNumberBlock.BlockTypeId], (_, _, output, _) =>
            { output.SetBlock(new ContextNumberBlock(3)); return ValueTask.CompletedTask; });
            TestAsset a = await context.GetAssetAsync<Source, TestAsset>(new("a"));
            TestAsset b = await context.GetAssetAsync<Source, TestAsset>(new("b"));
            Assert.NotSame(a, b);
            Assert.NotEqual(a.Id, b.Id);
            first = a.Id;
        }
        await using (var context = Context(metadata))
        {
            Select(context, [ContextNumberBlock.BlockTypeId], (_, _, output, _) =>
            { output.SetBlock(new ContextNumberBlock(8)); return ValueTask.CompletedTask; });
            TestAsset a = await context.GetAssetAsync<Source, TestAsset>(new("a"));
            Assert.Equal(first, a.Id);
            Assert.Equal(8, a.GetBlock<ContextNumberBlock>().Value);
        }
    }

    [Fact]
    public async Task ReversedProducerOrderAutomaticallyResolvesStronglyTypedBlockDependencies()
    {
        await using var context = Context();
        int numbers = 0;
        Select(context, [ContextTextBlock.BlockTypeId], async (_, imports, output, token) =>
        {
            ContextNumberBlock dependency = await imports.GetBlockAsync<ContextNumberBlock>(output.Target, token);
            output.SetBlock(new ContextTextBlock((dependency.Value * 3).ToString()));
        });
        Select(context, [ContextNumberBlock.BlockTypeId], (_, _, output, _) =>
        {
            numbers++;
            output.SetBlock(new ContextNumberBlock(7));
            return ValueTask.CompletedTask;
        });
        TestAsset asset = await context.GetAssetAsync<Source, TestAsset>(new("mesh"));
        Assert.Equal(1, numbers);
        Assert.Equal(7, asset.GetBlock<ContextNumberBlock>().Value);
        Assert.Equal("21", asset.GetBlock<ContextTextBlock>().Text);
        Assert.Same(asset.GetBlock<ContextNumberBlock>(), await context.GetBlockAsync<ContextNumberBlock>(asset));
    }

    [Fact]
    public async Task AssetDependenciesAndTraversalReuseOneChildResult()
    {
        await using var context = Context();
        var child = new Source("texture");
        int children = 0;
        Select(context, [ContextNumberBlock.BlockTypeId], async (source, imports, output, token) =>
        {
            if (source.Child is not null)
            {
                TestAsset texture = await imports.GetAssetAsync<Source, TestAsset>(source.Child, token);
                output.SetBlock(new ContextNumberBlock(texture.GetBlock<ContextNumberBlock>().Value));
            }
            else { children++; output.SetBlock(new ContextNumberBlock(91)); }
        });
        TestAsset first = await context.GetAssetAsync<Source, TestAsset>(new("material-a", child));
        TestAsset second = await context.GetAssetAsync<Source, TestAsset>(new("material-b", child));
        TestAsset texture = await context.GetAssetAsync<Source, TestAsset>(child);
        Assert.Equal(1, children);
        Assert.Equal(3, context.Assets.Count);
        Assert.Equal(91, first.GetBlock<ContextNumberBlock>().Value);
        Assert.Equal(91, second.GetBlock<ContextNumberBlock>().Value);
        Assert.Same(texture, await context.GetAssetAsync<Source, TestAsset>(child));
    }

    [Fact]
    public async Task CompetingBlockProducersAreRejectedBeforeEitherRuns()
    {
        await using var context = Context();
        int calls = 0;
        ValueTask Produce(Source source, ImportContext imports, ImportOutput output, CancellationToken token)
        { calls++; return ValueTask.CompletedTask; }
        Select(context, [ContextNumberBlock.BlockTypeId], Produce);
        Select(context, [ContextNumberBlock.BlockTypeId], Produce);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.GetAssetAsync<Source, TestAsset>(new("mesh")).AsTask());

        Assert.Equal(0, calls);
        Assert.Empty(context.Assets);
    }

    [Fact]
    public async Task SelectionCannotChangeAfterTheFirstRequest()
    {
        await using var context = Context();
        Select(context, [ContextNumberBlock.BlockTypeId], (_, _, output, _) =>
        { output.SetBlock(new ContextNumberBlock(1)); return ValueTask.CompletedTask; });
        TestAsset first = await context.GetAssetAsync<Source, TestAsset>(new("mesh"));
        Assert.Throws<InvalidOperationException>(() => Select(context, [ContextTextBlock.BlockTypeId], (_, _, _, _) => ValueTask.CompletedTask));
        Assert.Same(first, await context.GetAssetAsync<Source, TestAsset>(new("mesh")));
    }

    [Fact]
    public async Task FailedResultIsNotReimportedByASecondRequestInTheSameContext()
    {
        await using var context = Context();
        int calls = 0;
        ContextNumberBlock? failed = null;
        Select(context, [ContextNumberBlock.BlockTypeId], (_, _, output, _) =>
        {
            calls++;
            failed = new ContextNumberBlock(9);
            output.SetBlock(failed);
            throw new InvalidDataException("bad source");
        });
        var source = new Source("mesh");
        await Assert.ThrowsAsync<InvalidDataException>(() => context.GetAssetAsync<Source, TestAsset>(source).AsTask());
        await Assert.ThrowsAsync<InvalidDataException>(() => context.GetAssetAsync<Source, TestAsset>(source).AsTask());
        Assert.Equal(1, calls);
        Assert.True(failed!.Disposed);
        Assert.Empty(context.Assets);
    }

    [Fact]
    public async Task CompletedDependencyIsReleasedWhenALaterConversionFails()
    {
        await using var context = Context();
        ContextNumberBlock? attached = null;
        Select(context, [ContextTextBlock.BlockTypeId], async (_, imports, output, token) =>
        {
            attached = await imports.GetBlockAsync<ContextNumberBlock>(output.Target, token);
            throw new InvalidDataException("failed after dependency");
        });
        Select(context, [ContextNumberBlock.BlockTypeId], (_, _, output, _) =>
        { output.SetBlock(new ContextNumberBlock(5)); return ValueTask.CompletedTask; });
        await Assert.ThrowsAsync<InvalidDataException>(() => context.GetAssetAsync<Source, TestAsset>(new("mesh")).AsTask());
        Assert.Empty(context.Assets);
        Assert.False(attached!.Disposed);
        await context.DisposeAsync();
        Assert.True(attached.Disposed);
    }

    [Fact]
    public async Task EveryCloseWaitsUntilConversionsStopUsingTheirOutput()
    {
        var context = Context();
        var entered = Signal();
        var finish = Signal();
        ContextNumberBlock? block = null;
        Select(context, [ContextNumberBlock.BlockTypeId], async (_, _, output, _) =>
        {
            block = new ContextNumberBlock(2);
            output.SetBlock(block);
            entered.TrySetResult();
            await finish.Task;
        });
        Task<TestAsset> request = context.GetAssetAsync<Source, TestAsset>(new("mesh")).AsTask();
        await entered.Task.WaitAsync(Timeout);
        Task first = context.DisposeAsync().AsTask();
        Task second = context.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(block!.Disposed);
        }
        finally { finish.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(block!.Disposed);
    }

    [Fact]
    public async Task ContextCancellationStopsProductionWithoutReturningAnIncompleteAsset()
    {
        using var cancellation = new CancellationTokenSource();
        await using var context = Context(cancellationToken: cancellation.Token);
        var entered = Signal();
        ContextNumberBlock? block = null;
        Select(context, [ContextNumberBlock.BlockTypeId], async (_, _, output, token) =>
        {
            block = new ContextNumberBlock(4);
            output.SetBlock(block);
            entered.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, token);
        });
        Task<TestAsset> request = context.GetAssetAsync<Source, TestAsset>(new("mesh")).AsTask();
        await entered.Task.WaitAsync(Timeout);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(block!.Disposed);
        Assert.Empty(context.Assets);
    }

    [Fact]
    public async Task BlockDependencyCycleFailsInsteadOfHanging()
    {
        await using var context = Context();
        Select(context, [ContextNumberBlock.BlockTypeId], async (_, imports, output, token) =>
        { await imports.GetBlockAsync<ContextTextBlock>(output.Target, token); });
        Select(context, [ContextTextBlock.BlockTypeId], async (_, imports, output, token) =>
        { await imports.GetBlockAsync<ContextNumberBlock>(output.Target, token); });
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.GetAssetAsync<Source, TestAsset>(new("mesh")).AsTask().WaitAsync(Timeout));
    }

    [Fact]
    public async Task ParallelAssetRootsReportTheirCycleInsteadOfDeadlocking()
    {
        await using var context = Context();
        var first = new Source("a");
        var second = new Source("b");
        var a = Signal();
        var b = Signal();
        Select(context, [ContextNumberBlock.BlockTypeId], async (source, imports, _, token) =>
        {
            if (source.Key == "a")
            {
                a.TrySetResult();
                await b.Task.WaitAsync(token);
                await imports.GetAssetAsync<Source, TestAsset>(second, token);
            }
            else
            {
                b.TrySetResult();
                await a.Task.WaitAsync(token);
                await imports.GetAssetAsync<Source, TestAsset>(first, token);
            }
        });
        Task<TestAsset> one = context.GetAssetAsync<Source, TestAsset>(first).AsTask();
        Task<TestAsset> two = context.GetAssetAsync<Source, TestAsset>(second).AsTask();
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.WhenAll(one, two).WaitAsync(Timeout));
    }

    [Fact]
    public async Task RejectedBlockStaysWithItsCaller()
    {
        await using var context = Context();
        using var block = new ContextTextBlock("not declared");
        Select(context, [ContextNumberBlock.BlockTypeId], (_, _, output, _) =>
        { output.SetBlock(block); return ValueTask.CompletedTask; });
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.GetAssetAsync<Source, TestAsset>(new("mesh")).AsTask());
        Assert.False(block.Disposed);
        Assert.Empty(context.Assets);
    }

    [Fact]
    public async Task MissingSelectionDoesNotCreateAnAsset()
    {
        await using var context = new ImportContext();
        var sources = new TestSources();
        context.ConfigureAssets<Source, TestAsset>(sources);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.GetAssetAsync<Source, TestAsset>(new("mesh")).AsTask());
        Assert.Equal(0, sources.Created);
        Assert.Empty(context.Assets);
    }

    private static ImportContext Context(SourceMeta? metadata = null, CancellationToken cancellationToken = default)
    {
        var context = metadata is null ? new ImportContext(cancellationToken)
            : new ImportContext(Directory.GetCurrentDirectory(), "", metadata, cancellationToken);
        context.ConfigureAssets<Source, TestAsset>(new TestSources());
        return context;
    }

    private sealed class TestSources : AssetSource<Source, TestAsset>
    {
        public int Created { get; private set; }
        public override string GetElementKey(Source source) => source.Key;
        public override TestAsset CreateAsset(Source source, Guid id) { Created++; return new(id); }
        public override string GetOutputPath(Source source, Guid id) => id + ".asset";
    }

    private static void Select(ImportContext context, Guid[] blocks,
        Func<Source, ImportContext, ImportOutput, CancellationToken, ValueTask> convert)
        => context.EnableImporter<Source, TestAsset>(new AssetImporter<Source, int>(
            Guid.NewGuid(), blocks, new Importer(convert), 0));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record Source(string Key, Source? Child = null);
    private sealed class Importer(Func<Source, ImportContext, ImportOutput, CancellationToken, ValueTask> convert)
        : IAssetImporter<Source, int>
    {
        public ValueTask ImportAsync(Source source, int settings, ImportContext context, ImportOutput output, CancellationToken cancellationToken)
            => convert(source, context, output, cancellationToken);
    }
    private sealed class TestAsset : Asset
    {
        public TestAsset(Guid id) : base(id: id) { }
        private TestAsset(TestAsset source) : base(source) { }
        public override Asset Duplicate() => new TestAsset(this);
    }
}

[AssetBlock("SomeEngine.Tests.Context.Number")]
internal sealed partial class ContextNumberBlock : AssetBlock
{
    public int Value { get; }
    public bool Disposed { get; private set; }
    public ContextNumberBlock(int value) => Value = value;
    private ContextNumberBlock(ContextNumberBlock source, object? syncRoot = null) : base(source, syncRoot) => Value = source.Value;
    public override AssetBlock Duplicate() => new ContextNumberBlock(this);
    public override AssetBlock Duplicate(object syncRoot) => new ContextNumberBlock(this, syncRoot);
    protected override void ReleaseContent() => Disposed = true;
}

[AssetBlock("SomeEngine.Tests.Context.Text")]
internal sealed partial class ContextTextBlock : AssetBlock
{
    public string Text { get; }
    public bool Disposed { get; private set; }
    public ContextTextBlock(string text) => Text = text;
    private ContextTextBlock(ContextTextBlock source, object? syncRoot = null) : base(source, syncRoot) => Text = source.Text;
    public override AssetBlock Duplicate() => new ContextTextBlock(this);
    public override AssetBlock Duplicate(object syncRoot) => new ContextTextBlock(this, syncRoot);
    protected override void ReleaseContent() => Disposed = true;
}
