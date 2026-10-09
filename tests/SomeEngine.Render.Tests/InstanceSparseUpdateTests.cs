using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class InstanceSparseUpdateTests
{
    [Fact]
    public void EditingOneGroupPreservesTheOtherGroupsValuesAndRevision()
    {
        using var data = new InstanceData();
        var composition = new RenderInstanceCompositionSystem<int, GroupComposer>(
            new QueryDefinitionBuilder().Read<BatchGroup>().Read<RenderTransform>()
                .Read<RenderPreviousTransform>().Build(),
            new GroupComposer(), groupingPartitions: 1, rowsPerPacket: 1);
        data.Prepare.Add(composition);
        data.Batches = () => composition.Current!.Groups.ToArray().Select(group => group.Batch).ToArray();
        Entity first = data.World.CreateEntity(new BatchGroup(1));
        data.World.Add(first, Current(1));
        data.World.Add(first, Previous(11));
        Entity second = data.World.CreateEntity(new BatchGroup(2));
        data.World.Add(second, Current(2));
        data.World.Add(second, Previous(12));
        data.ExecuteFrame();
        RenderInstanceBatch unchanged = composition.Current!.Groups.ToArray().Single(group => group.Key == 2).Batch;
        ulong revision = unchanged.ContentRevision;

        data.World.Replace(first, Current(9));
        data.ExecuteFrame();

        RenderInstanceBatch changed = composition.Current!.Groups.ToArray().Single(group => group.Key == 1).Batch;
        Assert.Equal(new[] { Current(9) }, data.Values(changed, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Current(2) }, data.Values(unchanged, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(12) }, data.Values(unchanged, RenderInstanceTransformProperties.PreviousTransform));
        Assert.Same(unchanged, composition.Current.Groups.ToArray().Single(group => group.Key == 2).Batch);
        Assert.Equal(revision, unchanged.ContentRevision);
    }

    [Fact]
    public void UnwrittenOptionalColumnKeepsItsSharedBindingAndGpuValues()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindShared(CurrentProperty, Current(7));
            rows.BindPerInstance(PreviousProperty);
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();
        BufferRange changedValue = data.PropertyRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform, 1);
        BufferRange changedBinding = data.MetadataRange(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform);
        writer.Update = (context, batch) =>
        {
            using var update = context.RewriteBatch(batch.Batch!, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(PreviousProperty, 1, Previous(19));
            batch.Batch = update.Publish();
        };

        data.ExecuteFrame();

        Assert.Equal(new[] { Current(7), Current(7) }, data.Values(writer.Batch!, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(19) }, data.Values(writer.Batch!, RenderInstanceTransformProperties.PreviousTransform));
        Assert.Equal(new[] { changedValue }, data.PropertyUploads);
        Assert.Equal(new[] { changedBinding }, data.MetadataUploads);
    }

    [Fact]
    public void UnwrittenOptionalUpdateKeepsTheBatchRevisionAndUploadsNothing()
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2) });
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();
        RenderInstanceBatch original = writer.Batch!;
        ulong revision = original.ContentRevision;
        writer.Update = (context, batch) =>
        {
            using var update = context.RewriteBatch(batch.Batch!, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            batch.Batch = update.Publish();
        };

        data.ExecuteFrame();

        Assert.Same(original, writer.Batch);
        Assert.Equal(revision, writer.Batch!.ContentRevision);
        Assert.Empty(data.PropertyUploads);
        Assert.Empty(data.MetadataUploads);
        Assert.Equal(new[] { Current(1), Current(2) }, data.Values(writer.Batch, RenderInstanceTransformProperties.CurrentTransform));
        var changed = new List<uint>();
        writer.Batch.CollectChangedInstances(revision, changed);
        Assert.Empty(changed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RequiredReplacementRejectsMissingRowsAndPreservesGpuValues(int writtenRows)
    {
        using var data = new InstanceData();
        var writer = new BatchWriter();
        data.Prepare.Add(writer);
        data.Batches = () => [writer.Batch!];
        writer.Update = (context, batch) =>
        {
            using var create = context.AllocateBatch(Layout, 2);
            var rows = create.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(1), Current(2) });
            rows.Write(PreviousProperty, new[] { Previous(11), Previous(12) });
            batch.Batch = create.Publish();
        };
        data.ExecuteFrame();
        ulong revision = writer.Batch!.ContentRevision;
        writer.Update = (context, batch) =>
        {
            using var update = context.RewriteBatch(batch.Batch!, Layout, replacedProperties: Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, new[] { Current(8), Current(9) });
            rows.Slice(0, writtenRows).Write(PreviousProperty,
                new[] { Previous(18), Previous(19) }.AsSpan(0, writtenRows));
            Assert.Throws<InvalidOperationException>(() => update.Publish());
        };

        data.ExecuteFrame();

        Assert.Equal(revision, writer.Batch!.ContentRevision);
        Assert.Empty(data.PropertyUploads);
        Assert.Empty(data.MetadataUploads);
        Assert.Equal(new[] { Current(1), Current(2) }, data.Values(writer.Batch, RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) }, data.Values(writer.Batch, RenderInstanceTransformProperties.PreviousTransform));
    }

    private static RenderInstancePropertyLayout Layout => RenderInstanceTransformProperties.Layout;
    private static ResolvedRenderInstanceProperty<RenderTransform> CurrentProperty =>
        Layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
    private static ResolvedRenderInstanceProperty<RenderPreviousTransform> PreviousProperty =>
        Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform);
    private static RenderTransform Current(float value) =>
        new(Quaternion.Identity, new Vector3(value, 0, 0), 1, Vector3.One);
    private static RenderPreviousTransform Previous(float value) => new(Current(value));
    private readonly record struct BatchGroup(int Value) : IComponent;

    private readonly struct GroupComposer : IRenderInstanceBatchComposer<int>
    {
        public RenderInstanceChanges GetChanges(ReadOnlyQueryPacket packet, uint lastSystemVersion) =>
            packet.ChangedSince<BatchGroup>(lastSystemVersion) ? RenderInstanceChanges.Structure :
            packet.ChangedSince<RenderTransform>(lastSystemVersion) ||
            packet.ChangedSince<RenderPreviousTransform>(lastSystemVersion)
                ? RenderInstanceChanges.Values : RenderInstanceChanges.None;
        public int CountGroups(ReadOnlyQueryPacket packet, int entityRow) => 1;
        public int GetGroup(ReadOnlyQueryPacket packet, int entityRow, int groupIndex) =>
            packet.Read<BatchGroup>()[entityRow].Value;
        public RenderInstancePropertyLayout GetLayout(in int group) => Layout;
        public void Bind(in int group, RenderInstanceWriteSlice destination)
        {
            destination.BindPerInstance(CurrentProperty);
            destination.BindPerInstance(PreviousProperty);
        }
        public void Write(in int group, int groupIndex, ReadOnlyQueryPacket packet, int entityRow,
            RenderInstanceWriteSlice destination)
        {
            destination.Write(CurrentProperty, packet.Read<RenderTransform>().Slice(entityRow, 1));
            destination.Write(PreviousProperty, packet.Read<RenderPreviousTransform>().Slice(entityRow, 1));
        }
    }

    private sealed class BatchWriter : ISystem<RenderPrepareSystemContext>
    {
        internal RenderInstanceBatch? Batch;
        internal Action<RenderPrepareSystemContext, BatchWriter>? Update;
        public void OnCreate(ref RenderPrepareSystemContext context) { }
        public void OnUpdate(ref RenderPrepareSystemContext context) => Update?.Invoke(context, this);
        public void OnDestroy(ref RenderPrepareSystemContext context)
        {
            if (Batch is not null) context.Retire(Batch);
        }
    }

    private sealed class InstanceData : IDisposable
    {
        private readonly RenderTestDevice _device = new(DeviceFeatures.None,
            [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)]);
        private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
        private readonly RenderInstanceStorageSystem _storage;
        private readonly RenderExtractionSystems _extraction;
        private readonly RenderFrameSystems _frames;
        private readonly BytesReceiver _properties = new(), _metadata = new();
        private readonly Dictionary<RenderInstanceBatch, RenderInstanceBatchView> _views = [];
        internal RenderWorld World { get; } = new();
        internal RenderPrepareSystems Prepare { get; }
        internal Func<RenderInstanceBatch[]> Batches { get; set; } = () => [];
        internal BufferRange[] PropertyUploads { get; private set; } = [];
        internal BufferRange[] MetadataUploads { get; private set; } = [];

        internal InstanceData()
        {
            _graph = new(_device.Backend, _device.Device,
                [_device.Backend.GetQueue(_device.Device, QueueType.Graphics),
                    _device.Backend.GetQueue(_device.Device, QueueType.Compute),
                    _device.Backend.GetQueue(_device.Device, QueueType.Copy)]);
            _storage = new(_device.Backend, _device.Device, World, Layout,
                new RenderInstanceOptions { RowCapacity = 8, BatchCapacity = 4 });
            _storage.Install(_graph);
            using (var edit = _graph.BeginEdit())
            {
                edit.AddReadback(_storage.PropertyData, BufferRange.Whole, _properties, "Sparse instance values");
                edit.AddReadback(_storage.BatchMetadata, BufferRange.Whole, _metadata, "Sparse instance bindings");
                edit.Commit();
            }
            _extraction = new(World);
            Prepare = new(World, _storage, _extraction);
            _frames = new(World, _storage);
            _frames.Add(new BatchReader(this));
        }

        internal void ExecuteFrame()
        {
            Assert.True(_graph.TryOpenNextFrame(out var next, TimeSpan.FromSeconds(10)));
            using (next)
            {
                Assert.True(_device.DataLock.TryBeginPrepare(out var prepare));
                using (prepare) Prepare.Update(prepare, next);
                Assert.True(_device.DataLock.TryBeginRead(out var read));
                using (read)
                using (var frame = _graph.BeginFrame(next))
                {
                    _frames.Update(read, frame);
                    PropertyUploads = frame.GetBufferUploadRanges(_storage.PropertyData).Ranges.ToArray();
                    MetadataUploads = frame.GetBufferUploadRanges(_storage.BatchMetadata).Ranges.ToArray();
                    var completions = new QueueCompletion[_graph.MaximumQueueCompletionCount];
                    int count = frame.Execute(completions);
                    _device.RenderDevice.TrackSubmission(completions.AsSpan(0, count));
                    read.Dispose();
                    foreach (var completion in completions.AsSpan(0, count))
                        Assert.Equal(WaitStatus.Completed, _device.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                }
            }
            _graph.CollectCompleted();
        }

        internal T[] Values<T>(RenderInstanceBatch batch, RenderInstanceProperty<T> property) where T : unmanaged
        {
            var values = new T[batch.InstanceCount];
            for (int row = 0; row < values.Length; row++)
                values[row] = MemoryMarshal.Read<T>(_properties.Bytes.AsSpan(
                    checked((int)PropertyRange(batch, property, row).Offset)));
            return values;
        }

        internal BufferRange MetadataRange<T>(RenderInstanceBatch batch, RenderInstanceProperty<T> property) where T : unmanaged
        {
            var descriptor = Layout.Resolve(property).Descriptor;
            return new(_views[batch].MetadataRange.Offset + checked((ulong)descriptor.MetadataWordOffset * sizeof(uint)),
                checked((ulong)descriptor.Encoding.MetadataWordCount * sizeof(uint)));
        }

        internal BufferRange PropertyRange<T>(RenderInstanceBatch batch, RenderInstanceProperty<T> property, int row)
            where T : unmanaged
        {
            var descriptor = Layout.Resolve(property).Descriptor;
            uint binding = MemoryMarshal.Read<uint>(_metadata.Bytes.AsSpan(checked((int)MetadataRange(batch, property).Offset)));
            ulong offset = binding & RenderInstanceLinearMetadata.AddressMask;
            if ((binding & RenderInstanceLinearMetadata.PerInstanceBit) != 0)
                offset += checked((ulong)(row * descriptor.Encoding.StorageStride));
            return new(offset, checked((ulong)descriptor.Encoding.StorageStride));
        }

        public void Dispose()
        {
            _frames.Dispose();
            Assert.True(_device.DataLock.TryBeginPrepare(out var prepare));
            using (prepare)
            {
                Prepare.Shutdown(prepare);
                _storage.Shutdown(prepare);
            }
            Prepare.Dispose();
            _storage.Dispose();
            _graph.Dispose();
            _extraction.Dispose();
            World.Dispose();
            _device.Dispose();
        }

        private sealed class BatchReader(InstanceData data) : ISystem<RenderFrameSystemContext>
        {
            public void OnCreate(ref RenderFrameSystemContext context) { }
            public void OnDestroy(ref RenderFrameSystemContext context) { }
            public void OnUpdate(ref RenderFrameSystemContext context)
            {
                using var instances = context.OpenInstances();
                data._views.Clear();
                foreach (var batch in data.Batches())
                    data._views.Add(batch, instances.Bind(batch, Layout));
            }
        }
    }

    private sealed class BytesReceiver : IGraphReadbackReceiver
    {
        internal byte[] Bytes = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Bytes = bytes.ToArray();
    }
}
