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

public sealed class InstancePreparationTests
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
        using var data = new InstanceBuffers(4);
        Entity first = data.CreateEntity(Transform(1), Previous(11));
        Entity second = data.CreateEntity(Transform(2), Previous(12));
        data.ExecuteFrame();
        data.World.Replace(second, Transform(8));
        data.ExecuteFrame();
        Assert.Equal(new[] { data.World.Read<RenderTransform>(first), data.World.Read<RenderTransform>(second) },
            data.CurrentTransforms());
        Assert.Equal(new[] { data.World.Read<RenderPreviousTransform>(first), data.World.Read<RenderPreviousTransform>(second) },
            data.PreviousTransforms());
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

    private readonly struct TransformComposer : IRenderInstanceBatchComposer<RenderInstanceSingleGroup>
    {
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
        private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
        private readonly BytesReceiver _properties = new();
        private readonly BytesReceiver _metadata = new();
        private RenderInstanceBatchView _view;
        internal RenderWorld World { get; } = new();
        internal RenderPrepareSystems Prepare { get; }
        internal RenderInstanceCompositionSystem<RenderInstanceSingleGroup, TransformComposer> Composition { get; }

        internal InstanceBuffers(int capacity)
        {
            _graph = new(_device.Backend, _device.Device,
                [_device.Backend.GetQueue(_device.Device, QueueType.Graphics),
                    _device.Backend.GetQueue(_device.Device, QueueType.Compute),
                    _device.Backend.GetQueue(_device.Device, QueueType.Copy)],
                new RenderGraphDesc(MaximumFramesInFlight: 2));
            _instances = new(_device.Backend, _device.Device, World,
                RenderInstanceTransformProperties.Layout,
                new RenderInstanceOptions { RowCapacity = capacity, BatchCapacity = 2 });
            _instances.Install(_graph);
            using (var edit = _graph.BeginEdit())
            {
                edit.AddReadback(_instances.PropertyData, BufferRange.Whole, _properties, "Instance properties");
                edit.AddReadback(_instances.BatchMetadata, BufferRange.Whole, _metadata, "Instance metadata");
                edit.Commit();
            }
            _extraction = new(World);
            Prepare = new(World, _instances, _extraction);
            Composition = new(new QueryDefinitionBuilder().Read<RenderTransform>().Read<RenderPreviousTransform>().Build(),
                new TransformComposer(), groupingPartitions: 1, rowsPerPacket: 2);
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
            Assert.True(_device.DataLock.TryBeginPrepare(out var prepare));
            using (prepare)
            {
                Prepare.Update(prepare, next);
            }
            return next;
        }

        internal void ExecuteFrame()
        {
            using var next = ComposeFrame();
            {
                Assert.True(_device.DataLock.TryBeginRead(out var frame));
                using (frame)
                using (var commands = _graph.BeginFrame(next))
                {
                    using var systems = new RenderFrameSystems(World, _instances);
                    var reader = new BatchReader(Composition);
                    systems.Add(reader);
                    systems.Update(frame, commands);
                    _view = reader.View;
                    var completions = new QueueCompletion[_graph.MaximumQueueCompletionCount];
                    int count = commands.Execute(completions);
                    _device.RenderDevice.TrackSubmission(completions.AsSpan(0, count));
                    frame.Dispose();
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
            Assert.True(_device.DataLock.TryBeginPrepare(out var scope));
            using (scope)
            {
                Prepare.Shutdown(scope);
                _instances.Shutdown(scope);
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
