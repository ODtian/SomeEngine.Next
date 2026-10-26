namespace SomeEngine.Job;

public sealed class JobCategoryConfig
{
    public string Name { get; init; } = "work";
    public int MaxInFlight { get; init; } = 256;
    public int MaxExecuting { get; init; } = 1;
    public int ServiceShare { get; init; } = 1;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInFlight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxExecuting);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ServiceShare);
    }
}
