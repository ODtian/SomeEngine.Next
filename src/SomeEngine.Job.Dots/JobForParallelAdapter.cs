namespace SomeEngine.Job;

internal readonly struct JobForParallelAdapter<T> : IJobParallelFor
    where T : struct, IJobFor
{
    private readonly T _job;

    internal JobForParallelAdapter(in T job)
    {
        _job = job;
    }

    public void Execute(int index)
    {
        _job.Execute(index);
    }
}

