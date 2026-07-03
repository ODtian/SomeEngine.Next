namespace SomeEngine.Job;

public sealed class JobResourceSafetyException : InvalidOperationException
{
    public JobResourceSafetyException(
        string message,
        JobSafetyMode safetyMode,
        string? jobTypeName,
        string? resourceName,
        int resourceId,
        string resourceKind)
        : base(message)
    {
        SafetyMode = safetyMode;
        JobTypeName = jobTypeName;
        ResourceName = resourceName;
        ResourceId = resourceId;
        ResourceKind = resourceKind;
    }

    public JobSafetyMode SafetyMode { get; }

    public string? JobTypeName { get; }

    public string? ResourceName { get; }

    public int ResourceId { get; }

    public string ResourceKind { get; }
}



