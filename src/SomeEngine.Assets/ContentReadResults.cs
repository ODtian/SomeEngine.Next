namespace SomeEngine.Assets;

public readonly record struct ContentBytes(int ContentIndex, ReadOnlyMemory<byte> Bytes);

public sealed class ContentReadResults
{
    internal readonly List<ContentBytes> Values = [];
    public IReadOnlyList<ContentBytes> Items => Values;
}
