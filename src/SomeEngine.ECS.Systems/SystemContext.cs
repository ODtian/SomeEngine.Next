using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed class SystemContext
{
    public JobHandle GlobalDependency { get; set; }
}

public sealed class EngineDriver : ISystemDriver<SystemContext>
{
    private readonly World _world;
    private readonly SystemContext _context;

    public EngineDriver(World world, SystemContext context)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public uint AcquireSystemVersion(ref SystemSlot slot)
        => _world.AcquireSystemTick();

    public SystemContext CreateContext(ref SystemSlot slot)
        => _context;
}
