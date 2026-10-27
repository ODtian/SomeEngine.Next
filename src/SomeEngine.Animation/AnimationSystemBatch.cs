using SomeEngine.ECS.Systems;

namespace SomeEngine.Animation;

/// <summary>Runs consecutive animation evaluators within one system submission scope.</summary>
internal sealed unsafe class AnimationSystemBatch(AnimationInputs? inputs) : ISystem<ImmediateSystemContext>
{
    private readonly List<SystemEntry> _systems = new();

    internal void Add<TSystem>(TSystem system) where TSystem : ISystem<ImmediateSystemContext> =>
        _systems.Add(new SystemEntry<TSystem>(system));

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        if (inputs is not null && !inputs.HasWork())
            return;
        foreach (SystemEntry system in _systems)
        {
            system.CurrentVersion = context.World.AcquireSystemVersion();
            var evaluation = new ImmediateSystemContext(context.World, system.LastVersion,
                system.CurrentVersion, context.DeltaTime);
            if (!system.Created)
            {
                system.Create(ref evaluation);
                system.Created = true;
            }
            system.Update(system, ref evaluation);
            system.LastVersion = system.CurrentVersion;
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        List<Exception>? failures = null;
        foreach (SystemEntry system in _systems)
        {
            if (!system.Created)
                continue;
            var evaluation = new ImmediateSystemContext(context.World, system.LastVersion, system.CurrentVersion);
            try { system.Destroy(ref evaluation); }
            catch (Exception failure) { (failures ??= new()).Add(failure); }
        }
        if (failures is not null)
            throw new AggregateException(failures);
    }

    private abstract class SystemEntry(delegate* managed<SystemEntry, ref ImmediateSystemContext, void> update)
    {
        internal readonly delegate* managed<SystemEntry, ref ImmediateSystemContext, void> Update = update;
        internal uint LastVersion;
        internal uint CurrentVersion;
        internal bool Created;
        internal abstract void Create(ref ImmediateSystemContext context);
        internal abstract void Destroy(ref ImmediateSystemContext context);
    }

    private sealed class SystemEntry<TSystem>(TSystem system) : SystemEntry(&Evaluate)
        where TSystem : ISystem<ImmediateSystemContext>
    {
        private TSystem _system = system;

        internal override void Create(ref ImmediateSystemContext context) => _system.OnCreate(ref context);
        internal override void Destroy(ref ImmediateSystemContext context) => _system.OnDestroy(ref context);

        private static void Evaluate(SystemEntry system, ref ImmediateSystemContext context) =>
            ((SystemEntry<TSystem>)system)._system.OnUpdate(ref context);
    }
}
