namespace SomeEngine.Job;

/// <summary>
/// Tracks the current thread's nested Job execution for engine access checks.
/// Sharing this state with resource declarations keeps integrations independent of scheduler initialization.
/// </summary>
internal static class JobThreads
{
    [ThreadStatic]
    private static int s_depth;

    internal static bool IsActive => s_depth != 0;
    internal static int Depth => s_depth;

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
