namespace SomeEngine.Job;

public interface IJobChunkSource
{
    int ChunkCount { get; }

    JobChunk GetChunk(int index);
}

