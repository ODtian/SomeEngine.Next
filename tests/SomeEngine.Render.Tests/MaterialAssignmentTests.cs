using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;
using SomeEngine.Resources;

namespace SomeEngine.Render.Tests;

public sealed class MaterialAssignmentTests(RenderTestDevice graphics) : IClassFixture<RenderTestDevice>
{
    [Fact]
    public void SharedCombinationsKeepTheirRangesAndReleasedRangesServeNewBindings()
    {
        var assets = new TestAssets();
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new MeshRenderExtractor());
        var sources = new List<Entity>();
        var assignments = new ClusterMaterialAssignments();
        using var prepare = new RenderPrepareSystems(render, extraction);
        prepare.Add(new ClusterMaterialAssignmentSystem(assignments));
        Material first = assets.Material(1), second = assets.Material(2);
        Entity one = AddSource(first, second), two = AddSource(first, second);
        Apply();
        Assert.Equal(2u, assignments.Current.SlotCount);
        Assert.Equal(2u, assignments.Current.SlotCapacity);

        SetBindings(one, assets.Material(3), assets.Material(4));
        Apply();
        Assert.Equal(4u, assignments.Current.SlotCount);
        Assert.Equal(4u, assignments.Current.SlotCapacity);
        game.DestroyEntity(two);
        Apply();
        Assert.Equal(2u, assignments.Current.SlotCount);
        AddSource(assets.Material(5), assets.Material(6));
        Apply();
        Assert.Equal(4u, assignments.Current.SlotCount);
        Assert.Equal(4u, assignments.Current.SlotCapacity);

        Assert.True(graphics.Coordinator.TryBeginPrepare(out var closing));
        using (closing) { prepare.Shutdown(closing); closing.Commit(); }

        Entity AddSource(Material a, Material b)
        {
            Entity entity = game.CreateEntity(new WorldTransform());
            game.Add(entity, new MeshInstance { Mesh = assets.Mesh(1) });
            game.AddBuffer<MeshMaterialBinding>(entity);
            SetBindings(entity, a, b);
            sources.Add(entity);
            return entity;
        }

        void SetBindings(Entity entity, Material a, Material b) =>
            game.ExecuteBufferWrite<MeshMaterialBinding>(entity, values =>
            {
                values.Clear();
                values.Add(new(a));
                values.Add(new(b));
            });

        void Apply()
        {
            extraction.Extract(game);
            graphics.Apply(extraction, render);
            foreach (Entity entity in sources)
                if (game.IsAlive(entity)) AddGeometry(entity);
            Assert.True(graphics.Coordinator.TryBeginPrepare(out var scope));
            using (scope) { prepare.Update(scope); scope.Commit(); }
        }

        void AddGeometry(Entity source)
        {
            Entity mirror = extraction.RequireMirror(source);
            if (!render.Has<RenderClusterGeometry>(mirror))
                render.Add(mirror, new RenderClusterGeometry(new(0, 1, "render-tests.opaque", null, default), 0));
        }
    }
}
