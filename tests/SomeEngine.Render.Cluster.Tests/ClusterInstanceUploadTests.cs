using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Skinning;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Materials;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterInstanceUploadTests
{
    [Fact]
    public async Task AdjacentDifferentPropertyEditsChangeOnlyTheirOwnRowsAndValues()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 4);
        Entity a = data.Add(1), b = data.Add(2);
        await data.ExecuteAsync();
        await data.ExecuteAsync();
        data.World.Replace(a, Current(8));
        data.World.Replace(b, Previous(19));
        await data.ExecuteAsync();
        Assert.Equal(new uint[] { 0, 1 }, data.ChangedRows);
        Assert.Equal(new[] { Current(8), Current(2) }, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(19) }, data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public async Task SkinBindingEditChangesItsOwnGpuRowAndPreservesTransforms()
    {
        var layoutBuilder = new RenderInstancePropertyLayoutBuilder();
        layoutBuilder.Include(ClusterRenderFeature.GeometryLayout);
        layoutBuilder.Include(SkinInstanceProducer.Layout);
        QueryDefinition query = new QueryDefinitionBuilder().Read<RenderTransform>().Read<RenderPreviousTransform>()
            .Read<RenderClusterGeometry>().Read<RenderSkinBinding>().Build();
        using var data = new InstanceData<SkinInstanceProducer>(await RasterTestGeometry.CreateAsync(), 4,
            layoutBuilder.Freeze(), query, new SkinInstanceProducer());
        Entity first = data.Add(1);
        Entity second = data.Add(2);
        data.World.Add(first, new RenderSkinBinding(1, 11));
        data.World.Add(second, new RenderSkinBinding(2, 12));
        await data.ExecuteAsync();
        await data.ExecuteAsync();

        data.World.Replace(second, new RenderSkinBinding(9, 19));
        await data.ExecuteAsync();

        Assert.Equal(new uint[] { 1 }, data.ChangedRows);
        Assert.Equal(new[] { new RenderSkinBinding(1, 11), new RenderSkinBinding(9, 19) },
            data.Values<RenderSkinBinding>(SkinInstanceProducer.BindingKey));
        Assert.Equal(new[] { Current(1), Current(2) }, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) }, data.Values(RenderInstanceTransformProperties.PreviousTransform));
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
        Assert.NotEmpty(data.ChangedRows);
        Assert.Equal(new[] { Current(8), Current(2) }, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(19) }, data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public async Task LaterMeshRegistrationStaysOutsideCapturedGeometryRoots()
    {
        Mesh mesh = await RasterTestGeometry.CreateAsync();
        using var data = new InstanceData(mesh, 5);
        data.Add(1);
        Entity second = data.Add(2);
        data.World.Replace(second, new RenderMesh(mesh.Duplicate(), 0));
        using (await data.PrepareAsync()) { }

        data.World.AcquireSystemVersion();
        Entity third = data.Add(3);
        data.World.Replace(third, new RenderMesh(mesh.Duplicate(), 0));
        await data.ExecuteAsync();

        uint[] roots = data.Values<uint>(ClusterRenderFeature.BvhRootKey);
        Assert.Equal(2, roots.Length);
        Assert.NotEqual(ClusterRenderFeature.MissingBvhRoot, roots[0]);
        Assert.NotEqual(ClusterRenderFeature.MissingBvhRoot, roots[1]);
        Assert.NotEqual(roots[0], roots[1]);
        // Unpublished geometry is excluded from the main RenderClusterGeometry query.
    }

    [Fact]
    public async Task CancelledMappedPropertyWritesKeepPreviouslyAcceptedGpuValues()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 4);
        data.Add(1); data.Add(2);
        await data.ExecuteAsync();
        data.Prepare.Add(new CancelWrites(data.Composition));
        await data.ExecuteAsync();
        Assert.Empty(data.ChangedRows);
        Assert.Equal(new[] { Current(1), Current(2) }, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) }, data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public async Task DisjointQueryPacketsPreserveChangedRowsAndPropertyValuesInParallel()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 4097,
            new WorldJobs(JobSystem.Default));
        Entity[] entities = Enumerable.Range(0, 4097).Select(data.Add).ToArray();
        await data.ExecuteAsync(); await data.ExecuteAsync();

        for (int row = 0; row < entities.Length; row++)
        {
            if (row % 2 == 0)
            {
                data.World.Replace(entities[row], Current(row + 500));
            }
            else
            {
                data.World.Replace(entities[row], Previous(row + 800));
            }
        }
        await data.ExecuteAsync();
        var current = new List<RenderTransform>();
        var previous = new List<RenderPreviousTransform>();
        data.World.ExecuteReadSnapshot(data.Composition.EntityQueryHandle, cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
            {
                current.Add(row.Read<RenderTransform>());
                previous.Add(row.Read<RenderPreviousTransform>());
            }
        });
        Assert.Equal(Enumerable.Range(0, entities.Length).Select(row => (uint)row), data.ChangedRows);
        Assert.Equal(current, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(previous, data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public async Task ComputedGeometryValuesFillTheirMappedRangesAndPreserveOtherRows()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 4);
        Entity a = data.Add(1); data.Add(2);
        await data.ExecuteAsync(); await data.ExecuteAsync();
        uint[] roots = data.Values<uint>(ClusterRenderFeature.BvhRootKey);
        data.World.Replace(a, data.World.Read<RenderMesh>(a) with { BoundsExpansion = .625f });
        await data.ExecuteAsync();
        Assert.Equal(new uint[] { 0 }, data.ChangedRows);
        Assert.Equal(new[] { .625f, 0f }, data.Values<float>(ClusterRenderFeature.BoundsExpansionKey));
        Assert.Equal(roots, data.Values<uint>(ClusterRenderFeature.BvhRootKey));
        Assert.Equal(new[] { (uint)DepthCacheClass.Auto, (uint)DepthCacheClass.Auto },
            data.Values<uint>(ClusterRenderFeature.DepthCacheClassKey));
        Assert.Equal(new FirstPersonMeshData[] { default, default },
            data.Values<FirstPersonMeshData>(ClusterRenderFeature.FirstPersonKey));
        Assert.Equal(new[] { Current(1), Current(2) }, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) }, data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InvalidHierarchyRangesDoNotConsumeGeometryRegistration(int invalidRange)
    {
        Mesh mesh = await RasterTestGeometry.CreateAsync();
        var groups = mesh.ClusterGroups.ToArray();
        if (invalidRange == 2)
        {
            Assert.Empty(groups[0].Parents);
            groups[0] = groups[0] with { FirstPage = uint.MaxValue, PageCount = 1 };
        }
        else
        {
            var part = groups[0].HierarchyParts[0];
            part = invalidRange == 1
                ? part with { PageIndex = (uint)mesh.PageCount }
                : part with { FirstCluster = mesh.PageDigests[(int)part.PageIndex].ClusterCount };
            groups[0] = groups[0] with { HierarchyParts = groups[0].HierarchyParts.SetItem(0, part) };
        }
        var pages = Enumerable.Range(0, mesh.PageCount).Select(page =>
        {
            Assert.True(mesh.TryGetPage(page, out var bytes));
            return bytes;
        }).ToArray();
        Mesh invalid = Mesh.Create("Invalid hierarchy range", mesh.VertexEncoding, mesh.PageDigests,
            mesh.Bvh, pages, mesh.Bounds, mesh.Regions, mesh.LowestDetailPageIndices, clusterGroups: groups);
        using var data = new InstanceData(invalid, 4);
        Entity entity = data.Add(1);

        await Assert.ThrowsAsync<InvalidDataException>(() => data.PrepareMeshesAsync());
        data.World.AcquireSystemVersion();
        data.World.Replace(entity, new RenderMesh(mesh, 0));
        await data.ExecuteAsync();

        Assert.NotEqual(ClusterRenderFeature.MissingBvhRoot, Assert.Single(data.Values<uint>(ClusterRenderFeature.BvhRootKey)));
        Assert.Equal(1, data.RegisteredMeshCount);
    }

    [Fact]
    public async Task ChangedMeshFlagsReachGpuWithoutChangingOtherRowsOrTransforms()
    {
        using var data = new InstanceData(await RasterTestGeometry.CreateAsync(), 4);
        Entity a = data.Add(1); data.Add(2);
        await data.ExecuteAsync(); await data.ExecuteAsync();
        uint[] roots = data.Values<uint>(ClusterRenderFeature.BvhRootKey);
        var firstPerson = new FirstPersonMeshData(FirstPersonMeshKind.FirstPerson, 17, MeshPlayerVisibility.PlayerOnly);
        data.World.Replace(a, data.World.Read<RenderMesh>(a) with
        {
            DepthCacheClass = DepthCacheClass.Dynamic,
            FirstPerson = firstPerson,
        });
        await data.ExecuteAsync();
        Assert.Equal(new uint[] { 0 }, data.ChangedRows);
        Assert.Equal(new[] { (uint)DepthCacheClass.Dynamic, (uint)DepthCacheClass.Auto },
            data.Values<uint>(ClusterRenderFeature.DepthCacheClassKey));
        Assert.Equal(new[] { firstPerson, default }, data.Values<FirstPersonMeshData>(ClusterRenderFeature.FirstPersonKey));
        Assert.Equal(new[] { 0f, 0f }, data.Values<float>(ClusterRenderFeature.BoundsExpansionKey));
        Assert.Equal(roots, data.Values<uint>(ClusterRenderFeature.BvhRootKey));
        Assert.Equal(new[] { Current(1), Current(2) }, data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(new[] { Previous(11), Previous(12) }, data.Values(RenderInstanceTransformProperties.PreviousTransform));
    }

    [Fact]
    public async Task MaterialAndTransformChangesKeepIndependentRowsAndValuesAcrossVersions()
    {
        var first = new UnlitMaterial();
        var second = new UnlitMaterial();
        var assignments = new ClusterMaterialAssignments();
        using World source = new();
        Mesh mesh = await RasterTestGeometry.CreateAsync();
        using var data = new InstanceData<ClusterMaterialOffsetProducer>(
            mesh, 4, ClusterRenderFeature.InstanceLayout,
            ClusterMaterialAssignmentSystem.EntityQuery(), assignments.CreateProducer(),
            new ClusterMaterialAssignmentSystem(assignments));
        Entity sourceA = AddMaterialInstance(1, first);
        Entity sourceB = AddMaterialInstance(2, second);
        data.ExtractMeshes(source);
        Entity a = data.Mirror(sourceA), b = data.Mirror(sourceB);
        data.World.Replace(a, Previous(11));
        data.World.Replace(b, Previous(12));
        await data.ExecuteAsync(); await data.ExecuteAsync();
        uint[] offsets = data.Values<uint>(ClusterRenderFeature.MaterialSlotOffsetKey);
        Assert.NotEqual(offsets[0], offsets[1]);
        uint[] roots = data.Values<uint>(ClusterRenderFeature.BvhRootKey);
        var entityOrder = new List<Entity>();
        data.World.ExecuteReadSnapshot(data.Composition.EntityQueryHandle, query =>
        {
            foreach (QueryRow row in query.Rows)
                entityOrder.Add(row.Entity);
        });
        int firstRow = entityOrder.IndexOf(a), secondRow = entityOrder.IndexOf(b);
        Material replacement = second;
        data.World.ExecuteBufferWrite<RenderMaterialBinding, Material>(a, ref replacement,
            static (DynamicBuffer<RenderMaterialBinding> bindings, ref Material material) =>
            {
                bindings.Clear();
                bindings.Add(new(material.MaterialId));
            });
        await data.ExecuteAsync();
        Assert.Equal(entityOrder.Select(data.World.Read<RenderTransform>),
            data.Values(RenderInstanceTransformProperties.CurrentTransform));
        Assert.Equal(entityOrder.Select(data.World.Read<RenderPreviousTransform>),
            data.Values(RenderInstanceTransformProperties.PreviousTransform));
        Assert.Equal(new[] { a }, data.ChangedRows.Select(row => entityOrder[(int)row]));
        Assert.Equal(new[] { (uint)firstRow }, data.ChangedRows);
        Assert.Equal(new[] { offsets[secondRow], offsets[secondRow] },
            data.Values<uint>(ClusterRenderFeature.MaterialSlotOffsetKey));
        Assert.Equal(roots, data.Values<uint>(ClusterRenderFeature.BvhRootKey));
        Assert.Equal(new[] { 0f, 0f }, data.Values<float>(ClusterRenderFeature.BoundsExpansionKey));
        Assert.Equal(new[] { (uint)DepthCacheClass.Auto, (uint)DepthCacheClass.Auto },
            data.Values<uint>(ClusterRenderFeature.DepthCacheClassKey));
        Assert.Equal(new FirstPersonMeshData[] { default, default },
            data.Values<FirstPersonMeshData>(ClusterRenderFeature.FirstPersonKey));
        AssertGeometry(1, 2);


        // First-only: a transform edit must not repeat the previous material row changes.
        data.World.Replace(a, Current(101));
        await data.ExecuteAsync();
        Assert.Equal(new[] { (uint)firstRow }, data.ChangedRows);
        Assert.Equal(new[] { offsets[secondRow], offsets[secondRow] },
            data.Values<uint>(ClusterRenderFeature.MaterialSlotOffsetKey));
        AssertGeometry(101, 2);

        // Both producers change on the other row in the same version.
        data.World.Replace(b, Current(202));
        replacement = first;
        data.World.ExecuteBufferWrite<RenderMaterialBinding, Material>(b, ref replacement,
            static (DynamicBuffer<RenderMaterialBinding> bindings, ref Material material) =>
            {
                bindings.Clear();
                bindings.Add(new(material.MaterialId));
            });
        await data.ExecuteAsync();
        Assert.Equal(new[] { (uint)secondRow }, data.ChangedRows);
        uint[] changedOffsets = data.Values<uint>(ClusterRenderFeature.MaterialSlotOffsetKey);
        Assert.Equal(offsets[secondRow], changedOffsets[firstRow]);
        Assert.NotEqual(changedOffsets[firstRow], changedOffsets[secondRow]);
        AssertGeometry(101, 202);

        // Neither changes: the next system version must not report either old row change again.
        await data.ExecuteAsync();
        Assert.Empty(data.ChangedRows);
        Assert.Equal(changedOffsets, data.Values<uint>(ClusterRenderFeature.MaterialSlotOffsetKey));
        AssertGeometry(101, 202);

        void AssertGeometry(float currentAValue, float currentBValue)
        {
            Assert.Equal(roots, data.Values<uint>(ClusterRenderFeature.BvhRootKey));
            Assert.Equal(new[] { 0f, 0f }, data.Values<float>(ClusterRenderFeature.BoundsExpansionKey));
            Assert.Equal(new[] { (uint)DepthCacheClass.Auto, (uint)DepthCacheClass.Auto },
                data.Values<uint>(ClusterRenderFeature.DepthCacheClassKey));
            Assert.Equal(new FirstPersonMeshData[] { default, default },
                data.Values<FirstPersonMeshData>(ClusterRenderFeature.FirstPersonKey));
            Assert.Equal(entityOrder.Select(entity => entity == a ? Current(currentAValue) : Current(currentBValue)),
                data.Values(RenderInstanceTransformProperties.CurrentTransform));
            Assert.Equal(entityOrder.Select(entity => entity == a ? Previous(11) : Previous(12)),
                data.Values(RenderInstanceTransformProperties.PreviousTransform));
        }

        Entity AddMaterialInstance(int value, Material material)
        {
            Entity entity = source.CreateEntity(new MeshInstance { Mesh = mesh });
            source.Add(entity, new WorldTransform { Qvvs = new TransformQvvs(new Vector3(value, 0, 0), Quaternion.Identity) });
            source.AddBuffer<MeshMaterialBinding>(entity);
            source.ExecuteBufferWrite<MeshMaterialBinding, Material>(entity, ref material,
                static (DynamicBuffer<MeshMaterialBinding> bindings, ref Material value) => bindings.Add(new(value)));
            return entity;
        }
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
            for (int index = 0; index < rows.Count; index++)
                rows.Write(current, index, Current(999));
        }
    }

    private sealed class InstanceData(Mesh mesh, int capacity, WorldJobs? jobs = null) : InstanceData<EmptyRenderInstanceProducer>(
        mesh, capacity, ClusterRenderFeature.GeometryLayout,
        new QueryDefinitionBuilder().Read<RenderTransform>().Read<RenderPreviousTransform>().Read<RenderClusterGeometry>().Build(),
        new EmptyRenderInstanceProducer(), jobs: jobs);

    private class InstanceData<TProducer> : IDisposable where TProducer : struct, IRenderInstanceProducer
    {
        private readonly IGraphicsBackend _backend = D3D12GraphicsBackend.Create();
        private readonly Device _device;
        private readonly RenderDevice _renderDevice;
        private readonly RenderFrameCoordinator _coordinator;
        private readonly RenderInstanceDataTransfer _transfer;
        private ulong _submittedRevision;
        private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
        private readonly RenderInstanceStorageSystem _storage;
        private readonly ClusterRenderResources _cluster;
        private readonly RenderExtractionSystems _extraction;
        private readonly Mesh _mesh;
        private readonly RenderInstancePropertyLayout _layout;
        private readonly BytesReceiver _properties = new(), _metadata = new();
        private RenderInstanceBatchView _view;
        internal RenderWorld World { get; }
        internal RenderPrepareSystems Prepare { get; }
        internal ClusterInstanceSystem<TProducer> Composition { get; }
        internal uint[] ChangedRows { get; private set; } = [];
        internal int RegisteredMeshCount => _cluster.CaptureDiagnostics().Meshes.RegisteredMeshes;

        internal InstanceData(Mesh mesh, int capacity, RenderInstancePropertyLayout layout,
            QueryDefinition query, TProducer producer, ISystem<RenderPrepareSystemContext>? beforeComposition = null,
            WorldJobs? jobs = null)
        {
            World = new(execution: jobs);
            _mesh = mesh;
            _layout = layout;
            var enumeration = new AdapterEnumerationOptions(AdapterPreference.Unspecified, IncludeSoftware: true);
            _backend.TryEnumerateAdapters(enumeration, [], out int count);
            var adapters = new AdapterInfo[count];
            _backend.TryEnumerateAdapters(enumeration, adapters, out _);
            _device = _backend.CreateDevice(new DeviceDesc(adapters.First(adapter => !adapter.HardwareAccelerated).Id,
                [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)]));
            _renderDevice = new(_backend, _device);
            _coordinator = new(_renderDevice);
            _graph = new(_backend, _device, [_backend.GetQueue(_device, QueueType.Graphics),
                _backend.GetQueue(_device, QueueType.Compute), _backend.GetQueue(_device, QueueType.Copy)]);
            _storage = new(_backend, _device, _coordinator, World, layout,
                new RenderInstanceOptions { RowCapacity = capacity, BatchCapacity = 2 });
            _transfer = _storage.GetDataTransfer(_graph);
            _cluster = new(_backend, _device, _coordinator, World, _storage,
                new ClusterRenderOptions { Residency = new ClusterResidencyOptions { PageHeapCapacityBytes = 1048576 } });
            _extraction = new(World);
            Prepare = new(World, _storage, _extraction);
            Prepare.Add(new ClusterResidencySystem(_cluster));
            if (beforeComposition is not null) Prepare.Add(beforeComposition);
            Composition = new(_cluster, query, layout, producer, rowsPerPacket: 2);
            Prepare.Add(Composition);
            using var edit = _graph.BeginEdit();
            edit.AddReadback(_transfer.Data, BufferRange.Whole, _properties, "Geometry property output");
            edit.Commit();
        }

        internal Entity Add(int value)
        {
            Entity entity = World.CreateEntity(Current(value));
            World.Add(entity, Previous(value + 10));
            World.Add(entity, new RenderMesh(_mesh, 0));
            return entity;
        }

        internal void ExtractMeshes(World source)
        {
            _extraction.Add(new MeshRenderExtractor());
            _extraction.Extract(source);
            Assert.True(_coordinator.TryBeginPrepare(out var scope));
            using (scope) { Prepare.ApplyExtraction(scope); scope.Commit(); }
        }

        internal Entity Mirror(Entity source) => _extraction.RequireMirror(source);

        internal async Task PrepareMeshesAsync()
        {
            Assert.True(_coordinator.TryBeginPrepare(out var scope));
            using (scope) { await _cluster.PrepareMeshesAsync(); scope.Commit(); }
        }

        internal async Task<RenderGraphNextFrame> PrepareAsync()
        {
            Assert.True(_graph.TryOpenNextFrame(out var next, TimeSpan.FromSeconds(20)));
            try
            {
                Assert.True(_coordinator.TryBeginPrepare(out var scope));
                using (scope)
                {
                    await _cluster.PrepareMeshesAsync();
                    Prepare.Update(scope);
                    scope.Commit();
                }
                return next;
            }
            catch
            {
                next.Dispose();
                throw;
            }
        }

        internal async Task ExecuteAsync()
        {
            using var next = await PrepareAsync();
            Assert.True(_coordinator.TryBeginFrame(out var scope));
            using (scope)
            using (var frame = _graph.BeginFrame(next))
            {
                using var systems = new RenderFrameSystems(World, _storage);
                var reader = new BatchReader<TProducer>(Composition, _layout);
                systems.Add(reader);
                systems.Update(scope, frame);
                _view = reader.View;
                RenderInstanceBatch batch = Composition.Current!.Groups[0].Batch;
                var changed = new List<uint>();
                batch.CollectChangedInstances(_submittedRevision, changed);
                ChangedRows = changed.ToArray();
                using (var metadata = _backend.Map(_view.Metadata, MapType.Read, BufferRange.Whole))
                    _metadata.Bytes = metadata.Bytes.ToArray();
                var transferFrame = frame;
                _transfer.Stage(ref transferFrame, batch, _view);
                var completions = new QueueCompletion[_graph.MaximumQueueCompletionCount];
                int count;
                try { count = frame.Execute(completions); }
                catch
                {
                    QueueCompletion[] partial = completions.Where(value => value != default).ToArray();
                    if (partial.Length != 0 && !scope.IsClosed) scope.Complete(partial);
                    throw;
                }
                scope.Complete(completions.AsSpan(0, count));
                _submittedRevision = _view.ContentRevision;
                foreach (var completion in completions.AsSpan(0, count))
                    Assert.Equal(WaitStatus.Completed, _backend.WaitCpu(completion, TimeSpan.FromSeconds(20)));
            }
            _graph.CollectCompleted();
        }

        internal BufferRange PropertyRange<T>(RenderInstanceProperty<T> property, int row) where T : unmanaged => PropertyRange<T>(property.Key, row);

        internal BufferRange PropertyRange<T>(RenderInstancePropertyKey key, int row) where T : unmanaged
        {
            var resolved = _layout.Resolve<T>(key);
            uint word = MemoryMarshal.Read<uint>(_metadata.Bytes.AsSpan(checked((int)_view.MetadataRange.Offset + resolved.Descriptor.MetadataWordOffset * 4), 4));
            return new((word & RenderInstanceLinearMetadata.AddressMask) + checked((ulong)(row * resolved.Encoding.StorageStride)),
                checked((ulong)resolved.Encoding.StorageStride));
        }

        internal T[] Values<T>(RenderInstanceProperty<T> property) where T : unmanaged => Values<T>(property.Key);
        internal T[] Values<T>(RenderInstancePropertyKey key) where T : unmanaged => Enumerable.Range(0, _view.InstanceCount)
            .Select(row => MemoryMarshal.Read<T>(_properties.Bytes.AsSpan(checked((int)PropertyRange<T>(key, row).Offset)))).ToArray();

        public void Dispose()
        {
            Assert.True(_coordinator.TryBeginPrepare(out var scope));
            using (scope) { Prepare.Shutdown(scope); _cluster.Shutdown(scope); _storage.Shutdown(scope); scope.Commit(); }
            Prepare.Dispose(); _cluster.Dispose(); _storage.Dispose(); _graph.Dispose(); _extraction.Dispose();
            World.Dispose(); _coordinator.Dispose(); _renderDevice.Dispose(); _device.Dispose(); _backend.Dispose();
        }
    }

    private sealed class BatchReader<TProducer>(ClusterInstanceSystem<TProducer> composition, RenderInstancePropertyLayout layout)
        : ISystem<RenderFrameSystemContext> where TProducer : struct, IRenderInstanceProducer
    {
        internal RenderInstanceBatchView View;
        public void OnCreate(ref RenderFrameSystemContext context) { }
        public void OnDestroy(ref RenderFrameSystemContext context) { }
        public void OnUpdate(ref RenderFrameSystemContext context)
        {
            using var access = context.OpenInstances();
            View = access.Bind(composition.Current!.Groups[0].Batch, layout);
        }
    }
    private sealed class BytesReceiver : IGraphReadbackReceiver
    {
        internal byte[] Bytes = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Bytes = bytes.ToArray();
    }
}
