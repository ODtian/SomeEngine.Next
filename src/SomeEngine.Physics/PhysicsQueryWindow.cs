using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>Keeps version-filtered source membership on one observation interval throughout a numerical substep.</summary>
internal sealed class PhysicsQueryWindow
{
    private long _number;
    private int _substep;
    private ulong _baseline;
    private bool _hasStep;
    internal PhysicsQueryWindow(Query query) => UsesVersions = query.HasVersionFilters;
    internal bool UsesVersions { get; }
    internal ulong LastVersion { get; private set; }
    internal ulong Begin(PhysicsStepContext? context = null)
    {
        if (!UsesVersions || context is not { } step) return LastVersion;
        if (!_hasStep || step.Number != _number || step.Substep != _substep)
        {
            _number = step.Number; _substep = step.Substep; _baseline = LastVersion; _hasStep = true;
        }
        return _baseline;
    }
    internal void Accept(ulong version) => LastVersion = version;
}
