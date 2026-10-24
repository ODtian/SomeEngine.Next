namespace SomeEngine.Serialization;

/// <summary>Failure categories that callers can use when selecting recovery or reporting a read failure.</summary>
public enum SerializationError
{
    Unspecified = 0,
    InvalidData = 1,
    UnsupportedVersion = 2,
    SchemaMismatch = 3,
    UnknownType = 4,
    LimitExceeded = 5,
    IntegrityMismatch = 6,
    GenerationRejected = 7,
}

/// <summary>Preserves standard exception types while attaching a machine-readable serialization failure.</summary>
public static class SerializationErrors
{
    private const string ErrorKey = "SomeEngine.Serialization.Error";

    /// <summary>Creates a data failure with a category, diagnostic text and optional original failure.</summary>
    public static InvalidDataException CreateDataException(
        SerializationError error,
        string message,
        Exception? innerException = null)
    {
        var exception = new InvalidDataException(message, innerException);
        exception.Data[ErrorKey] = error;
        return exception;
    }

    /// <summary>
    /// Gets an attached category. Ordinary InvalidDataException values represent malformed data;
    /// BinarySchemaMismatchException values represent a schema mismatch. Other failures
    /// retain their standard exception type and return Unspecified.
    /// Custom codecs can use CreateDataException to participate in the same recovery policy.
    /// </summary>
    public static SerializationError GetError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.Data[ErrorKey] is SerializationError error)
            return error;
        if (exception is BinarySchemaMismatchException)
            return SerializationError.SchemaMismatch;
        return exception is InvalidDataException ? SerializationError.InvalidData : SerializationError.Unspecified;
    }
}
