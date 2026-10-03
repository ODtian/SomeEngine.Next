using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Views.Tests;

/// <summary>Public structure dependency behavior shared with the ECS NativeAOT executable.</summary>
internal static class StructureDependencyChecks
{
    internal static void Verify()
    {
        using var frames = new HeadlessFrameDriver();
        VerifyPresence(frames);
        VerifyEnemyList(frames);
    }

    private static void VerifyPresence(HeadlessFrameDriver frames)
    {
        Entity source = frames.World.CreateEntity();
        Entity root = Views.Spawn(frames.World, Entity.Null, new PresenceDisplay(source));
        frames.Frame();
        Require(frames.World.Read<DisplayText>(root).Value == "False:False", "An empty source displays absent sparse data and tag.");

        frames.World.AddSparse(source, new Health(100));
        frames.World.AddTag<Enemy>(source);
        frames.Frame();
        Require(frames.World.Read<DisplayText>(root).Value == "True:True", "Adding sparse data and a tag updates their presence.");
        uint displayVersion = frames.World.WriteVersion<DisplayText>(root);

        frames.World.ReplaceSparse(source, new Health(80));
        frames.Frame();
        Require(frames.World.WriteVersion<DisplayText>(root) == displayVersion, "Value writes retain the presence display version.");
        frames.World.RemoveSparse<Health>(source);
        frames.Frame();
        Require(frames.World.Read<DisplayText>(root).Value == "False:True", "Removing sparse data retains the tag presence.");
        frames.World.DestroyEntity(source);
        frames.Frame();
        Require(frames.World.Read<DisplayText>(root).Value == "False:False", "Destroying the source clears its displayed presence.");
    }

    private static void VerifyEnemyList(HeadlessFrameDriver frames)
    {
        Entity heading = frames.World.CreateEntity(new DisplayText("Enemies"));
        QueryHandle query = frames.World.Query(frames.World.QueryDefinition().All<Enemy>());
        Entity first = CreateEnemy(frames.World, 100);
        Entity second = CreateEnemy(frames.World, 80);
        Entity root = Views.Spawn(frames.World, Entity.Null, new EnemyList(query, heading));
        frames.Frame();
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Require(rows.Length == 2 && frames.World.Read<EnemyRow>(rows[0]).Source == first &&
            frames.World.Read<EnemyRow>(rows[1]).Source == second, "The list displays both query entities in order.");
        frames.World.Add(rows[1], new Selection(7));
        uint secondDisplayVersion = frames.World.WriteVersion<DisplayText>(rows[1]);

        frames.World.Replace(heading, new DisplayText("Survivors"));
        frames.World.ReplaceSparse(first, new Health(60));
        frames.Frame();
        Require(frames.World.Read<DisplayText>(rows[0]).Value == "60", "A sparse health write updates its row.");
        Require(frames.World.Read<DisplayText>(root).Value == "Enemies", "A sparse health write retains the parent heading.");
        Require(frames.World.WriteVersion<DisplayText>(rows[1]) == secondDisplayVersion, "The other row retains its display version.");

        Entity third = CreateEnemy(frames.World, 40);
        frames.Frame();
        Entity[] expanded = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Require(expanded.Length == 3 && expanded[0] == rows[0] && expanded[1] == rows[1] &&
            frames.World.Read<EnemyRow>(expanded[2]).Source == third, "A new enemy adds its keyed row and retains existing rows.");
        Require(frames.World.Read<DisplayText>(root).Value == "Survivors", "A membership change refreshes the parent heading.");

        frames.World.DestroyEntity(first);
        frames.World.ReplaceSparse(second, new Health(70));
        frames.Frame();
        Require(Hierarchy<ElementTree>.GetChildren(frames.World, root).Span.SequenceEqual([expanded[2], rows[1]]),
            "Removing an enemy applies query order while preserving the remaining row entities.");
        Require(frames.World.Read<Selection>(rows[1]).Value == 7, "A reordered row retains its local selection.");
        Require(frames.World.Read<DisplayText>(rows[1]).Value == "70", "The surviving row consumes its changed health.");
        Require(!frames.World.IsAlive(rows[0]), "The removed enemy's row is released.");
    }

    private static Entity CreateEnemy(World world, int health)
    {
        Entity entity = world.CreateEntity();
        world.AddSparse(entity, new Health(health));
        world.AddTag<Enemy>(entity);
        return entity;
    }

    private readonly record struct PresenceDisplay(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            WriteDisplay($"{Scope.Has<Health>(Source)}:{Scope.Has<Enemy>(Source)}");
            return default;
        }
    }

    private readonly record struct EnemyList(QueryHandle Query, Entity Heading) : IView, IComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Scope.World.Read<DisplayText>(Heading).Value);
            foreach (Entity entity in Scope.Each(Query))
            {
                using (Scope.Key(((ulong)(uint)entity.Generation << 32) | (uint)entity.Index))
                    Scope.View(new EnemyRow(entity));
            }
            return default;
        }
    }

    private readonly record struct EnemyRow(Entity Source) : IView, IComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Scope.Read<Health>(Source).Value.ToString());
            return default;
        }
    }

    private static void WriteDisplay(string text)
    {
        if (Scope.World.Has<DisplayText>(Scope.Entity))
            Scope.World.Replace(Scope.Entity, new DisplayText(text));
        else
            Scope.World.Add(Scope.Entity, new DisplayText(text));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private readonly struct Enemy : ITag;
    private readonly record struct Health(int Value) : ISparseComponent;
    private readonly record struct Selection(int Value) : IComponent;
    private readonly record struct DisplayText(string Value) : IComponent;
}
