namespace SomeEngine.Job;

/// <summary>
/// Minimal thread-local sentinel shared with integrations that must fail closed inside arbitrary
/// Job callbacks. It deliberately lives in the contracts assembly so checking the sentinel does
/// not couple an integration to, initialize, or create worker threads for the Job scheduler.
/// </summary>
internal static class JobExecutionContext
{
    [ThreadStatic]
    private static int s_depth;

    internal static bool IsActive => s_depth != 0;

    internal static void Enter()
    {
        s_depth = checked(s_depth + 1);
    }

    internal static void Exit()
    {
        if (s_depth <= 0)
            throw new InvalidOperationException("Job execution context is unbalanced.");
        s_depth--;
    }
}
