namespace SomeEngine.Views;

/// <summary>Restores the surrounding call-group and child-declaration key prefixes.</summary>
public readonly struct GroupKeyScope : IDisposable
{
    private readonly ulong? _previousKeyPrefix;
    private readonly ulong? _previousGroupPrefix;

    internal GroupKeyScope(ulong? previousKeyPrefix, ulong? previousGroupPrefix)
    {
        _previousKeyPrefix = previousKeyPrefix;
        _previousGroupPrefix = previousGroupPrefix;
    }

    public void Dispose() => Scope.RestoreGroupKeys(_previousKeyPrefix, _previousGroupPrefix);
}
