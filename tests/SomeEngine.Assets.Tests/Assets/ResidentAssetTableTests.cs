using SomeEngine.Tests.Assets;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ResidentAssetTableTests
{
    [Fact]
    public async Task ConcurrentLoadsReturnOneObject()
    {
        await using var fixture = new Fixture();
        ProbeAsset[] values = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => fixture.Loader.LoadAsync(fixture.Id).AsTask()));
        Assert.All(values, value => { Assert.Same(values[0], value); Assert.Equal(7, value.Value); });
    }
    [Fact]
    public async Task CallerCancellationDoesNotCancelOtherRequests()
    {
        await using var fixture = new Fixture();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Storage.Resume = resume.Task;
        using var cancellation = new CancellationTokenSource();
        Task<ProbeAsset> cancelled = fixture.Loader.LoadAsync(fixture.Id, cancellation.Token).AsTask();
        Task<ProbeAsset> surviving = fixture.Loader.LoadAsync(fixture.Id).AsTask();
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        finally { resume.TrySetResult(); }
        Assert.Equal(7, (await surviving.WaitAsync(TimeSpan.FromSeconds(5))).Value);
    }
    [Fact]
    public async Task ReloadChangesTheLoadedObjectAndRevision()
    {
        await using var fixture = new Fixture();
        ProbeAsset original = await fixture.Loader.LoadAsync(fixture.Id);
        fixture.Write(37);
        ProbeAsset updated = await fixture.Loader.ReloadAsync(original);
        Assert.Same(original, updated);
        Assert.Equal(37, original.Value);
        Assert.Equal<ulong>(2, fixture.Loader.GetRevision(original));
    }
    [Fact]
    public async Task FailedReloadPreservesValueAndCanBeRetried()
    {
        await using var fixture = new Fixture();
        ProbeAsset original = await fixture.Loader.LoadAsync(fixture.Id);
        fixture.Storage.Fail = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Loader.ReloadAsync(original).AsTask());
        Assert.Equal(7, original.Value);
        Assert.Equal<ulong>(1, fixture.Loader.GetRevision(original));
        fixture.Storage.Fail = false;
        fixture.Write(41);
        Assert.Same(original, await fixture.Loader.ReloadAsync(original));
        Assert.Equal(41, original.Value);
    }
    [Fact]
    public async Task ClosedLoaderRejectsNewRequests()
    {
        await using var fixture = new Fixture();
        _ = await fixture.Loader.LoadAsync(fixture.Id);
        await fixture.Loader.DisposeAsync();
        await fixture.Loader.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Loader.LoadAsync(fixture.Id).AsTask());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"asset-load-{Guid.NewGuid():N}");
        private readonly AssetGuid _guid = AssetGuid.New();
        internal Storage Storage { get; }
        internal AssetLoader Loader { get; }
        internal AssetId<ProbeAsset> Id => new(_guid);
        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            Storage = new Storage(_guid, Path.Combine(_directory, "value.probe.asset"));
            Write(7);
            Loader = new AssetLoader(Storage);
        }
        internal void Write(int value)
        {
            string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.probe.asset");
            AssetWriter.Write(new ProbeAsset
            {
                AssetGuid = _guid.ToFlatString(), Name = "loaded", Value = value,
            }, path);
            Storage.Publish(path);
        }
        public async ValueTask DisposeAsync()
        {
            await Loader.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
    private sealed class Storage(AssetGuid guid, string path) : IAssetStorage
    {
        private AssetEntry _entry = new(guid, typeof(ProbeAsset).FullName!, ProbeAsset.SchemaFingerprint, Guid.NewGuid());
        internal string Path { get; private set; } = path;
        internal void Publish(string path)
        {
            Path = path;
            _entry = new AssetEntry(guid, typeof(ProbeAsset).FullName!, ProbeAsset.SchemaFingerprint, Guid.NewGuid());
        }
        internal Task Resume { get; set; } = Task.CompletedTask;
        internal bool Fail { get; set; }
        public bool TryFind(AssetGuid requested, out AssetEntry entry)
        {
            entry = _entry;
            return requested == guid;
        }
        public async ValueTask<IRangeSource> OpenAsync(AssetEntry entry, CancellationToken cancellationToken = default)
        {
            await Resume.WaitAsync(cancellationToken);
            if (Fail) throw new InvalidDataException("Unreadable asset.");
            return FileRangeSource.Open(Path);
        }
    }
}
