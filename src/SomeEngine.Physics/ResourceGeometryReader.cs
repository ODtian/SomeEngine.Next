using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>The component geometry reader with the project's concrete referenced-data description.</summary>
public readonly struct ResourceGeometryReader<TGeometry, TResources> : ICollisionGeometryReader<TGeometry>
    where TGeometry : struct, ICollisionGeometry<TGeometry> where TResources : struct, IJobComponentResources<TGeometry>
{
    public static bool HasGeometry(World world, Entity entity) => world.Has<TGeometry>(entity);
    public QueryDescription AddReads(QueryDescription reads) => reads.Optional<TGeometry>().Optional<BodyPose>();
    private static Query Sources(World world) => world.Query<TGeometry>();
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses) =>
        accesses.AddRange(((WorldJobs)world.Execution!).CollectAccess<ReadSource>(Sources(world)));
    public void AddAccesses(World world, List<JobResourceAccess> accesses)
    {
        var jobs = (WorldJobs)world.Execution!;
        accesses.AddRange(jobs.CollectReadAccess<TGeometry, TResources>(Sources(world)));
        accesses.Add(jobs.Read<BodyPose>());
    }
    public TGeometry ReadGeometry(World world, Entity entity, out BodyPose pose) =>
        new ComponentGeometry<TGeometry>().ReadGeometry(world, entity, out pose);
    public int GetBodyCount(World world, Entity entity) => 1;
    public void GetBodies(World world, Entity entity, Vector3 point, Span<ContactBody> bodies) =>
        new ComponentGeometry<TGeometry>().GetBodies(world, entity, point, bodies);
    private readonly struct ReadSource : IChunkAction
    {
        private static readonly ChunkAccess[] Accesses = [ChunkAccess.Read<TGeometry>(hasResources: true)];
        public static ReadOnlySpan<ChunkAccess> Components => Accesses;
        public void Execute(SystemChunk rows) { }
    }
}
