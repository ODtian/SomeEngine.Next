using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Views.Tests;

public sealed partial class EachDependencyTests
{
    [Fact]
    public void PublicStructureDependenciesPassTheNativeSmokeScenario() => StructureDependencyChecks.Verify();

    [Fact]
    public void EnemyMembershipUpdatesTheListWhileHealthUpdatesOnlyItsRow()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.CreateEntity(new DisplayText("Enemies"));
        QueryHandle query = frames.World.Query(frames.World.QueryDefinition().All<Enemy>().All<Health>());
        Entity first = frames.World.CreateEntity(new Health(100));
        frames.World.AddTag<Enemy>(first);
        Entity second = frames.World.CreateEntity(new Health(80));
        frames.World.AddTag<Enemy>(second);
        Entity root = Views.Spawn(frames.World, Entity.Null, new EnemyList(query, heading));

        frames.Frame();
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal([first, second], rows.Select(row => frames.World.Read<EnemyRow>(row).Source));
        Assert.Equal(["100", "80"], rows.Select(row => frames.World.Read<DisplayText>(row).Value));
        frames.World.Add(rows[1], new Selection(7));
        uint secondDisplayVersion = frames.World.WriteVersion<DisplayText>(rows[1]);

        frames.World.Replace(heading, new DisplayText("Reinforcements"));
        frames.World.Replace(first, new Health(60));
        frames.Frame();
        Assert.Equal("Enemies", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal(["60", "80"], rows.Select(row => frames.World.Read<DisplayText>(row).Value));
        Assert.Equal(secondDisplayVersion, frames.World.WriteVersion<DisplayText>(rows[1]));

        Entity third = frames.World.CreateEntity(new Health(40));
        frames.World.AddTag<Enemy>(third);
        frames.Frame();
        Entity[] expanded = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal("Reinforcements", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal([first, second, third], expanded.Select(row => frames.World.Read<EnemyRow>(row).Source));
        Assert.Equal(rows, expanded.Take(2));
        Assert.Equal("40", frames.World.Read<DisplayText>(expanded[2]).Value);

        frames.World.Replace(heading, new DisplayText("Survivors"));
        frames.World.DestroyEntity(first);
        frames.World.Replace(second, new Health(70));
        frames.Frame();
        Entity[] remaining = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal("Survivors", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal([third, second], remaining.Select(row => frames.World.Read<EnemyRow>(row).Source));
        Assert.Equal([expanded[2], rows[1]], remaining);
        Assert.Equal(7, frames.World.Read<Selection>(rows[1]).Value);
        Assert.Equal("70", frames.World.Read<DisplayText>(rows[1]).Value);
        Assert.False(frames.World.IsAlive(rows[0]));

        frames.World.Replace(heading, new DisplayText("Empty"));
        frames.World.RemoveTag<Enemy>(second);
        frames.World.DestroyEntity(third);
        frames.Frame();
        Assert.Equal("Empty", frames.World.Read<DisplayText>(root).Value);
        Assert.Empty(Hierarchy<ElementTree>.GetChildren(frames.World, root));
    }

    [Fact]
    public void QueryOrderAndEnableChangesUpdateRowsAndPreserveTheirEntityKeys()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.CreateEntity(new DisplayText("Enemies"));
        Entity disabled = frames.World.CreateEntity(new Health(100));
        Entity first = frames.World.CreateEntity(new Health(80));
        Entity second = frames.World.CreateEntity(new Health(60));
        frames.World.Disable<Health>(disabled);
        QueryHandle query = frames.World.Query(frames.World.QueryDefinition().Enabled<Health>());
        Entity root = Views.Spawn(frames.World, Entity.Null, new EnemyList(query, heading));

        frames.Frame();
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal([first, second], rows.Select(row => frames.World.Read<EnemyRow>(row).Source));
        frames.World.Add(rows[0], new Selection(7));

        frames.World.Replace(heading, new DisplayText("Reordered"));
        frames.World.DestroyEntity(disabled);
        frames.Frame();
        Assert.Equal("Reordered", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal([rows[1], rows[0]], Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray());
        Assert.Equal(7, frames.World.Read<Selection>(rows[0]).Value);

        frames.World.Replace(heading, new DisplayText("Disabled"));
        frames.World.Disable<Health>(second);
        frames.Frame();
        Assert.Equal("Disabled", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal(rows[0], Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));
        Assert.False(frames.World.IsAlive(rows[1]));

        frames.World.Replace(heading, new DisplayText("Enabled"));
        frames.World.Enable<Health>(second);
        frames.Frame();
        Entity[] enabledRows = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal("Enabled", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal([second, first], enabledRows.Select(row => frames.World.Read<EnemyRow>(row).Source));
        Assert.Equal(rows[0], enabledRows[1]);
        Assert.Equal(7, frames.World.Read<Selection>(rows[0]).Value);
    }

    [Fact]
    public void SwitchingQueriesTracksTheSelectedListMembership()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.CreateEntity(new DisplayText("Enemies"));
        Entity enemy = frames.World.CreateEntity(new Health(100));
        frames.World.AddTag<Enemy>(enemy);
        Entity ally = frames.World.CreateEntity(new Health(80));
        QueryHandle enemies = frames.World.Query(frames.World.QueryDefinition().All<Enemy>().All<Health>());
        QueryHandle allies = frames.World.Query(frames.World.QueryDefinition().None<Enemy>().All<Health>());
        Entity root = Views.Spawn(frames.World, Entity.Null, new EnemyList(enemies, heading));
        frames.Frame();

        frames.World.Replace(heading, new DisplayText("Allies"));
        frames.World.Replace(root, new EnemyList(allies, heading));
        frames.Frame();
        Entity row = Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root));
        Assert.Equal(ally, frames.World.Read<EnemyRow>(row).Source);

        frames.World.Replace(heading, new DisplayText("Updated"));
        Entity otherEnemy = frames.World.CreateEntity(new Health(60));
        frames.World.AddTag<Enemy>(otherEnemy);
        frames.Frame();
        Assert.Equal("Allies", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal(row, Assert.Single(Hierarchy<ElementTree>.GetChildren(frames.World, root)));

        Entity otherAlly = frames.World.CreateEntity(new Health(40));
        frames.Frame();
        Entity[] rows = Hierarchy<ElementTree>.GetChildren(frames.World, root).ToArray();
        Assert.Equal("Updated", frames.World.Read<DisplayText>(root).Value);
        Assert.Equal([ally, otherAlly], rows.Select(item => frames.World.Read<EnemyRow>(item).Source));
        Assert.Equal(row, rows[0]);
    }

    [Fact]
    public void ReadingAnEmptyMembershipTracksLaterMembersBeforeEnumeration()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.CreateEntity(new DisplayText("Empty"));
        QueryHandle query = frames.World.Query(frames.World.QueryDefinition().All<Enemy>().All<Health>());
        Entity root = Views.Spawn(frames.World, Entity.Null, new MembershipDisplay(query, heading));
        frames.Frame();
        Assert.Equal("Empty", frames.World.Read<DisplayText>(root).Value);

        frames.World.Replace(heading, new DisplayText("Enemy"));
        Entity enemy = frames.World.CreateEntity(new Health(100));
        frames.World.AddTag<Enemy>(enemy);
        frames.Frame();
        Assert.Equal("Enemy", frames.World.Read<DisplayText>(root).Value);

        frames.World.Replace(heading, new DisplayText("Updated"));
        frames.World.Replace(enemy, new Health(80));
        frames.Frame();
        Assert.Equal("Enemy", frames.World.Read<DisplayText>(root).Value);
    }

    [Fact]
    public void OnceMembershipReadsKeepTheFirstDisplay()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.CreateEntity(new DisplayText("Empty"));
        QueryHandle query = frames.World.Query(frames.World.QueryDefinition().All<Enemy>().All<Health>());
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceMembershipDisplay(query, heading));
        frames.Frame();

        frames.World.Replace(heading, new DisplayText("Enemy"));
        Entity enemy = frames.World.CreateEntity(new Health(100));
        frames.World.AddTag<Enemy>(enemy);
        frames.Frame();
        Assert.Equal("Empty", frames.World.Read<DisplayText>(root).Value);
    }

#if DEBUG
    [Fact]
    public void EachOutsideBuildReportsTheRequiredScope()
    {
        var error = Assert.Throws<InvalidOperationException>(() => { Scope.Each(default); });
        Assert.Contains("active view build", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOrReleasedQueryReportsTheRequiredHandle(bool released)
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.CreateEntity(new DisplayText("Enemies"));
        QueryHandle query = default;
        if (released)
        {
            query = frames.World.Query(frames.World.QueryDefinition().All<Enemy>());
            frames.World.ReleaseQuery(query);
        }
        Views.Spawn(frames.World, Entity.Null, new MembershipDisplay(query, heading));

        var error = Assert.Throws<InvalidOperationException>(frames.Frame);
        Assert.Contains("active query handle", error.Message, StringComparison.OrdinalIgnoreCase);
    }
#endif

    private readonly record struct OnceMembershipDisplay(QueryHandle Query, Entity Heading) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;
        public BuildTask Build() => new MembershipDisplay(Query, Heading).Build();
    }

    private readonly record struct MembershipDisplay(QueryHandle Query, Entity Heading) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Each(Query);
            WriteDisplay(Scope.World.Read<DisplayText>(Heading).Value);
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

    private readonly struct Enemy : ITag;
    private readonly record struct Health(int Value) : IComponent, IEnableableComponent;
    private readonly record struct Selection(int Value) : IComponent;
    private readonly record struct DisplayText(string Value) : IComponent;
}
