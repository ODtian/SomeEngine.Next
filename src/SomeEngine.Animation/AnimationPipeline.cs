using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Expands declared combinations into the common stages before their first update.</summary>
internal sealed unsafe class AnimationPipeline : ISystem<ImmediateSystemContext>
{
    private readonly List<Algorithm> _algorithms = new();
    private readonly List<AdditionalSystem> _additionalSystems = new();
    private SystemGroup<ImmediateSystemContext>? _systems;
    private Entity _time;
    private bool _defaults;

    public void Add<TSystem>(TSystem system, AnimationPhase phase) where TSystem : ISystem<ImmediateSystemContext>
    {
        DevelopmentCheck.Require(_systems is null, "Animation systems are composed before their first update.");
        _additionalSystems.Add(new AdditionalSystem<TSystem>(system, phase));
    }

    public void UseDefaults(Entity time)
    {
        DevelopmentCheck.Require(_systems is null, "Animation systems are composed before their first update.");
        _defaults = true;
        _time = time;
    }

    public void Add<T, TOps, TParameters, TState, TEvaluator>()
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        DevelopmentCheck.Require(_systems is null, "Animation systems are composed before their first update.");
        Type type = typeof(MotionInstance<T, TOps, TParameters, TState, TEvaluator>);
        foreach (Algorithm algorithm in _algorithms)
            if (algorithm.Type == type)
                return;
        _algorithms.Add(new Algorithm(type, typeof(ValueOps<T, TOps>),
            &AddEvaluation<T, TOps, TParameters, TState, TEvaluator>, &AddComposition<T, TOps>));
    }

    public void OnCreate(ref ImmediateSystemContext context)
    {
        var systems = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(context.World));
        _systems = systems;
        Build(new SystemCollection(systems));
    }

    internal Type[] Describe()
    {
        if (_systems is not null)
            return _systems.DescribeSystems();
        var types = new TypeCollection();
        Build(types);
        return types.Types.ToArray();
    }

    private void Build(ISystemCollection systems)
    {
        systems.Add(new TimelineSystem(_time));
        AddAdditionalSystems(systems, AnimationPhase.Timeline);
        systems.Add(new GraphSystem());
        AddAdditionalSystems(systems, AnimationPhase.Graph);
        if (_defaults)
            systems.Add(new TrackSystem<float, FloatOps>(TrackFields.LayerWeights, evaluateTyped: false));
        foreach (Algorithm algorithm in _algorithms)
            algorithm.Evaluate(systems, TrackFields.LayerWeights);
        AddAdditionalSystems(systems, AnimationPhase.LayerWeights);
        systems.Add(new GraphOutputSystem<float, FloatOps>(TrackFields.LayerWeights));
        systems.Add(new ComposeSystem<float, FloatOps>(TrackFields.LayerWeights));
        AddAdditionalSystems(systems, AnimationPhase.LayerComposition);

        var compositions = new HashSet<Type>();
        if (_defaults)
        {
            AddExisting<float, FloatOps>(systems, compositions);
            AddExisting<Vector2, Vector2Ops>(systems, compositions);
            AddExisting<Vector3, Vector3Ops>(systems, compositions);
            AddExisting<Vector4, Vector4Ops>(systems, compositions);
            AddExisting<Quaternion, QuaternionOps>(systems, compositions);
            AddExisting<Color, ColorOps>(systems, compositions);
            AddExisting<Thickness, ThicknessOps>(systems, compositions);
            AddExisting<Rect, RectOps>(systems, compositions);
            AddExisting<Matrix3x2, Matrix3x2Ops>(systems, compositions);
        }
        foreach (Algorithm algorithm in _algorithms)
            algorithm.Evaluate(systems, TrackFields.Properties);
        AddAdditionalSystems(systems, AnimationPhase.Properties);

        if (_defaults)
        {
            systems.Add(new GraphOutputSystem<float, FloatOps>());
            systems.Add(new GraphOutputSystem<Vector2, Vector2Ops>());
            systems.Add(new GraphOutputSystem<Vector3, Vector3Ops>());
            systems.Add(new GraphOutputSystem<Vector4, Vector4Ops>());
            systems.Add(new GraphOutputSystem<Quaternion, QuaternionOps>());
            systems.Add(new GraphOutputSystem<Color, ColorOps>());
            systems.Add(new GraphOutputSystem<Thickness, ThicknessOps>());
            systems.Add(new GraphOutputSystem<Rect, RectOps>());
            systems.Add(new GraphOutputSystem<Matrix3x2, Matrix3x2Ops>());
            systems.Add(new ComposeSystem<float, FloatOps>());
            systems.Add(new ComposeSystem<Vector2, Vector2Ops>());
            systems.Add(new ComposeSystem<Vector3, Vector3Ops>());
            systems.Add(new ComposeSystem<Vector4, Vector4Ops>());
            systems.Add(new ComposeSystem<Quaternion, QuaternionOps>());
            systems.Add(new ComposeSystem<Color, ColorOps>());
            systems.Add(new ComposeSystem<Thickness, ThicknessOps>());
            systems.Add(new ComposeSystem<Rect, RectOps>());
            systems.Add(new ComposeSystem<Matrix3x2, Matrix3x2Ops>());
        }
        foreach (Algorithm algorithm in _algorithms)
            if (compositions.Add(algorithm.Composition))
                algorithm.Compose(systems, TrackFields.Properties);
        AddAdditionalSystems(systems, AnimationPhase.CompositionInputs);
        AddAdditionalSystems(systems, AnimationPhase.Composition);
        systems.Add(new GraphEndSystem());
        AddAdditionalSystems(systems, AnimationPhase.Completion);
        systems.Add(new TimelineEndSystem());
    }

    public void OnUpdate(ref ImmediateSystemContext context) => _systems!.Update(ref context);

    public void OnDestroy(ref ImmediateSystemContext context) => _systems?.Dispose();

    private void AddAdditionalSystems(ISystemCollection systems, AnimationPhase phase)
    {
        foreach (AdditionalSystem additional in _additionalSystems)
            if (additional.Phase == phase)
                additional.Add(systems);
    }

    private static void AddExisting<T, TOps>(ISystemCollection systems, HashSet<Type> compositions)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
    {
        systems.Add(new TrackSystem<T, TOps>(TrackFields.Properties, evaluateTyped: false));
        compositions.Add(typeof(ValueOps<T, TOps>));
    }

    private static void AddEvaluation<T, TOps, TParameters, TState, TEvaluator>(
        ISystemCollection systems, TrackFields fields)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
        where TParameters : struct
        where TState : struct
        where TEvaluator : struct, IMotion<T, TOps, TParameters, TState>
    {
        if (fields == TrackFields.LayerWeights && typeof(T) != typeof(float))
            return;
        systems.Add(new TypedTrackSystem<T, TOps, TParameters, TState, TEvaluator>(fields));
    }

    private static void AddComposition<T, TOps>(ISystemCollection systems, TrackFields fields)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
    {
        systems.Add(new GraphOutputSystem<T, TOps>(fields));
        systems.Add(new ComposeSystem<T, TOps>(fields));
    }

    private readonly struct Algorithm(Type type, Type composition,
        delegate* managed<ISystemCollection, TrackFields, void> evaluate,
        delegate* managed<ISystemCollection, TrackFields, void> compose)
    {
        public readonly Type Type = type;
        public readonly Type Composition = composition;
        public readonly delegate* managed<ISystemCollection, TrackFields, void> Evaluate = evaluate;
        public readonly delegate* managed<ISystemCollection, TrackFields, void> Compose = compose;
    }

    private abstract class AdditionalSystem(AnimationPhase phase)
    {
        public AnimationPhase Phase { get; } = phase;
        public abstract void Add(ISystemCollection systems);
    }

    private sealed class AdditionalSystem<TSystem>(TSystem system, AnimationPhase phase) : AdditionalSystem(phase)
        where TSystem : ISystem<ImmediateSystemContext>
    {
        public override void Add(ISystemCollection systems) => systems.Add(system);
    }

    private interface ISystemCollection
    {
        void Add<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext>;
    }

    private sealed class SystemCollection(SystemGroup<ImmediateSystemContext> systems) : ISystemCollection
    {
        public void Add<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> => systems.Add(system);
    }

    private sealed class TypeCollection : ISystemCollection
    {
        internal readonly List<Type> Types = new();
        public void Add<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> => Types.Add(typeof(TSystem));
    }

}
