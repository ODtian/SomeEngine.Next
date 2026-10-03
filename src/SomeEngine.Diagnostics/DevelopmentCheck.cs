using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.Core.Diagnostics;

/// <summary>
/// Checks engine implementation invariants in development builds without retaining
/// the call or its argument evaluation in shipping callers.
/// </summary>
public static class DevelopmentCheck
{
    [Conditional("DEBUG")]
    public static void Require(
        [DoesNotReturnIf(false)] bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    [Conditional("DEBUG")]
    public static void Fail(string message) =>
        throw new InvalidOperationException(message);
}
