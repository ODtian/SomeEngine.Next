using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Samples contact approach velocities and starts the common force/impulse interval.</summary>
public readonly struct InitializeContactResponses : IPhysicsStage
{
    private readonly QueryHandle _bodies, _contacts;
    private readonly JobResourceAccess[] _accesses;

    public InitializeContactResponses(World world, QueryHandle bodies, QueryHandle contacts)
    {
        _bodies = bodies;
        _contacts = contacts;
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, bodies, accesses);
        PhysicsQueryAccess.AddAccesses(world, contacts, accesses);
        _accesses = accesses.ToArray();
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new InitializeJob(context, _bodies, _contacts), _accesses, dependency);

    private readonly struct InitializeJob(PhysicsStepContext context, QueryHandle bodies, QueryHandle contacts) : IJob, IQueryExecution
    {
        public void Execute()
        {
            var execution = this;
            context.World.ExecuteQuery(bodies, ref execution);
        }

        public void Execute(scoped QueryCursor query)
        {
            var execution = new InitializeRows(new PhysicsBodies(query), context);
            context.World.ExecuteQuery(contacts, ref execution);
        }
    }

    private readonly ref struct InitializeRows(PhysicsBodies bodies, PhysicsStepContext context) : IQueryExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly PhysicsStepContext _context = context;

        public void Execute(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                ref readonly ContactPoint point = ref row.ReadRef<ContactPoint>();
                float speed = ContactBodyMath.GetSpeed(in point, row.ReadBuffer<ContactBodyLink>().AsSpan(), _bodies, in _context, true);
                row.ReadWrite<ContactResponse>() = new ContactResponse
                {
                    InitialNormalVelocity = speed,
                    StartTime = _context.StartTime,
                    EndTime = _context.EndTime,
                };
            }
        }
    }
}
