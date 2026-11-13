using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Reads current geometry and actual simulation endpoints through declared readonly component inputs.</summary>
public interface ICollisionGeometryReader<TGeometry> where TGeometry : struct
{
    static virtual bool HasGeometry(World world, Entity entity) => true;
    QueryDescription AddReads(QueryDescription definition);
    void AddInputAccesses(World world, List<JobResourceAccess> accesses) { }
    void AddAccesses(World world, List<JobResourceAccess> accesses) =>
        PhysicsQueryAccess.AddAccesses(world, world.Query(AddReads(new QueryDescription())), accesses);
    TGeometry ReadGeometry(World world, Entity entity, out BodyPose pose);
    int GetBodyCount(World world, Entity entity);
    void GetBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies);
}

public readonly struct ComponentGeometry<TGeometry>(ContactNormalReference normalReference = ContactNormalReference.MaterialPoint)
    : ICollisionGeometryReader<TGeometry> where TGeometry : struct, ICollisionGeometry<TGeometry>
{
    public static bool HasGeometry(World world, Entity entity) => world.Has<TGeometry>(entity);
    public QueryDescription AddReads(QueryDescription definition) => definition.Optional<TGeometry>().Optional<BodyPose>();
    private static Query SourceQuery(World world) => world.Query(new QueryDescription().With<TGeometry>());
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses)
    {
        if (TGeometry.HasResources) accesses.AddRange(((WorldJobs)world.Execution!).CollectAccess<ReadSource>(SourceQuery(world)));
    }
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        var jobs = (WorldJobs)world.Execution!;
        accesses.AddRange(jobs.StructureRead);
        if (TGeometry.HasResources) accesses.AddRange(jobs.CollectReadAccess<TGeometry, GeometryResources>(SourceQuery(world)));
        else accesses.Add(jobs.Read<TGeometry>());
        accesses.Add(jobs.Read<BodyPose>());
    }
    public TGeometry ReadGeometry(World world, Entity entity, out BodyPose pose)
    { pose = world.Has<BodyPose>(entity) ? world.Read<BodyPose>(entity) : default; return world.Read<TGeometry>(entity); }
    public int GetBodyCount(World world, Entity entity) => 1;
    public void GetBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies) =>
        bodies[0] = new(entity, GeometrySpace.ToLocal(in world.Read<BodyPose>(entity), point), 1, normalReference);
    private readonly struct GeometryResources : IJobComponentResources<TGeometry>
    {
        public static void Collect(in TGeometry component, ref JobResourceAccessBuilder resources) =>
            TGeometry.CollectResources(in component, ref resources);
    }
    private readonly struct ReadSource : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<TGeometry>(hasResources: true)];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void Execute(SystemChunk rows) { }
    }
}
