using System.Threading;
using SomeEngine.Assets;

namespace SomeEngine.Tests.Assets;

public class AssetStoreTests
{
    [Fact]
    public void Add_ReplacesGuid_AndInvalidatesOld()
    {
        using var store = new AssetStore();
        AssetGuid guid = AssetGuid.New();
        const string first = "First";
        const string second = "Second";

        ulong initialVersion = store.GetVersion<string>();
        Handle<string> firstHandle = store.Add(guid, first);
        ulong firstVersion = store.GetVersion<string>();
        Handle<string> secondHandle = store.Add(guid, second);
        ulong secondVersion = store.GetVersion<string>();

        Assert.True(firstVersion > initialVersion);
        Assert.True(secondVersion > firstVersion);
        Assert.Equal(firstHandle.Id, secondHandle.Id);
        Assert.NotEqual(firstHandle.Generation, secondHandle.Generation);
        Assert.False(store.TryGet(firstHandle, out _));
        Assert.True(store.TryFind(guid, out Handle<string> found));
        Assert.Equal(secondHandle, found);
        Assert.Same(second, store.Get(secondHandle));
    }

    [Fact]
    public void GenericStore_ReplacesGuid_AndInvalidatesOld()
    {
        using var store = new AssetStore<string>();
        AssetGuid guid = AssetGuid.New();
        const string first = "First";
        const string second = "Second";

        ulong initialVersion = store.Version;
        Handle<string> firstHandle = store.Add(guid, first);
        ulong firstVersion = store.Version;
        Handle<string> secondHandle = store.Add(guid, second);
        ulong secondVersion = store.Version;

        Assert.True(firstVersion > initialVersion);
        Assert.True(secondVersion > firstVersion);
        Assert.Equal(firstHandle.Id, secondHandle.Id);
        Assert.NotEqual(firstHandle.Generation, secondHandle.Generation);
        Assert.False(store.TryGet(firstHandle, out _));
        Assert.True(store.TryFind(guid, out Handle<string> found));
        Assert.Equal(secondHandle, found);
        Assert.Same(second, store.Get(secondHandle));
    }

    [Fact]
    public async Task Request_LoadsAsset_AndStoresHandle()
    {
        using var store = new AssetStore();
        AssetGuid guid = AssetGuid.New();

        Handle<string> handle = await store.Request(
            guid,
            static (_, _) => "Requested");

        Assert.True(handle.IsValid);
        Assert.True(store.TryFind(guid, out Handle<string> found));
        Assert.Equal(handle, found);
        Assert.True(store.TryGet(handle, out string? asset));
        Assert.Equal("Requested", asset);
    }

    [Fact]
    public async Task Request_DeduplicatesConcurrentGuid()
    {
        using var store = new AssetStore();
        AssetGuid guid = AssetGuid.New();
        int calls = 0;
        var source = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Handle<string>> first = store.Request(
            guid,
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return source.Task;
            });

        Task<Handle<string>> second = store.Request(
            guid,
            static (_, _) => Task.FromResult<string?>("Duplicate"));

        source.SetResult("Loaded");
        Handle<string>[] handles = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.Equal(handles[0], handles[1]);
        Assert.True(store.TryGet(handles[0], out string? asset));
        Assert.Equal("Loaded", asset);
    }

    [Fact]
    public async Task Request_ReturnsReadyHandle()
    {
        using var store = new AssetStore();
        AssetGuid guid = AssetGuid.New();
        Handle<string> ready = store.Add(guid, "Ready");
        Func<AssetGuid, CancellationToken, string?> load =
            static (_, _) => throw new InvalidOperationException("Loader should not run for ready assets.");

        Handle<string> requested = await store.Request(guid, load);

        Assert.Equal(ready, requested);
    }
}