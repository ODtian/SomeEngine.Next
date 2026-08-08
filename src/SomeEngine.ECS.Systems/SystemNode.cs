namespace SomeEngine.ECS.Systems;

internal abstract class SystemNode<TContext>
    where TContext : allows ref struct
{
    public abstract void OnCreate(ref TContext context);

    public abstract void OnUpdate(ref TContext context);

    public abstract void OnDestroy(ref TContext context);
}

internal sealed class SystemNode<TSystem, TContext> : SystemNode<TContext>
    where TSystem : ISystem<TContext>
    where TContext : allows ref struct
{
    private TSystem _system;

    public SystemNode(TSystem system)
    {
        _system = system;
    }

    public override void OnCreate(ref TContext context)
    {
        _system.OnCreate(ref context);
    }

    public override void OnUpdate(ref TContext context)
    {
        _system.OnUpdate(ref context);
    }

    public override void OnDestroy(ref TContext context)
    {
        _system.OnDestroy(ref context);
    }
}

