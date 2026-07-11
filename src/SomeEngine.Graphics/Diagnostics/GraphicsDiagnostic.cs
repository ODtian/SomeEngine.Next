namespace SomeEngine.Graphics;

public enum GraphicsDiagnosticSeverity : byte
{
    Information,
    Warning,
    Error,
    Corruption,
}

public readonly record struct GraphicsDiagnostic(
    GraphicsDiagnosticSeverity Severity,
    string Source,
    string Message,
    int NativeId = 0);
