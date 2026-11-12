using System.Diagnostics;
using System.Numerics;
using SomeEngine.Assets.Tests.Assets;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Tests;

public sealed class WorldStreamTests
{
    [Fact]
    public async Task RequestsLoadIndependentGroupsAndReleaseDestroysTheirActiveMembers()
    {
        using var files = new StreamFiles();
        AssetId<Asset> members = await files.SaveEntities(31, 47);
        AssetId<WorldAsset> directory = await files.SaveDirectory(new(members, new(0, 0, 0, 1, 1, 1)),
            new(members, new(10, 0, 0, 11, 1, 1)));
        await using var loader = new AssetLoader(files.Storage());
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity business = world.Create(new StreamNumber(91));
        using var stream = new WorldStream(world, loader, directory, new(), StreamFiles.ReadOptions);
        await stream.ContentsReady;

        stream.Request(0);
        stream.Request(1);
        await Load(stream, 0, 1);

        Assert.True(stream.LoadedGroups.TryGetEntities(0, out ReadOnlyMemory<Entity> first));
        Assert.True(stream.LoadedGroups.TryGetEntities(1, out ReadOnlyMemory<Entity> second));
        Assert.Equal(new[] { 31, 47 }, first.ToArray().Select(entity => world.ReadComponentData<StreamNumber>(entity).Value));
        Assert.Equal(new[] { 31, 47 }, second.ToArray().Select(entity => world.ReadComponentData<StreamNumber>(entity).Value));
        Assert.Empty(first.ToArray().Intersect(second.ToArray()));
        world.Destroy(first.Span[0]);

        stream.Release(0);
        stream.Update();

        Assert.False(stream.LoadedGroups.TryGetEntities(0, out _));
        Assert.All(first.ToArray(), entity => Assert.False(world.Has<StreamNumber>(entity)));
        Assert.All(second.ToArray(), entity => Assert.True(world.Has<StreamNumber>(entity)));
        stream.UnloadAll();
        Assert.False(stream.LoadedGroups.TryGetEntities(1, out _));
        Assert.Empty(stream.Requests.Priorities);
        Assert.Equal(1, world.Query<StreamNumber>().Count);
        Assert.Equal(91, world.ReadComponentData<StreamNumber>(business).Value);
    }

    [Fact]
    public async Task CompletionUsesTheLatestDemandAndStartsTheHighestPriorityReadFirst()
    {
        using var files = new StreamFiles();
        AssetId<Asset> low = await files.SaveEntities(1);
        AssetId<Asset> high = await files.SaveEntities(2);
        AssetId<WorldAsset> directory = await files.SaveDirectory(new(low, default), new(high, default));
        var highRead = new ReadGate();
        var lowRead = new ReadGate();
        lowRead.Continue.SetResult();
        var storage = new ControlledStorage(files.Storage(), new() { [low.Value] = lowRead, [high.Value] = highRead });
        await using var loader = new AssetLoader(storage);
        using var world = new World();
        using var stream = new WorldStream(world, loader, directory, new() { MaxConcurrentReads = 1 }, StreamFiles.ReadOptions);
        await stream.ContentsReady;
        stream.Request(0, 3);
        stream.Request(1, 8);
        stream.Update();
        await highRead.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(lowRead.Started.Task.IsCompleted);

        stream.Release(1);
        stream.Update();
        highRead.Continue.SetResult();
        await Load(stream, 0);

        Assert.True(lowRead.Started.Task.IsCompleted);
        Assert.False(stream.LoadedGroups.TryGetEntities(1, out _));
        Assert.Equal(1, world.Query<StreamNumber>().Count);
        Assert.True(stream.LoadedGroups.TryGetEntities(0, out ReadOnlyMemory<Entity> loaded));
        Assert.Equal(1, world.ReadComponentData<StreamNumber>(loaded.Span[0]).Value);
    }

    [Fact]
    public async Task ReadyBytesPauseFurtherReadsAndZeroCommitBudgetKeepsCompletedBytesVisible()
    {
        using var files = new StreamFiles();
        AssetId<Asset> first = await files.SaveEntities(1);
        AssetId<Asset> second = await files.SaveEntities(2);
        AssetId<WorldAsset> directory = await files.SaveDirectory(new(first, default), new(second, default));
        var firstRead = new ReadGate();
        var secondRead = new ReadGate();
        firstRead.Continue.SetResult();
        secondRead.Continue.SetResult();
        await using var loader = new AssetLoader(new ControlledStorage(files.Storage(),
            new() { [first.Value] = firstRead, [second.Value] = secondRead }));
        using var world = new World();
        using var stream = new WorldStream(world, loader, directory,
            new() { MaxConcurrentReads = 1, MaxReadyBytes = 1, CommitBudget = TimeSpan.Zero }, StreamFiles.ReadOptions);
        await stream.ContentsReady;
        stream.Request(0, 5);
        stream.Request(1, 2);
        long start = Stopwatch.GetTimestamp();
        while (stream.ReadResults.Items.Count == 0)
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(10), "The content read completes.");
            stream.Update();
            await stream.CompleteReadsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        ContentBytes bytes = Assert.Single(stream.ReadResults.Items);
        Assert.Equal(0, bytes.ContentIndex);
        Assert.True(bytes.Bytes.Length > 1);
        stream.Update();
        Assert.False(secondRead.Started.Task.IsCompleted);
        Assert.False(stream.LoadedGroups.TryGetEntities(0, out _));
        Assert.Equal(0, world.Query<StreamNumber>().Count);
    }

    [Fact]
    public async Task SourcesAndRequestsMergeTheirHighestPriorityAndReportOnlyChangedDemands()
    {
        using var files = new StreamFiles();
        AssetId<Asset> value = await files.SaveEntities(7);
        const double origin = 1_000_000_000_000;
        AssetId<WorldAsset> directory = await files.SaveDirectory(new(value, new(origin, 0, 0, origin + 2, 2, 2)),
            new(value, new(origin + 20, 0, 0, origin + 22, 2, 2)));
        await using var loader = new AssetLoader(files.Storage());
        using var world = new World();
        using var stream = new WorldStream(world, loader, directory, new() { CommitBudget = TimeSpan.Zero });
        await stream.ContentsReady;
        WorldStreamSource[] sources = new WorldStreamSource[2];
        WorldStreamSource[] input = [new(origin - 1, 1, 1, 2, 4), new(origin + 2, 1, 1, 1, 9)];
        JobHandle sourceReady = JobSystem.Schedule(new CopySources(input, sources),
            [JobResourceAccess.Read(input), JobResourceAccess.Write(sources)]);
        stream.Request(0, 6);
        stream.Request(1, -3);

        stream.Update(sources, sourceReady);

        Assert.Equal(new ContentDemand[] { new(true, 9), new(true, -3) }, stream.Demands.Values.ToArray());
        Assert.Equal(new[] { 0, 1 }, stream.Demands.Changed.ToArray());
        stream.Update(sources);
        Assert.Empty(stream.Demands.Changed.ToArray());
        stream.Release(0);
        stream.Update(sources.AsMemory(0, 1));
        Assert.Equal(new ContentDemand(true, 4), stream.Demands.Values.Span[0]);
        Assert.Equal(new[] { 0 }, stream.Demands.Changed.ToArray());
        stream.UnloadAll();
        stream.Update(sources.AsMemory(0, 1));
        Assert.Equal(new ContentDemand(true, 4), stream.Demands.Values.Span[0]);
        Assert.Equal(default, stream.Demands.Values.Span[1]);
    }

    [Fact]
    public async Task ClosingDuringAReadCompletesStreamWorkAndPreservesTheBorrowedWorldAndLoader()
    {
        using var files = new StreamFiles();
        AssetId<Asset> value = await files.SaveEntities(23);
        AssetId<WorldAsset> directory = await files.SaveDirectory(new WorldContent(value, default));
        var read = new ReadGate();
        await using var loader = new AssetLoader(new ControlledStorage(files.Storage(), new() { [value.Value] = read }));
        using var world = new World();
        await using var stream = new WorldStream(world, loader, directory, new(), StreamFiles.ReadOptions);
        await stream.ContentsReady;
        stream.Request(0);
        stream.Update();
        await read.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await stream.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(stream.LoadedGroups.TryGetEntities(0, out _));
        Assert.Empty(stream.ReadResults.Items);
        Assert.Empty(stream.Contents.ToArray());
        Entity business = world.Create(new StreamNumber(91));
        Assert.Equal(91, world.ReadComponentData<StreamNumber>(business).Value);
        read.Continue.SetResult();
        using EntityAsset stillReadable = await EntityAsset.OpenAsync(loader, new(value.Value));
        Assert.True((await stillReadable.GetBlockAsync<EntityBlock>()).ContentLength > 0);
    }

    [Fact]
    public async Task SystemCollectsAbsoluteSourcesAndGroupDisposalUnloadsItsEntities()
    {
        using var files = new StreamFiles();
        AssetId<Asset> value = await files.SaveEntities(19);
        const double origin = 1_000_000_000_000;
        AssetId<WorldAsset> directory = await files.SaveDirectory(new WorldContent(value, new(origin + 9, 0, 0, origin + 11, 2, 2)));
        await using var loader = new AssetLoader(files.Storage());
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity originEntity = world.Create(new WorldOrigin(origin, 0, 0));
        Entity source = world.Create(new StreamingSource(2, 5),
            new WorldTransform { Qvvs = new TransformQvvs(new Vector3(10, 1, 1), Quaternion.Identity) });
        var system = new WorldStreamSystem(world, loader, directory, new(), StreamFiles.ReadOptions);
        using (var group = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world)))
        {
            group.Add(system);
            await system.Stream.ContentsReady;
            long start = Stopwatch.GetTimestamp();
            while (!system.Stream.LoadedGroups.TryGetEntities(0, out _))
            {
                Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(10), "The system loads the covered content.");
                group.Update();
                await system.Stream.CompleteReadsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.Equal(new ContentDemand(true, 5), system.Stream.Demands.Values.Span[0]);
            Assert.Equal(1, world.Query<StreamNumber>().Count);
        }

        Assert.Equal(0, world.Query<StreamNumber>().Count);
        Assert.True(world.IsAlive(source));
        Assert.True(world.IsAlive(originEntity));
        using EntityAsset retained = await EntityAsset.OpenAsync(loader, new(value.Value));
        Assert.True((await retained.GetBlockAsync<EntityBlock>()).ContentLength > 0);
    }

    private static async Task Load(WorldStream stream, params int[] indices)
    {
        long start = Stopwatch.GetTimestamp();
        while (indices.Any(index => !stream.LoadedGroups.TryGetEntities(index, out _)))
        {
            Assert.True(Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(10), "Requested content loads.");
            stream.Update();
            await stream.CompleteReadsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class StreamFiles : IDisposable
    {
        private readonly AssetTestDirectory _directory = new();
        private readonly AssetManifest _manifest = new();
        private static readonly ComponentCodec NumberCodec = ComponentCodec.Create<StreamNumber, NumberCodec>(
            new(Guid.Parse("f6747350-aa7c-419c-9bdd-fcc4c235908e"), "stream.number", 1), default);
        internal static SerializationReadOptions ReadOptions => new(Codecs: [NumberCodec]);

        internal async Task<AssetId<Asset>> SaveEntities(params int[] values)
        {
            using var world = new World();
            Entity[] entities = values.Select(value => world.Create(new StreamNumber(value))).ToArray();
            using var asset = new EntityAsset(Guid.NewGuid());
            asset.GetBlock<EntityBlock>().WriteEntities(world, entities, new(Codecs: [NumberCodec]));
            string name = asset.Id.ToString("N") + EntityAsset.PathSuffix;
            await asset.SaveAsync(_directory.File(name));
            _manifest.AddAsset(asset.Id, name, name, EntityAsset.FileType, EntityAsset.SchemaHash);
            return new(asset.Id);
        }

        internal async Task<AssetId<WorldAsset>> SaveDirectory(params WorldContent[] contents)
        {
            using var asset = new WorldAsset(Guid.NewGuid());
            asset.GetBlock<WorldBlock>().Contents = [.. contents];
            string name = asset.Id.ToString("N") + WorldAsset.PathSuffix;
            await asset.SaveAsync(_directory.File(name));
            _manifest.AddAsset(asset.Id, name, name, WorldAsset.FileType, WorldAsset.SchemaHash);
            return new(asset.Id);
        }

        internal IAssetStorage Storage() => new LooseAssetStorage(_directory.Path, _manifest);
        public void Dispose() => _directory.Dispose();
    }

    private sealed class ReadGate
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledStorage(IAssetStorage source, Dictionary<Guid, ReadGate> gates) : IAssetStorage
    {
        public bool TryFind(Guid assetGuid, out AssetEntry entry) => source.TryFind(assetGuid, out entry);
        public async ValueTask<IRangeSource> OpenAsync(AssetEntry entry, CancellationToken cancellationToken = default)
        {
            if (gates.TryGetValue(entry.AssetGuid, out ReadGate? gate))
            {
                gate.Started.TrySetResult();
                await gate.Continue.Task.WaitAsync(cancellationToken);
            }
            return await source.OpenAsync(entry, cancellationToken);
        }
    }

    private readonly record struct StreamNumber(int Value) : IComponent;
    private readonly struct CopySources(WorldStreamSource[] input, WorldStreamSource[] output) : IJob
    {
        public void Execute() => input.CopyTo(output, 0);
    }
    private readonly struct NumberCodec : IComponentCodec<StreamNumber>
    {
        public void Write(ref DataWriter writer, in StreamNumber value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out StreamNumber value) => value = new(reader.ReadInt32());
    }
}
