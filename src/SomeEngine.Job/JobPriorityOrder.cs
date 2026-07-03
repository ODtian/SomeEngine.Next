namespace SomeEngine.Job;

internal static class JobPriorityOrder
{
    internal const int HighIndex = 0;
    internal const int NormalIndex = 1;
    internal const int LowIndex = 2;
    internal const int Count = 3;
    internal const string InvalidPriorityMessage = "Job priority is invalid.";

    internal static int PriorityIndex(JobPriority priority)
    {
        return priority switch
        {
            JobPriority.High => HighIndex,
            JobPriority.Normal => NormalIndex,
            JobPriority.Low => LowIndex,
            _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, InvalidPriorityMessage)
        };
    }
}

