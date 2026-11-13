using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public interface IContactNarrowPhases
{
    QueryDescription AddReads(QueryDescription description);
    void AddInputAccesses(World world, List<JobResourceAccess> accesses) { }
    void AddAccesses(World world, List<JobResourceAccess> accesses);
    void Detect<TPartition>(in PhysicsStepContext context, CollisionPairBins<TPartition> bins) where TPartition : struct;
}
public readonly struct ContactNarrowPhase<TDetection>(int bin, TDetection detection) : IContactNarrowPhases where TDetection : struct, ICollisionPairQueryDetection
{
    public QueryDescription AddReads(QueryDescription description) => detection.AddReads(description);
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses) => detection.AddInputAccesses(world, accesses);
    public void AddAccesses(World world, List<JobResourceAccess> accesses) => detection.AddAccesses(world, world.Query(detection.AddReads(new QueryDescription())), accesses);
    public void Detect<TPartition>(in PhysicsStepContext context, CollisionPairBins<TPartition> bins) where TPartition : struct =>
        CollisionPairQueries.DetectBinWithQuery(context.World, bins, bin, detection);
}
public static class ContactNarrowPhase
{
    public static ContactNarrowPhase<TDetection> Create<TDetection>(int bin, TDetection detection) where TDetection : struct, ICollisionPairQueryDetection => new(bin, detection);
}
public readonly struct ContactNarrowPhases<TFirst, TSecond>(TFirst first, TSecond second) : IContactNarrowPhases
    where TFirst : struct, IContactNarrowPhases where TSecond : struct, IContactNarrowPhases
{
    public QueryDescription AddReads(QueryDescription description) => second.AddReads(first.AddReads(description));
    public void AddInputAccesses(World world, List<JobResourceAccess> accesses) { first.AddInputAccesses(world, accesses); second.AddInputAccesses(world, accesses); }
    public void AddAccesses(World world, List<JobResourceAccess> accesses) { first.AddAccesses(world, accesses); second.AddAccesses(world, accesses); }
    public void Detect<TPartition>(in PhysicsStepContext context, CollisionPairBins<TPartition> bins) where TPartition : struct
    { first.Detect(in context, bins); second.Detect(in context, bins); }
}
public static class ContactNarrowPhases
{
    public static ContactNarrowPhases<TFirst, TSecond> Combine<TFirst, TSecond>(TFirst first, TSecond second)
        where TFirst : struct, IContactNarrowPhases where TSecond : struct, IContactNarrowPhases => new(first, second);
}
public static class DetectContactPoints
{
    public static DetectContactPoints<TEnumerable, TPartition, TClassifier, TNarrowPhases> Create<TEnumerable, TPartition, TClassifier, TNarrowPhases>(
        World world, RelationQuery<TEnumerable, (Entity, Entity)> pairs, CollisionPairBins<TPartition> bins,
        TClassifier classifier, TNarrowPhases narrowPhases, ContactPointBuffer output, ReadOnlySpan<JobResourceAccess> inputs)
        where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct
        where TClassifier : struct, ICollisionPairClassifier where TNarrowPhases : struct, IContactNarrowPhases => new(world, pairs, bins, classifier, narrowPhases, output, inputs);
}
/// <summary>Fills type bins once, then runs concrete kernels into one reusable contact output.</summary>
public readonly struct DetectContactPoints<TEnumerable, TPartition, TClassifier, TNarrowPhases> : IPhysicsStage
    where TEnumerable : struct, ISpatialPartitionEnumerable<TPartition> where TPartition : struct
    where TClassifier : struct, ICollisionPairClassifier where TNarrowPhases : struct, IContactNarrowPhases
{
    private readonly RelationQuery<TEnumerable, (Entity, Entity)> _pairs;
    private readonly CollisionPairBins<TPartition> _bins;
    private readonly TClassifier _classifier;
    private readonly TNarrowPhases _narrowPhases;
    private readonly ContactPointBuffer _output;
    private readonly Query _candidateReads;
    private readonly JobResourceAccess[] _inputs;
    public DetectContactPoints(World world, RelationQuery<TEnumerable, (Entity, Entity)> pairs, CollisionPairBins<TPartition> bins,
        TClassifier classifier, TNarrowPhases narrowPhases, ContactPointBuffer output, ReadOnlySpan<JobResourceAccess> inputs)
    {
        _pairs = pairs; _bins = bins; _classifier = classifier; _narrowPhases = narrowPhases; _output = output; _inputs = inputs.ToArray();
        QueryDescription reads = new();
        if (pairs.Enumerable.QueryCount == 0) reads = pairs.Enumerable.AddReads(reads, 0);
        for (int position = 0; position < pairs.Enumerable.QueryCount; position++) reads = pairs.Enumerable.AddReads(reads, position);
        _candidateReads = world.Query(classifier.AddReads(reads));
    }
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        (context.World.Execution!.Scheduler!).Schedule(new Capture(this, context), ((WorldJobs)context.World.Execution!).StructureRead, dependency: dependency);
    private readonly struct Capture(DetectContactPoints<TEnumerable, TPartition, TClassifier, TNarrowPhases> stage, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            var accesses = new List<JobResourceAccess>(); stage._narrowPhases.AddInputAccesses(context.World, accesses);
            if (accesses.Count == 0) ScheduleDetection(stage, in context);
            else
            {
                accesses.AddRange(((WorldJobs)context.World.Execution!).StructureRead);
                (context.World.Execution!.Scheduler!).Schedule(new CaptureInputs(stage, context), accesses.ToArray());
            }
        }
    }
    private readonly struct CaptureInputs(DetectContactPoints<TEnumerable, TPartition, TClassifier, TNarrowPhases> stage, PhysicsStepContext context) : IJob
    { public void Execute() => ScheduleDetection(stage, in context); }
    private static void ScheduleDetection(in DetectContactPoints<TEnumerable, TPartition, TClassifier, TNarrowPhases> stage, in PhysicsStepContext context)
    {
        var accesses = new List<JobResourceAccess>(stage._inputs);
        PhysicsQueryAccess.AddAccesses(context.World, stage._candidateReads, accesses);
        stage._narrowPhases.AddAccesses(context.World, accesses);
        accesses.Add(stage._bins.FillAccess); accesses.Add(stage._output.WriteAccess);
        (context.World.Execution!.Scheduler!).Schedule(new Detect(stage, context), accesses.ToArray());
    }
    private readonly struct Detect(DetectContactPoints<TEnumerable, TPartition, TClassifier, TNarrowPhases> stage, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            stage._output.Clear(); stage._pairs.FillPairBinsParallel(stage._bins, stage._classifier);
            stage._output.BeginParallelWrites();
            try { stage._narrowPhases.Detect(in context, stage._bins); }
            finally { stage._output.CompleteParallelWrites(); }
        }
    }
}
