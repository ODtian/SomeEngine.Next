using System.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Checks;

internal static unsafe partial class FullPathMeasurements
{
    private static void MeasureGraphs(int count)
    {
        MeasureGraph("fixed-sequence", count, CreateSequence, KeepInputs);
        MeasureGraph("settled-sequence", count, CreateSettled, KeepInputs);
        MeasureGraph("state-machine-with-inputs", count, CreateStateMachine, SetStateInput);
        MeasureGraph("blend-with-inputs", count, CreateBlend, SetBlendInput);
    }

    private static CompiledGraph CreateSequence(World world, Channel<float> channel)
    {
        using var builder = new AnimationBuilder(world);
        using (builder.Sequence())
        {
            builder.To(Entity.Null, channel, TargetValue, Spec.Tween<float, FloatOps, Linear>(10_000));
            builder.Delay(0.25f);
            builder.To(Entity.Null, channel, 0f, Spec.Spring<float, FloatOps>());
        }
        return builder.Compile();
    }

    private static CompiledGraph CreateSettled(World world, Channel<float> channel)
    {
        using var builder = new AnimationBuilder(world);
        using (builder.Sequence())
        {
            builder.To(Entity.Null, channel, TargetValue, Spec.Spring<float, FloatOps>());
            builder.To(Entity.Null, channel, 0f, Spec.Tween<float, FloatOps, Linear>(1));
        }
        return builder.Compile();
    }

    private static CompiledGraph CreateStateMachine(World world, Channel<float> channel)
    {
        using var builder = new AnimationBuilder(world);
        var parameters = new GraphParameters();
        using (var machine = builder.StateMachine(ref parameters))
        {
            using (machine.State(0, initial: true))
                builder.To(Entity.Null, channel, 0f, Spec.Tween<float, FloatOps, Linear>(10_000));
            using (machine.State(1))
                builder.To(Entity.Null, channel, TargetValue, Spec.Tween<float, FloatOps, Linear>(10_000));
            machine.Transition(0, 1, GraphCondition.Equal(0, 1), crossfade: 0.1f);
            machine.Transition(1, 0, GraphCondition.Equal(0, 0), crossfade: 0.1f);
        }
        return builder.Compile();
    }

    private static CompiledGraph CreateBlend(World world, Channel<float> channel)
    {
        using var builder = new AnimationBuilder(world);
        var parameters = new GraphParameters();
        using (var blend = builder.Blend1D(0, ref parameters))
        {
            using (blend.Point(0))
                builder.To(Entity.Null, channel, 0f, Spec.Tween<float, FloatOps, Linear>(10_000));
            using (blend.Point(1))
                builder.To(Entity.Null, channel, TargetValue, Spec.Tween<float, FloatOps, Linear>(10_000));
        }
        return builder.Compile();
    }

    private static void MeasureGraph(string name, int count, Func<World, Channel<float>, CompiledGraph> create,
        Action<World, Entity[], int> writeInputs)
    {
        using var world = new World(count);
        var channel = Channel<float>.Of<Plain, PlainAccess>(1, false, 0);
        CompiledGraph graph = default;
        RecordFirst("graph-compile-" + name, 1, () => graph = create(world, channel));
        try
        {
            var instances = new Entity[count];
            var targets = new Entity[count];
            for (int item = 0; item < count; item++) targets[item] = world.CreateEntity(new Plain());
            RecordFirst("graph-shared-play-" + name, count, () =>
            {
                for (int item = 0; item < count; item++) instances[item] = graph.Play(world, targets[item]);
            });
            using var advance = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
            advance.Add(new TimelineSystem());
            advance.Add(new GraphSystem());
            using var evaluate = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
            evaluate.Add(new TypedTrackSystem<float, FloatOps, TweenParameters, TweenState, Tween<float, FloatOps, Linear>>());
            evaluate.Add(new TypedTrackSystem<float, FloatOps, SpringParameters, SpringState<float>, Spring<float, FloatOps>>());
            evaluate.Add(new ComposeSystem<float, FloatOps>());
            evaluate.Add(new GraphEndSystem());
            evaluate.Add(new TimelineEndSystem());
            advance.Initialize();
            evaluate.Initialize();
            var context = new ImmediateSystemContext(world, 0, 0, FrameDelta);
            int frame = 0;
            Action advanceFrame = () =>
            {
                writeInputs(world, instances, frame++);
                advance.Update(ref context);
            };
            Action evaluateFrame = () => evaluate.Update(ref context);
            RecordGraphPhases(name, count, advanceFrame, evaluateFrame);
            Require(world.IsEnabled<Timeline>(instances[^1]), "Graph measurements require active shared definitions.");
        }
        finally { graph.Dispose(); }
    }

    private static void RecordGraphPhases(string name, int count, Action advance, Action evaluate)
    {
        long allocatedBeforeWarmup = GC.GetAllocatedBytesForCurrentThread();
        for (int warmup = 0; warmup < Warmups; warmup++) { advance(); evaluate(); }
        long warmupAllocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeWarmup;
        var advanceTimes = new double[Samples];
        var evaluateTimes = new double[Samples];
        var totalTimes = new double[Samples];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int sample = 0; sample < Samples; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            advance();
            long middle = Stopwatch.GetTimestamp();
            evaluate();
            long end = Stopwatch.GetTimestamp();
            advanceTimes[sample] = Stopwatch.GetElapsedTime(start, middle).TotalNanoseconds / count;
            evaluateTimes[sample] = Stopwatch.GetElapsedTime(middle, end).TotalNanoseconds / count;
            totalTimes[sample] = Stopwatch.GetElapsedTime(start, end).TotalNanoseconds / count;
        }
        long allocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        PrintGraphPhase(name + "-advance", count, advanceTimes, allocation, warmupAllocation);
        PrintGraphPhase(name + "-evaluate", count, evaluateTimes, allocation, warmupAllocation);
        PrintGraphPhase(name + "-full", count, totalTimes, allocation, warmupAllocation);
    }

    private static void PrintGraphPhase(string name, int count, double[] times, long allocation, long warmupAllocation)
    {
        Array.Sort(times);
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(FormattableString.Invariant($"graph,{name},{count},{times[Samples / 2]:F3},{times[(int)(Samples * 0.95)]:F3},{times[^1]:F3},{allocation / (double)Samples:F3},0,0,0,{process.PeakWorkingSet64},{warmupAllocation}"));
    }

    private static void KeepInputs(World world, Entity[] instances, int frame) { }

    private static void SetStateInput(World world, Entity[] instances, int frame)
    {
        var parameters = new GraphParameters();
        parameters.Values[0] = frame / 16 % 2;
        foreach (Entity instance in instances) world.Replace(instance, in parameters);
    }

    private static void SetBlendInput(World world, Entity[] instances, int frame)
    {
        var parameters = new GraphParameters();
        parameters.Values[0] = (frame % 4 + 1) * 0.2f;
        foreach (Entity instance in instances) world.Replace(instance, in parameters);
    }
}
