using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>Reuses one actor's local traversal and boundary buffers.</summary>
public readonly ref struct NavigationBoundaryBuilder
{
    private readonly NavigationAvoidanceWorkspace _workspace;
    private readonly Vector3 _position;
    private readonly float _rangeSquared;
    private readonly NavigationProfile _profile;

    internal NavigationBoundaryBuilder(NavigationAvoidanceWorkspace workspace, Vector3 position, float range, NavigationProfile profile)
    {
        _workspace = workspace;
        _position = position;
        _rangeSquared = range * range;
        _profile = profile;
        workspace.Boundaries.Clear();
        workspace.SearchQueue.Clear();
        workspace.Visited.Clear();
    }

    public void Visit(int node)
    {
        if (_workspace.Visited.Add(node)) _workspace.SearchQueue.Enqueue(node);
    }

    public bool TryPop(out int node) => _workspace.SearchQueue.TryDequeue(out node);
    public void BeginOpenings() => _workspace.Openings.Clear();
    public void Open(float start, float end) => _workspace.Openings.Add(new(MathF.Max(0, start), MathF.Min(1, end)));

    public void AddClosedParts(Vector3 start, Vector3 end, float bakedRadius)
    {
        _workspace.Openings.Sort(static (a, b) => a.X.CompareTo(b.X));
        float cursor = 0;
        foreach (Vector2 interval in _workspace.Openings)
        {
            if (interval.X > cursor) Add(Vector3.Lerp(start, end, cursor), Vector3.Lerp(start, end, interval.X), bakedRadius);
            cursor = MathF.Max(cursor, interval.Y);
        }
        if (cursor < 1) Add(Vector3.Lerp(start, end, cursor), end, bakedRadius);
    }

    public void Add(Vector3 start, Vector3 end, float bakedRadius = 0)
    {
        Vector2 edge = Xz(end - start), offset = Xz(_position - start);
        float lengthSquared = edge.LengthSquared();
        if (lengthSquared <= 0.00000001f || Cross(edge, offset) > 0.0001f) return;
        float parameter = Math.Clamp(Vector2.Dot(offset, edge) / lengthSquared, 0, 1);
        if ((offset - parameter * edge).LengthSquared() > _rangeSquared ||
            MathF.Min(start.Y, end.Y) > _position.Y + _profile.Height ||
            MathF.Max(start.Y, end.Y) < _position.Y - _profile.MaximumStepHeight - 0.001f) return;
        _workspace.Boundaries.Add(new(start, end, bakedRadius));
    }

    internal static Vector2 Xz(Vector3 point) => new(point.X, point.Z);
    internal static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
