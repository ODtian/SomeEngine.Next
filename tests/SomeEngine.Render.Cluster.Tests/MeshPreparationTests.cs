using System.Buffers;
using System.Numerics;
using SomeEngine.Geometry.Cluster;
using SomeEngine.Job;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;
using SomeEngine.Render.Tests;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class MeshPreparationTests
{
    [Fact]
    public async Task IndependentMeshBvhInputsAreReadTogether()
    {
        using var context = new PreparationContext();
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Mesh original = CreateTriangle();
        using var firstBytes = new BvhBytes(original.Bvh.ToArray());
        using var secondBytes = new BvhBytes(original.Bvh.ToArray());
        Mesh first = CopyWithBvh(original, firstBytes.Memory);
        Mesh second = CopyWithBvh(original, secondBytes.Memory);
        context.World.CreateEntity(new RenderMesh(first, 0));
        context.World.CreateEntity(new RenderMesh(second, 0));
        firstBytes.OnRead = () => { firstEntered.Set(); release.Wait(); };
        secondBytes.OnRead = () => { secondEntered.Set(); release.Wait(); };
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        Task<ClusterMeshPrepareResult> preparation = Task.Run(async () => await context.Resources.PrepareMeshesAsync());
        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Set();
            await preparation.WaitAsync(TimeSpan.FromSeconds(10));
            JobSystem.Shutdown();
        }

        Assert.Equal(new ClusterMeshPrepareResult(2, 2, 0), await preparation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedMeshInputsCanBeConsumedAgain(bool cancel)
    {
        using var context = new PreparationContext();
        using var cancellation = new CancellationTokenSource();
        Mesh original = CreateTriangle();
        using var firstBytes = new BvhBytes(original.Bvh.ToArray());
        using var secondBytes = new BvhBytes(original.Bvh.ToArray());
        context.World.CreateEntity(new RenderMesh(CopyWithBvh(original, firstBytes.Memory), 0));
        context.World.CreateEntity(new RenderMesh(CopyWithBvh(original, secondBytes.Memory), 0));
        firstBytes.OnRead = () =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new IOException("mesh read interrupted");
        };
        JobSystem.Initialize(new JobRuntimeConfig { WorkerCount = 2 });
        try
        {
            if (cancel)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await context.Resources.PrepareMeshesAsync(cancellation.Token));
            else
                await Assert.ThrowsAsync<IOException>(async () => await context.Resources.PrepareMeshesAsync());
            firstBytes.OnRead = null;
            Assert.Equal(new ClusterMeshPrepareResult(2, 2, 0), await context.Resources.PrepareMeshesAsync());
            Assert.Equal(new ClusterMeshPrepareResult(2, 0, 0), await context.Resources.PrepareMeshesAsync());
        }
        finally { JobSystem.Shutdown(); }
    }

    private static Mesh CreateTriangle()
    {
        Vector3[] positions = [new(-1, 0, 0), new(1, 0, 0), new(0, 2, 0)];
        StandardClusterAttributes[] attributes =
        [
            new(Vector3.UnitZ, new(1, 0, 0, 1), new(0, 0)),
            new(Vector3.UnitZ, new(1, 0, 0, 1), new(1, 0)),
            new(Vector3.UnitZ, new(1, 0, 0, 1), new(.5f, 1)),
        ];
        return ClusterMeshBuilder.Build(positions, attributes, [0u, 1u, 2u],
            [new ClusterMaterialGroup(0, 3, 0)], StandardClusterVertexEncoder.Instance,
            ClusterBuildStrategy.Realtime, "triangle");
    }

    private static Mesh CopyWithBvh(Mesh mesh, ReadOnlyMemory<byte> bvh)
    {
        var pages = new ReadOnlyMemory<byte>[mesh.PageCount];
        for (int page = 0; page < pages.Length; page++)
            Assert.True(mesh.TryGetPage(page, out pages[page]));
        return Mesh.Create(mesh.Name!, mesh.VertexEncoding, mesh.PageDigests, bvh, pages, mesh.Bounds,
            mesh.Regions, mesh.LowestDetailPageIndices, mesh.ClusterGeometryIndices);
    }

    private sealed class BvhBytes(byte[] bytes) : MemoryManager<byte>
    {
        internal Action? OnRead { get; set; }
        public override Span<byte> GetSpan() { OnRead?.Invoke(); return bytes; }
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class PreparationContext : IDisposable
    {
        private readonly RenderTestDevice _device = new();
        private readonly RenderInstanceStorageSystem _instances;
        internal RenderWorld World { get; } = new();
        internal ClusterRenderResources Resources { get; }

        internal PreparationContext()
        {
            _instances = new RenderInstanceStorageSystem(_device.Backend, _device.Device, _device.Coordinator,
                World, ClusterRenderFeature.GeometryLayout, new RenderInstanceOptions { RowCapacity = 4, BatchCapacity = 1 });
            Resources = new ClusterRenderResources(_device.Backend, _device.Device, _device.Coordinator, World,
                _instances, new ClusterRenderOptions
                {
                    Residency = new ClusterResidencyOptions { PageHeapCapacityBytes = 1024 * 1024, BvhCapacityBytes = 64 * 1024 },
                });
        }

        public void Dispose()
        {
            Assert.True(_device.Coordinator.TryBeginPrepare(out var scope));
            using (scope)
            {
                Resources.Shutdown(scope);
                _instances.Shutdown(scope);
                scope.Commit();
            }
            Resources.Dispose();
            _instances.Dispose();
            World.Dispose();
            _device.Dispose();
        }
    }
}
