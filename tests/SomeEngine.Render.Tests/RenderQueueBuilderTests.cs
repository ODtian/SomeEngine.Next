using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class RenderQueueBuilderTests
{
    [Fact]
    public void PipelineKeysGroupStateWhileTransparentWorkPreservesDepth()
    {
        var world = new RenderWorld();
        Entity first = Create(
            world,
            new QueueInput(10, 5.0f, 0b11),
            new TestMembership(new StateKey(0, 1, 7, 9), Ordered: false, Pass: 0),
            new TestMembership(new StateKey(1, 3, 7, 9), Ordered: true, Pass: 1));
        Entity second = Create(
            world,
            new QueueInput(20, 4.0f, 0b01),
            new TestMembership(new StateKey(0, 1, 7, 9), Ordered: false, Pass: 0),
            new TestMembership(new StateKey(1, 4, 7, 9), Ordered: true, Pass: 1));
        Entity third = Create(
            world,
            new QueueInput(30, 3.0f, 0b11),
            new TestMembership(new StateKey(0, 2, 8, 9), Ordered: false, Pass: 0),
            new TestMembership(new StateKey(1, 3, 7, 9), Ordered: true, Pass: 1));

        QueryHandle query = world.Query(
            new QueryDefinitionBuilder()
                .Read<QueueInput>()
                .ReadBuffer<TestMembership>()
                .Build());
        var builder = new RenderQueueBuilder<TestMembership, StateKey, Draw>(
            stateGroupingPartitions: 3,
            rowsPerPacket: 1);
        var calls = new ClassifierCalls();
        var classifier = new TestQueueClassifier(calls);

        var firstView = new CapturedQueue();
        var all = new TestView(0b01);
        builder.Build(
            world,
            query,
            in all,
            in classifier,
            ref firstView,
            static (ref CapturedQueue capture, RenderQueueView<StateKey, Draw> queue) =>
                capture.Capture(queue));

        Assert.Equal(3, calls.CountCalls);
        Assert.Equal(3, calls.WriteCalls);
        Assert.Equal(2, firstView.StateBins.Count);
        Assert.Equal(3, firstView.StateDraws.Count);

        RenderQueueBin<StateKey> sharedState = Assert.Single(
            firstView.StateBins,
            bin => bin.Key == new StateKey(0, 1, 7, 9));
        Assert.Equal(2, sharedState.Count);
        Assert.Equal(
            [10, 20],
            firstView.StateDraws
                .Skip(sharedState.Start)
                .Take(sharedState.Count)
                .Select(static draw => draw.Material));
        Assert.Equal([first, second],
            firstView.StateDraws
                .Skip(sharedState.Start)
                .Take(sharedState.Count)
                .Select(static draw => draw.Entity));

        Assert.Equal(3, firstView.OrderedBins.Count);
        Assert.Equal(
            [new StateKey(1, 3, 7, 9), new StateKey(1, 4, 7, 9), new StateKey(1, 3, 7, 9)],
            firstView.OrderedBins.Select(static bin => bin.Key));
        Assert.Equal([5.0f, 4.0f, 3.0f],
            firstView.OrderedDraws.Select(static draw => draw.Depth));
        Assert.Equal([first, second, third],
            firstView.OrderedDraws.Select(static draw => draw.Entity));

        var secondView = new CapturedQueue();
        var masked = new TestView(0b10);
        builder.Build(
            world,
            query,
            in masked,
            in classifier,
            ref secondView,
            static (ref CapturedQueue capture, RenderQueueView<StateKey, Draw> queue) =>
                capture.Capture(queue));

        Assert.Equal(6, calls.CountCalls);
        Assert.Equal(6, calls.WriteCalls);
        Assert.Equal(2, secondView.StateDraws.Count);
        Assert.Equal([10, 30],
            secondView.StateDraws.Select(static draw => draw.Material).Order());
        Assert.Equal(2, secondView.OrderedDraws.Count);
        Assert.Equal([5.0f, 3.0f],
            secondView.OrderedDraws.Select(static draw => draw.Depth));

        Assert.Equal(2, BufferCount(world, first));
        Assert.Equal(2, BufferCount(world, second));
        Assert.Equal(2, BufferCount(world, third));
        world.ReleaseQuery(query);
    }

    private static Entity Create(
        RenderWorld world,
        QueueInput input,
        params TestMembership[] memberships)
    {
        Entity entity = world.CreateEntity(input);
        world.AddBuffer<TestMembership>(entity);
        world.ExecuteBufferWrite<TestMembership>(entity, buffer =>
        {
            for (int index = 0; index < memberships.Length; index++)
                buffer.Add(memberships[index]);
        });
        return entity;
    }

    private static int BufferCount(RenderWorld world, Entity entity)
    {
        int count = -1;
        world.ExecuteBufferRead<TestMembership>(entity, buffer => count = buffer.Count);
        return count;
    }

    private readonly record struct TestView(int VisibilityMask);

    private readonly record struct QueueInput(
        int Material,
        float Depth,
        int VisibilityMask) : SomeEngine.ECS.IComponent;

    private readonly record struct StateKey(
        int Phase,
        int Pipeline,
        int Layout,
        int Geometry);

    private readonly record struct TestMembership(
        StateKey Key,
        bool Ordered,
        int Pass) : IBufferElement;

    private readonly record struct Draw(
        Entity Entity,
        int Material,
        int Pass,
        float Depth);

    private sealed class ClassifierCalls
    {
        internal int CountCalls;
        internal int WriteCalls;
    }

    private readonly struct TestQueueClassifier(ClassifierCalls calls) :
        IRenderQueueClassifier<TestView, TestMembership, StateKey, Draw>
    {
        public RenderQueueWorkCounts Count(
            in TestView view,
            in RenderQueueEntityContext queueEntity,
            ReadOnlyQueryPacket packet,
            int row,
            BufferView<TestMembership> memberships)
        {
            Interlocked.Increment(ref calls.CountCalls);
            QueueInput input = packet.Read<QueueInput>()[row];
            if ((input.VisibilityMask & view.VisibilityMask) == 0)
                return default;

            int state = 0;
            int ordered = 0;
            for (int index = 0; index < memberships.Count; index++)
            {
                if (memberships[index].Ordered)
                    ordered++;
                else
                    state++;
            }
            return new RenderQueueWorkCounts(state, ordered);
        }

        public void Write(
            in TestView view,
            in RenderQueueEntityContext queueEntity,
            ReadOnlyQueryPacket packet,
            int row,
            BufferView<TestMembership> memberships,
            ref RenderQueueWorkWriter<StateKey, Draw> output)
        {
            Interlocked.Increment(ref calls.WriteCalls);
            QueueInput input = packet.Read<QueueInput>()[row];
            if ((input.VisibilityMask & view.VisibilityMask) == 0)
                return;

            Entity entity = packet.Entities[row];
            for (int index = 0; index < memberships.Count; index++)
            {
                TestMembership membership = memberships[index];
                var draw = new Draw(
                    entity,
                    input.Material,
                    membership.Pass,
                    input.Depth);
                if (membership.Ordered)
                    output.AddBackToFront(input.Depth, membership.Key, draw);
                else
                    output.AddStateGrouped(membership.Key, draw);
            }
        }
    }

    private sealed class CapturedQueue
    {
        internal List<RenderQueueBin<StateKey>> StateBins { get; } = [];
        internal List<Draw> StateDraws { get; } = [];
        internal List<RenderQueueBin<StateKey>> OrderedBins { get; } = [];
        internal List<Draw> OrderedDraws { get; } = [];

        internal void Capture(RenderQueueView<StateKey, Draw> queue)
        {
            for (int index = 0; index < queue.StateBins.Length; index++)
                StateBins.Add(queue.StateBins[index]);
            for (int index = 0; index < queue.StateDraws.Length; index++)
                StateDraws.Add(queue.StateDraws[index]);
            for (int index = 0; index < queue.BackToFrontBins.Length; index++)
                OrderedBins.Add(queue.BackToFrontBins[index]);
            for (int index = 0; index < queue.BackToFrontDraws.Length; index++)
                OrderedDraws.Add(queue.BackToFrontDraws[index]);
        }
    }

}
