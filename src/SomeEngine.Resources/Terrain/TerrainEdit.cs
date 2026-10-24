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

    public ValueTask WaitAsync() => Completion.WaitAsync();
}
