namespace SomeEngine.Job;

internal readonly struct ChunkJobParallelAdapter<TJob, TSource> : IJobParallelFor
    where TJob : struct, IJobChunk
    where TSource : IJobChunkSource
{
    private readonly TJob _job;
    private readonly TSource _source;

    internal ChunkJobParallelAdapter(in TJob job, in TSource source)
    {
        _job = job;
        _source = source;
    }

    public void Execute(int index)
    {
        _job.Execute(_source.GetChunk(index));
    }
}

