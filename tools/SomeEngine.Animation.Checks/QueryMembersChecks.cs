using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Animation.Checks;

internal static class QueryMembersChecks
{
    internal static void Check()
    {
        CheckMembership();
        CheckWritableRows();
    }

    private static void CheckMembership()
    {
        using var world = new World(execution: new WorldJobs(JobSystem.Default));
        QueryHandle enabled = world.Query(world.QueryDefinition().Read<Visibility>().Enabled<Visibility>());
        QueryHandle disabled = world.Query(world.QueryDefinition().Read<Visibility>().Disabled<Visibility>());
        Require((false, false), Members());

        Entity first = world.CreateEntity(new Visibility());
        Require((true, false), Members());
        world.Disable<Visibility>(first);
        Require((false, true), Members());
        world.Enable<Visibility>(first);
        Require((true, false), Members());
        world.DestroyEntity(first);
        Require((false, false), Members());
        world.CreateEntity(new Visibility());
        Require((true, false), Members());
        world.ReleaseQuery(enabled);
        world.ReleaseQuery(disabled);

        (bool Enabled, bool Disabled) Members() =>
            (world.HasQueryMembers(enabled), world.HasQueryMembers(disabled));
    }

    private static void Require((bool Enabled, bool Disabled) expected, (bool Enabled, bool Disabled) actual)
    {
        if (actual != expected) throw new InvalidOperationException("Query members must follow the current entity and enable state.");
    }

    private static void CheckWritableRows()
    {
        using var world = new World(execution: new WorldJobs(JobSystem.Default));
        Entity first = Create(), second = Create(), held = Create();
        world.Disable<Visibility>(held);
        uint heldVersion = world.WriteVersion<Visibility>(held);
        QueryHandle query = world.Query(world.QueryDefinition().ReadWrite<Visibility>().Enabled<Visibility>().WriteBuffer<Visits>());
        Update();
        if ((world.Read<Visibility>(first).Count, world.Read<Visibility>(second).Count, world.Read<Visibility>(held).Count)
            != (1, 1, 0) || world.WriteVersion<Visibility>(held) != heldVersion)
            throw new InvalidOperationException("Writable query rows must update the selected members and their versions.");
        world.Enable<Visibility>(held);
        Update();
        if ((world.Read<Visibility>(first).Count, world.Read<Visibility>(second).Count, world.Read<Visibility>(held).Count)
            != (2, 2, 1)) throw new InvalidOperationException("Newly enabled rows must enter the same writable traversal.");
        int visited = 0;
        world.ExecuteBufferRead<Visits, int>(first, ref visited, static (values, ref visited) => visited = values[0].Count);
        if (visited != 3) throw new InvalidOperationException("Writable row buffers must retain both updates.");
        world.ReleaseQuery(query);

        world.Disable<Visibility>(held);
        heldVersion = world.WriteVersion<Visibility>(held);
        QueryHandle columns = world.Query(world.QueryDefinition().ReadWrite<Visibility>().Enabled<Visibility>().Buffer<Visits>(QueryAccess.Read));
        world.ExecuteQuery(columns, static (scoped QueryCursor rows) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                var values = chunk.ReadWriteColumn<Visibility>();
                var visits = chunk.ReadBufferColumn<Visits>();
                foreach (int row in chunk.RowIndices) values[row].Count += visits[row][0].Count;
            }
        });
        if ((world.Read<Visibility>(first).Count, world.Read<Visibility>(second).Count, world.Read<Visibility>(held).Count)
            != (5, 5, 1) || world.WriteVersion<Visibility>(held) != heldVersion)
            throw new InvalidOperationException("Bound columns must retain the query selection and write versions while reading row buffers.");
        world.ReleaseQuery(columns);

        QueryHandle buffers = world.Query(world.QueryDefinition().Read<Visibility>().WriteBuffer<Visits>());
        world.ExecuteQuery(buffers, static (scoped QueryCursor rows) =>
        {
            foreach (QueryChunkView chunk in rows.Chunks)
            {
                var enabled = chunk.ReadEnabledColumn<Visibility>();
                var visits = chunk.WriteBufferColumn<Visits>();
                foreach (int row in chunk.RowIndices)
                    if (enabled[row]) visits[row][0].Count++;
            }
        });
        world.ExecuteBufferRead<Visits, int>(first, ref visited, static (values, ref visited) => visited = values[0].Count);
        if (visited != 4) throw new InvalidOperationException("Bound writable buffer columns must retain the enabled updates.");
        world.ExecuteBufferRead<Visits, int>(held, ref visited, static (values, ref visited) => visited = values[0].Count);
        if (visited != 1) throw new InvalidOperationException("Bound enable columns must read each row's actual enable bit.");
        world.ReleaseQuery(buffers);

        Entity Create()
        {
            Entity entity = world.CreateEntity(new Visibility());
            world.AddBuffer<Visits>(entity);
            world.ExecuteBufferWrite<Visits>(entity, static values => values.Add(default));
            return entity;
        }
        void Update() => world.ExecuteQuery(query, static (scoped QueryCursor rows) =>
        {
            var selected = rows.ReadWriteRows<Visibility, Visits>();
            while (selected.MoveNext(out _, out var component, out var buffer))
            {
                component.Value.Count++;
                buffer[0].Count += component.Value.Count;
            }
        });
    }

    private struct Visibility : IEnableableComponent { public int Count; }
    private struct Visits : IBufferElement { public int Count; }
}
