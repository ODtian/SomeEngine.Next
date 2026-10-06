using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterInstanceUploadTests
{
    [Fact]
    public async Task AdjacentDifferentPropertyEditsUploadOnlyTheirOwnRowsAndValues()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 4);
        Entity a = data.Add(1), b = data.Add(2);
        await data.ExecuteAsync();
        await data.ExecuteAsync();
        BufferRange currentA = data.PropertyRange(RenderInstanceTransformProperties.CurrentTransform, 0);
        BufferRange previousB = data.PropertyRange(RenderInstanceTransformProperties.PreviousTransform, 1);
        data.World.Replace(a, Current(8));
        data.World.Replace(b, Previous(19));
        await data.ExecuteAsync();
        Assert.Equal(new[] { currentA, previousB }.OrderBy(range => range.Offset), data.Uploads.OrderBy(range => range.Offset));
        Assert.Equal(new[] { Current(8), Current(2) }, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(19) }, data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public async Task AbandonedPreparationRewritesBothChangedPropertiesOnTheNextFrame()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 4);
        Entity a = data.Add(1), b = data.Add(2);
        await data.ExecuteAsync();
        data.World.Replace(a, Current(8));
        data.World.Replace(b, Previous(19));
        using (await data.PrepareAsync()) { }
        await data.ExecuteAsync();
        Assert.NotEmpty(data.Uploads);
        Assert.Equal(new[] { Current(8), Current(2) }, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(19) }, data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public async Task CancelledMappedPropertyWritesKeepPreviouslyAcceptedGpuValues()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 4);
        data.Add(1); data.Add(2);
        await data.ExecuteAsync();
        data.Prepare.Add(new CancelWrites(data.Composition));
        await data.ExecuteAsync();
        Assert.Empty(data.Uploads);
        Assert.Equal(new[] { Current(1), Current(2) }, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) }, data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public async Task DisjointQueryPacketsWriteOnlyTheirChangedPropertyRangesInParallel()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 128);
        Entity[] entities = Enumerable.Range(0, 128).Select(data.Add).ToArray();
        await data.ExecuteAsync(); await data.ExecuteAsync();
        var expected = new List<BufferRange>();
        for (int row = 0; row < entities.Length; row++)
        {
            if (row % 2 == 0)
            {
                expected.Add(data.PropertyRange(RenderInstanceTransformProperties.CurrentTransform, row));
                data.World.Replace(entities[row], Current(row + 500));
            }
            else
            {
                expected.Add(data.PropertyRange(RenderInstanceTransformProperties.PreviousTransform, row));
                data.World.Replace(entities[row], Previous(row + 800));
            }
        }
        await data.ExecuteAsync();
        Assert.Equal(expected.OrderBy(range => range.Offset), data.Uploads.OrderBy(range => range.Offset));
        Assert.Equal(Enumerable.Range(0, 128).Select(row => Current(row % 2 == 0 ? row + 500 : row)),
            data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(Enumerable.Range(0, 128).Select(row => Previous(row % 2 == 1 ? row + 800 : row + 10)),
            data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public async Task ComputedGeometryValuesFillTheirMappedRangesAndPreserveOtherRows()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 4);
        Entity a = data.Add(1); data.Add(2);
        await data.ExecuteAsync(); await data.ExecuteAsync();
        uint[] roots = data.Values<uint>(ClusterRenderFeature.BvhRootKey);
        BufferRange rootA = data.PropertyRange<uint>(ClusterRenderFeature.BvhRootKey, 0);
        BufferRange expansionA = data.PropertyRange<float>(ClusterRenderFeature.BoundsExpansionKey, 0);
        data.World.Replace(a, data.World.Read<RenderMesh>(a) with { BoundsExpansion = .625f });
        await data.ExecuteAsync();
        Assert.Equal(new[] { rootA, expansionA }.OrderBy(range => range.Offset), data.Uploads.OrderBy(range => range.Offset));
        Assert.Equal(new[] { .625f, 0f }, data.Values<float>(ClusterRenderFeature.BoundsExpansionKey));
        Assert.Equal(roots, data.Values<uint>(ClusterRenderFeature.BvhRootKey));
    }

    private static RenderTransform Current(float value) => new(Quaternion.Identity, new(value, 0, 0), 1, Vector3.One);
    private static RenderPreviousTransform Previous(float value) => new(Current(value));

    private sealed class CancelWrites(ClusterInstanceSystem<EmptyRenderInstanceProducer> composition) : ISystem<RenderPrepareSystemContext>
    {
        public void OnCreate(ref RenderPrepareSystemContext context) { }
        public void OnDestroy(ref RenderPrepareSystemContext context) { }
        public void OnUpdate(ref RenderPrepareSystemContext context)
        {
            using var update = context.RewriteBatch(composition.Current!.Groups[0].Batch, ClusterRenderFeature.GeometryLayout);
            var rows = update.OpenWrite(ClusterRenderFeature.GeometryLayout);
            var current = ClusterRenderFeature.GeometryLayout.Resolve<RenderTransform>(RenderInstanceTransformProperties.CurrentTransformKey);
            rows.BindPerInstance(current);
            rows.GetWriteSpan(current).Fill(Current(999));
        }
    }

    private sealed class InstanceData : IDisposable
    {
        private readonly IGraphicsBackend _backend = D3D12GraphicsBackend.Create();
        private readonly Device _device;
        private readonly RenderDevice _renderDevice;
        private readonly RenderDataLock _lock = new();
        private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
        private readonly RenderInstanceStorageSystem _storage;
        private readonly ClusterRenderResources _cluster;
        private readonly RenderExtractionSystems _extraction;
        private readonly Mesh _mesh;
        private readonly BytesReceiver _properties = new(), _metadata = new();
        private RenderInstanceBatchView _view;
        internal RenderWorld World { get; } = new();
        internal RenderPrepareSystems Prepare { get; }
        internal ClusterInstanceSystem<EmptyRenderInstanceProducer> Composition { get; }
        internal BufferRange[] Uploads { get; private set; } = [];

        internal InstanceData(Mesh mesh, int capacity)
        {
            _mesh = mesh;
            var enumeration = new AdapterEnumerationOptions(AdapterPreference.Unspecified, IncludeSoftware: true);
            _backend.TryEnumerateAdapters(enumeration, [], out int count);
            var adapters = new AdapterInfo[count];
            _backend.TryEnumerateAdapters(enumeration, adapters, out _);
            _device = _backend.CreateDevice(new DeviceDesc(adapters.First(adapter => !adapter.HardwareAccelerated).Id,
                [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)]));
            _renderDevice = new(_backend, _device);
            _graph = new(_backend, _device, [_backend.GetQueue(_device, QueueType.Graphics),
                _backend.GetQueue(_device, QueueType.Compute), _backend.GetQueue(_device, QueueType.Copy)]);
            _storage = new(_backend, _device, World, ClusterRenderFeature.GeometryLayout,
                new RenderInstanceOptions { RowCapacity = capacity, BatchCapacity = 2 });
            _storage.Install(_graph);
            _cluster = new(_backend, _device, World, _storage,
                new ClusterRenderOptions { Residency = new ClusterResidencyOptions { PageHeapCapacityBytes = 1048576 } });
            _extraction = new(World);
            Prepare = new(World, _storage, _extraction);
            Prepare.Add(new ClusterResidencySystem(_cluster));
            Composition = new(_cluster, new QueryDefinitionBuilder().Read<RenderTransform>().Read<RenderPreviousTransform>().Read<RenderMesh>().Build(),
                ClusterRenderFeature.GeometryLayout, new EmptyRenderInstanceProducer(), rowsPerPacket: 2);
            Prepare.Add(Composition);
            using var edit = _graph.BeginEdit();
            edit.AddReadback(_storage.PropertyData, BufferRange.Whole, _properties, "Geometry property output");
            edit.AddReadback(_storage.BatchMetadata, BufferRange.Whole, _metadata, "Geometry property bindings");
            edit.Commit();
        }

        internal Entity Add(int value)
        {
            Entity entity = World.CreateEntity(Current(value));
            World.Add(entity, Previous(value + 10));
            World.Add(entity, new RenderMesh(_mesh, 0));
            return entity;
        }

        internal async Task<RenderGraphNextFrame> PrepareAsync()
        {
            Assert.True(_graph.TryOpenNextFrame(out var next, TimeSpan.FromSeconds(20)));
            Assert.True(_lock.TryBeginPrepare(out var scope));
            using (scope)
            {
                await _cluster.PrepareMeshesAsync();
                Prepare.Update(scope, next);
            }
            return next;
        }

        internal async Task ExecuteAsync()
        {
            using var next = await PrepareAsync();
            Assert.True(_lock.TryBeginRead(out var scope));
            using (scope)
            using (var frame = _graph.BeginFrame(next))
            {
                using var systems = new RenderFrameSystems(World, _storage);
                var reader = new BatchReader(Composition);
                systems.Add(reader);
                systems.Update(scope, frame);
                _view = reader.View;
                Uploads = frame.GetBufferUploadRanges(_storage.PropertyData).Ranges.ToArray();
                var completions = new QueueCompletion[_graph.MaximumQueueCompletionCount];
                int count = frame.Execute(completions);
                _renderDevice.TrackSubmission(completions.AsSpan(0, count));
                scope.Dispose();
                foreach (var completion in completions.AsSpan(0, count))
                    Assert.Equal(WaitStatus.Completed, _backend.WaitCpu(completion, TimeSpan.FromSeconds(20)));
            }
            _graph.CollectCompleted();
        }

        internal BufferRange PropertyRange<T>(RenderInstanceProperty<T> property, int row) where T : unmanaged => PropertyRange<T>(property.Key, row);

        internal BufferRange PropertyRange<T>(RenderInstancePropertyKey key, int row) where T : unmanaged
        {
            var resolved = ClusterRenderFeature.GeometryLayout.Resolve<T>(key);
            uint word = MemoryMarshal.Read<uint>(_metadata.Bytes.AsSpan(checked((int)_view.MetadataRange.Offset + resolved.Descriptor.MetadataWordOffset * 4), 4));
            return new((word & RenderInstanceLinearMetadata.AddressMask) + checked((ulong)(row * resolved.Encoding.StorageStride)),
                checked((ulong)resolved.Encoding.StorageStride));
        }

        internal T[] Values<T>(RenderInstanceProperty<T> property) where T : unmanaged => Values<T>(property.Key);
        internal T[] Values<T>(RenderInstancePropertyKey key) where T : unmanaged => Enumerable.Range(0, _view.InstanceCount)
            .Select(row => MemoryMarshal.Read<T>(_properties.Bytes.AsSpan(checked((int)PropertyRange<T>(key, row).Offset)))).ToArray();

        public void Dispose()
        {
            Assert.True(_lock.TryBeginPrepare(out var scope));
            using (scope) { Prepare.Shutdown(scope); _cluster.Shutdown(scope); _storage.Shutdown(scope); }
            Prepare.Dispose(); _cluster.Dispose(); _storage.Dispose(); _graph.Dispose(); _extraction.Dispose();
            World.Dispose(); _renderDevice.Dispose(); _device.Dispose(); _backend.Dispose();
        }
    }

    private sealed class BatchReader(ClusterInstanceSystem<EmptyRenderInstanceProducer> composition) : ISystem<RenderFrameSystemContext>
    {
        internal RenderInstanceBatchView View;
        public void OnCreate(ref RenderFrameSystemContext context) { }
        public void OnDestroy(ref RenderFrameSystemContext context) { }
        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            using var access = context.OpenInstances();
            View = access.Bind(composition.Current!.Groups[0].Batch, ClusterRenderFeature.GeometryLayout);
        }
    }
    private sealed class BytesReceiver : IGraphReadbackReceiver
    {
        internal byte[] Bytes = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Bytes = bytes.ToArray();
    }
}
