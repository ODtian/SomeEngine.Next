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
        internal readonly BodyResultTarget[] Targets;
        internal readonly JobResourceAccess[] Accesses, WriteAccesses;
        internal List<BodyResultTarget>[] PacketTargets = [];
        internal int Count;

        internal ResultRows(World world, QueryDefinition members, int capacity)
        {
            World = world; Targets = new BodyResultTarget[capacity];
            QueryDefinition definition = members.AsFilter().WithOptionalRead<DeviceBodyBinding>();
            QueryDefinition writes = definition.WithAccess(new QueryDefinitionBuilder()
                .ReadWrite<BodyPose>().ReadWrite<BodyVelocity>().ReadWrite<BodyStepState>().Build());
            QueryDefinition reads = writes.AsFilter().WithOptionalRead<DeviceBodyBinding>();
            Members = world.Query(reads);
            Writes = world.Query(writes);
            var resources = new QueryReadResources();
            resources.Add<DeviceBodyBinding, BodyResultResources>();
            Accesses = new JobResourceAccess[QueryJobAccess.Count(reads)];
            QueryJobAccess.CopyAccesses(world, reads, Accesses, resources);
            WriteAccesses = new JobResourceAccess[QueryJobAccess.Count(writes)];
            QueryJobAccess.CopyAccesses(world, writes, WriteAccesses, resources);
        }

        public void Dispose() { World.ReleaseQuery(Members); World.ReleaseQuery(Writes); }
    }

    private readonly record struct BodyResultTarget(Entity Entity, int Body, int Result) : IComparable<BodyResultTarget>
    {
        public int CompareTo(BodyResultTarget other) => Body.CompareTo(other.Body);
    }

    private readonly struct CaptureResults(DeviceBodyResults results, ResultRows rows, Scheduler scheduler, int rowsPerPacket) : IJob
    {
        public void Execute()
        {
            rows.Count = 0;
            var read = new CaptureRows(results, rows, rowsPerPacket);
            rows.World.ExecuteReadQuery(rows.Members, ref read,
                static (scoped QueryCursor query, ref CaptureRows read) => read.Execute(query));
            JobHandle applied = default;
            if (rows.Count != 0)
            {
                var accesses = new List<JobResourceAccess>(rows.WriteAccesses)
                {
                    JobResourceAccess.Read(scheduler, results.States),
                    JobResourceAccess.Read(scheduler.GetResourceToken(rows)),
                };
                var resources = new JobResourceAccessBuilder(scheduler, accesses);
                int index = 0;
                while (index < rows.Count)
                {
                    int first = rows.Targets[index++].Body, last = first;
                    while (index < rows.Count && rows.Targets[index].Body == last + 1)
                        last = rows.Targets[index++].Body;
                    results.Bodies.CollectCpuResults(first, last - first + 1, ref resources);
                }
                applied = scheduler.Schedule(new ApplyResults(results, rows), accesses.ToArray());
            }
            scheduler.ScheduleFinally(new Collect(results), default, applied);
        }
    }

    private readonly struct BodyResultResources : IJobComponentResources<DeviceBodyBinding>
    {
        public static void Collect(in DeviceBodyBinding binding, ref JobResourceAccessBuilder resources) =>
            binding.Bodies.CollectCpuResult(binding.Index, ref resources);
    }

    private readonly record struct CaptureRows(DeviceBodyResults Results, ResultRows Rows, int RowsPerPacket)
    {
        internal void Execute(scoped QueryCursor query)
        {
            using ReadOnlyQueryPacketPlan packets = ReadOnlyQueryPacketJobs.CreatePlan(query, RowsPerPacket);
            if (Rows.PacketTargets.Length < packets.PacketCount)
            {
                int previous = Rows.PacketTargets.Length;
                Array.Resize(ref Rows.PacketTargets, Math.Max(packets.PacketCount, previous * 2));
                for (int packet = previous; packet < Rows.PacketTargets.Length; packet++) Rows.PacketTargets[packet] = [];
            }
            Scheduler scheduler = WorldJobs.GetScheduler(Rows.World);
            JobResourceAccess[] outputs = [JobResourceAccess.Write(scheduler.GetResourceToken(Rows))];
            packets.ExecuteParallel(new CaptureTargets(Results, Rows), outputs);
            for (int packet = 0; packet < packets.PacketCount; packet++)
            {
                List<BodyResultTarget> targets = Rows.PacketTargets[packet];
                targets.CopyTo(Rows.Targets, Rows.Count);
                Rows.Count += targets.Count;
                targets.Clear();
            }
            Array.Sort(Rows.Targets, 0, Rows.Count);
        }
    }

    private readonly struct CaptureTargets(DeviceBodyResults results, ResultRows rows) : IReadOnlyQueryPacketJob
    {
        public void Execute(in ReadOnlyQueryPacketContext context, ReadOnlyQueryPacket packet)
        {
            List<BodyResultTarget> targets = rows.PacketTargets[context.PacketIndex];
            targets.Clear();
            if (!packet.TryRead<DeviceBodyBinding>(out var bindings)) return;
            ReadOnlySpan<Entity> entities = packet.Entities;
            for (int index = 0; index < packet.Count; index++)
            {
                ref readonly DeviceBodyBinding binding = ref bindings[index];
                Entity entity = entities[index];
                if (!ReferenceEquals(binding.Bodies, results.Bodies)
                    || !results.TryGetResultIndex(binding.Index, entity, out int ordinal)) continue;
                ref readonly DeviceBodyState state = ref results.States[ordinal];
                if (state.EntityIndex == (uint)entity.Index && state.EntityGeneration == (uint)entity.Generation)
                    targets.Add(new(entity, binding.Index, ordinal));
            }
        }
    }

    private readonly struct ApplyResults(DeviceBodyResults results, ResultRows rows) : IJob
    {
        public void Execute()
        {
            var writes = new ResultWrites(results, rows);
            rows.World.ExecuteQuery(rows.Writes, ref writes);
        }
    }

    private readonly struct ResultWrites(DeviceBodyResults results, ResultRows rows) : IQueryExecution
    {
        public void Execute(scoped QueryCursor query)
        {
            foreach (QueryChunkView chunk in query.Chunks)
            {
                if (!chunk.Has<DeviceBodyBinding>()) continue;
                ComponentWriteColumn<BodyPose> poses = chunk.ReadWriteColumn<BodyPose>();
                ComponentWriteColumn<BodyVelocity> velocities = chunk.ReadWriteColumn<BodyVelocity>();
                ComponentWriteColumn<BodyStepState> steps = chunk.ReadWriteColumn<BodyStepState>();
                foreach (int index in chunk.RowIndices)
                {
                    var binding = chunk.Read<DeviceBodyBinding>(index);
                    if (!ReferenceEquals(binding.Bodies, results.Bodies)) continue;
                    Entity entity = chunk.GetEntity(index);
                    int targetIndex = Array.BinarySearch(rows.Targets, 0, rows.Count, new(entity, binding.Index, 0));
                    if (targetIndex < 0 || rows.Targets[targetIndex].Entity != entity) continue;
                    ref readonly BodyResultTarget target = ref rows.Targets[targetIndex];
                    ref readonly DeviceBodyState state = ref results.States[target.Result];
                    ref BodyPose pose = ref poses[index];
                    ref BodyVelocity velocity = ref velocities[index];
                    ref BodyStepState step = ref steps[index];
                    pose.Position = state.Position; pose.Rotation = state.Rotation;
                    velocity.Linear = state.LinearVelocity; velocity.Angular = state.AngularVelocity;
                    step.StartCenterOfMass = state.StartCenterOfMass; step.StartRotation = state.StartRotation;
                    step.PredictedCenterOfMass = state.PredictedCenterOfMass; step.PredictedRotation = state.PredictedRotation;
                    step.Number = state.Number; step.Substep = state.Substep; step.Time = state.Time;
                    results.Bodies.AcceptCpuResult(target.Body, entity, chunk.CurrentSystemVersion);
                }
            }
        }
    }
}
