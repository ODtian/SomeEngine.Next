namespace SomeEngine.Navigation;

/// <summary>A fixed-goal request retains D* Lite values, queue and moving-start key modifier across scene revisions.</summary>
public sealed class DStarLiteSearchState
{
    private readonly DStarLiteWorkspace _workspace;
    private SceneSearchRun _run;
    private NavigationPath? _path;
    private long _version, _contentVersion;
    private bool _replace;
    public float KeyModifier => _workspace.KeyModifier;
    public int ReusedVertexCount => _workspace.ReusedVertexCount;
    public int KnownVertexCount => _workspace.KnownVertexCount;
    public long ExpandedVertexCount => _workspace.ExpandedVertexCount;

    internal DStarLiteSearchState(in NavigationSearchInput<NavigationScene> input)
    {
        _workspace = new(input.Start, input.Policy);
        _run = new(in input, _workspace);
        _version = input.Space.Content.Version;
        _contentVersion = input.Space.Content.ContentVersion;
    }

    internal bool Update(in NavigationSearchInput<NavigationScene> input)
    {
        _workspace.MoveStart(input.Start);
        NavigationSceneContent current = input.Space.Content;
        if (_version == current.Version) return false;
        _version = current.Version;
        bool changed = _contentVersion != current.ContentVersion;
        _contentVersion = current.ContentVersion;
        if (changed || (_run.Waiting && (_run.StartMissing || (_run.GoalMissing && current.TryGetTile(_run.GoalTile, out _)))))
        {
            _run = new(in input, _workspace);
            _path = null;
            _replace = true;
            return true;
        }
        if (!_run.Waiting) return false;
        _run.Resume();
        return true;
    }

    internal NavigationSearchStep Advance(int budget)
    {
        NavigationSearchStep step = _run.Advance(budget);
        bool reset = false;
        if (step.Path is { } path)
        {
            _path = path;
            if (_replace && !path.Corners.IsEmpty) { _replace = false; reset = true; }
        }
        return step with { Path = _path, ResetPathProgress = reset };
    }
}

/// <summary>Repairs the placed scene entrance graph and shares endpoint, detailed path, streaming and traversal work with scene A*.</summary>
public readonly struct DStarLiteNavigationSearch : INavigationSearch<NavigationScene, DStarLiteSearchState>
{
    public static DStarLiteSearchState Create(in NavigationSearchInput<NavigationScene> input) => new(in input);
    public static NavigationSearchStep Advance(DStarLiteSearchState state, int expansionBudget) => state.Advance(expansionBudget);
    public static bool Update(DStarLiteSearchState state, in NavigationSearchInput<NavigationScene> input) => state.Update(in input);
}
