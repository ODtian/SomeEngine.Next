using SomeEngine.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodyActivities
{
    private static readonly TraceSite NativeWritesTrace = new("physics", "Activity.NativeWrites");
    internal bool UseResultCandidates;
    internal readonly List<Entity> ResultCandidates = [];
    private readonly HashSet<Entity> _resultCandidates = [];

    internal JobHandle ScheduleApply(JobHandle dependency)
    {
        Scheduler scheduler = World.Execution!.Scheduler!;
        JobHandle selected = scheduler.Schedule(new SelectResults(this), [JobResourceAccess.Write(scheduler.GetResourceToken(this))], dependency);
        return _runner.Schedule(ResultRows.Since(_resultBaseline), new ApplyResults(this), selected, parallel: false);
    }
    internal static void AddEvents(in DeviceBodyActivityState result, in PhysicsStepContext context, PhysicsEventBuffer events)
    {
        if ((result.Flags & 16) != 0) events.Add(new PhysicsEvent { Kind = PhysicsEventKind.Wake,
            Subject = new((int)result.EntityIndex, (int)result.EntityGeneration), Substep = context.Substep,
            Time = (result.Flags & 64) != 0 ? context.EndTime : context.StartTime });
        if ((result.Flags & 32) != 0) events.Add(new PhysicsEvent { Kind = PhysicsEventKind.Sleep,
            Subject = new((int)result.EntityIndex, (int)result.EntityGeneration), Substep = context.Substep, Time = context.EndTime });
    }
    private readonly struct SelectResults(DeviceBodyActivities activities) : IJob
    {
        public void Execute()
        {
            activities._resultCandidates.Clear();
            if (activities.UseResultCandidates)
                foreach (Entity entity in activities.ResultCandidates) activities._resultCandidates.Add(entity);
        }
    }
    private readonly struct ApplyResults(DeviceBodyActivities activities) : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<DeviceBodyBinding>(hasResources: true),
                ChunkAccess.Read<DeviceBodyActivityBinding>(hasResources: true), ChunkAccess.Write<BodyActivity>()];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        {
            resources.Write(activities);
        }
        public void Execute(SystemChunk rows)
        {
            ReadOnlySpan<DeviceBodyBinding> bodies = rows.Read<DeviceBodyBinding>();
            ReadOnlySpan<DeviceBodyActivityBinding> bindings = rows.Read<DeviceBodyActivityBinding>();
            ReadOnlySpan<BodyActivity> values = rows.Read<BodyActivity>();
            int writes = 0;
            for (int row = 0; row < rows.Count; row++)
            {
                Entity entity = rows.Entities[row];
                if (activities.UseResultCandidates && !activities._resultCandidates.Contains(entity)) continue;
                DeviceBodyBinding body = bodies[row];
                if (!ReferenceEquals(body.Bodies, activities.Bodies) || !ReferenceEquals(bindings[row].Activities, activities)) continue;
                ref readonly DeviceBodyActivityState result = ref activities.Results[body.Index];
                if (result.EntityIndex != (uint)entity.Id || result.EntityGeneration != (uint)entity.Generation) continue;
                BodyActivity current = values[row];
                bool sleeping = (result.Flags & 4) != 0, motionPredicted = (result.Flags & 8) != 0;
                bool changed = current.Sleeping != sleeping || current.QuietSeconds != result.QuietSeconds
                    || current.Radius != result.Radius || current.MotionPredicted != motionPredicted;
                CpuVersion accepted = activities._versions[body.Index];
                changed &= accepted.Entity == entity && !rows.WasWritten<BodyActivity>(row, accepted.Activity);
                if (changed)
                {
                    ref BodyActivity activity = ref rows.Write<BodyActivity>(row);
                    activity.Sleeping = sleeping; activity.QuietSeconds = result.QuietSeconds;
                    activity.Radius = result.Radius; activity.MotionPredicted = motionPredicted;
                    activities._versions[body.Index] = new(entity, activities.World.Version); writes++;
                }

            }
            EngineTrace.Counter(NativeWritesTrace, writes);
        }
    }
}
