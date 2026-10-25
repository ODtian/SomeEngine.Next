using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Expands declared combinations into the common stages before their first update.</summary>
internal sealed unsafe class AnimationPipeline : ISystem<ImmediateSystemContext>
{
    private readonly List<Algorithm> _algorithms = new();
    private readonly List<AdditionalSystem> _additionalSystems = new();
    private readonly List<TrackMembers> _tracks = new();
    private List<PhaseSystems>? _systems;
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
        _systems = new();
        Build(new SystemCollection(context.World, _systems, _tracks));
    }

    internal Type[] Describe()
    {
        if (_systems is not null)
        {
            var result = new List<Type>();
            foreach (PhaseSystems phase in _systems) result.AddRange(phase.Systems.DescribeSystems());
            return result.ToArray();
        }
        var types = new TypeCollection();
        Build(types);
        return types.Types.ToArray();
    }

    private void Build(ISystemCollection systems)
    {
        systems.BeginPhase();
        systems.Add(new TimelineSystem(_time));
        AddAdditionalSystems(systems, AnimationPhase.Timeline);
        systems.Add(new GraphSystem());
        AddAdditionalSystems(systems, AnimationPhase.Graph);
        systems.BeginPhase();
        if (_defaults)
            systems.AddTracks<float, TrackSystem<float, FloatOps>>(new(TrackFields.LayerWeights, evaluateTyped: false));
        foreach (Algorithm algorithm in _algorithms)
            algorithm.Evaluate(systems, TrackFields.LayerWeights);
        AddAdditionalSystems(systems, AnimationPhase.LayerWeights);
        systems.BeginPhase();
        systems.AddTracks<float, GraphOutputSystem<float, FloatOps>>(new(TrackFields.LayerWeights));
        systems.Add(new ComposeSystem<float, FloatOps>(TrackFields.LayerWeights));
        AddAdditionalSystems(systems, AnimationPhase.LayerComposition);

        systems.BeginPhase();
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

        systems.BeginPhase();
        if (_defaults)
        {
            systems.AddTracks<float, GraphOutputSystem<float, FloatOps>>(new());
            systems.AddTracks<Vector2, GraphOutputSystem<Vector2, Vector2Ops>>(new());
            systems.AddTracks<Vector3, GraphOutputSystem<Vector3, Vector3Ops>>(new());
            systems.AddTracks<Vector4, GraphOutputSystem<Vector4, Vector4Ops>>(new());
            systems.AddTracks<Quaternion, GraphOutputSystem<Quaternion, QuaternionOps>>(new());
            systems.AddTracks<Color, GraphOutputSystem<Color, ColorOps>>(new());
            systems.AddTracks<Thickness, GraphOutputSystem<Thickness, ThicknessOps>>(new());
            systems.AddTracks<Rect, GraphOutputSystem<Rect, RectOps>>(new());
            systems.AddTracks<Matrix3x2, GraphOutputSystem<Matrix3x2, Matrix3x2Ops>>(new());
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

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        foreach (PhaseSystems phase in _systems!) phase.Update(ref context);
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        List<PhaseSystems> systems = _systems!;
        foreach (PhaseSystems phase in systems) phase.Systems.Complete();
        foreach (PhaseSystems phase in systems) phase.Systems.Dispose();
        foreach (TrackMembers tracks in _tracks) context.World.ReleaseQuery(tracks.Query);
    }

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
        systems.AddTracks<T, TrackSystem<T, TOps>>(new(TrackFields.Properties, evaluateTyped: false));
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
        systems.AddTracks<T, TypedTrackSystem<T, TOps, TParameters, TState, TEvaluator>>(new(fields));
    }

    private static void AddComposition<T, TOps>(ISystemCollection systems, TrackFields fields)
        where T : unmanaged
        where TOps : struct, IValueOps<T>
    {
        systems.AddTracks<T, GraphOutputSystem<T, TOps>>(new(fields));
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
        void BeginPhase();
        void Add<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext>;
        void AddTracks<T, TSystem>(TSystem system) where T : unmanaged where TSystem : ISystem<ImmediateSystemContext>;
    }

    private sealed class SystemCollection(World world, List<PhaseSystems> systems, List<TrackMembers> tracks) : ISystemCollection
    {
        private PhaseSystems _phase = null!;

        public void BeginPhase()
        {
            _phase = new(world);
            systems.Add(_phase);
        }

        public void Add<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> => _phase.Systems.Add(system);

        public void AddTracks<T, TSystem>(TSystem system) where T : unmanaged where TSystem : ISystem<ImmediateSystemContext>
        {
            TrackMembers? members = null;
            foreach (TrackMembers candidate in tracks)
                if (candidate.ValueType == typeof(T)) { members = candidate; break; }
            if (members is null)
            {
                QueryHandle query = world.Query(world.QueryDefinition().Read<Timeline>().ReadBuffer<Track<T>>());
                members = new(typeof(T), query);
                tracks.Add(members);
            }
            _phase.Add(members, _phase.Systems.Add(system));
        }
    }

    private sealed class TypeCollection : ISystemCollection
    {
        internal readonly List<Type> Types = new();
        public void BeginPhase() { }
        public void Add<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> => Types.Add(typeof(TSystem));
        public void AddTracks<T, TSystem>(TSystem system) where T : unmanaged where TSystem : ISystem<ImmediateSystemContext> => Add(system);
    }

    private sealed class TrackMembers(Type valueType, QueryHandle query)
    {
        internal readonly Type ValueType = valueType;
        internal readonly QueryHandle Query = query;
    }

    private sealed class TrackNodes(TrackMembers members)
    {
        internal readonly TrackMembers Members = members;
        internal readonly List<int> Indices = new();
        internal bool Enabled = true;
    }

    private sealed class PhaseSystems(World world)
    {
        internal readonly SystemGroup<ImmediateSystemContext> Systems = new(new ImmediateSystemDriver(world));
        private readonly List<TrackNodes> _tracks = new();

        internal void Add(TrackMembers members, int index)
        {
            foreach (TrackNodes nodes in _tracks)
                if (ReferenceEquals(nodes.Members, members)) { nodes.Indices.Add(index); return; }
            var added = new TrackNodes(members);
            added.Indices.Add(index);
            _tracks.Add(added);
        }

        internal void Update(ref ImmediateSystemContext context)
        {
            foreach (TrackNodes nodes in _tracks)
            {
                bool enabled = context.World.HasQueryMembers(nodes.Members.Query);
                if (enabled == nodes.Enabled) continue;
                foreach (int index in nodes.Indices)
                    if (enabled) Systems.Enable(index);
                    else Systems.Disable(index);
                nodes.Enabled = enabled;
            }
            Systems.Update(ref context);
        }
    }

}
