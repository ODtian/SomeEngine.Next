using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Animation.Consumers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Checks;

internal static unsafe partial class FullPathMeasurements
{
    private const int Warmups = 12;
    private const int Samples = 48;
    private const float FrameDelta = 1f / 120;
    private const float TargetValue = 1_000_000f;

    public static void Run(int[] counts)
    {
        Console.WriteLine($"framework={RuntimeInformation.FrameworkDescription}; architecture={RuntimeInformation.ProcessArchitecture}; nativeAot={!RuntimeFeature.IsDynamicCodeSupported}; executableBytes={new FileInfo(Environment.ProcessPath!).Length}; warmup={Warmups}; samples={Samples}; frameDelta={FrameDelta}; target={TargetValue}");
        Console.WriteLine("category,name,count,median_ns_per_item,p95_ns_per_item,p99_ns_per_item,allocated_bytes_per_sample,first_ns,first_allocated_bytes,retained_heap_bytes,process_peak_working_set_bytes");
        MeasureSystemEntry();
        foreach (int count in counts)
        {
            MeasureEcs<Plain, PlainAccess>("plain", count, static () => default);
            MeasureEcs<Nested, NestedAccess>("nested", count, static () => default);
            MeasureEcs<Referenced, ReferenceAccess>("reference", count, static () => new() { Value = new ReferenceValue() });
            MeasureFields<Plain, PlainAccess>("plain", count, static () => default, Channel<float>.Of<Plain, PlainAccess>(1, false, 0));
            MeasureFields<Nested, NestedAccess>("nested", count, static () => default, Channel<float>.Of<Nested, NestedAccess>(1, false, 0));
            MeasureFields<Referenced, ReferenceAccess>("reference", count, static () => new() { Value = new ReferenceValue() }, Channel<float>.Of<Referenced, ReferenceAccess>(1));
            MeasureControls(count);
            MeasureLifecycle(count);
            MeasureSources(count);
        }
    }

    private static void MeasureSystemEntry()
    {
        using var world = new World();
        var counter = new Counter();
        var direct = new CountSystem(counter);
        var context = new ImmediateSystemContext(world, 0, 0, FrameDelta);
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(direct);
        systems.Initialize();
        const int calls = 1024;
        Record("ecs", "direct-system", calls, () =>
        {
            for (int call = 0; call < calls; call++) direct.OnUpdate(ref context);
        });
        Record("ecs", "system-group", calls, () =>
        {
            for (int call = 0; call < calls; call++) systems.Update(ref context);
        });
        GC.KeepAlive(counter);
    }

    private static void MeasureEcs<TComponent, TAccess>(string name, int count, Func<TComponent> create)
        where TComponent : struct, IComponent
        where TAccess : struct, IFieldAccess<TComponent, float>
    {
        using var world = new World(count);
        var entities = new Entity[count];
        for (int item = 0; item < count; item++) entities[item] = world.CreateEntity(create());
        QueryHandle read = world.Query(world.QueryDefinition().Read<TComponent>());
        QueryHandle write = world.Query(world.QueryDefinition().Write<TComponent>());
        float value = 0.25f;
        float sum = 0;
        Record("ecs", name + "-root-read", count, () => world.ExecuteQuery(read, ref sum, &ReadRoots<TComponent, TAccess>));
        Record("ecs", name + "-field-column", count, () => world.ExecuteQuery(write, ref value, &WriteFields<TComponent, TAccess>));
        Record("ecs", name + "-world-field", count, () =>
        {
            foreach (Entity entity in entities) world.WriteField<TComponent, float, TAccess>(entity, in value);
        });
        if (name == "plain")
        {
            const int calls = 1024;
            Record("ecs", "query-delegate", calls, () =>
            {
                for (int call = 0; call < calls; call++) world.ExecuteQuery(read, EmptyQuery);
            });
            Record("ecs", "query-function", calls, () =>
            {
                for (int call = 0; call < calls; call++) world.ExecuteQuery(read, &EmptyQuery);
            });
        }
        GC.KeepAlive(sum);
        world.ReleaseQuery(read);
        world.ReleaseQuery(write);
    }

    private static void MeasureFields<TComponent, TAccess>(string path, int count, Func<TComponent> create, Channel<float> channel)
        where TComponent : struct, IComponent
        where TAccess : struct, IFieldAccess<TComponent, float>
    {
        MeasureFull<TComponent, TAccess, TweenParameters, TweenState, Tween<float, FloatOps, Linear>>(
            "tween-" + path, count, create, channel, Spec.Tween<float, FloatOps, Linear>(10_000));
        MeasureFull<TComponent, TAccess, SpringParameters, SpringState<float>, Spring<float, FloatOps>>(
            "spring-" + path, count, create, channel, Spec.Spring<float, FloatOps>());
        MeasureFull<TComponent, TAccess, HistoryParameters, HistoryState, HistoryMotion>(
            "history-" + path, count, create, channel,
            Spec.Create<float, FloatOps, HistoryParameters, HistoryState, HistoryMotion>(new(10_000, 0.75f, "shared-path")));
    }

    private static void MeasureFull<TComponent, TAccess, TParameters, TState, TMotion>(string name, int count,
        Func<TComponent> create, Channel<float> channel, Spec<float, FloatOps, TParameters, TState, TMotion> spec)
        where TComponent : struct, IComponent
        where TAccess : struct, IFieldAccess<TComponent, float>
        where TParameters : struct
        where TState : struct
        where TMotion : struct, IMotion<float, FloatOps, TParameters, TState>
    {
        long heapBefore = GC.GetTotalMemory(true);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        using var world = new World(count);
        var targets = new Entity[count];
        for (int item = 0; item < count; item++)
        {
            Entity target = targets[item] = world.CreateEntity(create());
            PropertyAnimation.Declare(world, target, channel, spec);
            PropertyAnimation.To(world, target, channel, TargetValue, spec);
        }
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TypedTrackSystem<float, FloatOps, TParameters, TState, TMotion>());
        systems.Add(new ComposeSystem<float, FloatOps>());
        systems.Add(new TimelineEndSystem());
        systems.Initialize();
        double firstTime = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        long firstAllocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long retained = GC.GetTotalMemory(true) - heapBefore;
        var context = new ImmediateSystemContext(world, 0, 0, FrameDelta);
        Record("full", name, count, () => systems.Update(ref context), firstTime, firstAllocation, retained);
        Require(world.IsEnabled<Timeline>(targets[^1]), "Full field measurement requires continuously active tracks.");
        Require(world.ReadField<TComponent, float, TAccess>(targets[^1]) > 0, "Full field measurement must reach the declared field.");
        Console.WriteLine($"storage,{name},{count},track_bytes={Unsafe.SizeOf<Track<float>>()},motion_bytes={Unsafe.SizeOf<MotionInstance<float, FloatOps, TParameters, TState, TMotion>>()}");
    }

    private static void MeasureControls(int count)
    {
        using var world = new World(count);
        var channel = Channel<float>.Of<Plain, PlainAccess>(1, false, 0);
        var tween = Spec.Tween<float, FloatOps, Linear>(10_000);
        var spring = Spec.Spring<float, FloatOps>();
        var targets = new Entity[count];
        for (int item = 0; item < count; item++)
        {
            Entity target = targets[item] = world.CreateEntity(new Plain());
            PropertyAnimation.Declare(world, target, channel, tween);
            PropertyAnimation.Declare(world, target, channel, spring);
            PropertyAnimation.To(world, target, channel, TargetValue, tween);
        }
        float targetValue = TargetValue;
        Record("control", "retarget", count, () =>
        {
            targetValue = targetValue == TargetValue ? TargetValue * 2 : TargetValue;
            foreach (Entity target in targets) PropertyAnimation.Retarget(world, target, channel, targetValue);
        });
        bool useSpring = false;
        Record("control", "declared-switch", count, () =>
        {
            useSpring = !useSpring;
            foreach (Entity target in targets)
                if (useSpring) PropertyAnimation.To(world, target, channel, TargetValue, spring);
                else PropertyAnimation.To(world, target, channel, TargetValue, tween);
        });
        Record("control", "stop-start", count, () =>
        {
            foreach (Entity target in targets)
            {
                PropertyAnimation.Stop(world, target);
                PropertyAnimation.To(world, target, channel, TargetValue, tween);
            }
        });
    }

    private static void MeasureLifecycle(int count)
    {
        using var world = new World(count);
        var channel = Channel<float>.Of<Plain, PlainAccess>(1, false, 0);
        var spec = Spec.Tween<float, FloatOps, Linear>(0.001f);
        var targets = new Entity[count];
        for (int item = 0; item < count; item++) targets[item] = world.CreateEntity(new Plain());
        RecordFirst("first-declaration", count, () =>
        {
            foreach (Entity target in targets) PropertyAnimation.Declare(world, target, channel, spec);
        });
        RecordFirst("first-play", count, () =>
        {
            foreach (Entity target in targets) PropertyAnimation.To(world, target, channel, TargetValue, spec);
        });
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        systems.Add(new TimelineSystem());
        systems.Add(new TypedTrackSystem<float, FloatOps, TweenParameters, TweenState, Tween<float, FloatOps, Linear>>());
        systems.Add(new TimelineEndSystem());
        systems.Initialize();
        var context = new ImmediateSystemContext(world, 0, 0, FrameDelta);
        RecordFirst("first-complete-frame", count, () => systems.Update(ref context));
        float value = TargetValue;
        Record("lifecycle", "completed-reuse", count, () =>
        {
            value = value == TargetValue ? 0 : TargetValue;
            foreach (Entity target in targets) PropertyAnimation.To(world, target, channel, value, spec);
            systems.Update(ref context);
        });
        Require(world.Read<Timeline>(targets[^1]).Completions == Warmups + Samples + 1,
            "Lifecycle measurements must complete and reuse the same declared instances.");
    }

    private static void RecordFirst(string name, int count, Action sample)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        sample();
        double elapsed = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine(FormattableString.Invariant($"lifecycle,{name},{count},{elapsed / count:F3},{elapsed / count:F3},{elapsed / count:F3},{allocated},{elapsed:F0},{allocated},0,{Environment.WorkingSet}"));
    }

    private static void ReadRoots<TComponent, TAccess>(QueryCursor query, ref float sum)
        where TComponent : struct, IComponent
        where TAccess : struct, IFieldAccess<TComponent, float>
    {
        foreach (QueryChunkView chunk in query.Chunks)
        {
            ReadOnlySpan<TComponent> roots = chunk.Read<TComponent>();
            for (int row = 0; row < roots.Length; row++) sum += TAccess.Read(in roots[row]);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteFields<TComponent, TAccess>(QueryCursor query, ref float value)
        where TComponent : struct, IComponent
        where TAccess : struct, IFieldAccess<TComponent, float>
    {
        foreach (QueryChunkView chunk in query.Chunks)
        {
            FieldWriteView<TComponent, float, TAccess> fields = chunk.WriteFields<TComponent, float, TAccess>();
            for (int row = 0; row < fields.Count; row++) fields.Write(row, in value);
        }
    }

    private static void EmptyQuery(QueryCursor query) { }

    private static void Record(string category, string name, int count, Action sample,
        double firstTime = 0, long firstAllocation = 0, long retained = 0)
    {
        for (int warmup = 0; warmup < Warmups; warmup++) sample();
        var times = new double[Samples];
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < times.Length; index++)
        {
            long start = Stopwatch.GetTimestamp();
            sample();
            times[index] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Array.Sort(times);
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(FormattableString.Invariant($"{category},{name},{count},{times[Samples / 2]:F3},{times[(int)(Samples * 0.95)]:F3},{times[^1]:F3},{allocated / (double)Samples:F3},{firstTime:F0},{firstAllocation},{retained},{process.PeakWorkingSet64}"));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Counter { public int Value; }

    private struct CountSystem(Counter counter) : ISystem<ImmediateSystemContext>
    {
        public void OnUpdate(ref ImmediateSystemContext context) => counter.Value++;
    }

    private struct Plain : IComponent { public float Value; }
    private struct Number { public float Value; }
    private struct Nested : IComponent { public Number Value; }
    private sealed class ReferenceValue { public float Value { get; set; } }
    private struct Referenced : IComponent { public ReferenceValue Value; }

    private readonly struct PlainAccess : IFieldAccess<Plain, float>
    {
        public static float Read(in Plain component) => component.Value;
        public static void Write(ref Plain component, in float value) => component.Value = value;
    }

    private readonly struct NestedAccess : IFieldAccess<Nested, float>
    {
        public static float Read(in Nested component) => component.Value.Value;
        public static void Write(ref Nested component, in float value) => component.Value.Value = value;
    }

    private readonly struct ReferenceAccess : IFieldAccess<Referenced, float>
    {
        public static float Read(in Referenced component) => component.Value.Value;
        public static void Write(ref Referenced component, in float value) => component.Value.Value = value;
    }
}
