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

    public uint AcquireSystemVersion(ref SystemSlot slot)
    {
        return _world.AcquireSystemTick();
    }

    public ImmediateSystemContext CreateContext(ref SystemSlot slot)
    {
        return new ImmediateSystemContext(
            _world,
            slot.LastSystemVersion,
            slot.CurrentSystemVersion);
    }
}

