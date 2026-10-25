using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;

namespace SomeEngine.Render.Tests;

public sealed class InstancePreparationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(129)]
    public void RepeatedWritesRestoreCanceledValuesAndPreserveUntouchedRows(int count)
    {
        using var test = new RenderTestDevice();
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var builder = new RenderInstancePropertyLayoutBuilder();
        var token = builder.Register<Vector4>("Instance preparation tests", new("test.instance.values"),
            new("test.float4", 16, 16, 16, 1));
        var layout = builder.Freeze();
        var property = layout.Resolve(token);
        var instances = new RenderInstanceStorageSystem(test.Backend, test.Device, test.Coordinator, world,
            layout, new RenderInstanceOptions { RowCapacity = count, BatchCapacity = 1 });
        var writer = new Writer(layout, property, count);
        var reader = new Reader(writer, layout);
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        prepare.Add(writer);
        using var frames = new RenderFrameSystems(world, instances);
        frames.Add(reader);
        using var graph = new global::SomeEngine.RenderGraph.RenderGraph(test.Backend, test.Device,
            [test.Backend.GetQueue(test.Device, QueueType.Graphics)]);
        try
        {
            Write();
            Vector4[] expected = writer.Values.ToArray();
            Assert.Equal(expected, Read());
            for (int iteration = 0; iteration < 4; iteration++)
            {
                writer.Values = Enumerable.Range(0, count).Select(row => new Vector4(iteration + 10, row, -row, 7)).ToArray();
                writer.Cancel = true;
                Write();
                Assert.Equal(expected, Read());
                writer.Cancel = false;
                writer.OneRow = true;
                Write();
                expected[count / 2] = writer.Values[count / 2];
                Assert.Equal(expected, Read());
                writer.OneRow = false;
            }
            writer.FailAfterWrite = true;
            Assert.Throws<ArgumentException>(Write);
            writer.FailAfterWrite = false;
            writer.Cancel = true;
            Write();
            Assert.Equal(expected, Read());
        }
        finally
        {
            Assert.True(test.Coordinator.TryBeginPrepare(out var shutdown));
            using (shutdown)
            {
                prepare.Shutdown(shutdown);
                instances.Shutdown(shutdown);
                shutdown.Commit();
            }
            prepare.Dispose();
            instances.Dispose();
        }

        void Write()
        {
            Assert.True(test.Coordinator.TryBeginPrepare(out var scope));
            using (scope)
            {
                Assert.False(prepare.ApplyExtraction(scope));
                Assert.False(prepare.ApplyExtraction(scope));
                prepare.Update(scope);
                scope.Commit();
            }
        }

        Vector4[] Read()
        {
            Assert.True(test.Coordinator.TryBeginFrame(out var frame));
            using (frame)
            using (var commands = graph.BeginFrame())
            {
                frames.Update(frame, commands);
                var view = reader.View;
                using var metadata = test.Backend.Map(view.Metadata, MapType.Read, view.MetadataRange);
                uint word = MemoryMarshal.Read<uint>(metadata.Bytes);
                Assert.NotEqual(0u, word & RenderInstanceLinearMetadata.PerInstanceBit);
                using var data = test.Backend.Map(view.PropertyData!, MapType.Read, BufferRange.Whole);
                return MemoryMarshal.Cast<byte, Vector4>(data.Bytes.Slice(
                    checked((int)(word & RenderInstanceLinearMetadata.AddressMask)), count * 16)).ToArray();
            }
        }
    }

    [Fact]
    public void AbandoningExtractionOnlyDoesNotInvalidateGpuPreparation()
    {
        using var test = new RenderTestDevice();
        using var world = new RenderWorld();
        using var extraction = new RenderExtractionSystems(world);
        var instances = new RenderInstanceStorageSystem(test.Backend, test.Device, test.Coordinator, world,
            RenderInstanceTransformProperties.Layout, new RenderInstanceOptions { RowCapacity = 1, BatchCapacity = 1 });
        var prepare = new RenderPrepareSystems(world, instances, extraction);
        try
        {
            Assert.True(test.Coordinator.TryBeginPrepare(out var scope));
            using (scope) Assert.False(prepare.ApplyExtraction(scope));
            Assert.True(test.Coordinator.TryBeginFrame(out var frame));
            frame.Dispose();
        }
        finally
        {
            Assert.True(test.Coordinator.TryBeginPrepare(out var shutdown));
            using (shutdown)
            {
                prepare.Shutdown(shutdown);
                instances.Shutdown(shutdown);
                shutdown.Commit();
            }
            prepare.Dispose();
            instances.Dispose();
        }
    }

    private sealed class Writer(RenderInstancePropertyLayout layout,
        ResolvedRenderInstanceProperty<Vector4> property, int count) : ISystem<RenderPrepareSystemContext>
    {
        internal RenderInstanceBatch? Batch;
        internal Vector4[] Values = Enumerable.Range(0, count).Select(row => new Vector4(1, row, 3, 4)).ToArray();
        internal bool Cancel;
        internal bool OneRow;
        internal bool FailAfterWrite;

        public void OnCreate(ref RenderPrepareSystemContext context) { }
        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            using var write = Batch is null ? context.AllocateBatch(layout, count) : context.RewriteBatch(Batch, layout);
            var all = write.OpenWrite(layout);
            all.BindPerInstance(property);
            if (OneRow) all.Write(property, count / 2, Values[count / 2]);
            else all.Write(property, Values);
            if (FailAfterWrite) all.Write(property, Values.AsSpan(0, count - 1));
            if (!Cancel) Batch = write.Publish();
        }
        public void OnDestroy(ref RenderPrepareSystemContext context)
        {
            if (Batch is not null) context.Retire(Batch);
            Batch = null;
        }
    }

    private sealed class Reader(Writer writer, RenderInstancePropertyLayout layout) : ISystem<RenderFrameSystemContext>
    {
        internal RenderInstanceBatchView View;
        public void OnCreate(ref RenderFrameSystemContext context) { }
        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            using var instances = context.OpenInstances();
            View = instances.Bind(writer.Batch!, layout);
        }
        public void OnDestroy(ref RenderFrameSystemContext context) { }
    }
}

[Collection("Render job configuration")]
public sealed class InstanceCompositionTests
{
    [Fact]
    public void UnchangedRenderWorldKeepsItsInstanceComposition()
    {
        using var data = new InstanceBuffers(4);
        data.CreateEntity(Transform(1), Previous(11));
        data.ExecuteFrame();
        data.ExecuteFrame();
        Assert.False(data.Composition.Changed);
    }

    [Fact]
    public void SingleRowChangeReachesGpuAndPreservesOtherRowsAndProperties()
    {
        const int count = 4097;
        bool oneInstancePerEntity = true;
        using var data = new InstanceBuffers(count, () => oneInstancePerEntity, rowsPerPacket: 257,
            jobs: new WorldJobs(SomeEngine.Job.JobSystem.Default));
        Entity[] entities = Enumerable.Range(0, count)
            .Select(index => data.CreateEntity(Transform(index), Previous(index + 20))).ToArray();
        data.ExecuteFrame();
        RenderInstanceBatches<RenderInstanceSingleGroup> original = data.Composition.Current!;
        RenderInstanceBatch batch = original.Groups[0].Batch;
        Assert.Equal(count, original.EntityCount);
        Assert.Equal(count, original.AddressCount);
        Assert.Equal(1, original.GroupCount);
        for (int row = 0; row < count; row++)
        {
            var addresses = original.RowsForEntity(row);
            Assert.Equal(1, addresses.Length);
            Assert.Same(batch, addresses[0].Batch);
            Assert.Equal(default(RenderInstanceSingleGroup), addresses[0].Key);
            Assert.Equal(row, addresses[0].Row);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = original.RowsForEntity(-1); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = original.RowsForEntity(count); });
        var borrowed = original.RowsForEntity(count - 1);

        // Keep one contribution, but exercise validation and general writes over the existing rows.
        oneInstancePerEntity = false;
        data.World.Replace(entities[^1], Transform(-8));
        data.ExecuteFrame();

        Assert.Same(original, data.Composition.Current);
        Assert.Same(batch, borrowed[0].Batch);
        Assert.Equal(count - 1, borrowed[0].Row);
        Assert.Equal(entities.Select(data.World.Read<RenderTransform>).ToArray(), data.CurrentTransforms());
        Assert.Equal(entities.Select(data.World.Read<RenderPreviousTransform>).ToArray(), data.PreviousTransforms());
    }

    [Fact]
    public void ReplacingBatchMembershipReachesGpuInRenderWorldOrder()
    {
        using var data = new InstanceBuffers(8);
        Entity first = data.CreateEntity(Transform(1), Previous(11));
        data.ExecuteFrame();
        Entity second = data.CreateEntity(Transform(2), Previous(12));
        Entity third = data.CreateEntity(Transform(3), Previous(13));
        data.ExecuteFrame();
        Assert.Equal(new[] { first, second, third }.Select(data.World.Read<RenderTransform>).ToArray(),
            data.CurrentTransforms());
    }

    [Fact]
    public void CancelledBatchUpdateKeepsGpuValuesAndMetadataConsistentWithRenderWorld()
    {
        using var data = new InstanceBuffers(4);
        Entity first = data.CreateEntity(Transform(1), Previous(11));
        Entity second = data.CreateEntity(Transform(2), Previous(12));
        data.ExecuteFrame();
        data.Prepare.Add(new CancelledUpdate(data.Composition));
        data.ExecuteFrame();
        Assert.Equal(new[] { data.World.Read<RenderTransform>(first), data.World.Read<RenderTransform>(second) },
            data.CurrentTransforms());
        Assert.Equal(new[] { data.World.Read<RenderPreviousTransform>(first), data.World.Read<RenderPreviousTransform>(second) },
            data.PreviousTransforms());
    }

    [Fact]
    public void AllTransformsChangingEveryFrameReachGpuAcrossConsecutiveFrames()
    {
        using var data = new InstanceBuffers(16);
        Entity[] entities = Enumerable.Range(0, 8)
            .Select(index => data.CreateEntity(Transform(index), Previous(index + 20))).ToArray();
        for (int frame = 0; frame < 12; frame++)
        {
            for (int row = 0; row < entities.Length; row++)
            {
                data.World.Replace(entities[row], Transform(frame * 10 + row));
                data.World.Replace(entities[row], Previous(frame * 10 + row + 20));
            }
            data.ExecuteFrame();
            Assert.Equal(entities.Select(data.World.Read<RenderTransform>).ToArray(), data.CurrentTransforms());
            Assert.Equal(entities.Select(data.World.Read<RenderPreviousTransform>).ToArray(), data.PreviousTransforms());
        }
    }

    [Fact]
    public void AbandonedNextFrameRewritesRenderWorldValuesOnTheFollowingFrame()
    {
        using var data = new InstanceBuffers(4);
        Entity entity = data.CreateEntity(Transform(1), Previous(11));
        data.ExecuteFrame();
        data.World.Replace(entity, Transform(9));
        using (data.ComposeFrame()) { }
        data.ExecuteFrame();
        Assert.Equal(new[] { data.World.Read<RenderTransform>(entity) }, data.CurrentTransforms());
    }

    private static RenderTransform Transform(float value) =>
        new(Quaternion.Identity, new Vector3(value, -value, value * 2), 1, Vector3.One);

    private static RenderPreviousTransform Previous(float value) => new(Transform(value));

    private readonly struct TransformComposer(Func<bool>? oneInstancePerEntity = null)
        : IRenderInstanceBatchComposer<RenderInstanceSingleGroup>
    {
        public bool OneInstancePerEntity => oneInstancePerEntity?.Invoke() ?? true;

        public RenderInstanceChanges GetChanges(ReadOnlyQueryPacket packet, uint lastSystemVersion) =>
            packet.ChangedSince<RenderTransform>(lastSystemVersion) ||
            packet.ChangedSince<RenderPreviousTransform>(lastSystemVersion)
                ? RenderInstanceChanges.Values : RenderInstanceChanges.None;

        public int CountGroups(ReadOnlyQueryPacket packet, int entityRow) => 1;
        public RenderInstanceSingleGroup GetGroup(ReadOnlyQueryPacket packet, int entityRow, int groupIndex) => default;
        public RenderInstancePropertyLayout GetLayout(in RenderInstanceSingleGroup group) =>
            RenderInstanceTransformProperties.Layout;

        public void Bind(in RenderInstanceSingleGroup group, RenderInstanceWriteSlice destination)
        {
            destination.BindPerInstance(RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.CurrentTransform));
            destination.BindPerInstance(RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform));
        }

        public void WritePacket(in RenderInstanceSingleGroup group, int groupIndex,
            ReadOnlyQueryPacket packet, RenderInstanceWriteSlice destination)
        {
            destination.Write(RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.CurrentTransform),
                packet.Read<RenderTransform>());
            destination.Write(RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform),
                packet.Read<RenderPreviousTransform>());
        }

        public void Write(in RenderInstanceSingleGroup group, int groupIndex,
            ReadOnlyQueryPacket packet, int entityRow, RenderInstanceWriteSlice destination)
        {
            var current = RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
            var previous = RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform);
            destination.Write(current, packet.Read<RenderTransform>().Slice(entityRow, 1));
            destination.Write(previous, packet.Read<RenderPreviousTransform>().Slice(entityRow, 1));
        }
    }

    private sealed class CancelledUpdate(
        RenderInstanceCompositionSystem<RenderInstanceSingleGroup, TransformComposer> composition)
        : ISystem<RenderPrepareSystemContext>
    {
        public void OnCreate(ref RenderPrepareSystemContext context) { }
        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            using var update = context.RewriteBatch(composition.Current!.Groups[0].Batch,
                RenderInstanceTransformProperties.Layout);
            var values = update.OpenWrite(RenderInstanceTransformProperties.Layout);
            // Change both a value and its binding; disposing the batch drops both ranges.
            values.BindShared(RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.CurrentTransform),
                Transform(999));
            values.BindPerInstance(RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform));
            values.Write(RenderInstanceTransformProperties.Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform),
                0, Previous(888));
        }
        public void OnDestroy(ref RenderPrepareSystemContext context) { }
    }

    private sealed class InstanceBuffers : IDisposable
    {
        private readonly RenderTestDevice _device = new(DeviceFeatures.None,
            [new DeviceQueueDesc(QueueType.Graphics), new DeviceQueueDesc(QueueType.Compute), new DeviceQueueDesc(QueueType.Copy)]);
        private readonly RenderExtractionSystems _extraction;
        private readonly RenderInstanceStorageSystem _instances;
        private readonly RenderInstanceDataTransfer _transfer;
        private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
        private readonly BytesReceiver _properties = new();
        private readonly BytesReceiver _metadata = new();
        private RenderInstanceBatchView _view;
        internal RenderWorld World { get; }
        internal RenderPrepareSystems Prepare { get; }
        internal RenderInstanceCompositionSystem<RenderInstanceSingleGroup, TransformComposer> Composition { get; }

        internal InstanceBuffers(int capacity, Func<bool>? oneInstancePerEntity = null, int rowsPerPacket = 2,
            WorldJobs? jobs = null)
        {
            World = new(execution: jobs);
            _graph = new(_device.Backend, _device.Device,
                [_device.Backend.GetQueue(_device.Device, QueueType.Graphics),
                    _device.Backend.GetQueue(_device.Device, QueueType.Compute),
                    _device.Backend.GetQueue(_device.Device, QueueType.Copy)],
                new RenderGraphDesc(MaximumFramesInFlight: 2));
            _instances = new(_device.Backend, _device.Device, _device.Coordinator, World,
                RenderInstanceTransformProperties.Layout,
                new RenderInstanceOptions { RowCapacity = capacity, BatchCapacity = 2 });
            _transfer = _instances.GetDataTransfer(_graph);
            using (var edit = _graph.BeginEdit())
            {
                edit.AddReadback(_transfer.Data, BufferRange.Whole, _properties, "Instance properties");
                edit.Commit();
            }
            _extraction = new(World);
            Prepare = new(World, _instances, _extraction);
            Composition = new(new QueryDefinitionBuilder().Read<RenderTransform>().Read<RenderPreviousTransform>().Build(),
                new TransformComposer(oneInstancePerEntity), groupingPartitions: 1, rowsPerPacket);
            Prepare.Add(Composition);
        }

        internal Entity CreateEntity(RenderTransform current, RenderPreviousTransform previous)
        {
            Entity entity = World.CreateEntity(current);
            World.Add(entity, previous);
            return entity;
        }

        internal RenderGraphNextFrame ComposeFrame()
        {
            Assert.True(_graph.TryOpenNextFrame(out var next, TimeSpan.FromSeconds(10)));
            try
            {
                Assert.True(_device.Coordinator.TryBeginPrepare(out var prepare));
                using (prepare)
                {
                    Prepare.Update(prepare);
                    prepare.Commit();
                }
                return next;
            }
            catch (Exception error)
            {
                try { next.Dispose(); }
                catch (Exception cleanupError) { throw new AggregateException(error, cleanupError); }
                throw;
            }
        }

        internal void ExecuteFrame()
        {
            using var next = ComposeFrame();
            {
                Assert.True(_device.Coordinator.TryBeginFrame(out var frame));
                using (frame)
                using (var commands = _graph.BeginFrame(next))
                {
                    using var systems = new RenderFrameSystems(World, _instances);
                    var reader = new BatchReader(Composition);
                    systems.Add(reader);
                    systems.Update(frame, commands);
                    _view = reader.View;
                    using (var metadata = _device.Backend.Map(_view.Metadata, MapType.Read, BufferRange.Whole))
                        _metadata.Bytes = metadata.Bytes.ToArray();
                    var transferFrame = commands;
                    _transfer.Stage(ref transferFrame, Composition.Current!.Groups[0].Batch, _view);
                    var completions = new QueueCompletion[_graph.MaximumQueueCompletionCount];
                    int count;
                    try { count = commands.Execute(completions); }
                    catch
                    {
                        QueueCompletion[] partial = completions.Where(value => value != default).ToArray();
                        if (partial.Length != 0 && !frame.IsClosed) frame.Complete(partial);
                        throw;
                    }
                    frame.Complete(completions.AsSpan(0, count));
                    foreach (var completion in completions.AsSpan(0, count))
                        Assert.Equal(WaitStatus.Completed, _device.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
                }
            }
            _graph.CollectCompleted();
        }

        internal RenderTransform[] CurrentTransforms() => Read(RenderInstanceTransformProperties.CurrentTransform);
        internal RenderPreviousTransform[] PreviousTransforms() => Read(RenderInstanceTransformProperties.PreviousTransform);

        private T[] Read<T>(RenderInstanceProperty<T> property) where T : unmanaged
        {
            var field = RenderInstanceTransformProperties.Layout.Resolve(property).Descriptor;
            uint word = MemoryMarshal.Read<uint>(_metadata.Bytes.AsSpan(
                checked((int)_view.MetadataRange.Offset + field.MetadataWordOffset * 4), 4));
            int offset = checked((int)(word & RenderInstanceLinearMetadata.AddressMask));
            var result = new T[_view.InstanceCount];
            for (int row = 0; row < result.Length; row++)
                result[row] = MemoryMarshal.Read<T>(_properties.Bytes.AsSpan(
                    offset + ((word & RenderInstanceLinearMetadata.PerInstanceBit) != 0 ? row * field.Encoding.StorageStride : 0)));
            return result;
        }

        public void Dispose()
        {
            Assert.True(_device.Coordinator.TryBeginPrepare(out var scope));
            using (scope)
            {
                Prepare.Shutdown(scope);
                _instances.Shutdown(scope);
                scope.Commit();
            }
            Prepare.Dispose();
            _instances.Dispose();
            _graph.Dispose();
            _extraction.Dispose();
            World.Dispose();
            _device.Dispose();
        }
    }

    private sealed class BatchReader(
        RenderInstanceCompositionSystem<RenderInstanceSingleGroup, TransformComposer> composition)
        : ISystem<RenderFrameSystemContext>
    {
        internal RenderInstanceBatchView View;
        public void OnCreate(ref RenderFrameSystemContext context) { }
        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            using var instances = context.OpenInstances();
            View = instances.Bind(composition.Current!.Groups[0].Batch, RenderInstanceTransformProperties.Layout);
        }
        public void OnDestroy(ref RenderFrameSystemContext context) { }
    }

    private sealed class BytesReceiver : IGraphReadbackReceiver
    {
        internal byte[] Bytes = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Bytes = bytes.ToArray();
    }
}
