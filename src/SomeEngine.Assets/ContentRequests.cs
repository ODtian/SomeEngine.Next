namespace SomeEngine.Assets;

public sealed class ContentRequests
{
    internal readonly Dictionary<int, int> Values = [];
    public IReadOnlyDictionary<int, int> Priorities => Values;
}
