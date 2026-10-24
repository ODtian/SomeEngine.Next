using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class UnaryRelationTests
{
    [Fact]
    public void ProductCountsOriginalQueryMembers()
    {
        using var world = new World();
        world.CreateEntity(new Score { Value = 3 });
        world.CreateEntity(new Score { Value = 8 });
        world.CreateEntity();
        var members = world.Query(world.QueryDefinition().All<Score>());

        Assert.Equal(2, world.Product(members).Count());
    }

    [Fact]
    public void WhereFiltersByParameterizedComponentSemantics()
    {
        using var world = new World();
        var low = world.CreateEntity(new Score { Value = 3 });
        world.AddTag<Selected>(low);
        var high = world.CreateEntity(new Score { Value = 8 });
        world.AddTag<Selected>(high);
        world.AddTag<Selected>(world.CreateEntity());
        var members = world.Query(world.QueryDefinition().All<Selected>());

        Assert.Equal(1, world.Product(members).Where(new MinimumScore(5)).Count());
    }

    [Fact]
    public void ExceptKeepsMembersWithoutTheRequiredComponent()
    {
        using var world = new World();
        var low = world.CreateEntity(new Score { Value = 3 });
        world.AddTag<Selected>(low);
        var high = world.CreateEntity(new Score { Value = 8 });
        world.AddTag<Selected>(high);
        var missing = world.CreateEntity();
        world.AddTag<Selected>(missing);
        var members = world.Query(world.QueryDefinition().All<Selected>());
        var entities = new List<Entity>();
        var output = new EntityList(entities);

        world.Product(members).Except(new MinimumScore(5)).Enumerate(ref output);

        Assert.True(entities.ToHashSet().SetEquals([low, missing]));
    }

    [Fact]
    public void AnyReportsWhenEveryMemberIsExcluded()
    {
        using var world = new World();
        world.CreateEntity(new Score { Value = 8 });
        var members = world.Query(world.QueryDefinition().All<Score>());

        Assert.False(world.Product(members).Except(new MinimumScore(5)).Any());
    }

    [Fact]
    public void CountUsesTheSuppliedComponentChangeBaseline()
    {
        using var world = new World();
        world.CreateEntity(new Score { Value = 3 });
        uint baseline = world.AcquireSystemTick();
        world.CreateEntity(new Score { Value = 8 });
        var members = world.Query(world.QueryDefinition().Changed<Score>());

        Assert.Equal(1, world.Product(members).Where(new MinimumScore(5)).Count(baseline));
    }

    [Fact]
    public void ChainedRulesForwardRefStructOutputAndStopState()
    {
        using var world = new World();
        world.CreateEntity(new Score { Value = 3 });
        var first = world.CreateEntity(new Score { Value = 8 });
        var second = world.CreateEntity(new Score { Value = 9 });
        world.CreateEntity(new Score { Value = 12 });
        var members = world.Query(world.QueryDefinition().Read<Score>());
        Span<Entity> result = stackalloc Entity[1];
        var output = new FirstEntity(result);

        bool completed = world.Product(members)
            .Where(new MinimumScore(5))
            .Except(new MinimumScore(10))
            .Enumerate(ref output);

        Assert.False(completed);
        Assert.Equal(1, output.Count);
        Assert.Contains(result[0], new[] { first, second });
        world.CreateEntity(new Score { Value = 20 });
        Assert.Equal(5, world.Product(members).Count());
        world.ReleaseQuery(members);
    }

    [Fact]
    public void SemanticRulesReadManagedComponentValues()
    {
        using var world = new World();
        world.CreateEntity(new DisplayName { Value = "Iris" });
        world.CreateEntity(new DisplayName { Value = "Rose" });
        var members = world.Query(world.QueryDefinition().All<DisplayName>());

        Assert.True(world.Product(members).Where(new NamePrefix("Ir")).Any());
    }

    [Fact]
    public void ReadOnlyRelationsKeepTheComponentChangeBaseline()
    {
        using var world = new World();
        world.CreateEntity(new Score { Value = 8 });
        uint baseline = world.AcquireSystemTick();
        uint version = world.CurrentTick;
        var members = world.Query(world.QueryDefinition().Read<Score>());
        var changes = world.Query(world.QueryDefinition().Changed<Score>());

        Assert.Equal(1, world.Product(members).Where(new MinimumScore(5)).Count());

        Assert.Equal(version, world.CurrentTick);
        Assert.Equal(0, world.Product(changes).Count(baseline));
        world.ReleaseQuery(members);
        world.ReleaseQuery(changes);
    }

    [Fact]
    public void ChainedComponentRulesShareTheDeclaredWorldJobsScope()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var light = world.CreateEntity(new Score { Value = 8 });
        world.Add(light, new Weight { Value = 3 });
        var heavy = world.CreateEntity(new Score { Value = 9 });
        world.Add(heavy, new Weight { Value = 12 });
        var members = world.Query(world.QueryDefinition().All<Score>());

        Assert.Equal(1, world.Product(members)
            .Where(new MinimumScore(5))
            .Except(new MinimumWeight(10))
            .Count());
    }

    private ref struct FirstEntity : IRelationOutput<Entity>
    {
        private readonly Span<Entity> _result;

        public FirstEntity(Span<Entity> result)
        {
            _result = result;
            Count = 0;
        }

        public int Count { get; private set; }

        public bool Add(in Entity entity)
        {
            _result[Count++] = entity;
            return false;
        }
    }

    private struct EntityList(List<Entity> entities) : IRelationOutput<Entity>
    {
        public bool Add(in Entity entity)
        {
            entities.Add(entity);
            return true;
        }
    }
}

public struct Score : IComponent
{
    public int Value;
}

public struct Selected : ITag;

public readonly struct MinimumScore(int minimum) : IRelation<Score>
{
    public bool Matches(in Score score) => score.Value >= minimum;
}

public struct DisplayName : IComponent
{
    public string Value;
}

public readonly struct NamePrefix(string value) : IRelation<DisplayName>
{
    public bool Matches(in DisplayName name) => name.Value.StartsWith(value, StringComparison.Ordinal);
}

public struct Weight : IComponent
{
    public int Value;
}

public readonly struct MinimumWeight(int minimum) : IRelation<Weight>
{
    public bool Matches(in Weight weight) => weight.Value >= minimum;
}
