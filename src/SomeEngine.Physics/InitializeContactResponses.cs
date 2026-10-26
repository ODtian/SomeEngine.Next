using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Samples contact approach velocities and starts the common force/impulse interval.</summary>
public readonly struct InitializeContactResponses : IPhysicsStage, IDisposable
{
    private readonly World _world;
    private readonly QueryHandle _bodies, _contacts;
    private readonly JobResourceAccess[] _accesses;

    public InitializeContactResponses(World world, QueryHandle bodies, QueryHandle contacts)
    {
        _world = world;
        _bodies = world.Query(world.GetQueryDefinition(bodies).AsReadOnly().WithOptionalRead<BodyActivity>());
        _contacts = contacts;
        var accesses = new List<JobResourceAccess>();
        PhysicsQueryAccess.AddAccesses(world, _bodies, accesses);
        PhysicsQueryAccess.AddAccesses(world, contacts, accesses);
        _accesses = accesses.ToArray();
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(context.World).Schedule(new InitializeJob(context, _bodies, _contacts), _accesses, dependency);

    public void Dispose() => _world.ReleaseQuery(_bodies);

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
                ReadOnlySpan<ContactBodyLink> links = row.ReadBuffer<ContactBodyLink>().AsSpan();
                bool anyDynamic = false, anyAwake = false;
                foreach (ref readonly ContactBodyLink link in links)
                {
                    QueryRow body = _bodies.GetRow(ContactBodyMath.GetBody(in link, _bodies, in _context));
                    if (body.ReadRef<BodyMass>().Motion != BodyMotion.Dynamic) continue;
                    anyDynamic = true;
                    anyAwake |= BodyActivities.IsAwake(body);
                }
                ContactResponse response = default;
                if (anyDynamic && !anyAwake) response.ForceOnSecond = row.ReadRef<ContactResponse>().ForceOnSecond;
                else response.InitialNormalVelocity = ContactBodyMath.GetSpeed(in point, links, _bodies, in _context, true);
                response.StartTime = _context.StartTime;
                response.EndTime = _context.EndTime;
                row.ReadWrite<ContactResponse>() = response;
            }
        }
    }
}
