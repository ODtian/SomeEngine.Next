using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

public enum AnimationPhase : byte { Timeline, Graph, LayerWeights, LayerComposition, Properties, Composition, Completion }

public static class AnimationSystems
{
    private static readonly ConditionalWeakTable<SystemGroup<ImmediateSystemContext>, AnimationPipeline> Schedules = new();

    /// <summary>Describes the concrete phase systems before and after their first update.</summary>
    public static Type[] Describe(SystemGroup<ImmediateSystemContext> schedule)
    {
        Type[] systems = schedule.DescribeSystems();
        if (!Schedules.TryGetValue(schedule, out AnimationPipeline? pipeline))
            return systems;
        var result = new List<Type>();
        foreach (Type system in systems)
        {
            if (system == typeof(AnimationPipeline))
                result.AddRange(pipeline.Describe());
            else
                result.Add(system);
        }
        return result.ToArray();
    }

    /// <summary>Adds a user system after the built-in work of the selected animation phase.</summary>
    public static void Add<TSystem>(SystemGroup<ImmediateSystemContext> schedule, TSystem system, AnimationPhase phase)
        where TSystem : ISystem<ImmediateSystemContext> => For(schedule).Add(system, phase);

    /// <summary>Adds the concrete motion combination to the common animation stages.</summary>
    public static void Add<T, TOps, TParameters, TState, TEvaluator>(
        SystemGroup<ImmediateSystemContext> schedule,
        in Spec<T, TOps, TParameters, TState, TEvaluator> spec)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState> =>
        For(schedule).Add<T, TOps, TParameters, TState, TEvaluator>();

    private static AnimationPipeline For(SystemGroup<ImmediateSystemContext> schedule) =>
        Schedules.GetValue(schedule, static schedule =>
        {
            var pipeline = new AnimationPipeline();
            schedule.Add(pipeline);
            return pipeline;
        });

    /// <summary>Adds the built-in motion combinations and existing curve inputs to the common stages.</summary>
    public static void AddDefaults(SystemGroup<ImmediateSystemContext> schedule, Entity time = default)
    {
        For(schedule).UseDefaults(time);
        AddValue<float, FloatOps>(schedule);
        AddValue<Vector2, Vector2Ops>(schedule);
        AddValue<Vector3, Vector3Ops>(schedule);
        AddValue<Vector4, Vector4Ops>(schedule);
        AddValue<Quaternion, QuaternionOps>(schedule);
        AddValue<Color, ColorOps>(schedule);
        AddValue<Thickness, ThicknessOps>(schedule);
        AddValue<Rect, RectOps>(schedule);
        AddValue<Matrix3x2, Matrix3x2Ops>(schedule);
    }

    private static void AddValue<T, TOps>(SystemGroup<ImmediateSystemContext> schedule)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
    {
        Add(schedule, default(Spec<T, TOps, TweenParameters, TweenState, Tween<T, TOps, Linear>>));
        Add(schedule, default(Spec<T, TOps, SpringParameters, SpringState<T>, Spring<T, TOps>>));
        Add(schedule, default(Spec<T, TOps, KeyframeParameters, KeyframeState, Keyframes<T, TOps>>));
        Add(schedule, default(Spec<T, TOps, FunctionTweenParameters, TweenState, FunctionTween<T, TOps>>));
    }
}
