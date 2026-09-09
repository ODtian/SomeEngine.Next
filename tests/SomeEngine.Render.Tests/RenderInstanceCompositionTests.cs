using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class RenderInstanceCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SparseValueChangesPreserveOtherRowsAndForcedUpdatesRefreshAllRows(bool singleGroup)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (singleGroup) CheckUpdates<RenderInstanceSingleGroup>();
        else CheckUpdates<int>();
    }

    private static void CheckUpdates<TKey>() where TKey : notnull
    {
        var layoutBuilder = new RenderInstancePropertyLayoutBuilder();
        RenderInstanceProperty<Value> property = layoutBuilder.Register<Value>("composition test",
            new RenderInstancePropertyKey("test.composition.value"), new RenderInstancePropertyEncoding(
                "test.value", valueSize: 4, storageAlignment: 4, storageStride: 4, metadataWordCount: 1));
        RenderInstancePropertyLayout layout = layoutBuilder.Freeze();
        var world = new RenderWorld();
        Entity[] entities = Enumerable.Range(0, 130).Select(i => world.CreateEntity(new Value(i))).ToArray();
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<Value>().Build());
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        AdapterEnumerationOptions options = new(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(options, [], out int adapterCount);
        var adapters = new AdapterInfo[adapterCount];
        backend.TryEnumerateAdapters(options, adapters, out _);
        AdapterInfo warp = adapters.First(adapter => !adapter.HardwareAccelerated);
        using Device device = backend.CreateDevice(new DeviceDesc(warp.Id, [new DeviceQueueDesc(QueueType.Graphics)]));
        using var renderDevice = new RenderDevice(backend, device);
        using var coordinator = new RenderFrameCoordinator(renderDevice);
        var instances = new RenderInstanceStorageSystem(backend, device, coordinator, world, layout,
            new RenderInstanceOptions { RowCapacity = 256, BatchCapacity = 4 });
        var builder = new RenderInstanceBatchBuilder<TKey>(groupingPartitions: 2, rowsPerPacket: 64);
        var counter = new WriteCount();
        var composer = new ValueComposer<TKey>(layout, layout.Resolve(property), counter);
        uint lastVersion = 0;
        bool Update(RenderInstanceChanges forced = RenderInstanceChanges.None)
        {
            Assert.True(coordinator.TryBeginPrepare(out RenderPrepareScope? scope));
            using (scope)
            {
                bool changed;
                uint currentVersion = world.AcquireSystemVersion();
                using (RenderInstanceWriteScope write = instances.OpenWrite(scope))
                {
                    var context = new RenderPrepareSystemContext(world, instances, write, lastVersion, currentVersion);
                    changed = builder.Update(context, query, in composer, forced);
                }
                scope.Commit();
                lastVersion = currentVersion;
                return changed;
            }
        }
        Assert.True(Update());
        Assert.Equal(130, counter.Rows);
        RenderInstanceBatch batch = builder.Current!.Groups[0].Batch;
        ulong before = batch.ContentRevision;
        counter.Rows = 0;
        _ = world.AcquireSystemVersion();
        world.Replace(entities[3], new Value(303));
        world.Replace(entities[129], new Value(9129));
        Assert.True(Update());
        Assert.Same(batch, builder.Current.Groups[0].Batch);
        Assert.Equal(2, counter.Rows);
        var values = new Value[130];
        instances.Storage.ReadInstances(batch, layout.Resolve(property), values.AsSpan());
        Assert.Equal(Enumerable.Range(0, 130).Select(i => new Value(i == 3 ? 303 : i == 129 ? 9129 : i)), values);
        var changedRows = new List<uint>();
        batch.CollectChangedInstances(before, changedRows);
        Assert.Equal([3u, 129u], changedRows);
        counter.Rows = 0;
        Assert.False(Update());
        Assert.Equal(0, counter.Rows);
        Assert.True(Update(RenderInstanceChanges.Values));
        Assert.Equal(130, counter.Rows);
        Assert.True(coordinator.TryBeginPrepare(out RenderPrepareScope? shutdown));
        using (shutdown)
        {
            using (RenderInstanceWriteScope write = instances.OpenWrite(shutdown))
                builder.Clear(new RenderPrepareSystemContext(world, instances, write, lastVersion, world.AcquireSystemVersion()));
            instances.Shutdown(shutdown);
            shutdown.Commit();
        }
        instances.Dispose();
        world.ReleaseQuery(query);
    }

    private readonly record struct Value(int Number) : IComponent;
    private sealed class WriteCount { internal int Rows; }

    private readonly struct ValueComposer<TKey>(RenderInstancePropertyLayout layout,
        ResolvedRenderInstanceProperty<Value> property, WriteCount counter) : IRenderInstanceBatchComposer<TKey>
        where TKey : notnull
    {
        public RenderInstanceChanges GetChanges(ReadOnlyQueryPacket packet, uint lastSystemVersion) =>
            packet.ChangedSince<Value>(lastSystemVersion) ? RenderInstanceChanges.Values : RenderInstanceChanges.None;
        public int CountGroups(ReadOnlyQueryPacket packet, int entityRow) => 1;
        public TKey GetGroup(ReadOnlyQueryPacket packet, int entityRow, int groupIndex) => default!;
        public RenderInstancePropertyLayout GetLayout(in TKey group) => layout;
        public void Bind(in TKey group, RenderInstanceWriteSlice destination) => destination.BindPerInstance(property);
        public void Write(in TKey group, int groupIndex, ReadOnlyQueryPacket packet, int entityRow, RenderInstanceWriteSlice destination)
        {
            Interlocked.Increment(ref counter.Rows);
            destination.Write(property, packet.Read<Value>().Slice(entityRow, 1));
        }
        public void WritePacket(in TKey group, int groupIndex, ReadOnlyQueryPacket packet, RenderInstanceWriteSlice destination)
        {
            Interlocked.Add(ref counter.Rows, packet.Count);
            destination.Write(property, packet.Read<Value>());
        }
    }
}
