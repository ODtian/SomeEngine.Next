using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

/// <summary>Keeps version-filtered source membership on one observation interval throughout a numerical substep.</summary>
internal sealed class PhysicsQueryWindow
{
    private long _number;
    private int _substep;
    private uint _baseline;
    private bool _hasStep;
    internal PhysicsQueryWindow(QueryDefinition definition)
    {
        foreach (QueryTerm term in definition.Terms)
            UsesVersions |= (term.Filters & (QueryTermFilter.Added | QueryTermFilter.Changed | QueryTermFilter.ChunkChanged)) != 0;
    }
    internal bool UsesVersions { get; }
    internal uint LastVersion { get; private set; }
    internal uint Begin(PhysicsStepContext? context = null)
    {
        if (!UsesVersions || context is not { } step) return LastVersion;
        if (!_hasStep || step.Number != _number || step.Substep != _substep)
        {
            _number = step.Number; _substep = step.Substep; _baseline = LastVersion; _hasStep = true;
        }
        return _baseline;
    }
    internal void Accept(uint version) => LastVersion = version;
}
