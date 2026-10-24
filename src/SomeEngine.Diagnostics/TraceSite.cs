namespace SomeEngine.Diagnostics;

/// <summary>Declare once in a static field. Scope recording only copies the integer ID.</summary>
public sealed class TraceSite
{
    private static readonly Lock Gate = new();
    private static readonly List<TraceSite> Sites = [];
    internal static readonly TraceSite Frame = new("frame", "Frame");
    public int Id { get; }
    public string Category { get; }
    public string Name { get; }
    public bool Detail { get; }

    public TraceSite(string category, string name, bool detail = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Category = category; Name = name; Detail = detail;
        lock (Gate) { Id = Sites.Count + 1; Sites.Add(this); }
    }

    internal static TraceSite[] GetSites() { lock (Gate) return [.. Sites]; }
}
