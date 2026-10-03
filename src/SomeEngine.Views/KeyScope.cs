namespace SomeEngine.Views;

/// <summary>Restores the surrounding key when a using block ends.</summary>
public readonly struct KeyScope : IDisposable
{
    private readonly ulong? _previousKey;

    internal KeyScope(ulong? previousKey) => _previousKey = previousKey;

    public void Dispose() => Scope.RestoreKey(_previousKey);
}
