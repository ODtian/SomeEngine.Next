using SomeEngine.ECS;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionGrid
{
    private readonly Dictionary<DeviceCollisionParticipants, MemberVersions> _memberVersions = [];
    internal ulong FirstMemberVersion { get; private set; }
    internal ulong SecondMemberVersion { get; private set; }

    internal void CaptureMemberVersions(DeviceCollisionParticipants participants, in PhysicsStepContext context)
    {
        if (!_memberVersions.TryGetValue(participants, out MemberVersions? versions))
            _memberVersions.Add(participants, versions = new(participants));
        FirstMemberVersion = Capture(participants.World, participants.First, versions.First, in context);
        SecondMemberVersion = Capture(participants.World, participants.Second, versions.Second, in context);
    }
    private static ulong Capture(World world, Query members, PhysicsQueryWindow window, in PhysicsStepContext context)
    {
        ulong baseline = window.Begin(context);
        window.Accept(world.Version);
        return baseline;
    }
    private sealed class MemberVersions(DeviceCollisionParticipants participants)
    {
        internal readonly PhysicsQueryWindow First = new(participants.First);
        internal readonly PhysicsQueryWindow Second = new(participants.Second);
    }
}
