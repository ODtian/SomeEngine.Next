using System.Diagnostics;
using System.Runtime.CompilerServices;
using SomeEngine.Animation.Consumers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation.Checks;

internal static unsafe partial class FullPathMeasurements
{
    private static void MeasureDeclaredMemory(int count)
    {
        var tween = Spec.Tween<float, FloatOps, Linear>(10_000);
        MeasureDeclaredMemory("single-tween", count, tween, false);
        MeasureDeclaredMemory("tween-spring", count, tween, true);
        var history = Spec.Create<float, FloatOps, HistoryParameters, WindowState, WindowMotion>(new(10_000, 1, "shared-window"));
        MeasureDeclaredMemory("large-history", count, history, false);
    }

    private static void MeasureDeclaredMemory<TParameters, TState, TMotion>(string name, int count,
        Spec<float, FloatOps, TParameters, TState, TMotion> spec, bool withSpring)
        where TParameters : struct
        where TState : struct
        where TMotion : struct, IMotion<float, FloatOps, TParameters, TState>
    {
        long heapBefore = GC.GetTotalMemory(true);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        using var world = new World(count);
        var targets = new Entity[count];
        var channel = Channel<float>.Of<Plain, PlainAccess>(1, false, 0);
        var spring = Spec.Spring<float, FloatOps>();
        for (int item = 0; item < count; item++)
        {
            Entity target = targets[item] = world.CreateEntity(new Plain());
            PropertyAnimation.Declare(world, target, channel, spec);
            if (withSpring) PropertyAnimation.Declare(world, target, channel, spring);
        }
        double declarationTime = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        long declarationAllocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long declaredHeap = GC.GetTotalMemory(true) - heapBefore;
        allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        using var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(world));
        AnimationSystems.Add(systems, in spec);
        if (withSpring) AnimationSystems.Add(systems, in spring);
        systems.Initialize();
        var context = new ImmediateSystemContext(world, 0, 0, FrameDelta);
        foreach (Entity target in targets) PropertyAnimation.To(world, target, channel, TargetValue, spec);
        systems.Update(ref context);
        if (withSpring)
        {
            foreach (Entity target in targets) PropertyAnimation.To(world, target, channel, TargetValue * 2, spring);
            systems.Update(ref context);
        }
        double activationTime = Stopwatch.GetElapsedTime(start).TotalNanoseconds;
        long activationAllocation = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long activeHeap = GC.GetTotalMemory(true) - heapBefore;
        int trackCapacity = 0, motionCapacity = 0, springCapacity = 0;
        world.ExecuteBufferRead<Track<float>, int>(targets[0], ref trackCapacity, static (buffer, ref capacity) => capacity = buffer.Capacity);
        world.ExecuteBufferRead<MotionInstance<float, FloatOps, TParameters, TState, TMotion>, int>(targets[0], ref motionCapacity,
            static (buffer, ref capacity) => capacity = buffer.Capacity);
        if (withSpring)
            world.ExecuteBufferRead<MotionInstance<float, FloatOps, SpringParameters, SpringState<float>, Spring<float, FloatOps>>, int>(
                targets[0], ref springCapacity, static (buffer, ref capacity) => capacity = buffer.Capacity);
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(FormattableString.Invariant($"memory,{name},{count},declared_ns={declarationTime:F0},declared_allocated_bytes={declarationAllocation},declared_retained_bytes={declaredHeap},activation_ns={activationTime:F0},activation_allocated_bytes={activationAllocation},active_retained_bytes={activeHeap},track_bytes={Unsafe.SizeOf<Track<float>>()},track_capacity={trackCapacity},motion_bytes={Unsafe.SizeOf<MotionInstance<float, FloatOps, TParameters, TState, TMotion>>()},motion_capacity={motionCapacity},spring_bytes={(withSpring ? Unsafe.SizeOf<MotionInstance<float, FloatOps, SpringParameters, SpringState<float>, Spring<float, FloatOps>>>() : 0)},spring_capacity={springCapacity},state_bytes={Unsafe.SizeOf<TState>()},peak_working_set_bytes={process.PeakWorkingSet64}"));
        Require(channel.Read(world, targets[^1]) > 0, "Declared memory measurements include an activated output field.");
        GC.KeepAlive(targets);
    }

    [InlineArray(64)]
    private struct SampleWindow { private float _sample; }

    private struct WindowState
    {
        public SampleWindow Samples;
        public string? Name;
        public int Count;
    }

    private readonly struct WindowMotion : IMotion<float, FloatOps, HistoryParameters, WindowState>
    {
        public static MotionBoundary Describe(in HistoryParameters parameters) => new(StageEnd.Duration, parameters.Duration);
        public static void Initialize(in HistoryParameters parameters, in MotionValues<float> values, ref WindowState state) =>
            state = new() { Name = parameters.Name };
        public static MotionSample<float> Evaluate(in HistoryParameters parameters, in MotionTime time,
            in MotionValues<float> values, ref WindowState state)
        {
            float value = values.From + (values.To - values.From) * time.Time / parameters.Duration * parameters.Gain;
            state.Samples[state.Count++ % 64] = value;
            return new(value, time.Time >= parameters.Duration);
        }
        public static void Retarget(in HistoryParameters previous, in HistoryParameters next,
            in MotionValues<float> values, ref WindowState state) => state.Name = next.Name;
        public static bool ParametersEqual(in HistoryParameters previous, in HistoryParameters next) =>
            HistoryMotion.ParametersEqual(in previous, in next);
    }
}
