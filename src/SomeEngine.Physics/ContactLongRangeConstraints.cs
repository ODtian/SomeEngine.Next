using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Stationary rigid contact supports in an explicitly selected body work range.</summary>
public readonly struct ContactLongRangeConstraints(QueryHandle supportBodies, QueryHandle contacts, ContactSupportSettings settings) : IAvbdConstraints
{
    private readonly ContactSupportData _data = new(settings);
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        PhysicsQueryAccess.AddAccesses(world, supportBodies, accesses);
        PhysicsQueryAccess.AddAccesses(world, contacts, accesses);
        accesses.Add(JobResourceAccess.Exclusive(WorldJobs.GetScheduler(world).GetResourceToken(_data)));
    }
    public void Initialize(in PhysicsStepContext context, scoped PhysicsBodies bodies, float retention)
    {
        var work = new InitializeSupports(_data, bodies, context);
        Execute(in context, work);
    }
    public void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation) { }
    public void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation)
    {
        var work = new CorrectSupport(_data, body, bodies, context, in equation, ref translation, ref rotation);
        Execute(in context, work);
    }
    public void UpdateDuals(in PhysicsStepContext context, scoped PhysicsBodies bodies, float initialErrorFraction)
    {
        var work = new UpdateSupports(_data, bodies, context);
        Execute(in context, work);
    }
    private void Execute<TWork>(in PhysicsStepContext context, TWork work)
        where TWork : struct, ISupportExecution, allows ref struct
    {
        var execution = new ReadBodies<TWork>(context.World, contacts, work);
        context.World.ExecuteQuery(supportBodies, ref execution);
    }
    private interface ISupportExecution
    {
        void Execute(scoped PhysicsQueryRows workBodies, scoped PhysicsQueryRows contacts);
    }
    private ref struct ReadBodies<TWork>(World world, QueryHandle contacts, TWork work) : IQueryExecution
        where TWork : struct, ISupportExecution, allows ref struct
    {
        private TWork _work = work;
        public void Execute(scoped QueryCursor workBodies)
        {
            var read = new ReadContacts<TWork>(new PhysicsQueryRows(workBodies), _work);
            world.ExecuteQuery(contacts, ref read);
        }
    }
    private ref struct ReadContacts<TWork>(PhysicsQueryRows workBodies, TWork work) : IQueryExecution
        where TWork : struct, ISupportExecution, allows ref struct
    {
        private readonly PhysicsQueryRows _workBodies = workBodies;
        private TWork _work = work;
        public void Execute(scoped QueryCursor contacts) => _work.Execute(_workBodies, new PhysicsQueryRows(contacts));
    }
    private readonly ref struct InitializeSupports(ContactSupportData data, PhysicsBodies bodies, PhysicsStepContext context) : ISupportExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly PhysicsStepContext _context = context;
        public void Execute(scoped PhysicsQueryRows workBodies, scoped PhysicsQueryRows contacts) => data.Initialize(workBodies, contacts, _bodies, in _context);
    }
    private readonly ref struct UpdateSupports(ContactSupportData data, PhysicsBodies bodies, PhysicsStepContext context) : ISupportExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly PhysicsStepContext _context = context;
        public void Execute(scoped PhysicsQueryRows workBodies, scoped PhysicsQueryRows contacts) => data.Update(workBodies, contacts, _bodies, in _context);
    }
    private ref struct CorrectSupport(ContactSupportData data, Entity body, PhysicsBodies bodies, PhysicsStepContext context,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation) : ISupportExecution
    {
        private readonly PhysicsBodies _bodies = bodies;
        private readonly PhysicsStepContext _context = context;
        private readonly ref readonly AvbdBodyEquation _equation = ref equation;
        private ref Vector3 _translation = ref translation;
        private ref Vector3 _rotation = ref rotation;
        public void Execute(scoped PhysicsQueryRows workBodies, scoped PhysicsQueryRows contacts) => data.Correct(workBodies, contacts, body, _bodies, in _context,
            in _equation, ref _translation, ref _rotation);
    }
}
