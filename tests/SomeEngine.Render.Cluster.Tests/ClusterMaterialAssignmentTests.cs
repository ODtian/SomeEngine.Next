using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterMaterialAssignmentTests
{
    [Fact]
    public void RemovingTheLastSharedBindingPreservesEarlierAssignmentSets()
    {
        using var scene = new AssignmentScene();
        using var first = new ImportedScenes.SceneMaterial();
        using var second = new ImportedScenes.SceneMaterial();
        scene.Register(first);
        scene.Register(second);
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
    }

    [Fact]
    public void RebindingAfterASharedMaterialEditPublishesOnlyReferencedMaterials()
    {
        using var scene = new AssignmentScene();
        using var first = new ImportedScenes.SceneMaterial();
        using var second = new ImportedScenes.SceneMaterial();
        scene.Register(first);
        scene.Register(second);
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

    [Fact]
    public void FailedMaterialDiscoveryKeepsThePublishedAssignmentsForRetry()
    {
        using var scene = new AssignmentScene();
        using var first = new ImportedScenes.SceneMaterial();
        using var later = new ImportedScenes.SceneMaterial();
        scene.Register(first);
        _ = scene.Add(first);
        scene.Update();
        ClusterMaterialAssignmentSet before = scene.Assignments.Current;

        first.Roughness = .2f;
        _ = scene.Add(later);
        Assert.Throws<KeyNotFoundException>(scene.Update);
        Assert.Same(before, scene.Assignments.Current);
        Assert.Same(first, Assert.Single(before.DistinctMaterials));

        scene.Register(later);
        scene.Update();
        Assert.Equal(2u, scene.Assignments.Current.MaterialCount);
        Assert.Contains(first, scene.Assignments.Current.DistinctMaterials);
        Assert.Contains(later, scene.Assignments.Current.DistinctMaterials);
        Assert.Equal(1u, before.MaterialCount);
    }

    private sealed class AssignmentScene : IDisposable
    {
        private readonly RenderDataLock _access = new();
        private readonly RenderExtractionSystems _extraction;
        private readonly RenderPrepareSystems _prepare;
        internal RenderWorld World { get; } = new();
        internal ClusterMaterialAssignments Assignments { get; } = new();

        internal AssignmentScene()
        {
            _extraction = new RenderExtractionSystems(World);
            _prepare = new RenderPrepareSystems(World, _extraction);
            _prepare.Add(new ClusterMaterialAssignmentSystem(Assignments));
        }

        internal void Register(Material material) =>
            World.CreateEntity(new RenderMaterialReference(material));

        internal Entity Add(Material material)
        {
            Entity entity = World.CreateEntity(new RenderInstance());
            World.Add(entity, new RenderTransform());
            World.Add(entity, new RenderPreviousTransform(default));
            World.Add(entity, new RenderMesh(new Mesh(), 0));
            World.AddBuffer<RenderMaterialBinding>(entity);
            Bind(entity, material);
            return entity;
        }

        internal void Bind(Entity entity, Material material)
        {
            World.AcquireSystemVersion();
            long id = material.MaterialId;
            World.ExecuteBufferWrite<RenderMaterialBinding, long>(entity, ref id,
                static (DynamicBuffer<RenderMaterialBinding> bindings, ref long value) =>
                {
                    bindings.Clear();
                    bindings.Add(new RenderMaterialBinding(value));
                });
        }

        internal void Update()
        {
            Assert.True(_access.TryBeginPrepare(out RenderPrepareScope? scope));
            using (scope) _prepare.Update(scope);
        }

        public void Dispose()
        {
            Assert.True(_access.TryBeginPrepare(out RenderPrepareScope? scope));
            using (scope) _prepare.Shutdown(scope);
            _prepare.Dispose();
            _extraction.Dispose();
            World.Dispose();
        }
    }
}
