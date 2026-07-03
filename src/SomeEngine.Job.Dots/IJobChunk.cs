namespace SomeEngine.Job;

public interface IJobChunk
{
    void Execute(JobChunk chunk);
}

