using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Vulkan;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Tests;

public sealed class InstanceDataTransferTests
{
    [Fact]
    public void MetadataOnlyUpdatesReachEachGraph()
    {
        using var data = new InstanceData(2);
        data.Run([0, 1]);
        uint binding = data.Binding(CurrentProperty) & RenderInstanceLinearMetadata.AddressMask;
        data.Edit = context =>
        {
            using var update = context.RewriteBatch(data.Batch, Layout);
            update.OpenWrite(Layout).BindMetadata(CurrentProperty, new uint[] { binding });
            update.Publish();
        };

        data.Run([0]);
        data.AssertDeviceMetadata(0);
        data.Run([1]);
        data.AssertDeviceMetadata(1);
    }

    [Fact]
    public void AdjacentRevisionCopiesOnlyTheWrittenPropertyRows()
    {
        using var data = new InstanceData(4);
        data.Run([0]);
        data.Poison([0]);
        data.Edit = context =>
        {
            using var update = context.RewriteBatch(data.Batch, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            rows.Write(CurrentProperty, 0, Current(81));
            rows.Write(PreviousProperty, 1, Previous(92));
            update.Publish();
        };
        data.Run([0]);
        data.AssertOnlyCopied(0, data.Range(CurrentProperty, 0), data.Range(PreviousProperty, 1));
    }

    [Fact]
    public void FullSpanAndPartialColumnWritesKeepIndependentExactCoverage()
    {
        using var data = new InstanceData(129);
        data.Run([0]);
        data.Poison([0]);
        data.Edit = context =>
        {
            using var update = context.RewriteBatch(data.Batch, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            RenderTransform[] current = Enumerable.Repeat(Current(81), data.Count).ToArray();
            // The partial property allocates the shared bitmap; the full property must not
            // be mistaken for an empty mask merely because that bitmap now exists.
            Parallel.Invoke(
                () => rows.Write(CurrentProperty, current),
                () => rows.Write(PreviousProperty, 64, Previous(92)));
            update.Publish();
        };
        data.Run([0]);
        BufferRange first = data.Range(CurrentProperty, 0);
        data.AssertOnlyCopied(0,
            new BufferRange(first.Offset, first.Size * checked((ulong)data.Count)),
            data.Range(PreviousProperty, 64));
    }

    [Fact]
    public void TwoGraphsConsumeTheSameDeltaIndependently()
    {
        using var data = new InstanceData(2);
        data.Run([0, 1]);
        data.Poison([0, 1]);
        data.ChangeCurrent(0, 81);
        data.Run([0]);
        data.AssertOnlyCopied(0, data.Range(CurrentProperty, 0));
        data.Run([1]);
        data.AssertOnlyCopied(1, data.Range(CurrentProperty, 0));
    }

    [Fact]
    public void MissedPredecessorCopiesTheWholeBatchThenReturnsToSparseCopies()
    {
        using var data = new InstanceData(2);
        data.Run([0, 1]);
        data.Poison([0, 1]);
        data.ChangeCurrent(0, 81);
        data.Run([0]);
        data.ChangeCurrent(1, 92);
        data.Run([0]);
        data.Run([1]);
        data.AssertFullCopy(1);
        data.Poison([1]);
        data.ChangeCurrent(0, 103);
        data.Run([1]);
        data.AssertOnlyCopied(1, data.Range(CurrentProperty, 0));
    }

    [Fact]
    public void DiscardedStagingDoesNotAcknowledgeTheDelta()
    {
        using var data = new InstanceData(2);
        data.Run([0]);
        data.Poison([0]);
        data.ChangeCurrent(0, 81);
        data.Run([0], discard: true);
        data.Run([0]);
        data.AssertOnlyCopied(0, data.Range(CurrentProperty, 0));
    }

    [Fact]
    public void DiscardedRevisionFollowedByAnotherUpdateRequiresFullCatchup()
    {
        using var data = new InstanceData(2);
        data.Run([0]);
        data.Poison([0]);
        data.ChangeCurrent(0, 81);
        data.Run([0], discard: true);
        data.ChangeCurrent(1, 92);
        data.Run([0]);
        data.AssertFullCopy(0);
    }

    [Fact]
    public void CancelledWritesAndBindOnlyUpdatesLeaveAllGpuBytesUntouched()
    {
        using var data = new InstanceData(2);
        data.Run([0]);
        data.Poison([0]);
        ulong revision = data.Batch.ContentRevision;
        data.Edit = context =>
        {
            using var update = context.RewriteBatch(data.Batch, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.Write(CurrentProperty, 0, Current(999));
        };
        data.Run([0]);
        Assert.Equal(revision, data.Batch.ContentRevision);
        data.AssertOnlyCopied(0);
        data.Edit = context =>
        {
            using var update = context.RewriteBatch(data.Batch, Layout);
            update.OpenWrite(Layout).BindPerInstance(CurrentProperty);
            update.Publish();
        };
        data.Run([0]);
        Assert.Equal(revision, data.Batch.ContentRevision);
        data.AssertOnlyCopied(0);
    }

    [Fact]
    public void RebindingSharedDataCopiesEveryNewlyVisibleInstanceRow()
    {
        using var data = new InstanceData(3, sharedCurrent: true);
        data.Run([0]);
        data.Poison([0]);
        data.ChangeCurrent(1, 81);
        data.Run([0]);
        BufferRange first = data.Range(CurrentProperty, 0);
        data.AssertOnlyCopied(0, new BufferRange(first.Offset, first.Size * 3));
        data.Poison([0]);
        data.Edit = context =>
        {
            using var update = context.RewriteBatch(data.Batch, Layout);
            update.OpenWrite(Layout).BindShared(CurrentProperty, Current(92));
            update.Publish();
        };
        data.Run([0]);
        data.AssertOnlyCopied(0, data.Range(CurrentProperty, 0));
    }

    [Fact]
    public void CustomMetadataRetainsTheConservativeFullCopyPath()
    {
        using var data = new InstanceData(2);
        data.Run([0]);
        uint binding = data.Binding(CurrentProperty);
        data.Poison([0]);
        data.Edit = context =>
        {
            using var update = context.RewriteBatch(data.Batch, Layout);
            update.OpenWrite(Layout).BindMetadata(CurrentProperty, new uint[] { binding });
            update.Publish();
        };
        data.Run([0]);
        data.AssertFullCopy(0);
    }

    [Fact]
    public void ANewAllocationReusingTheOnlySlotGetsAFullCopy()
    {
        using var data = new InstanceData(2);
        data.Run([0]);
        RenderInstanceBatch previous = data.Batch;
        BufferRange previousMetadata = data.View.MetadataRange;
        data.Poison([0]);
        data.Edit = context =>
        {
            context.Retire(data.Batch);
            data.Create(context, sharedCurrent: false);
        };
        data.Run([0]);
        Assert.NotSame(previous, data.Batch);
        Assert.Equal(previousMetadata, data.View.MetadataRange);
        data.AssertFullCopy(0);
    }

    [Fact]
    public void ParallelWritesKeepPropertyMasksSeparateAcrossWordBoundaries()
    {
        using var data = new InstanceData(4097);
        data.Run([0]);
        data.Poison([0]);
        data.Edit = context =>
        {
            using var update = context.RewriteBatch(data.Batch, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.BindPerInstance(PreviousProperty);
            Parallel.For(0, data.Count, row =>
            {
                if (row < 128 || (row & 1) == 0) rows.Write(CurrentProperty, row, Current(81));
                else rows.Write(PreviousProperty, row, Previous(92));
            });
            update.Publish();
        };
        data.Run([0]);
        var ranges = new List<BufferRange>();
        for (int row = 0; row < data.Count; row++)
            ranges.Add(row < 128 || (row & 1) == 0 ? data.Range(CurrentProperty, row) : data.Range(PreviousProperty, row));
        data.AssertOnlyCopied(0, ranges.ToArray());
    }

    [Fact]
    public void SparseTransfersKeepOrdinaryValuesCorrectAcrossTwoGraphs()
    {
        using var data = new InstanceData(2);
        data.Run([0, 1]);
        data.ChangeCurrent(0, 81);
        data.Run([0, 1]);
        foreach (int graph in new[] { 0, 1 })
        {
            Assert.Equal(new[] { Current(81), Current(2) }, data.Values<RenderTransform>(graph, CurrentProperty));
            Assert.Equal(new[] { Previous(11), Previous(12) }, data.Values<RenderPreviousTransform>(graph, PreviousProperty));
        }
    }

    private static RenderInstancePropertyLayout Layout => RenderInstanceTransformProperties.Layout;
    private static ResolvedRenderInstanceProperty<RenderTransform> CurrentProperty =>
        Layout.Resolve(RenderInstanceTransformProperties.CurrentTransform);
    private static ResolvedRenderInstanceProperty<RenderPreviousTransform> PreviousProperty =>
        Layout.Resolve(RenderInstanceTransformProperties.PreviousTransform);
    private static RenderTransform Current(float value) =>
        new(Quaternion.Identity, new Vector3(value, 0, 0), 1, Vector3.One);
    private static RenderPreviousTransform Previous(float value) => new(Current(value));

    private sealed class InstanceData : IDisposable
    {
        private readonly RenderTestDevice _device = CreateDevice();
        private readonly RenderWorld _world = new();
        private readonly RenderInstanceStorageSystem _storage;
        private readonly RenderExtractionSystems _extraction;
        private readonly RenderPrepareSystems _prepare;
        private readonly RenderFrameSystems _frames;
        private readonly GraphData[] _graphs;
        private byte[] _source = [];
        private byte[] _metadata = [];
        internal RenderInstanceBatch Batch = null!;
        internal RenderInstanceBatchView View;
        internal Action<RenderPrepareSystemContext>? Edit;
        internal int Count { get; }

        private static RenderTestDevice CreateDevice()
        {
            DeviceQueueDesc[] queues = [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)];
            return OperatingSystem.IsWindows()
                ? new RenderTestDevice(DeviceFeatures.None, queues)
                : new RenderTestDevice(VulkanGraphicsBackend.Create(), new DeviceDesc(default, queues));
        }

        internal InstanceData(int count, bool sharedCurrent = false)
        {
            Count = count;
            _storage = new(_device.Backend, _device.Device, _device.Coordinator, _world, Layout,
                new RenderInstanceOptions { RowCapacity = Math.Max(count, 1), BatchCapacity = 1 });
            _graphs = [new(_device, _storage), new(_device, _storage)];
            _extraction = new(_world);
            _prepare = new(_world, _storage, _extraction);
            _prepare.Add(new Writer(this));
            _frames = new(_world, _storage);
            _frames.Add(new Reader(this));
            Edit = context => Create(context, sharedCurrent);
        }

        internal void Create(RenderPrepareSystemContext context, bool sharedCurrent)
        {
            using var create = context.AllocateBatch(Layout, Count);
            var rows = create.OpenWrite(Layout);
            if (sharedCurrent) rows.BindShared(CurrentProperty, Current(7));
            else
            {
                rows.BindPerInstance(CurrentProperty);
                rows.Write(CurrentProperty, Enumerable.Range(0, Count).Select(row => Current(1 + row % 16)).ToArray());
            }
            rows.BindPerInstance(PreviousProperty);
            rows.Write(PreviousProperty, Enumerable.Range(0, Count).Select(row => Previous(11 + row % 16)).ToArray());
            Batch = create.Publish();
        }

        internal void ChangeCurrent(int row, float value) => Edit = context =>
        {
            using var update = context.RewriteBatch(Batch, Layout);
            var rows = update.OpenWrite(Layout);
            rows.BindPerInstance(CurrentProperty);
            rows.Write(CurrentProperty, row, Current(value));
            update.Publish();
        };

        internal void Poison(int[] graphs)
        {
            Assert.Null(Edit);
            foreach (int graph in graphs) Assert.Equal(Batch.ContentRevision, _graphs[graph].SubmittedRevision);
            // This is a separate unchanged-revision frame: both transfer writer passes are
            // disabled, so the only active writer is this public test-owned canary pass.
            Run(graphs, poison: true);
            foreach (int graph in graphs)
            {
                AssertReadbackSize(_graphs[graph].Readback.Bytes);
                Assert.All(_graphs[graph].Readback.Bytes, value => Assert.Equal((byte)255, value));
            }
        }

        internal void Run(int[] graphs, bool poison = false, bool discard = false)
        {
            Assert.True(_device.Coordinator.TryBeginPrepare(out var prepare));
            using (prepare) { _prepare.Update(prepare); prepare.Commit(); }
            Edit = null;
            Assert.True(_device.Coordinator.TryBeginFrame(out var read));
            var allCompletions = new List<QueueCompletion>();
            int[] receivedBefore = graphs.Select(index => _graphs[index].Readback.Count).ToArray();
            using (read)
            {
                try
                {
                    foreach (int index in graphs)
                    {
                        GraphData graph = _graphs[index];
                        Assert.True(graph.Graph.TryOpenNextFrame(out var next, TimeSpan.FromSeconds(10)));
                        using (next)
                        using (var frame = graph.Graph.BeginFrame(next))
                        {
                            _frames.Update(read, frame);
                            using (var mapping = _device.Backend.Map(View.PropertyData!, MapType.Read, BufferRange.Whole))
                                _source = mapping.Bytes.ToArray();
                            using (var mapping = _device.Backend.Map(View.Metadata, MapType.Read, BufferRange.Whole))
                                _metadata = mapping.Bytes.ToArray();
                            var transferFrame = frame;
                            graph.Transfer.Stage(ref transferFrame, Batch, View);
                            frame.SetPassEnabled(graph.Poison, poison);
                            if (discard) continue;
                            var completions = new QueueCompletion[graph.Graph.MaximumQueueCompletionCount];
                            int count;
                            try { count = frame.Execute(completions); }
                            catch
                            {
                                allCompletions.AddRange(completions.Where(value => value != default));
                                throw;
                            }
                            allCompletions.AddRange(completions.AsSpan(0, count));
                            graph.SubmittedRevision = View.ContentRevision;
                        }
                    }
                    if (allCompletions.Count != 0) read.Complete(CollectionsMarshal.AsSpan(allCompletions));
                }
                catch
                {
                    if (allCompletions.Count != 0 && !read.IsClosed)
                        read.Complete(CollectionsMarshal.AsSpan(allCompletions));
                    throw;
                }
            }
            foreach (QueueCompletion completion in allCompletions)
                Assert.Equal(WaitStatus.Completed, _device.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            for (int index = 0; index < graphs.Length; index++)
            {
                GraphData graph = _graphs[graphs[index]];
                graph.Graph.CollectCompleted();
                Assert.Equal(receivedBefore[index] + (discard ? 0 : 1), graph.Readback.Count);
            }
        }

        internal uint Binding<T>(ResolvedRenderInstanceProperty<T> property) where T : unmanaged =>
            MemoryMarshal.Read<uint>(_metadata.AsSpan(checked((int)View.MetadataRange.Offset +
                property.Descriptor.MetadataWordOffset * sizeof(uint))));

        internal BufferRange Range<T>(ResolvedRenderInstanceProperty<T> property, int row) where T : unmanaged
        {
            uint binding = Binding(property);
            ulong offset = binding & RenderInstanceLinearMetadata.AddressMask;
            if ((binding & RenderInstanceLinearMetadata.PerInstanceBit) != 0)
                offset += checked((ulong)(row * property.Descriptor.Encoding.StorageStride));
            return new(offset, checked((ulong)property.Descriptor.Encoding.StorageStride));
        }

        internal T[] Values<T>(int graph, ResolvedRenderInstanceProperty<T> property) where T : unmanaged =>
            Enumerable.Range(0, Count).Select(row => MemoryMarshal.Read<T>(
                _graphs[graph].Readback.Bytes.AsSpan(checked((int)Range(property, row).Offset)))).ToArray();

        internal void AssertFullCopy(int graph) => AssertOnlyCopied(graph, View.PropertyDataRanges.ToArray());

        internal void AssertDeviceMetadata(int graph) =>
            Assert.Equal(_metadata, _graphs[graph].MetadataReadback.Bytes);

        internal void AssertOnlyCopied(int graph, params BufferRange[] ranges)
        {
            byte[] actual = _graphs[graph].Readback.Bytes;
            AssertReadbackSize(actual);
            Assert.DoesNotContain((byte)255, _source);
            byte[] expected = Enumerable.Repeat((byte)255, actual.Length).ToArray();
            foreach (BufferRange range in ranges)
                _source.AsSpan(checked((int)range.Offset), checked((int)range.Size))
                    .CopyTo(expected.AsSpan(checked((int)range.Offset)));
            // Public GPU readback proves affected byte coverage, not hardware traffic counts.
            Assert.Equal(expected, actual);
        }

        private void AssertReadbackSize(byte[] actual)
        {
            long bytes = _storage.CaptureDiagnostics().PropertyDataBytes;
            Assert.True(bytes > 0);
            Assert.Equal(checked((int)((bytes + 3L) & ~3L)), actual.Length);
            Assert.Equal(checked((int)bytes), _source.Length);
        }

        public void Dispose()
        {
            _frames.Dispose();
            Assert.True(_device.Coordinator.TryBeginPrepare(out var prepare));
            using (prepare)
            {
                _prepare.Shutdown(prepare);
                _storage.Shutdown(prepare);
                prepare.Commit();
            }
            _prepare.Dispose();
            _storage.Dispose();
            foreach (GraphData graph in _graphs) graph.Graph.Dispose();
            _extraction.Dispose();
            _world.Dispose();
            _device.Dispose();
        }

        private sealed class Writer(InstanceData data) : ISystem<RenderPrepareSystemContext>
        {
            public void OnCreate(ref RenderPrepareSystemContext context) { }
            public void OnUpdate(ref RenderPrepareSystemContext context) => data.Edit?.Invoke(context);
            public void OnDestroy(ref RenderPrepareSystemContext context)
            {
                if (data.Batch is not null) context.Retire(data.Batch);
            }
        }

        private sealed class Reader(InstanceData data) : ISystem<RenderFrameSystemContext>
        {
            public void OnCreate(ref RenderFrameSystemContext context) { }
            public void OnDestroy(ref RenderFrameSystemContext context) { }
            public void OnUpdate(ref RenderFrameSystemContext context)
            {
                using var instances = context.OpenInstances();
                data.View = instances.Bind(data.Batch, Layout);
            }
        }

        private sealed class GraphData
        {
            internal readonly global::SomeEngine.RenderGraph.RenderGraph Graph;
            internal readonly RenderInstanceDataTransfer Transfer;
            internal readonly GraphPassId Poison;
            internal readonly BytesReceiver Readback = new();
            internal readonly BytesReceiver MetadataReadback = new();
            internal ulong SubmittedRevision;

            internal GraphData(RenderTestDevice device, RenderInstanceStorageSystem storage)
            {
                Graph = new(device.Backend, device.Device,
                    [device.Backend.GetQueue(device.Device, QueueType.Graphics),
                     device.Backend.GetQueue(device.Device, QueueType.Compute),
                     device.Backend.GetQueue(device.Device, QueueType.Copy)]);
                Transfer = storage.GetDataTransfer(Graph);
                using var edit = Graph.BeginEdit();
                Poison = edit.AddCopyFramePass("Test canary bytes", PassQueueSelection.AnyOfType(QueueType.Graphics),
                    Transfer.Data, new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition pass, ref GraphBufferId buffer) =>
                        pass.Write(buffer, BufferRange.Whole, PipelineSync.Copy,
                            ResourceAccess.CopyDestination, WriteCoverage.Complete),
                    static (ref CopyPassCommandScope commands, in GraphBufferId buffer) =>
                        commands.ClearBuffer(commands.GetBuffer(buffer), BufferRange.Whole, uint.MaxValue));
                edit.AddReadback(Transfer.Data, BufferRange.Whole, Readback, "Instance transfer values");
                edit.AddReadback(Transfer.Metadata, BufferRange.Whole, MetadataReadback, "Instance transfer metadata");
                edit.Commit();
            }
        }
    }

    private sealed class BytesReceiver : IGraphReadbackReceiver
    {
        internal byte[] Bytes = [];
        internal int Count;
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            Bytes = bytes.ToArray();
            Count++;
        }
    }
}
