namespace SomeEngine.Assets;

public readonly record struct ContentDemand(bool Required, int Priority);

public sealed class ContentDemands
{
    internal ContentDemand[] Data = [];
    internal int[] Changes = [];
    internal int ChangeCount;
    public ReadOnlyMemory<ContentDemand> Values => Data;
    public ReadOnlyMemory<int> Changed => Changes.AsMemory(0, ChangeCount);
}
