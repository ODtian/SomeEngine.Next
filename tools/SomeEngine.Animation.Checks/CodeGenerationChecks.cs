using System.Runtime.CompilerServices;
using SomeEngine.Animation.Consumers;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Animation.Checks;

internal static unsafe class CodeGenerationChecks
{
    internal static void Run()
    {
        BatchEntry<TweenParameters, TweenState, Tween<float, FloatOps, SquareEasing>>(new(1));
        BatchEntry<SpringParameters, SpringState<float>, Spring<float, FloatOps>>(new());
        BatchEntry<HistoryParameters, HistoryState, HistoryMotion>(new(1, 1, "code-generation"));
        BatchEntry<FunctionTweenParameters, TweenState, FunctionTween<float, FloatOps>>(new(1, &ScalarEasing));
        ReferenceFieldEntry();
        Console.WriteLine("CODEGEN_PASSED tween-static-easing spring consumer-history scalar-function reference-field-batch");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BatchEntry<TParameters, TState, TMotion>(TParameters parameters)
        where TParameters : struct
        where TState : struct
        where TMotion : struct, IMotion<float, FloatOps, TParameters, TState>
    {
        var data = new MotionData<float, TParameters, TState>[2];
        for (int item = 0; item < data.Length; item++)
        {
            data[item] = new() { Parameters = parameters, Values = new(0, 10, 0), Time = new(0.25f, 1f / 120), Active = true };
            TMotion.Initialize(in data[item].Parameters, in data[item].Values, ref data[item].State);
        }
        var batch = new MotionBatch<float, TParameters, TState>(data);
        MotionDispatcher<float, FloatOps, TParameters, TState, TMotion>.EvaluateBatch(ref batch);
        if (data[0].Sample.Value <= 0 || data[1].Sample.Value <= 0)
            throw new InvalidOperationException("Code generation entries execute the concrete batch samples.");
        GC.KeepAlive(data);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReferenceFieldEntry()
    {
        using var world = new World();
        var first = new FieldValue();
        var second = new FieldValue();
        Entity a = world.CreateEntity(new FieldRoot { Value = first });
        Entity b = world.CreateEntity(new FieldRoot { Value = second });
        var channel = Channel<float>.Of<FieldRoot, FieldAccess>(1);
        FieldWrite<float>[] writes = [new(a, channel, 7), new(b, channel, 11)];
        channel.WriteBatch(world, writes);
        if (first.Value != 7 || second.Value != 11)
            throw new InvalidOperationException("Code generation field batches execute current reference setters.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static float ScalarEasing(float progress) => progress * progress;

    private sealed class FieldValue { public float Value { get; set; } }
    private struct FieldRoot : IComponent { public FieldValue Value; }
    private readonly struct FieldAccess : IFieldAccess<FieldRoot, float>
    {
        public static float Read(in FieldRoot component) => component.Value.Value;
        public static void Write(ref FieldRoot component, in float value) => component.Value.Value = value;
    }
}
