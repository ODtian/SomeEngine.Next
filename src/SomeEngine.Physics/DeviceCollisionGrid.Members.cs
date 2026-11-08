using SomeEngine.ECS;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public sealed partial class DeviceCollisionGrid
{
    private readonly Dictionary<DeviceCollisionParticipants, MemberVersions> _memberVersions = [];
    internal uint FirstMemberVersion { get; private set; }
    internal uint SecondMemberVersion { get; private set; }

    internal void CaptureMemberVersions(DeviceCollisionParticipants participants, in PhysicsStepContext context)
    {
        if (!_memberVersions.TryGetValue(participants, out MemberVersions? versions))
            _memberVersions.Add(participants, versions = new(participants));
        FirstMemberVersion = Capture(participants.World, participants.First, versions.First, in context);
        SecondMemberVersion = Capture(participants.World, participants.Second, versions.Second, in context);
    }
    private static uint Capture(World world, QueryHandle members, PhysicsQueryWindow window, in PhysicsStepContext context)
    {
        uint baseline = window.Begin(context);
        world.ExecuteReadQuery(members, baseline, ref window,
            static (scoped QueryCursor rows, ref PhysicsQueryWindow source) => source.Accept(rows.CurrentSystemVersion));
        return baseline;
    }
    private sealed class MemberVersions(DeviceCollisionParticipants participants)
    {
        internal readonly PhysicsQueryWindow First = new(participants.World.GetQueryDefinition(participants.First));
        internal readonly PhysicsQueryWindow Second = new(participants.World.GetQueryDefinition(participants.Second));
    }
}
