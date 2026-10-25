using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>The concrete numerical components recorded for newly created persistent points.</summary>
public interface IContactPointComponents
{
    void AddReads(World world, List<JobResourceAccess> accesses);
    void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
        ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context);
}

public readonly struct AvbdContactComponents(AvbdContactState parameters) : IContactPointComponents
{
    public void AddReads(World world, List<JobResourceAccess> accesses) { }

    public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
        ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context)
    {
        AvbdContactState state = parameters;
        state.MultiplierNormal = geometry.Normal;
        commands.Add(point, state);
    }
}
