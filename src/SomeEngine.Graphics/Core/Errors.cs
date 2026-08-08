namespace SomeEngine.Graphics;

/// <summary>The closed set of exceptional graphics failures that prevent an operation continuing.</summary>
public enum GraphicsError : byte
{
    DeviceLost,
    OutOfMemory,
    OutOfDescriptors,
    ShaderCompilation,
    PipelineCreation,
    NativeFailure,
}

/// <summary>Reports a Slang, serialization, native creation, execution, or terminal device failure.</summary>
public sealed class GraphicsException : Exception
{
    public GraphicsException(
        GraphicsError error,
        string message,
        long? nativeCode = null,
        string? diagnostic = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(error))
            throw new ArgumentOutOfRangeException(nameof(error));

        Error = error;
        NativeCode = nativeCode;
        Diagnostic = diagnostic;
    }

    public GraphicsError Error { get; }
    public long? NativeCode { get; }
    public string? Diagnostic { get; }
}

public enum RetirementType : byte
{
    Manual,
    Automatic,
}

public enum DeviceStatus : byte
{
    Active,
    Lost,
    Disposed,
}

public enum WaitStatus : byte
{
    Completed,
    Timeout,
}

public enum NativeObjectOwnership : byte
{
    Borrowed,
    Transferred,
}

public enum RecordedCommandsStatus : byte
{
    Executable,
    Submitting,
    Submitted,
    Completed,
    Discarded,
    DeviceLost,
    Disposed,
}

public enum PersistentParameterBindingsStatus : byte
{
    Unpublished,
    Published,
    Disposed,
}

public enum SwapchainImageStatus : byte
{
    Acquired,
    Submitted,
    Presented,
    Invalidated,
    DeviceLost,
}
