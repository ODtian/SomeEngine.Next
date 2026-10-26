using SomeEngine.Graphics;

namespace SomeEngine.Physics;

/// <summary>Distributes linear compute work across the common X/Y group range.</summary>
public static class DevicePhysicsDispatch
{
    public const int GroupsPerRow = 65_535;

    public static DispatchArguments CreateGroups(int groupCount) => new((uint)Math.Min(groupCount, GroupsPerRow),
        (uint)((groupCount + GroupsPerRow - 1) / GroupsPerRow), 1);
}
