using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

internal static class CommandChecks
{
    internal static void Run()
    {
        using var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity created = commands.Create(new Position { X = 3 });
        commands.Set(created, new Position { X = 7 });
        commands.Add(created, new Extra { Value = 9 });
        commands.Remove<Extra>(created);
        commands.SetBuffer<BufferValue>(created, [new(4), new(8)]);
        commands.AppendBuffer<BufferValue>(created, [new(12)]);
        commands.SetSparse(created, new SparseValue(15));
        Require(!created.TryResolve(out _), "create results publish with completed playback");
        commands.Playback();
        Entity entity = created.Resolve();
        using (var buffer = world.ReadBuffer<BufferValue>(entity))
            Require(world.Read<Position>(entity).X == 7 && !world.Has<Extra>(entity)
                && buffer.Count == 3 && buffer[2].Value == 12 && world.ReadSparse<SparseValue>(entity).Value == 15,
                "typed commands preserve FIFO values, independent buffer elements and sparse data");
        Require(commands.Count == 0 && commands.CompletedCommands == 7, "successful playback clears recording payloads and exposes its completed prefix");
        commands.Clear();
        Require(created.Resolve() == entity && world.IsAlive(entity), "published handles retain their identity across Clear");
        bool oldBatchRejected = false;
        try { commands.Set(created, new Position()); } catch (ArgumentException) { oldBatchRejected = true; }
        Require(oldBatchRejected, "recording checks the deferred handle's batch");
        commands.Add(entity, new Switch { Value = 5 });
        commands.Disable<Switch>(entity);
        commands.Enable<Switch>(entity);
        commands.RemoveSparse<SparseValue>(entity);
        commands.RemoveBuffer<BufferValue>(entity);
        commands.Playback();
        Require(world.IsEnabled<Switch>(entity) && !world.HasSparse<SparseValue>(entity)
            && !world.Has<Buffer<BufferValue>>(entity), "enable and typed removal commands finish through the native World operations");
        commands.Clear();
        DeferredEntity doomed = commands.Create(new Position { X = 11 });
        commands.Destroy(doomed);
        commands.Playback();
        Require(!world.IsAlive(doomed.Resolve()), "created-then-destroyed results retain the completed creation identity");
        commands.Clear();
        DeferredEntity prefix = commands.Create(new Position { X = 13 });
        commands.Set(doomed.Resolve(), new Position { X = 17 });
        commands.Set(entity, new Position { X = 19 });
        bool faulted = false;
        try { commands.Playback(); } catch (ArgumentException) { faulted = true; }
        Require(faulted && commands.CompletedCommands == 1 && world.Read<Position>(prefix.Resolve()).X == 13
            && world.Read<Position>(entity).X == 7, "failed playback publishes its successful prefix and leaves the following command for batch cleanup");
        commands.Clear();

        using var batch = new CommandBatch(world, 4);
        var results = new DeferredEntity[4];
        Parallel.For(0, 4, producer =>
        {
            CommandBuffer writer = batch[producer];
            results[producer] = writer.Create(new Position { X = producer });
            writer.Set(entity, new Position { X = producer + 20 });
        });
        batch.Playback();
        for (int i = 0; i < results.Length; i++) Require(world.Read<Position>(results[i].Resolve()).X == i,
            "producer-local creates resolve through deterministic producer offsets");
        Require(world.Read<Position>(entity).X == 23, "playback observes producer index order");
        batch.Dispose();
        Require(world.IsAlive(results[3].Resolve()), "producer results retain their identities after Dispose");
        commands.Dispose();
        Require(created.Resolve() == entity, "single-producer results retain their identities after Dispose");
        CheckTopology();
        CheckEntityFields();
        Console.WriteLine("Commands: FIFO playback, typed values/buffers/sparse, successful prefixes, retained handles and four producers passed.");
    }

    private static void Require(bool condition, string behavior)
    {
        if (!condition) throw new InvalidOperationException(behavior);
    }

    private static void CheckTopology()
    {
        using var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity root = commands.Create(), child = commands.Create(), leaf = commands.Create();
        commands.EditParents<CommandTree>([new(child, root), new(leaf, child)]);
        commands.Playback();
        Entity r = root.Resolve(), c = child.Resolve(), l = leaf.Resolve();
        Require(Hierarchy<CommandTree>.GetParent(world, c) == r && Hierarchy<CommandTree>.GetParent(world, l) == c,
            "parent batches decode preceding create results before final forest validation");
        commands.Clear();
        ulong before = world.Version;
        commands.EditParents<CommandTree>([new(c, l), new(l, r)]);
        commands.Playback();
        Require(world.Version == before + 1 && Hierarchy<CommandTree>.GetParent(world, c) == l
            && Hierarchy<CommandTree>.GetParent(world, l) == r,
            "a composite parent exchange commits its final forest with one write version");
        commands.Clear();
        commands.Set(r, new Position { X = 5 });
        commands.EditParents<CommandTree>([new(c, l), new(l, c)]);
        bool rejected = false;
        try { commands.Playback(); } catch (ArgumentException) { rejected = true; }
        Require(rejected && world.Read<Position>(r).X == 5 && Hierarchy<CommandTree>.GetParent(world, l) == r,
            "a failed composite parent batch preserves its preceding command and complete earlier forest");
        commands.Clear();
        Entity a = world.Create(), b = world.Create(), d = world.Create(), e = world.Create();
        var first = world.CreateRelation(a, b, new CommandLink(1));
        var second = world.CreateRelation(d, e, new CommandLink(2));
        before = world.Version;
        commands.EditEndpoints<CommandLink>([new(first.Entity, a, e), new(second.Entity, d, b)]);
        commands.Playback();
        Require(world.Version == before + 1 && world.Read<RelationEndpoints<CommandLink>>(first.Entity).Target == e
            && world.Read<RelationEndpoints<CommandLink>>(second.Entity).Target == b,
            "a composite endpoint exchange validates final cardinality and commits one write version");
        commands.Clear();
        commands.EditEndpoints<CommandLink>([new(first.Entity, a, b), new(second.Entity, d, b)]);
        rejected = false;
        try { commands.Playback(); } catch (InvalidOperationException) { rejected = true; }
        Require(rejected && world.Read<RelationEndpoints<CommandLink>>(first.Entity).Target == e,
            "a conflicting composite endpoint batch preserves the complete preceding mapping");
    }

    private static void CheckEntityFields()
    {
        using var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity a = commands.Create(new Position { X = 1 }), b = commands.Create(new Position { X = 2 });
        DeferredEntity holder = commands.Create<CommandReference, CommandReferenceFields>(new() { Value = 7 },
            [new(0, a), new(1, b)]);
        commands.SetBuffer<CommandReference, CommandReferenceFields>(holder, [new() { Value = 11 }, new() { Value = 13 }],
            [new(0, 0, b), new(1, 1, a)]);
        commands.Playback();
        CommandReference value = world.Read<CommandReference>(holder.Resolve());
        Require(value.First == a.Resolve() && value.Second == b.Resolve() && value.Value == 7,
            "typed field writers decode preceding create results into component entity fields");
        using var buffer = world.ReadBuffer<CommandReference>(holder.Resolve());
        Require(buffer[0].First == b.Resolve() && buffer[0].Value == 11 && buffer[1].Second == a.Resolve() && buffer[1].Value == 13,
            "buffer entity fields decode through explicit element and field indices");
    }

    private readonly record struct BufferValue(int Value);
    private readonly record struct SparseValue(int Value);
    private struct Switch : IEnableableComponent { internal int Value; }
    private readonly struct CommandTree : IHierarchyDomain;
    [RelationSchema(RelationDirection.Directed, RelationCardinality.OneToOne)]
    private readonly record struct CommandLink(int Value) : IComponent;
    private struct CommandReference : IComponent
    {
        internal Entity First, Second;
        internal int Value;
    }
    private readonly struct CommandReferenceFields : IEntityFieldWriter<CommandReference>
    {
        public static void Set(ref CommandReference value, int field, Entity entity)
        {
            if (field == 0) value.First = entity; else value.Second = entity;
        }
    }
}
