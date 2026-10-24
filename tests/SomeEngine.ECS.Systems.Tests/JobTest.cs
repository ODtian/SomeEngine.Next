using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public abstract class JobTest : IDisposable
{
    private readonly List<Scheduler> _schedulers = [];
    private readonly List<World> _worlds = [];
    private Scheduler? _jobs;

    protected Scheduler Jobs
    {
        get
        {
            if (_jobs is null)
                ConfigureJobs(new JobRuntimeConfig
                {
                    WorkerCount = 4,
                    SafetyMode = JobSafetyMode.Checked,
                    ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
                });
            return _jobs!;
        }
    }

    protected void ConfigureJobs(JobRuntimeConfig config)
    {
        _jobs = new Scheduler(config);
        _schedulers.Add(_jobs);
    }

    protected World CreateJobWorld(int initialEntityCapacity = 256, Scheduler? scheduler = null)
    {
        var world = WorldJobs.CreateWorld(initialEntityCapacity, scheduler ?? Jobs);
        _worlds.Add(world);
        return world;
    }

    public void Dispose()
    {
        foreach (var world in _worlds)
            world.Dispose();
        foreach (var scheduler in _schedulers)
            scheduler.Dispose();
    }
}
