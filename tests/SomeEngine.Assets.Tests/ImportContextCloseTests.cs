using System.Collections.Concurrent;
using SomeEngine.Assets.Importing;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets.Tests;

public sealed class ImportContextCloseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EmptyCloseCompletesAndRepeatedCloseReturnsTheSameTask()
    {
        var context = new ImportContext();
        try
        {
            Task first = context.DisposeAsync().AsTask();
            Task second = context.DisposeAsync().AsTask();
            await Task.WhenAll(first, second).WaitAsync(Timeout);
            Assert.Same(first, second);
            Assert.Same(first, context.DisposeAsync().AsTask());
        }
        finally { await context.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task CloseReleasesCompletedAssetsBeforeHeldResourcesInReverseOrder()
    {
        var events = new List<string>();
        var context = CreateContext();
        try
        {
            context.EnableImporter<CloseSource, CloseAsset>(Guid.NewGuid(), [],
                (_, _, _, _) => ValueTask.CompletedTask);
            await context.GetAssetAsync<CloseSource, CloseAsset>(new("first", () => events.Add("first")));
            await context.GetAssetAsync<CloseSource, CloseAsset>(new("second", () => events.Add("second")));
            string[] assetOrder = context.Assets.Cast<CloseAsset>().Select(asset => asset.Label).ToArray();
            context.Hold(new CloseResource(() => events.Add("earlier-resource")));
            context.Hold(new CloseResource(() => events.Add("later-resource")));

            await context.DisposeAsync().AsTask().WaitAsync(Timeout);

            Assert.Equal(assetOrder.Concat(["later-resource", "earlier-resource"]), events);
        }
        finally { await context.DisposeAsync().AsTask().WaitAsync(Timeout); }
    }

    [Fact]
    public async Task ClosePreservesDisposalFailureOrderAndNestedExceptionsForRepeatedClose()
    {
        var events = new List<string>();
        var firstError = new InvalidDataException();
        var secondError = new IOException();
        var earlierResourceError = new NotSupportedException();
        var nestedError = new AggregateException(new ArgumentException(), new InvalidOperationException());
        var context = CreateContext();
        try
        {
            context.EnableImporter<CloseSource, CloseAsset>(Guid.NewGuid(), [],
                (_, _, _, _) => ValueTask.CompletedTask);
            await context.GetAssetAsync<CloseSource, CloseAsset>(new("first", () =>
            { events.Add("first"); throw firstError; }));
            await context.GetAssetAsync<CloseSource, CloseAsset>(new("second", () =>
            { events.Add("second"); throw secondError; }));
            string[] assetOrder = context.Assets.Cast<CloseAsset>().Select(asset => asset.Label).ToArray();
            Exception[] assetErrors = assetOrder.Select(label => label == "first"
                ? (Exception)firstError : secondError).ToArray();
            context.Hold(new CloseResource(() =>
            { events.Add("earlier-resource"); throw earlierResourceError; }));
            context.Hold(new CloseResource(() =>
            { events.Add("later-resource"); throw nestedError; }));

            Task first = context.DisposeAsync().AsTask();
            Task second = context.DisposeAsync().AsTask();
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => first.WaitAsync(Timeout));
            AggregateException repeated = await Assert.ThrowsAsync<AggregateException>(() => second.WaitAsync(Timeout));

            Assert.Same(first, second);
            Assert.Same(failure, repeated);
            Assert.Collection(failure.InnerExceptions,
                error => Assert.Same(assetErrors[0], error),
                error => Assert.Same(assetErrors[1], error),
                error => Assert.Same(nestedError, error),
                error => Assert.Same(earlierResourceError, error));
            Assert.Equal(assetOrder.Concat(["later-resource", "earlier-resource"]), events);
        }
        finally
        {
            try { await context.DisposeAsync().AsTask().WaitAsync(Timeout); }
            catch (AggregateException) { }
        }
    }

    [Fact]
    public async Task CloseReportsCancellationCallbackFailureBeforeAssetAndResourceFailures()
    {
        var events = new ConcurrentQueue<string>();
        var cancellationError = new InvalidOperationException();
        var assetError = new InvalidDataException();
        var resourceError = new IOException();
        CancellationTokenRegistration registration = default;
        var context = CreateContext();
        try
        {
            context.EnableImporter<CloseSource, CloseAsset>(Guid.NewGuid(), [], (_, _, _, token) =>
            {
                registration = token.Register(() =>
                { events.Enqueue("cancellation"); throw cancellationError; });
                return ValueTask.CompletedTask;
            });
            await context.GetAssetAsync<CloseSource, CloseAsset>(new("asset", () =>
            { events.Enqueue("asset"); throw assetError; }));
            context.Hold(new CloseResource(() =>
            { events.Enqueue("resource"); throw resourceError; }));

            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
                () => context.DisposeAsync().AsTask().WaitAsync(Timeout));

            Assert.Collection(failure.InnerExceptions,
                error =>
                {
                    AggregateException cancellation = Assert.IsType<AggregateException>(error);
                    Assert.Same(cancellationError, Assert.Single(cancellation.InnerExceptions));
                },
                error => Assert.Same(assetError, error),
                error => Assert.Same(resourceError, error));
            Assert.Equal(["cancellation", "asset", "resource"], events.ToArray());
        }
        finally
        {
            try { await context.DisposeAsync().AsTask().WaitAsync(Timeout); }
            catch (AggregateException) { }
            finally { registration.Dispose(); }
        }
    }

    [Fact]
    public async Task CloseJoinsActiveConversionBeforeReleasingItsAssetAndHeldResources()
    {
        var events = new ConcurrentQueue<string>();
        var entered = Signal();
        var canceled = Signal();
        var finish = Signal();
        CancellationTokenRegistration registration = default;
        var context = CreateContext();
        try
        {
            context.EnableImporter<CloseSource, CloseAsset>(Guid.NewGuid(), [], async (_, _, _, token) =>
            {
                registration = token.Register(() =>
                {
                    events.Enqueue("cancellation");
                    canceled.TrySetResult();
                });
                events.Enqueue("conversion-started");
                entered.TrySetResult();
                await finish.Task;
                events.Enqueue("conversion-ended");
            });
            context.Hold(new CloseResource(() => events.Enqueue("resource")));
            Task<CloseAsset> request = context.GetAssetAsync<CloseSource, CloseAsset>(
                new("asset", () => events.Enqueue("asset"))).AsTask();
            await entered.Task.WaitAsync(Timeout);
            Task first = context.DisposeAsync().AsTask();
            Task second = context.DisposeAsync().AsTask();
            await canceled.Task.WaitAsync(Timeout);
            finish.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(Timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Timeout));

            Assert.Same(first, second);
            Assert.Equal(["conversion-started", "cancellation", "conversion-ended", "asset", "resource"], events.ToArray());
        }
        finally
        {
            finish.TrySetResult();
            try { await context.DisposeAsync().AsTask().WaitAsync(Timeout); }
            finally { registration.Dispose(); }
        }
    }

    private static ImportContext CreateContext()
    {
        var context = new ImportContext();
        context.ConfigureAssets<CloseSource, CloseAsset>(source => source.Key,
            (source, id) => new CloseAsset(id, source.Key, source.Release),
            (_, id) => id.ToString("N") + ".asset",
            (asset, path, _) => ValueTask.FromResult(
                new AssetEntry(asset.Id, "test", 1, Guid.NewGuid()) { Path = path }));
        return context;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed record CloseSource(string Key, Action Release);
    private sealed class CloseResource(Action release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
    }
    private sealed class CloseAsset(Guid id, string label, Action release) : Asset(id)
    {
        public string Label { get; } = label;
        public override Asset Duplicate() => throw new NotSupportedException();
        protected override void ReleaseContent() => release();
    }
}
