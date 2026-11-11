using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal abstract partial class ComparisonWorld
{
    internal Scheduler? JobScheduler;
    internal IQueryJobs? QueryJobs;
    private bool _scheduledJobs, _parallelJobs, _dependentJobs;
    private int _randomJobMode;
    internal abstract void PrepareQueryJobs(int randomMode);
    internal abstract void PrepareNativeQueryJobs();
    internal abstract int ReadJobExtra(int index);

    internal Scheduler GetJobScheduler() => JobScheduler ??= new(new JobConfiguration
    {
        WorkerCount = 3, EnableCounters = false, MaxQueuedWorkItems = 512,
        MaxCompletionStates = 512, MaxResourceStates = 64
    });

    internal void PrepareQueryJobsComparison(string workload)
    {
        _scheduledJobs = workload.StartsWith("job-schedule");
        _parallelJobs = !workload.StartsWith("job-execute");
        _dependentJobs = workload.Contains("-dependent");
        _randomJobMode = workload == "job-system-group" ? 3
            : workload.EndsWith("-random-read") ? 1 : workload.EndsWith("-random-write") ? 2 : 0;
        if (QueryJobs is null)
        {
            if (workload == "job-native-parallel") PrepareNativeQueryJobs();
            else PrepareQueryJobs(_randomJobMode);
        }
    }
    internal void QueryJobsCycle() => QueryJobs!.Run(_scheduledJobs, _parallelJobs, _dependentJobs);
    internal bool CheckQueryJobs(int runs = 1)
    {
        int steps = runs * (_dependentJobs ? 2 : 1);
        if (_randomJobMode == 3) steps = ((ISystemGroupJobs)QueryJobs!).Runs * 2;
        for (int i = 0; i < Count; i++)
        {
            Position position = ReadPosition(i);
            int expectedX = _randomJobMode == 3 && i < 125 ? i + 4 : i + steps * (_randomJobMode == 1 ? 2 : 1);
            int expectedY = _randomJobMode == 3 && i < 125 ? 2 : steps * 2;
            if (position.X != expectedX || position.Y != expectedY) return false;
        }
        if (_randomJobMode == 2)
        {
            var expected = new int[Count];
            for (int i = 0; i < Count; i++) expected[Pick(i)] += steps;
            for (int i = 0; i < Count; i++) if (ReadJobExtra(i) != expected[i]) return false;
        }
        if (_randomJobMode == 3 && !((ISystemGroupJobs)QueryJobs!).Check()) return false;
        return true;
    }
}

internal sealed partial class SomeEngineWorld
{
    internal override void PrepareQueryJobs(int randomMode)
    {
        if (randomMode == 3) { PrepareSystemGroupJobs(); return; }
        if (randomMode != 0) { PrepareRandomQueryJobs(randomMode); return; }
        QueryJobs = new SourceQueryJobs(new SystemRunner(_world, GetJobScheduler()), _world.Query<Position, Velocity>());
    }
    private sealed class SourceQueryJobs(SystemRunner runner, Query query) : IQueryJobs
    {
        public void Run(bool scheduled, bool parallel, bool dependent)
        {
            var action = new MovePositions();
            if (scheduled)
            {
                JobHandle first = runner.Schedule<Position, Velocity, MovePositions>(query, action, parallel: parallel);
                (dependent ? runner.Schedule<Position, Velocity, MovePositions>(query, action, dependency: first, parallel: parallel) : first).Complete();
            }
            else if (parallel) runner.ExecuteParallel<Position, Velocity, MovePositions>(query, action);
            else runner.Execute<Position, Velocity, MovePositions>(query, ref action);
        }
        public void Dispose() { }
    }
}
internal sealed partial class EasyEcsWorld
{
    internal override void PrepareQueryJobs(int randomMode)
    {
        if (randomMode == 3) { PrepareSystemGroupJobs(); return; }
        if (randomMode != 0) { PrepareRandomQueryJobs(randomMode); return; }
        QueryJobs = new NativeQueryJobs<EasyMotionRange, EasyMotionQuery>(GetJobScheduler(), new(_world));
    }
}
internal sealed partial class FrifloWorld
{
    internal override void PrepareQueryJobs(int randomMode)
    {
        if (randomMode == 3) { PrepareSystemGroupJobs(); return; }
        if (randomMode != 0) { PrepareRandomQueryJobs(randomMode); return; }
        QueryJobs = new NativeQueryJobs<FrifloMotionRange, FrifloMotionQuery>(GetJobScheduler(), new(_world, _world.Query<Position, Velocity>()));
    }
}
internal sealed partial class ArchWorld
{
    internal override void PrepareQueryJobs(int randomMode)
    {
        if (randomMode == 3) { PrepareSystemGroupJobs(); return; }
        if (randomMode != 0) { PrepareRandomQueryJobs(randomMode); return; }
        QueryJobs = new NativeQueryJobs<ArchMotionRange, ArchMotionQuery>(GetJobScheduler(), new(_world, _two));
    }
}
internal sealed partial class DefaultEcsWorld
{
    internal override void PrepareQueryJobs(int randomMode)
    {
        if (randomMode == 3) { PrepareSystemGroupJobs(); return; }
        if (randomMode != 0) { PrepareRandomQueryJobs(randomMode); return; }
        QueryJobs = new NativeQueryJobs<DefaultMotionRange, DefaultMotionQuery>(GetJobScheduler(), new(_world));
    }
}
