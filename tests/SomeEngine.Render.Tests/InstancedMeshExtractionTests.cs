using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;
using SomeEngine.Render.Instances;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class InstancedMeshExtractionTests
{
    [Fact]
    public void ExtractionPublishesStableResourceIdentityAndTransformWithoutMeshPrototype()
    {
        using var collection = new RenderMeshInstanceCollection();
        using RenderMeshInstanceSet first = CreateSet(4);
        using RenderMeshInstanceSet second = CreateSet(8);
        RenderMeshInstanceHandle firstHandle = collection.Add(first);
        RenderMeshInstanceHandle secondHandle = collection.Add(second);

        using var mainWorld = new World();
        using var renderWorld = new RenderWorld();
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity source = mainWorld.CreateEntity();
        mainWorld.Add(source, new WorldTransform
        {
            Qvvs = new TransformQvvs(new Vector3(1, 2, 3), Quaternion.Identity),
        });
        mainWorld.Add(source, new InstancedMesh(firstHandle));

        extraction.Extract(mainWorld);
        Entity mirror = FindMirror(renderWorld, source);
        Assert.False(renderWorld.Has<RenderMesh>(mirror));
        Assert.False(renderWorld.Has<RenderInstance>(mirror));
        Assert.Equal(firstHandle, renderWorld.Read<RenderInstancedMesh>(mirror).Resource);
        Assert.Equal(new Vector3(1, 2, 3), renderWorld.Read<RenderTransform>(mirror).Position);

        mainWorld.Replace(source, new InstancedMesh(secondHandle));
        mainWorld.Replace(source, new WorldTransform
        {
            Qvvs = new TransformQvvs(new Vector3(4, 5, 6), Quaternion.Identity),
        });
        extraction.Extract(mainWorld);

        Assert.Equal(secondHandle, renderWorld.Read<RenderInstancedMesh>(mirror).Resource);
        Assert.Equal(new Vector3(4, 5, 6), renderWorld.Read<RenderTransform>(mirror).Position);
    }

    [Fact]
    public void RemovingLastRenderFacetDestroysTheMirror()
    {
        using var collection = new RenderMeshInstanceCollection();
        using RenderMeshInstanceSet set = CreateSet(1);
        RenderMeshInstanceHandle handle = collection.Add(set);
        using var mainWorld = new World();
        using var renderWorld = new RenderWorld();
        using var extraction = new RenderExtractionSystems(renderWorld);
        Entity source = mainWorld.CreateEntity();
        mainWorld.Add(source, new WorldTransform { Qvvs = TransformQvvs.Identity });
        mainWorld.Add(source, new InstancedMesh(handle));

        extraction.Extract(mainWorld);
        Entity mirror = FindMirror(renderWorld, source);
        mainWorld.Remove<InstancedMesh>(source);
        extraction.Extract(mainWorld);

        Assert.False(renderWorld.IsAlive(mirror));
    }

    private static RenderMeshInstanceSet CreateSet(int count) => new(
        TestAssets.Mesh(101),
        [TestAssets.Material(102)],
        count,
        static (_, current, previous) =>
        {
            current.Clear();
            previous.Clear();
        });

    private static Entity FindMirror(RenderWorld world, Entity source)
    {
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<RenderSource>());
        try
        {
            var state = new MirrorSearch(source);
            world.ExecuteQuery(
                query,
                ref state,
                static (QueryCursor cursor, ref MirrorSearch search) =>
                {
                    foreach (QueryRow row in cursor.Rows)
                    {
                        if (row.Read<RenderSource>().Entity == search.Source)
                        {
                            search.Mirror = row.Entity;
                            return;
                        }
                    }
                });
            return state.Mirror != Entity.Null
                ? state.Mirror
                : throw new InvalidOperationException($"No RenderWorld mirror exists for {source}.");
        }
        finally
        {
            world.ReleaseQuery(query);
        }
    }

    private struct MirrorSearch(Entity source)
    {
        internal Entity Source = source;
        internal Entity Mirror = Entity.Null;
    }
}
