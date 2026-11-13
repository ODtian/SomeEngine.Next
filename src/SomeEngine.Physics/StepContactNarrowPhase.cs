using SomeEngine.ECS;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public interface IPhysicsCollisionPairDetection
{
    bool SupportsParallel => false;
    void BeginParallel(int partitions) { }
    void SelectPartition(int partition) { }
    QueryDescription AddReads(QueryDescription description);
    void AddInputAccesses(World world, List<JobResourceAccess> accesses) { }
    void AddAccesses(World world, Query query, List<JobResourceAccess> accesses) => PhysicsQueryAccess.AddAccesses(world, query, accesses);
    void Execute(in CollisionGeometryPair pair, World world, in PhysicsStepContext context);
}
public static class StepContactNarrowPhase
{
    public static StepContactNarrowPhase<TDetection> Create<TDetection>(int bin, TDetection detection)
        where TDetection : struct, IPhysicsCollisionPairDetection => new(bin, detection);
}
public readonly struct StepContactNarrowPhase<TDetection>(int bin, TDetection detection) : IContactNarrowPhases where TDetection : struct, IPhysicsCollisionPairDetection
{
    public QueryDescription AddReads(QueryDescription description) => detection.AddReads(description);
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => detection.AddInputAccesses(world, accesses);
    public void AddAccesses(World world, List<JobResourceAccess> accesses) => detection.AddAccesses(world, world.Query(detection.AddReads(new QueryDescription())), accesses);
    public void Detect<TPartition>(in PhysicsStepContext context, CollisionPairBins<TPartition> bins) where TPartition : struct =>
        CollisionPairQueries.DetectBinWithQuery(context.World, bins, bin, new StepDetection(detection, context));
    private struct StepDetection(TDetection detection, PhysicsStepContext context) : ICollisionPairQueryDetection
    {
        public bool SupportsParallel => detection.SupportsParallel;
        public void BeginParallel(int partitions) => detection.BeginParallel(partitions);
        public void SelectPartition(int partition) => detection.SelectPartition(partition);
        public QueryDescription AddReads(QueryDescription description) => detection.AddReads(description);
        public void Execute(in CollisionGeometryPair pair, World world) => detection.Execute(in pair, world, in context);
    }
}
