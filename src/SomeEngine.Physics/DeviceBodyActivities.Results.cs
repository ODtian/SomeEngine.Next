using System.Runtime.InteropServices;
using SomeEngine.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public sealed partial class DeviceBodyActivities
{
    private static readonly TraceSite NativeWritesTrace = new("physics", "Activity.NativeWrites");
    private readonly QueryHandle _resultReads;
    private List<ActivityChange>[] _resultChanges = [];
    private bool[] _resultSourceChanges = [];
    private readonly List<Entity> _writeTargets = [];
    private readonly List<int> _writeIndices = [];
    private int _resultPacketCount;

    internal unsafe void ApplyResults(in PhysicsStepContext context, PhysicsEventBuffer events)
    {
        try
        {
            var read = new ReadResults(this);
            World.ExecuteReadQuery(_resultReads, ref read,
                static (scoped QueryCursor rows, ref ReadResults read) => read.Execute(rows));
            EngineTrace.Counter(NativeWritesTrace, _writeTargets.Count);
            if (_writeTargets.Count != 0)
            {
                var write = new WriteResults(this);
                World.ExecuteComponentWrite<BodyActivity, WriteResults>(ref write, &WriteResults.Execute);
                if (_sourceVersionFilters == QueryTermFilter.None
                    && !_resultSourceChanges.AsSpan(0, _resultPacketCount).Contains(true)
                    && _membershipVersion == World.MembershipVersion(SourceRows)
                    && _activityMembershipVersion == World.MembershipVersion(_activityMembers))
                    _lastSystemVersion = write.Version;
            }
            for (int packet = 0; packet < _resultPacketCount; packet++)
                foreach (ActivityChange change in _resultChanges[packet])
                {
                    ref readonly DeviceBodyActivityState result = ref Results[change.BodyIndex];
                    if ((result.Flags & 16) != 0) events.Add(new PhysicsEvent { Kind = PhysicsEventKind.Wake,
                        Subject = change.Entity, Substep = context.Substep,
                        Time = (result.Flags & 64) != 0 ? context.EndTime : context.StartTime });
                    if ((result.Flags & 32) != 0) events.Add(new PhysicsEvent { Kind = PhysicsEventKind.Sleep,
                        Subject = change.Entity, Substep = context.Substep, Time = context.EndTime });
                }
        }
        finally
        {
            for (int packet = 0; packet < _resultPacketCount; packet++) _resultChanges[packet].Clear();
            _resultPacketCount = 0;
            _writeTargets.Clear(); _writeIndices.Clear();
        }
    }

    private readonly record struct ActivityChange(Entity Entity, int BodyIndex, bool ActivityChanged);
    private readonly record struct ReadResults(DeviceBodyActivities Activities)
    {
        internal void Execute(scoped QueryCursor rows)
        {
            using ReadOnlyQueryPacketPlan packets = ReadOnlyQueryPacketJobs.CreatePlan(rows);
            if (Activities._resultChanges.Length < packets.PacketCount)
            {
                int previous = Activities._resultChanges.Length;
                Array.Resize(ref Activities._resultChanges, Math.Max(packets.PacketCount, previous * 2));
                Array.Resize(ref Activities._resultSourceChanges, Activities._resultChanges.Length);
                for (int packet = previous; packet < Activities._resultChanges.Length; packet++)
                    Activities._resultChanges[packet] = [];
            }
            Activities._resultPacketCount = packets.PacketCount;
            Scheduler scheduler = WorldJobs.GetScheduler(Activities.World);
            JobResourceAccess[] outputs = [JobResourceAccess.Write(scheduler.GetResourceToken(Activities))];
            packets.ExecuteParallel(new CompareResults(Activities), outputs);
            for (int packet = 0; packet < Activities._resultPacketCount; packet++)
                foreach (ActivityChange change in Activities._resultChanges[packet])
                    if (change.ActivityChanged)
                    {
                        Activities._writeTargets.Add(change.Entity);
                        Activities._writeIndices.Add(change.BodyIndex);
                    }
            if (Activities._writeTargets.Count != 0 && Activities._sourceVersionFilters == QueryTermFilter.None)
                packets.ExecuteParallel(new ReadSourceChanges(Activities), outputs);
        }
    }
    private readonly struct CompareResults(DeviceBodyActivities activities) : IReadOnlyQueryPacketJob
    {
        public void Execute(in ReadOnlyQueryPacketContext context, ReadOnlyQueryPacket packet)
        {
            if (!packet.TryRead<BodyActivity>(out var current) || !packet.TryRead<DeviceBodyActivityBinding>(out var bindings)) return;
            ReadOnlySpan<DeviceBodyBinding> bodies = packet.Read<DeviceBodyBinding>();
            ReadOnlySpan<Entity> entities = packet.Entities;
            List<ActivityChange> changes = activities._resultChanges[context.PacketIndex];
            for (int index = 0; index < packet.Count; index++)
            {
                ref readonly DeviceBodyBinding body = ref bodies[index];
                if (!ReferenceEquals(body.Bodies, activities.Bodies) || !ReferenceEquals(bindings[index].Activities, activities)) continue;
                Entity entity = entities[index];
                ref readonly DeviceBodyActivityState result = ref activities.Results[body.Index];
                if (result.EntityIndex != (uint)entity.Index || result.EntityGeneration != (uint)entity.Generation) continue;
                bool sleeping = (result.Flags & 4) != 0, motionPredicted = (result.Flags & 8) != 0;
                bool changed = current[index].Sleeping != sleeping || current[index].QuietSeconds != result.QuietSeconds
                    || current[index].Radius != result.Radius || current[index].MotionPredicted != motionPredicted;
                if (changed)
                {
                    CpuVersion version = activities._versions[body.Index];
                    changed = version.Entity == entity && version.Activity == activities.World.WriteVersion<BodyActivity>(entity);
                }
                if (changed || (result.Flags & 48) != 0) changes.Add(new(entity, body.Index, changed));
            }
        }
    }
    private readonly struct ReadSourceChanges(DeviceBodyActivities activities) : IReadOnlyQueryPacketJob
    {
        public void Execute(in ReadOnlyQueryPacketContext context, ReadOnlyQueryPacket packet) =>
            activities._resultSourceChanges[context.PacketIndex] =
                packet.ChangedSince<DeviceBodyBinding>(activities._lastSystemVersion)
                || packet.Has<DeviceBodyActivityBinding>() && packet.ChangedSince<DeviceBodyActivityBinding>(activities._lastSystemVersion)
                || packet.Has<BodyActivity>() && packet.ChangedSince<BodyActivity>(activities._lastSystemVersion);
    }
    private struct WriteResults(DeviceBodyActivities activities)
    {
        private readonly DeviceBodyActivities _activities = activities;
        internal uint Version;

        internal static void Execute(ComponentWriteBatch<BodyActivity> rows, ref WriteResults write)
        {
            write.Version = rows.CurrentSystemVersion;
            var job = new WriteActivities(write._activities, rows.CurrentSystemVersion);
            ComponentWriteJobs.ExecuteParallel<BodyActivity, WriteActivities, ActivityResources>(
                rows, CollectionsMarshal.AsSpan(write._activities._writeTargets), in job);
        }
    }
    private readonly struct WriteActivities(DeviceBodyActivities activities, uint version) : IComponentWriteJob<BodyActivity>
    {
        public void Execute(int index, ref BodyActivity activity)
        {
            int body = activities._writeIndices[index];
            ref readonly DeviceBodyActivityState result = ref activities.Results[body];
            activity.Sleeping = (result.Flags & 4) != 0; activity.QuietSeconds = result.QuietSeconds;
            activity.Radius = result.Radius; activity.MotionPredicted = (result.Flags & 8) != 0;
            activities._versions[body] = new(activities._writeTargets[index], version);
        }
    }
    private readonly struct ActivityResources : IJobComponentResources<BodyActivity>
    {
        public static void Collect(in BodyActivity component, ref JobResourceAccessBuilder resources) { }
    }
}
