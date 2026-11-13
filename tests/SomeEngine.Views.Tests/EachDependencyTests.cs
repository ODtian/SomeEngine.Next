using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.Views.Tests;

public sealed partial class EachDependencyTests
{
    [Fact]
    public void PublicStructureDependenciesPassTheNativeSmokeScenario() => StructureDependencyChecks.Verify();

    [Fact]
    public void EnemyMembershipUpdatesTheListWhileHealthUpdatesOnlyItsRow()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.Create(new DisplayText("Enemies"));
        Query query = frames.World.Query(new QueryDescription().With<Enemy>().With<Health>());
        Entity first = frames.World.Create(new Health(100));
        frames.World.Set(first, default(Enemy));
        Entity second = frames.World.Create(new Health(80));
        frames.World.Set(second, default(Enemy));
        Entity root = Views.Spawn(frames.World, Entity.Null, new EnemyList(query, heading));

        frames.Frame();
        Entity[] rows = ElementChildren.Read(frames.World, root);
        Assert.Equal([first, second], rows.Select(row => frames.World.ReadComponentData<EnemyRow>(row).Source));
        Assert.Equal(["100", "80"], rows.Select(row => frames.World.ReadComponentData<DisplayText>(row).Value));
        frames.World.Add(rows[1], new Selection(7));
        ulong secondDisplayVersion = frames.World.ComponentDataWriteVersion<DisplayText>(rows[1]);

        frames.World.Set(heading, new DisplayText("Reinforcements"));
        frames.World.Set(first, new Health(60));
        frames.Frame();
        Assert.Equal("Enemies", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal(["60", "80"], rows.Select(row => frames.World.ReadComponentData<DisplayText>(row).Value));
        Assert.Equal(secondDisplayVersion, frames.World.ComponentDataWriteVersion<DisplayText>(rows[1]));

        Entity third = frames.World.Create(new Health(40));
        frames.World.Set(third, default(Enemy));
        frames.Frame();
        Entity[] expanded = ElementChildren.Read(frames.World, root);
        Assert.Equal("Reinforcements", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal([first, second, third], expanded.Select(row => frames.World.ReadComponentData<EnemyRow>(row).Source));
        Assert.Equal(rows, expanded.Take(2));
        Assert.Equal("40", frames.World.ReadComponentData<DisplayText>(expanded[2]).Value);

        frames.World.Set(heading, new DisplayText("Survivors"));
        frames.World.Destroy(first);
        frames.World.Set(second, new Health(70));
        frames.Frame();
        Entity[] remaining = ElementChildren.Read(frames.World, root);
        Assert.Equal("Survivors", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal([third, second], remaining.Select(row => frames.World.ReadComponentData<EnemyRow>(row).Source));
        Assert.Equal([expanded[2], rows[1]], remaining);
        Assert.Equal(7, frames.World.ReadComponentData<Selection>(rows[1]).Value);
        Assert.Equal("70", frames.World.ReadComponentData<DisplayText>(rows[1]).Value);
        Assert.False(frames.World.IsAlive(rows[0]) && frames.World.Has<ViewType>(rows[0]));

        frames.World.Set(heading, new DisplayText("Empty"));
        frames.World.Remove<Enemy>(second);
        frames.World.Destroy(third);
        frames.Frame();
        Assert.Equal("Empty", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Empty(ElementChildren.Read(frames.World, root));
    }

    [Fact]
    public void QueryMembershipRefreshesCapturedOrderAndPreservesEntityKeys()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.Create(new DisplayText("Enemies"));
        Entity disabled = frames.World.Create(new Health(100));
        Entity first = frames.World.Create(new Health(80));
        Entity second = frames.World.Create(new Health(60));
        frames.World.Disable<Health>(disabled);
        Query query = frames.World.Query(new QueryDescription().With<Health>());
        Entity[] captured = ReadMembers(query);
        Entity root = Views.Spawn(frames.World, Entity.Null, new EnemyList(query, heading));
        frames.Frame();
        Entity[] rows = ElementChildren.Read(frames.World, root);
        Assert.Equal(captured, rows.Select(row => frames.World.ReadComponentData<EnemyRow>(row).Source));
        var keyedRows = rows.ToDictionary(row => frames.World.ReadComponentData<EnemyRow>(row).Source);
        frames.World.Add(keyedRows[first], new Selection(7));

        frames.World.Set(heading, new DisplayText("Reordered"));
        frames.World.Destroy(disabled);
        frames.Frame();
        Assert.Equal("Enemies", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal(rows, ElementChildren.Read(frames.World, root));
        Assert.Equal(7, frames.World.ReadComponentData<Selection>(keyedRows[first]).Value);

        frames.World.Set(heading, new DisplayText("Disabled"));
        frames.World.Disable<Health>(second);
        frames.Frame();
        Assert.Equal("Disabled", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal(keyedRows[first], Assert.Single(ElementChildren.Read(frames.World, root)));
        Assert.False(frames.World.IsAlive(keyedRows[second]) && frames.World.Has<ViewType>(keyedRows[second]));

        frames.World.Set(heading, new DisplayText("Enabled"));
        frames.World.Enable<Health>(second);
        captured = ReadMembers(query);
        frames.Frame();
        Entity[] enabledRows = ElementChildren.Read(frames.World, root);
        Assert.Equal("Enabled", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal(captured, enabledRows.Select(row => frames.World.ReadComponentData<EnemyRow>(row).Source));
        Assert.Contains(keyedRows[first], enabledRows);
        Assert.Equal(7, frames.World.ReadComponentData<Selection>(keyedRows[first]).Value);
    }

    private static Entity[] ReadMembers(Query query)
    {
        var entities = new List<Entity>();
        using QueryScope scope = query.OpenRead();
        foreach (QueryChunk chunk in scope)
            foreach (Entity entity in chunk.Entities) entities.Add(entity);
        return entities.ToArray();
    }

    [Fact]
    public void SwitchingQueriesTracksTheSelectedListMembership()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.Create(new DisplayText("Enemies"));
        Entity enemy = frames.World.Create(new Health(100));
        frames.World.Set(enemy, default(Enemy));
        Entity ally = frames.World.Create(new Health(80));
        Query enemies = frames.World.Query(new QueryDescription().With<Enemy>().With<Health>());
        Query allies = frames.World.Query(new QueryDescription().Without<Enemy>().With<Health>());
        Entity root = Views.Spawn(frames.World, Entity.Null, new EnemyList(enemies, heading));
        frames.Frame();

        frames.World.Set(heading, new DisplayText("Allies"));
        frames.World.Set(root, new EnemyList(allies, heading));
        frames.Frame();
        Entity row = Assert.Single(ElementChildren.Read(frames.World, root));
        Assert.Equal(ally, frames.World.ReadComponentData<EnemyRow>(row).Source);

        frames.World.Set(heading, new DisplayText("Updated"));
        Entity otherEnemy = frames.World.Create(new Health(60), default(Enemy));
        frames.Frame();
        Assert.Equal("Allies", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal(row, Assert.Single(ElementChildren.Read(frames.World, root)));

        Entity otherAlly = frames.World.Create(new Health(40));
        frames.Frame();
        Entity[] rows = ElementChildren.Read(frames.World, root);
        Assert.Equal("Updated", frames.World.ReadComponentData<DisplayText>(root).Value);
        Assert.Equal([ally, otherAlly], rows.Select(item => frames.World.ReadComponentData<EnemyRow>(item).Source));
        Assert.Equal(row, rows[0]);
    }

    [Fact]
    public void ReadingAnEmptyMembershipTracksLaterMembersBeforeEnumeration()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.Create(new DisplayText("Empty"));
        Query query = frames.World.Query(new QueryDescription().With<Enemy>().With<Health>());
        Entity root = Views.Spawn(frames.World, Entity.Null, new MembershipDisplay(query, heading));
        frames.Frame();
        Assert.Equal("Empty", frames.World.ReadComponentData<DisplayText>(root).Value);

        frames.World.Set(heading, new DisplayText("Enemy"));
        Entity enemy = frames.World.Create(new Health(100));
        frames.World.Set(enemy, default(Enemy));
        frames.Frame();
        Assert.Equal("Enemy", frames.World.ReadComponentData<DisplayText>(root).Value);

        frames.World.Set(heading, new DisplayText("Updated"));
        frames.World.Set(enemy, new Health(80));
        frames.Frame();
        Assert.Equal("Enemy", frames.World.ReadComponentData<DisplayText>(root).Value);
    }

    [Fact]
    public void OnceMembershipReadsKeepTheFirstDisplay()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.Create(new DisplayText("Empty"));
        Query query = frames.World.Query(new QueryDescription().With<Enemy>().With<Health>());
        Entity root = Views.Spawn(frames.World, Entity.Null, new OnceMembershipDisplay(query, heading));
        frames.Frame();

        frames.World.Set(heading, new DisplayText("Enemy"));
        Entity enemy = frames.World.Create(new Health(100));
        frames.World.Set(enemy, default(Enemy));
        frames.Frame();
        Assert.Equal("Empty", frames.World.ReadComponentData<DisplayText>(root).Value);
    }

#if DEBUG
    [Fact]
    public void EachOutsideBuildReportsTheRequiredScope()
    {
        Assert.Throws<InvalidOperationException>(() => { Scope.Each(default); });
    }

    [Fact]
    public void InvalidQueryReportsTheRequiredHandle()
    {
        using var frames = new HeadlessFrameDriver();
        Entity heading = frames.World.Create(new DisplayText("Enemies"));
        Query query = default;
        Entity root = Views.Spawn(frames.World, Entity.Null, new MembershipDisplay(query, heading));

        frames.Frame();
        var error = Assert.IsType<InvalidOperationException>(frames.World.ReadComponentData<BuildError>(root).Exception);
    }
#endif

    private readonly record struct OnceMembershipDisplay(Query Query, Entity Heading) : IView, IComponent
    {
        public static BuildMode Mode => BuildMode.Once;
        public BuildTask Build() => new MembershipDisplay(Query, Heading).Build();
    }

    private readonly record struct MembershipDisplay(Query Query, Entity Heading) : IView, IComponent
    {
        public BuildTask Build()
        {
            Scope.Each(Query);
            WriteDisplay(Scope.World.ReadComponentData<DisplayText>(Heading).Value);
            return default;
        }
    }

    private readonly record struct EnemyList(Query Query, Entity Heading) : IView, IComponent
    {
        public BuildTask Build()
        {
            WriteDisplay(Scope.World.ReadComponentData<DisplayText>(Heading).Value);
            foreach (Entity entity in Scope.Each(Query))
            {
                using (Scope.Key(((ulong)(uint)entity.Generation << 32) | (uint)entity.Id))
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
            Scope.World.Set(Scope.Entity, new DisplayText(text));
        else
            Scope.World.Add(Scope.Entity, new DisplayText(text));
    }

    private readonly struct Enemy : ITag;
    private readonly record struct Health(int Value) : ITrackedComponent, IEnableableComponent;
    private readonly record struct Selection(int Value) : IComponent;
    private readonly record struct DisplayText(string Value) : ITrackedComponent;
}
