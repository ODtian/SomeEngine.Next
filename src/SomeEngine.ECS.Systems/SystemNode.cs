namespace SomeEngine.ECS.Systems;

internal abstract unsafe class SystemNode<TContext>
    where TContext : allows ref struct
{
    protected SystemNode(delegate* managed<SystemNode<TContext>, ref TContext, void> update)
    {
        Update = update;
    }

    public readonly delegate* managed<SystemNode<TContext>, ref TContext, void> Update;

    public abstract Type SystemType { get; }

    public abstract void OnCreate(ref TContext context);

    public abstract void OnDestroy(ref TContext context);
}

internal sealed unsafe class SystemNode<TSystem, TContext> : SystemNode<TContext>
    where TSystem : ISystem<TContext>
    where TContext : allows ref struct
{
    private TSystem _system;

    public SystemNode(TSystem system) : base(SystemFunctions<TSystem, TContext>.Update)
    {
        _system = system;
    }

    public override Type SystemType => typeof(TSystem);

    public override void OnCreate(ref TContext context)
    {
        _system.OnCreate(ref context);
    }

    internal void UpdateSystem(ref TContext context) => _system.OnUpdate(ref context);

    public override void OnDestroy(ref TContext context)
    {
        _system.OnDestroy(ref context);
    }
}

internal static unsafe class SystemFunctions<TSystem, TContext>
    where TSystem : ISystem<TContext>
    where TContext : allows ref struct
{
    internal static readonly delegate* managed<SystemNode<TContext>, ref TContext, void> Update = &UpdateSystem;

    private static void UpdateSystem(SystemNode<TContext> node, ref TContext context) =>
        ((SystemNode<TSystem, TContext>)node).UpdateSystem(ref context);
}

