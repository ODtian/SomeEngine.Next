using SomeEngine.ECS;

namespace SomeEngine.Assets;

public readonly record struct WorldStreamSource(double X, double Y, double Z, double Radius, int Priority = 0);

public readonly record struct StreamingSource(double Radius, int Priority = 0) : IComponent;

public sealed class WorldStreamOptions
{
    public int MaxConcurrentReads { get; init; } = 4;
    public long MaxReadyBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan CommitBudget { get; init; } = TimeSpan.FromMilliseconds(2);
}
