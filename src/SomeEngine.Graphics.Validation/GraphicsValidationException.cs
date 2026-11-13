namespace SomeEngine.Graphics.Validation;

public enum ValidationError
{
    ResourceDisposedWhileRecorded,
    ConcurrentCommandUse,
}

public sealed class GraphicsValidationException(ValidationError error, string message) : InvalidOperationException(message)
{
    public ValidationError Error { get; } = error;
}
