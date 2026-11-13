using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Samples contact approach velocities and starts the common force/impulse interval.</summary>
public readonly struct InitializeContactResponses : IPhysicsStage
{
    private readonly Query _bodies;
    private readonly ContactColumns _contacts;
    private readonly BodyColumns _columns = new();
    public InitializeContactResponses(World world, Query bodies, ContactColumns contacts)
    {
        _bodies = bodies; _contacts = contacts;
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        JobHandle completed = PhysicsBodyJobs.Schedule<ReadBodyFields, Initialize>(_bodies, _columns, in context, new Initialize(this), dependency);
        var step = new PhysicsStepResult(context.Number, context.StartTime - context.Substep * context.Duration,
            context.EndTime, completed);
        _contacts.SetStep(in step);
        return completed;
    }
    private readonly struct Initialize(InitializeContactResponses stage) : IPhysicsBodyWork
    {
        public void AddAccesses(World world, List<JobResourceAccess> accesses) => accesses.Add(stage._contacts.WriteAccess);
        public void Execute(in PhysicsStepContext context, scoped PhysicsBodies bodies)
        {
            for (int index = 0; index < stage._contacts.PointCount; index++)
            {
                ContactPoint point = stage._contacts.GetPoint(index);
                ReadOnlySpan<ContactBodyLink> links = stage._contacts.BodyLinksOf(index);
                bool anyDynamic = false, anyAwake = false;
                foreach (ref readonly ContactBodyLink link in links)
                {
                    Entity body = link.Body;
                    if (bodies.ReadMass(body).Motion != BodyMotion.Dynamic) continue;
                    anyDynamic = true; anyAwake |= bodies.IsAwake(body);
                }
                ContactResponse response = default;
                if (anyDynamic && !anyAwake) response.ForceOnSecond = stage._contacts.Responses[index].ForceOnSecond;
                else response.InitialNormalVelocity = ContactBodyMath.GetSpeed(in point, links, bodies, in context, true);
                response.StartTime = context.StartTime; response.EndTime = context.EndTime;
                stage._contacts.Responses[index] = response;
            }
        }
    }
}
