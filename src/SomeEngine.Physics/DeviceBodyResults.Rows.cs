using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodyResults
{
    private sealed class ResultRows : IDisposable
    {
        internal readonly World World;
        internal readonly QueryHandle Members, Writes;
        internal readonly Entity[] Entities;
        internal readonly JobResourceAccess[] Accesses;
        internal readonly QueryReadResources Resources = new();
        internal int Count;
        internal ResultRows(World world, QueryDefinition members, int capacity)
        {
            World = world; Entities = new Entity[capacity];
            QueryDefinition definition = members.AsFilter().WithOptionalRead<DeviceBodyBinding>();
            Members = world.Query(definition);
            Writes = world.Query(definition.WithAccess(new QueryDefinitionBuilder()
                .ReadWrite<BodyPose>().ReadWrite<BodyVelocity>().ReadWrite<BodyStepState>().Build()));
            Resources.Add<DeviceBodyBinding, BodyResultResources>();
            Accesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
            QueryJobAccess.CopyAccesses(world, definition, Accesses, Resources);
        }
        public void Dispose() { World.ReleaseQuery(Members); World.ReleaseQuery(Writes); }
    }

    private readonly struct CaptureResults(DeviceBodyResults results, ResultRows rows, Scheduler scheduler, int rowsPerPacket) : IJob
    {
        public void Execute()
        {
            rows.Count = 0;
            var read = new CaptureRows(results, rows);
            rows.World.ExecuteReadQuery(rows.Members, ref read,
                static (scoped QueryCursor query, ref CaptureRows read) => read.Execute(query));
            int packetSize = rowsPerPacket == 0 ? Math.Max(1, rows.Count) : rowsPerPacket;
            var work = new ResultWork(rows, packetSize, rows.Count);
            JobResourceAccess[] accesses = [JobResourceAccess.Read(scheduler, results.States),
                JobResourceAccess.Read(scheduler.GetResourceToken(rows))];
            JobResourceAccess[] reads = results._selection is { } selection
                ? [.. accesses, JobResourceAccess.Read(scheduler.GetResourceToken(selection))] : accesses;
            JobHandle applied = EntityQueryJobs.Schedule(rows.World, in work, new ApplyResults(results), reads, rows.Resources);
            scheduler.ScheduleFinally(new Collect(results), default, applied);
        }
    }
    private readonly struct BodyResultResources : IJobComponentResources<DeviceBodyBinding>
    {
        public static void Collect(in DeviceBodyBinding binding, ref JobResourceAccessBuilder resources) =>
            binding.Bodies.CollectCpuResult(binding.Index, ref resources);
    }
    private readonly record struct CaptureRows(DeviceBodyResults Results, ResultRows Rows)
    {
        internal void Execute(scoped QueryCursor query)
        {
            foreach (QueryRow row in query.Rows)
            {
                if (!row.TryRead<DeviceBodyBinding>(out var binding) || !ReferenceEquals(binding.Bodies, Results.Bodies)) continue;
                if (!Results.TryGetResultIndex(binding.Index, row.Entity, out int ordinal)) continue;
                ref readonly DeviceBodyState state = ref Results.States[ordinal];
                if (state.EntityIndex == (uint)row.Entity.Index && state.EntityGeneration == (uint)row.Entity.Generation)
                    Rows.Entities[Rows.Count++] = row.Entity;
            }
        }
    }
    private readonly struct ResultWork(ResultRows rows, int rowsPerPacket, int count) : IEntityQueryWork
    {
        public int Count => (count + rowsPerPacket - 1) / rowsPerPacket;
        public int QueryCount => 1;
        public QueryHandle GetQuery(int workIndex, int queryIndex) => rows.Writes;
        public ReadOnlySpan<Entity> GetEntities(int workIndex, int queryIndex)
        {
            int first = workIndex * rowsPerPacket;
            return rows.Entities.AsSpan(first, Math.Min(rowsPerPacket, count - first));
        }
    }
    private readonly struct ApplyResults(DeviceBodyResults results) : IEntityQueryJob
    {
        public void Execute(int index, scoped EntityQueryViews queries)
        {
            foreach (QueryRow row in queries.Get(0).Rows)
            {
                var binding = row.Read<DeviceBodyBinding>();
                if (!ReferenceEquals(binding.Bodies, results.Bodies)) continue;
                if (!results.TryGetResultIndex(binding.Index, row.Entity, out int ordinal)) continue;
                ref readonly DeviceBodyState state = ref results.States[ordinal];
                if (state.EntityIndex != (uint)row.Entity.Index || state.EntityGeneration != (uint)row.Entity.Generation) continue;
                ref BodyPose pose = ref row.ReadWrite<BodyPose>();
                ref BodyVelocity velocity = ref row.ReadWrite<BodyVelocity>();
                ref BodyStepState step = ref row.ReadWrite<BodyStepState>();
                pose.Position = state.Position; pose.Rotation = state.Rotation;
                velocity.Linear = state.LinearVelocity; velocity.Angular = state.AngularVelocity;
                step.StartCenterOfMass = state.StartCenterOfMass; step.StartRotation = state.StartRotation;
                step.PredictedCenterOfMass = state.PredictedCenterOfMass; step.PredictedRotation = state.PredictedRotation;
                step.Number = state.Number; step.Substep = state.Substep; step.Time = state.Time;
                results.Bodies.AcceptCpuResult(binding.Index, row);
            }
        }
    }
}
