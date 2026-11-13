using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodyResults
{
    private sealed class ResultRows : IDisposable
    {
        internal readonly World World;
        internal readonly Query Members;
        internal readonly SystemRunner Runner;
        internal readonly PhysicsQueryWindow Window;
        internal ulong Baseline, ObservedVersion;
        internal ResultRows(World world, Query members, int rowsPerRange)
        {
            World = world; Members = members; Runner = new(world, rowsPerRange);
            Window = new(members);
        }
        public void Dispose() { }
    }

    private void ScheduleApply(ResultRows rows, Scheduler scheduler, JobHandle dependency)
    {
        JobHandle applied = rows.Runner.Schedule(rows.Members.Since(rows.Baseline), new ApplyResults(this, rows), dependency);
        scheduler.ScheduleFinally(new FinishResults(this, rows), default, applied);
    }

    private readonly struct ApplyResults(DeviceBodyResults results, ResultRows source) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<DeviceBodyBinding>(hasResources: true), ChunkAccess.Write<BodyPose>(),
                ChunkAccess.Write<BodyVelocity>(), ChunkAccess.Write<BodyStepState>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        {
            resources.Read(results.States);
            if (results._selection is { } selected) resources.Read(selected);
            source.ObservedVersion = source.World.Version;
        }
        public void CollectRangeResources(SystemChunk rows, ref JobResourceAccessBuilder resources)
        {
            ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
            int first = 0;
            while (first < bindings.Length)
            {
                DeviceBodyBinding binding = bindings[first];
                if (!ReferenceEquals(binding.Bodies, results.Bodies)) { first++; continue; }
                int end = first + 1;
                while (end < bindings.Length && ReferenceEquals(bindings[end].Bodies, binding.Bodies)
                    && bindings[end].Index == binding.Index + end - first) end++;
                binding.Bodies.CollectCpuResults(binding.Index, end - first, ref resources);
                first = end;
            }
        }
        public void Execute(SystemChunk rows)
        {
            ReadOnlySpan<DeviceBodyBinding> bindings = rows.Read<DeviceBodyBinding>();
            for (int row = 0; row < rows.Count; row++)
            {
                DeviceBodyBinding binding = bindings[row];
                var entity = rows.Entities[row];
                if (!ReferenceEquals(binding.Bodies, results.Bodies)
                    || !results.TryGetResultIndex(binding.Index, entity, out int ordinal)) continue;
                ref readonly DeviceBodyState state = ref results.States[ordinal];
                if (state.EntityIndex != (uint)entity.Id || state.EntityGeneration != (uint)entity.Generation) continue;
                DeviceBodies.BodyInputVersions accepted = results.Bodies.ReadCpuVersions(binding.Index);
                if (accepted.Entity != entity || accepted.InputVersion != state.InputVersion) continue;
                DeviceBodies.BodyInputComponents applied = 0;
                if (!rows.WasWritten<BodyPose>(row, accepted.Pose))
                {
                    ref BodyPose pose = ref rows.Write<BodyPose>(row);
                    pose.Position = state.Position; pose.Rotation = state.Rotation;
                    applied |= DeviceBodies.BodyInputComponents.Pose;
                }
                if (!rows.WasWritten<BodyVelocity>(row, accepted.Velocity))
                {
                    ref BodyVelocity velocity = ref rows.Write<BodyVelocity>(row);
                    velocity.Linear = state.LinearVelocity; velocity.Angular = state.AngularVelocity;
                    applied |= DeviceBodies.BodyInputComponents.Velocity;
                }
                if (!rows.WasWritten<BodyStepState>(row, accepted.Step))
                {
                    ref BodyStepState step = ref rows.Write<BodyStepState>(row);
                    step.StartCenterOfMass = state.StartCenterOfMass; step.StartRotation = state.StartRotation;
                    step.PredictedCenterOfMass = state.PredictedCenterOfMass; step.PredictedRotation = state.PredictedRotation;
                    step.Number = state.Number; step.Substep = state.Substep; step.Time = state.Time;
                    applied |= DeviceBodies.BodyInputComponents.Step;
                }
                results.Bodies.AcceptCpuResult(binding.Index, entity, source.World.Version, applied);
            }
        }
    }
    private readonly struct FinishResults(DeviceBodyResults results, ResultRows rows) : IJob
    {
        public void Execute()
        {
            rows.Window.Accept(rows.ObservedVersion);
            results._backend.CollectCompleted(results._device);
        }
    }
}
