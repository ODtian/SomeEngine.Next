using System.Diagnostics;

namespace SomeEngine.Core.Diagnostics;

/// <summary>
/// Checks engine implementation invariants in Debug builds. Release callers keep neither the call
/// nor its argument evaluation; there is no separate runtime switch.
/// </summary>
public static class DevelopmentCheck
{
    [Conditional("DEBUG")]
    public static void Require(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [Conditional("DEBUG")]
    public static void Fail(string message) => throw new InvalidOperationException(message);

    /// <summary>Reports a recovered failure through the application's trace listeners.</summary>
    [Conditional("DEBUG")]
    public static void Report(string message) => Trace.TraceError(message);
}
