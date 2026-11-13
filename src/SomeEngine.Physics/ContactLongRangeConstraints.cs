using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Stationary rigid supports derived from the same CPU contact state and original body fields.</summary>
public readonly partial struct ContactLongRangeConstraints(Query supportBodies,
    ContactManifolds<AvbdSupportedContactState> contacts, ContactSupportSettings settings) : IAvbdConstraints
{
    private readonly ContactSupportData _data = new(settings, contacts);
    private readonly List<Entity> _members = [];
    public LongRangeBodySupport ReadBodySupport(Entity body) => _data.ReadBodySupport(body);
    public ReadOnlySpan<LongRangeSupportLoad> ReadBodyLoads(Entity body) => _data.ReadBodyLoads(body);
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        PhysicsQueryAccess.AddAccesses(world, supportBodies, accesses);
        accesses.Add(contacts.WriteAccess);
        accesses.Add(JobResourceAccess.Write((world.Execution!.Scheduler!).GetResourceToken(_data)));
    }
    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    {
        _members.Clear();
        using var scope = supportBodies.OpenRead();
        foreach (QueryChunk chunk in scope)
            foreach (Entity entity in chunk.Entities) _members.Add(entity);
        _data.Initialize(CollectionsMarshal.AsSpan(_members), bodies, in context);
    }
    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation)
    { }
    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation) =>
        _data.Correct(body, bodies, in context, in equation, ref translation, ref rotation);
    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction) =>
        _data.Update(bodies, in context);
}
