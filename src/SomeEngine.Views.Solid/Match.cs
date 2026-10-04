namespace SomeEngine.Views.Solid;

/// <summary>A condition and the content declared when Switch selects this case.</summary>
public readonly struct Match
{
    internal Match(Func<bool> when, Action children)
    {
        When = when;
        Children = children;
    }

    public Func<bool> When { get; }
    public Action Children { get; }
}
