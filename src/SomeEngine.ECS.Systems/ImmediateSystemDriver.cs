using SomeEngine.ECS;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Context driver that binds system version advancement to <see cref="World.AcquireSystemTick"/>.
/// </summary>
public sealed class ImmediateSystemDriver : ISystemDriver<ImmediateSystemContext>
{
    private readonly World _world;

    public ImmediateSystemDriver(World world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public World World => _world;

    public ulong AcquireSystemVersion(ref SystemState state)
    {
        return _world.AcquireSystemVersion();
    }

    public ImmediateSystemContext CreateContext(ref SystemState state)
    {
        return new ImmediateSystemContext(
            _world,
            state.LastSystemVersion,
            state.CurrentSystemVersion);
    }

    public void CreateContext(ref SystemState state, ref ImmediateSystemContext context)
    {
        context = new ImmediateSystemContext(
            _world, state.LastSystemVersion, state.CurrentSystemVersion, context.DeltaTime);
    }
}
