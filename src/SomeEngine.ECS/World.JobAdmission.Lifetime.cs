namespace SomeEngine.ECS;

public partial class World
{
    private void ThrowIfCurrentThreadHasWorldAdmission()
    {
        Dictionary<World, int>? admissions = s_unboundJobAdmissions;
        if (admissions is not null &&
            admissions.TryGetValue(this, out int depth) &&
            depth > 0)
        {
            throw new InvalidOperationException(
                "World cannot be disposed from inside one of its active callbacks.");
        }

        IWorldJobAdmission? admission = Volatile.Read(ref _jobAdmission);
        if (admission?.HasCurrentThreadScope(this) == true)
        {
            throw new InvalidOperationException(
                "World cannot be disposed from inside one of its active callbacks.");
        }
    }

    private void WaitForUnboundJobAdmissionsToDrain()
    {
        lock (_jobAdmissionBindingGate)
        {
            while (_unboundJobAdmissionCount != 0)
                Monitor.Wait(_jobAdmissionBindingGate);
        }
    }
}
