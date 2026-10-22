using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>A submitted field revision and the job that completes its samples.</summary>
public readonly struct TerrainEdit
{
    internal TerrainEdit(ulong revision, JobHandle completion)
    {
        Revision = revision;
        Completion = completion;
    }

    public ulong Revision { get; }
    public JobHandle Completion { get; }

    public ValueTask WaitAsync()
    {
        if (Completion.IsCompleted)
        {
            Completion.Complete();
            return ValueTask.CompletedTask;
        }

        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        JobSystem.OnCompleted(Completion, static (handle, state) =>
        {
            var result = (TaskCompletionSource)state!;
            try
            {
                handle.Complete();
                result.SetResult();
            }
            catch (Exception error)
            {
                result.SetException(error);
            }
        }, result);
        return new ValueTask(result.Task);
    }
}
