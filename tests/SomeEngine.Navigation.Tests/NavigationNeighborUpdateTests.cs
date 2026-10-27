using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation.Tests;

public sealed class NavigationNeighborUpdateTests
{
    [Fact]
    public void DisposingNeighborsCompletesCollectionAndReleasesItsQuery()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-.2f), new(.2f, -.2f), new(.2f), new(-.2f, .2f)], 1);
        Entity actor = world.CreateEntity(new LayeredMotion { Position = new(1, 0, 2) });
        world.Add(actor, new NavigationAgent(profile, new(), 1));
        world.Add(actor, new AvoidanceAgent(.3f));
        QueryHandle query = world.Query(world.QueryDefinition().All<NavigationAgent>().All<AvoidanceAgent>().All<LayeredMotion>());
        using var neighbors = new NavigationNeighborSystem<LayeredMotion, LayeredMotionReader>(world, scheduler);
        world.ReleaseQuery(query);

        neighbors.Schedule();
        neighbors.Dispose();

        Assert.Equal(actor, Assert.Single(neighbors.Data.Agents.ToArray()).Entity);
        Assert.Throws<InvalidOperationException>(() => world.GetQueryDefinition(query));
    }

    [Fact]
    public void SharedNeighborsFollowMotionAndEntityReplacementAcrossTheCrowd()
    {
        using var scheduler = new Scheduler(new JobRuntimeConfig { WorkerCount = 3 });
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        var profile = new NavigationProfile([new Vector2(-.2f), new(.2f, -.2f), new(.2f), new(-.2f, .2f)], 1);
        var positions = new Dictionary<Entity, Vector3>();
        // This lattice is acceptance input spanning several query chunks and spatial cells.
        for (int z = 0; z < 16; z++)
        for (int x = 0; x < 16; x++) Add(new(x, 0, z));
        using var neighbors = new NavigationNeighborSystem<LayeredMotion, LayeredMotionReader>(world, scheduler);
        neighbors.Schedule().Complete();
        CheckNeighbors();

        Entity removed = positions.Keys.First();
        world.DestroyEntity(removed);
        positions.Remove(removed);
        Add(new(-.5f, 0, 0));
        JobHandle moved = new AdvanceNeighborSamples().ScheduleParallel(world);
        neighbors.Schedule(moved).Complete();
        foreach (Entity entity in positions.Keys.ToArray()) positions[entity] += new Vector3(.5f, 0, 0);
        CheckNeighbors();

        void Add(Vector3 position)
        {
            Entity entity = world.CreateEntity(new LayeredMotion { Position = position, Velocity = new(.5f, 0, 0) });
            world.Add(entity, new NavigationAgent(profile, new(), 1));
            world.Add(entity, new AvoidanceAgent(.3f, NeighborDistance: 1.1f));
            positions.Add(entity, position);
        }

        void CheckNeighbors()
        {
            NavigationNeighborAgent[] agents = neighbors.Data.Agents.ToArray();
            Assert.Equal(positions.Count, agents.Length);
            foreach (NavigationNeighborAgent agent in agents)
            {
                Assert.Equal(positions[agent.Entity], agent.Motion.Position);
                Entity[] expected = positions.Where(pair => pair.Key != agent.Entity &&
                        Vector3.DistanceSquared(pair.Value, agent.Motion.Position) <= 1.1f * 1.1f)
                    .Select(pair => pair.Key).OrderBy(entity => entity.Index).ToArray();
                int[] indices = neighbors.Data.GetNeighbors(agent.Entity).ToArray();
                Entity[] actual = indices.Select(index => agents[index].Entity).OrderBy(entity => entity.Index).ToArray();
                Assert.Equal(expected, actual);
                float[] distances = indices.Select(index => Vector3.DistanceSquared(agents[index].Motion.Position,
                    agent.Motion.Position)).ToArray();
                Assert.Equal(distances.Order().ToArray(), distances);
            }
        }
    }
}

public partial struct AdvanceNeighborSamples : IJobEntity
{
    public void Execute(ref LayeredMotion motion) => motion.Position += motion.Velocity;
}
