using System.Collections.Immutable;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Backend", "D3D12-WARP")]
public sealed class ClusterMaterialAssignmentTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void PreparingSharedMaterialsPreservesMembershipAcrossRebindingAndRemoval()
    {
        using var scene = new AssignmentScene();
        scene.Update();
        ClusterMaterialAssignmentSet empty = scene.Assignments.Current;
        Assert.Empty(empty.DistinctMaterials);
        Assert.Equal(0u, empty.MaterialCount);
        Assert.Equal(0u, empty.SlotCount);
        scene.Update();
        Assert.Empty(scene.Assignments.Current.DistinctMaterials);
        Assert.Equal(0u, scene.Assignments.Current.MaterialCount);
        Assert.Equal(0u, scene.Assignments.Current.SlotCount);

        var first = new StandardMaterial();
        var second = new StandardMaterial();
        Entity left = scene.Add(first);
        Entity right = scene.Add(first);
        scene.Update();
        ClusterMaterialAssignmentSet shared = scene.Assignments.Current;
        Assert.Same(first, Assert.Single(shared.DistinctMaterials));
        Assert.Equal(1u, shared.MaterialCount);
        Assert.Equal(1u, shared.SlotCount);
        scene.Update();
        Assert.Same(first, Assert.Single(scene.Assignments.Current.DistinctMaterials));
        Assert.Equal(1u, scene.Assignments.Current.MaterialCount);
        Assert.Equal(1u, scene.Assignments.Current.SlotCount);

        scene.Bind(right, second);
        scene.Update();
        ClusterMaterialAssignmentSet rebound = scene.Assignments.Current;
        Assert.Equal(2u, rebound.MaterialCount);
        Assert.Equal(2u, rebound.SlotCount);
        Assert.Contains(first, rebound.DistinctMaterials);
        Assert.Contains(second, rebound.DistinctMaterials);
        scene.World.DestroyEntity(left);
        scene.Update();
        Assert.Same(second, Assert.Single(scene.Assignments.Current.DistinctMaterials));
        Assert.Equal(1u, scene.Assignments.Current.MaterialCount);
        Assert.Equal(1u, scene.Assignments.Current.SlotCount);

        second.Roughness = .75f;
        scene.Update();
        Material observed = Assert.Single(scene.Assignments.Current.DistinctMaterials);
        Assert.Same(second, observed);
        Assert.Equal(.75f, Assert.IsType<StandardMaterial>(observed).Roughness);
        Assert.Equal(1u, scene.Assignments.Current.MaterialCount);
        Assert.Equal(1u, scene.Assignments.Current.SlotCount);
        scene.World.DestroyEntity(right);
        scene.Update();
        Assert.Empty(scene.Assignments.Current.DistinctMaterials);
        Assert.Equal(0u, scene.Assignments.Current.MaterialCount);
        Assert.Equal(0u, scene.Assignments.Current.SlotCount);

        Assert.Empty(empty.DistinctMaterials);
        Assert.Equal(0u, empty.MaterialCount);
        Assert.Equal(0u, empty.SlotCount);
        Assert.Same(first, Assert.Single(shared.DistinctMaterials));
        Assert.Equal(1u, shared.MaterialCount);
        Assert.Equal(1u, shared.SlotCount);
        Assert.Equal(2u, rebound.MaterialCount);
        Assert.Equal(2u, rebound.SlotCount);
        Assert.Contains(first, rebound.DistinctMaterials);
        Assert.Contains(second, rebound.DistinctMaterials);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingTheLastSharedBindingPreservesEarlierAssignmentSets(bool denseRevisionSnapshots)
    {
        using var scene = new AssignmentScene(denseRevisionSnapshots);
        var first = new StandardMaterial();
        var second = new StandardMaterial();
        Entity left = scene.Add(first);
        Entity right = scene.Add(first);
        _ = scene.Add(second);
        scene.Update();
        ClusterMaterialAssignmentSet before = scene.Assignments.Current;
        Assert.Equal(2u, before.MaterialCount);
        Assert.Equal(2u, before.SlotCount);

        scene.World.DestroyEntity(left);
        scene.Update();
        Assert.Equal(2u, scene.Assignments.Current.MaterialCount);
        scene.World.DestroyEntity(right);
        scene.Update();

        Assert.Same(second, Assert.Single(scene.Assignments.Current.DistinctMaterials));
        Assert.Equal(1u, scene.Assignments.Current.SlotCount);
        Assert.Equal(2u, before.MaterialCount);
        Assert.Equal(2u, before.SlotCount);
        Assert.Contains(first, before.DistinctMaterials);
        Assert.Contains(second, before.DistinctMaterials);
        ulong firstRevision = first.Revision, secondRevision = second.Revision;
        first.Roughness = first.Roughness == .25f ? .75f : .25f;
        second.Roughness = second.Roughness == .75f ? .25f : .75f;
        Assert.Equal(firstRevision + 1, first.Revision);
        Assert.Equal(secondRevision + 1, second.Revision);
        Assert.Same(first, Assert.Single(before.DistinctMaterials, material => ReferenceEquals(material, first)));
        Assert.Same(second, Assert.Single(before.DistinctMaterials, material => ReferenceEquals(material, second)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RebindingAfterASharedMaterialEditPublishesOnlyReferencedMaterials(bool denseRevisionSnapshots)
    {
        using var scene = new AssignmentScene(denseRevisionSnapshots);
        var first = new StandardMaterial();
        var second = new StandardMaterial();
        Entity entity = scene.Add(first);
        scene.Update();
        ClusterMaterialAssignmentSet before = scene.Assignments.Current;

        first.Roughness = .25f;
        scene.Bind(entity, second);
        second.Roughness = .75f;
        scene.Update();

        Assert.Same(second, Assert.Single(scene.Assignments.Current.DistinctMaterials));
        Assert.Equal(1u, scene.Assignments.Current.MaterialCount);
        Assert.Same(first, Assert.Single(before.DistinctMaterials));
        Assert.Equal(.75f, second.Roughness);
        scene.Bind(entity, first);
        scene.Update();
        Assert.Same(first, Assert.Single(scene.Assignments.Current.DistinctMaterials));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaterialBindingsUseTheirObjectsWithoutSeparateRegistration(bool denseRevisionSnapshots)
    {
        using var scene = new AssignmentScene(denseRevisionSnapshots);
        var first = new StandardMaterial();
        var later = new StandardMaterial();
        _ = scene.Add(first);
        scene.Update();
        ClusterMaterialAssignmentSet before = scene.Assignments.Current;

        first.Roughness = .2f;
        _ = scene.Add(later);
        scene.Update();

        Assert.Equal(2u, scene.Assignments.Current.MaterialCount);
        Material observed = Assert.Single(scene.Assignments.Current.DistinctMaterials,
            material => ReferenceEquals(material, first));
        Assert.Same(first, observed);
        Assert.Equal(.2f, Assert.IsType<StandardMaterial>(observed).Roughness);
        Assert.Same(later, Assert.Single(scene.Assignments.Current.DistinctMaterials,
            material => ReferenceEquals(material, later)));
        Assert.Equal(1u, before.MaterialCount);
        Assert.Same(first, Assert.Single(before.DistinctMaterials));
    }


    [Theory]
    [InlineData(131, 131)]
    [InlineData(131, 1)]
    [InlineData(1024, 1)]
    public void ReportMaterialRevisionSnapshotBudget(int materialCount, int changedCount)
    {
        const int warmup = 128, samples = 512;
        using var scene = new AssignmentScene();
        var materials = Enumerable.Range(0, materialCount)
            .Select(_ => new StandardMaterial()).ToArray();
        try
        {
            foreach (var material in materials) _ = scene.Add(material);
            scene.Update();
            var prepareTicks = new long[samples];
            var prepareBytes = new long[samples];
            for (int iteration = -warmup; iteration < samples; iteration++)
            {
                Edit(iteration);
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                scene.Update();
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                if (iteration >= 0) { prepareTicks[iteration] = elapsed; prepareBytes[iteration] = allocated; }
            }

            // This isolates only the current SetValue builder/freeze work. It is an upper
            // bound for removing that work, not a forecast of a different implementation.
            var values = materials.ToImmutableDictionary(m => m.MaterialId,
                m => (Resource: (Material)m, Revision: m.Revision));
            var offsets = materials.ToImmutableDictionary(m => m.MaterialId,
                _ => ImmutableHashSet.Create(0));
            var updateTicks = new long[samples];
            var updateBytes = new long[samples];
            for (int iteration = -warmup; iteration < samples; iteration++)
            {
                Edit(iteration);
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                ImmutableDictionary<long, (Material Resource, ulong Revision)>.Builder? builder = null;
                ImmutableHashSet<long>.Builder? changed = null;
                foreach (var material in materials)
                {
                    if (!offsets.ContainsKey(material.MaterialId)) continue;
                    bool found = builder is null
                        ? values.TryGetValue(material.MaterialId, out var value)
                        : builder.TryGetValue(material.MaterialId, out value);
                    if (found && ReferenceEquals(value.Resource, material) && value.Revision == material.Revision) continue;
                    (builder ??= values.ToBuilder())[material.MaterialId] = (material, material.Revision);
                    (changed ??= ImmutableHashSet<long>.Empty.ToBuilder()).Add(material.MaterialId);
                }
                var next = builder?.ToImmutable() ?? values;
                var changes = changed?.ToImmutable() ?? ImmutableHashSet<long>.Empty;
                long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                if (iteration >= 0) { updateTicks[iteration] = elapsed; updateBytes[iteration] = allocated; }
                Assert.Equal(materialCount, next.Count);
                Assert.Equal(changedCount, changes.Count);
                Assert.NotSame(values, next);
                foreach (long id in changes)
                {
                    Assert.NotEqual(values[id].Revision, next[id].Revision);
                    Assert.Same(values[id].Resource, next[id].Resource);
                }
                values = next;
            }
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "PrepareMaterialRevisionBudget",
                materialCount, changedCount, samples, warmup,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                nativeAot = false,
                actualPrepare = Summarize(prepareTicks, prepareBytes),
                isolatedCurrentBuilder = Summarize(updateTicks, updateBytes),
                interpretation = "CPU budget only; no product algorithm changed and no Native performance claim."
            }));
        }
        finally { GC.KeepAlive(materials); // Managed materials remain caller-owned across preparation. }

        void Edit(int iteration)
        {
            float value = (iteration & 1) == 0 ? .25f : .75f;
            for (int index = 0; index < changedCount; index++) materials[index].Roughness = value;
        }
        static object Summarize(long[] ticks, long[] bytes)
        {
            double scale = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
            double[] us = ticks.Select(value => value * scale).Order().ToArray();
            return new { meanUs = us.Average(), minUs = us[0], p50Us = us[us.Length / 2],
                p95Us = us[(int)Math.Ceiling(us.Length * .95) - 1], maxUs = us[^1],
                meanAllocatedBytes = bytes.Average(), minAllocatedBytes = bytes.Min(), maxAllocatedBytes = bytes.Max() };
        }
    }


    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(131)]
    public void MaterialChangesPreserveHistoryOrderAndTopology(int materialCount)
    {
        using var baseline = new AssignmentScene(false);
        using var candidate = new AssignmentScene(true);
        var materials = Enumerable.Range(0, materialCount).Select(_ => new StandardMaterial()).ToArray();
        var later = new StandardMaterial();
        var first = new Entity[materialCount];
        var second = new Entity[materialCount];
        var history = new List<(ClusterMaterialAssignmentSet Baseline, ClusterMaterialAssignmentSet Candidate,
            PublicAssignmentSnapshot Snapshot)>();
        for (int index = 0; index < materials.Length; index++)
        {
            first[index] = baseline.Add(materials[index]);
            second[index] = candidate.Add(materials[index]);
        }
        UpdateAndCompare();
        UpdateAndCompare();
        PublicAssignmentSnapshot initialized = CaptureSnapshot(candidate.Assignments.Current);
        UpdateAndCompare();
        AssertSnapshotEqual(initialized, candidate.Assignments.Current);
        EditAll(.1f); UpdateAndCompare();
        materials[0].Roughness = .2f; UpdateAndCompare();
        UpdateAndCompare();
        EditAll(.3f); UpdateAndCompare();
        materials[1].Roughness = .4f; UpdateAndCompare();
        baseline.Bind(first[0], materials[1]); candidate.Bind(second[0], materials[1]); UpdateAndCompare();
        UpdateAndCompare();
        Assert.Equal((uint)(materialCount - 1), candidate.Assignments.Current.MaterialCount);
        baseline.Bind(first[0], materials[0]); candidate.Bind(second[0], materials[0]); UpdateAndCompare();
        EditAll(.5f); UpdateAndCompare();

        Entity extraFirst = baseline.Add(later), extraSecond = candidate.Add(later);
        EditAll(.6f); UpdateAndCompare();
        baseline.World.DestroyEntity(extraFirst); candidate.World.DestroyEntity(extraSecond); UpdateAndCompare();
        baseline.World.DestroyEntity(first[^1]); candidate.World.DestroyEntity(second[^1]); UpdateAndCompare();
        first[^1] = baseline.Add(materials[^1]); second[^1] = candidate.Add(materials[^1]); UpdateAndCompare();
        EditAll(.7f); UpdateAndCompare();
        materials[0].Roughness = .8f; UpdateAndCompare();
        GC.KeepAlive(materials);

        void EditAll(float value) { foreach (var material in materials) material.Roughness = value; }
        void UpdateAndCompare()
        {
            baseline.Update(); candidate.Update();
            var a = baseline.Assignments.Current;
            var b = candidate.Assignments.Current;
            AssertAssignmentEqual(a, b);
            foreach (var previous in history)
            {
                AssertSnapshotEqual(previous.Snapshot, previous.Baseline);
                AssertSnapshotEqual(previous.Snapshot, previous.Candidate);
                AssertAssignmentEqual(previous.Baseline, previous.Candidate);
            }
            history.Add((a, b, CaptureSnapshot(a)));
        }
        // Exact captured revision deltas, private slot maps and dense-array identity are not public.
        // Rendered all/one/no-edit and discarded-frame correctness is covered by RawMaterialScalarRenderingTests.
    }

    private sealed record PublicAssignmentSnapshot(ulong TopologyVersion, uint MaterialCount,
        uint SlotCount, uint SlotCapacity, Material[] Materials);

    private static PublicAssignmentSnapshot CaptureSnapshot(ClusterMaterialAssignmentSet assignments) =>
        new(assignments.TopologyVersion, assignments.MaterialCount, assignments.SlotCount,
            assignments.SlotCapacity, assignments.DistinctMaterials.ToArray());

    private static void AssertSnapshotEqual(PublicAssignmentSnapshot expected, ClusterMaterialAssignmentSet actual)
    {
        Assert.Equal(expected.TopologyVersion, actual.TopologyVersion);
        Assert.Equal(expected.MaterialCount, actual.MaterialCount);
        Assert.Equal(expected.SlotCount, actual.SlotCount);
        Assert.Equal(expected.SlotCapacity, actual.SlotCapacity);
        Assert.Equal(expected.Materials, actual.DistinctMaterials.ToArray());
    }

    private static void AssertAssignmentEqual(ClusterMaterialAssignmentSet baseline, ClusterMaterialAssignmentSet candidate)
        => AssertSnapshotEqual(CaptureSnapshot(baseline), candidate);

    [Theory]
    [InlineData(131, 131)]
    [InlineData(131, 1)]
    [InlineData(1024, 1)]
    public void CompareDenseRevisionSnapshotCost(int materialCount, int changedCount)
    {
        const int warmup = 512, samples = 1024;
        using var baseline = new AssignmentScene(false);
        using var candidate = new AssignmentScene(true);
        var materials = Enumerable.Range(0, materialCount)
            .Select(_ => new StandardMaterial()).ToArray();
        var beforeTicks = new long[samples]; var afterTicks = new long[samples];
        var beforeBytes = new long[samples]; var afterBytes = new long[samples];
        try
        {
            foreach (var material in materials)
            {
                _ = baseline.Add(material); _ = candidate.Add(material);
            }
            baseline.Update(); candidate.Update();
            for (int iteration = -warmup; iteration < samples; iteration++)
            {
                float value = (iteration & 1) == 0 ? .25f : .75f;
                for (int index = 0; index < changedCount; index++) materials[index].Roughness = value;
                if ((iteration & 1) == 0)
                {
                    Measure(baseline, iteration, beforeTicks, beforeBytes);
                    Measure(candidate, iteration, afterTicks, afterBytes);
                }
                else
                {
                    Measure(candidate, iteration, afterTicks, afterBytes);
                    Measure(baseline, iteration, beforeTicks, beforeBytes);
                }
                AssertAssignmentEqual(baseline.Assignments.Current, candidate.Assignments.Current);
            }
            output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                kind = "DenseMaterialRevisionComparison", materialCount, changedCount, samples, warmup,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, nativeAot = false,
                baseline = Summarize(beforeTicks, beforeBytes), candidate = Summarize(afterTicks, afterBytes),
                alternatingOrder = true,
                interpretation = "Same real WARP/ECS extraction and residency fixture and material identities; CPU preparation cost only, not isolated assignment allocation or Native frame-time evidence."
            }));
        }
        finally { GC.KeepAlive(materials); // Managed materials remain caller-owned across preparation. }

        static void Measure(AssignmentScene scene, int iteration, long[] ticks, long[] bytes)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            scene.Update();
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (iteration >= 0) { ticks[iteration] = elapsed; bytes[iteration] = allocated; }
        }
        static object Summarize(long[] ticks, long[] bytes)
        {
            double scale = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
            double[] us = ticks.Select(value => value * scale).Order().ToArray();
            return new { meanUs = us.Average(), minUs = us[0], p50Us = us[us.Length / 2],
                p95Us = us[(int)Math.Ceiling(us.Length * .95) - 1], maxUs = us[^1],
                meanAllocatedBytes = bytes.Average(), minAllocatedBytes = bytes.Min(), maxAllocatedBytes = bytes.Max() };
        }
    }

    private sealed class AssignmentScene : IDisposable
    {
        private readonly IGraphicsBackend _backend;
        private readonly Device _device;
        private readonly RenderDevice _renderDevice;
        private readonly RenderFrameCoordinator _access;
        private readonly RenderInstanceStorageSystem _instances;
        private readonly ClusterRenderResources _cluster;
        private readonly RenderExtractionSystems _extraction;
        private readonly RenderPrepareSystems _prepare;
        private readonly Mesh _mesh;
        private readonly RenderWorld _world = new();
        internal World World { get; } = new();
        internal ClusterMaterialAssignments Assignments { get; } = new();

        internal AssignmentScene(bool denseRevisionSnapshots = false)
        {
            // Main coordination needs a real RenderDevice. WARP avoids requiring a physical GPU;
            // geometry is published by the real residency owner instead of fabricating epoch handles.
            _backend = D3D12GraphicsBackend.Create();
            var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
            _backend.TryEnumerateAdapters(options, [], out int count);
            var adapters = new AdapterInfo[count];
            _backend.TryEnumerateAdapters(options, adapters, out _);
            AdapterInfo warp = adapters.First(adapter => !adapter.HardwareAccelerated);
            _device = _backend.CreateDevice(new DeviceDesc(warp.Id,
                [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)],
                label: "Material assignment public behavior tests"));
            _renderDevice = new RenderDevice(_backend, _device);
            _access = new RenderFrameCoordinator(_renderDevice);
            _instances = new RenderInstanceStorageSystem(_backend, _device, _access, _world,
                ClusterRenderFeature.InstanceLayout,
                new RenderInstanceOptions { RowCapacity = 2048, BatchCapacity = 8 });
            _cluster = new ClusterRenderResources(_backend, _device, _access, _world, _instances,
                new ClusterRenderOptions { Residency = new ClusterResidencyOptions
                    { PageHeapCapacityBytes = 1048576, BvhCapacityBytes = 65536, PageFaultCapacity = 16 } });
            _mesh = RasterTestGeometry.CreatePlaneAsync().GetAwaiter().GetResult();
            _extraction = new RenderExtractionSystems(_world, new MeshRenderExtractor());
            _prepare = new RenderPrepareSystems(_world, _instances, _extraction);
            _prepare.Add(new ClusterResidencySystem(_cluster));
            _prepare.Add(new ClusterMaterialAssignmentSystem(Assignments, denseRevisionSnapshots));
        }

        internal Entity Add(Material material)
        {
            Entity entity = World.CreateEntity(new MeshInstance { Mesh = _mesh });
            World.Add(entity, new WorldTransform { Qvvs = new TransformQvvs(Vector3.Zero, Quaternion.Identity) });
            World.AddBuffer<MeshMaterialBinding>(entity);
            Bind(entity, material);
            return entity;
        }

        internal void Bind(Entity entity, Material material)
        {
            World.AcquireSystemVersion();
            World.ExecuteBufferWrite<MeshMaterialBinding, Material>(entity, ref material,
                static (DynamicBuffer<MeshMaterialBinding> bindings, ref Material value) =>
                {
                    bindings.Clear();
                    bindings.Add(new MeshMaterialBinding(value));
                });
        }

        internal void Update()
        {
            _extraction.Extract(World);
            Assert.True(_access.TryBeginPrepare(out RenderPrepareScope? scope));
            using (scope)
            {
                _prepare.ApplyExtraction(scope);
                _cluster.PrepareMeshesAsync().AsTask().GetAwaiter().GetResult();
                _prepare.Update(scope);
                scope.Commit();
            }
        }

        public void Dispose()
        {
            _access.WaitForTrackedSubmissions();
            Assert.True(_access.TryBeginPrepare(out RenderPrepareScope? scope));
            using (scope)
            {
                _prepare.Shutdown(scope);
                _cluster.Shutdown(scope);
                _instances.Shutdown(scope);
                scope.Commit();
            }
            _prepare.Dispose();
            _cluster.Dispose();
            _instances.Dispose();
            _extraction.Dispose();
            _world.Dispose();
            World.Dispose();
            _access.Dispose();
            _renderDevice.Dispose();
            _device.Dispose();
            _backend.Dispose();
        }
    }
}
