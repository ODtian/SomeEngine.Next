using SomeEngine.ECS;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Simple non-job context for direct, single-threaded system loops.
/// </summary>
public readonly struct ImmediateSystemContext
{
    public ImmediateSystemContext(
        World world,
        ulong lastSystemVersion,
        ulong currentSystemVersion,
        double deltaTime = 0)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        LastSystemVersion = lastSystemVersion;
        CurrentSystemVersion = currentSystemVersion;
        DeltaTime = deltaTime;
    }

    public World World { get; }

    public ulong LastSystemVersion { get; }

    public ulong CurrentSystemVersion { get; }

    public double DeltaTime { get; }

}
