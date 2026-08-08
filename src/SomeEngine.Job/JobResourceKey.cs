namespace SomeEngine.Job;

/// <summary>
/// Stable logical resource identity whose current runtime token is resolved whenever an access is
/// declared. Unlike <see cref="JobResource"/>, a key remains usable after JobSystem.Initialize.
/// </summary>
internal sealed class JobResourceKey
{
    internal JobResourceKey(IJobSubmissionObserver? submissionObserver = null)
    {
        SubmissionObserver = submissionObserver;
    }

    internal IJobSubmissionObserver? SubmissionObserver { get; }
}
