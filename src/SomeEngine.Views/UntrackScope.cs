namespace SomeEngine.Views;

/// <summary>Restores the surrounding build's dependency tracking when a using block ends.</summary>
public readonly struct UntrackScope : IDisposable
{
    private readonly bool _previousDependenciesEnabled;

    internal UntrackScope(bool previousDependenciesEnabled) =>
        _previousDependenciesEnabled = previousDependenciesEnabled;

    public void Dispose() => Scope.RestoreDependencyTracking(_previousDependenciesEnabled);
}
