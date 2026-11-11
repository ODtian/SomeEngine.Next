using System.Diagnostics;

namespace SomeEngine.Core.Diagnostics;

/// <summary>
/// Checks engine implementation invariants in development builds without retaining
/// the call or its argument evaluation in shipping callers.
/// </summary>
public static class DevelopmentCheck
{
    private static volatile bool s_enabled = true;

    public static bool Enabled
    {
        get => s_enabled;
        set => s_enabled = value;
    }

    [Conditional("DEBUG")]
    public static void Require(
        bool condition,
        string message)
    {
        if (s_enabled && !condition)
            throw new InvalidOperationException(message);
    }

    [Conditional("DEBUG")]
    public static void Fail(string message)
    {
        if (s_enabled) throw new InvalidOperationException(message);
    }

    /// <summary>Reports a recovered failure through the application's trace listeners.</summary>
    [Conditional("DEBUG")]
    public static void Report(string message)
    {
        if (s_enabled) Trace.TraceError(message);
    }
}
