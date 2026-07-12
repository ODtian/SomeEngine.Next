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

public enum DeviceErrorKind : byte
{
    None,
    Validation,
    Unsupported,
    OutOfMemory,
    DeviceLost,
    Backend,
}

public readonly record struct DeviceError(DeviceErrorKind Kind, string Message, int NativeCode = 0)
{
    public static DeviceError None => new(DeviceErrorKind.None, string.Empty);
    public bool IsError => Kind != DeviceErrorKind.None;
}
