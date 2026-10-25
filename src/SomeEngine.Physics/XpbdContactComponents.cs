using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Records XPBD multipliers on the common persistent contact entity.</summary>
public readonly struct XpbdContactComponents : IContactPointComponents
{
    public void AddReads(World world, List<JobResourceAccess> accesses) { }

    public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
        ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context) =>
        commands.Add(point, new XpbdContactState());
}
