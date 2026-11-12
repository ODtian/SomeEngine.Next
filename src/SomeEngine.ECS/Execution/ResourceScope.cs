namespace SomeEngine.ECS;

public ref struct ResourceReadScope<T>
{
    private readonly World _world;
    private readonly ref T _value;
    internal ResourceReadScope(World world, bool write)
    {
        _world = world;
        world.Execution?.EnterValues([new(ResourceType<T>.Id, WorldValueKind.Resource, write, false)]);
        try { _value = ref world.Resource<T>(); }
        catch { world.Execution?.Exit(); throw; }
        world.BeginRead();
    }
    public ref readonly T Value => ref _value;
    internal ref T Get() => ref _value;
    public void Dispose() => _world.EndValueRead();
}

public ref struct ResourceWriteScope<T>
{
    private ResourceReadScope<T> _read;
    internal ResourceWriteScope(World world) => _read = new(world, true);
    public ref T Value => ref _read.Get();
    public void Dispose() => _read.Dispose();
}

public sealed partial class World
{
    public ResourceReadScope<T> OpenResourceRead<T>() => new(this, false);
    public ResourceWriteScope<T> OpenResourceWrite<T>() => new(this);
}
