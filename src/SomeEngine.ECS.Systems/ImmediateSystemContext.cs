using SomeEngine.ECS;

namespace SomeEngine.ECS.Systems;

/// <summary>
/// Simple non-job context for direct, single-threaded system loops.
/// </summary>
public readonly struct ImmediateSystemContext
{
    public ImmediateSystemContext(
        World world,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        LastSystemVersion = lastSystemVersion;
        CurrentSystemVersion = currentSystemVersion;
    }

    public World World { get; }

    public uint LastSystemVersion { get; }

    public uint CurrentSystemVersion { get; }

}

