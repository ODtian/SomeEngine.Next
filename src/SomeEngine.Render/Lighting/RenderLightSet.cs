using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Lighting;

/// <summary>
/// Owns the immutable-for-rendering light values collected for one render-frame preparation.
/// Geometry and shading pipelines borrow this set; they do not own light lifetime or ECS queries.
/// </summary>
public sealed class RenderLightSet
{
    private readonly List<RenderDirectionalLight> _directional = [];
    private readonly List<RenderPointLight> _points = [];
    private readonly List<RenderSpotLight> _spots = [];
    private readonly List<RenderLightCookie?> _directionalCookies = [];
    private readonly List<RenderLightCookie?> _pointCookies = [];
    private readonly List<RenderLightCookie?> _spotCookies = [];

    public IReadOnlyList<RenderDirectionalLight> Directional => _directional;
    public IReadOnlyList<RenderPointLight> Points => _points;
    public IReadOnlyList<RenderSpotLight> Spots => _spots;
    public IReadOnlyList<RenderLightCookie?> DirectionalCookies => _directionalCookies;
    public IReadOnlyList<RenderLightCookie?> PointCookies => _pointCookies;
    public IReadOnlyList<RenderLightCookie?> SpotCookies => _spotCookies;

    public ulong Revision { get; private set; }

    public void Clear()
    {
        _directional.Clear();
        _points.Clear();
        _spots.Clear();
        _directionalCookies.Clear();
        _pointCookies.Clear();
        _spotCookies.Clear();
    }

    internal void CommitChanges() => Revision = checked(Revision + 1uL);

    public void CollectDirectional(QueryCursor cursor)
    {
        foreach (QueryRow row in cursor.Rows)
        {
            _directional.Add(row.Read<RenderDirectionalLight>());
            _directionalCookies.Add(row.TryRead<RenderLightCookie>(out RenderLightCookie cookie)
                ? cookie
                : null);
        }
    }

    public void AddDirectional(in RenderDirectionalLight light)
    {
        _directional.Add(light);
        _directionalCookies.Add(null);
    }

    public void AddDirectional(
        in RenderDirectionalLight light,
        in RenderLightCookie cookie)
    {
        _directional.Add(light);
        _directionalCookies.Add(cookie);
    }

    public void AddPoint(in RenderPointLight light)
    {
        _points.Add(light);
        _pointCookies.Add(null);
    }

    public void AddPoint(in RenderPointLight light, in RenderLightCookie cookie)
    {
        _points.Add(light);
        _pointCookies.Add(cookie);
    }

    public void AddSpot(in RenderSpotLight light)
    {
        _spots.Add(light);
        _spotCookies.Add(null);
    }

    public void AddSpot(in RenderSpotLight light, in RenderLightCookie cookie)
    {
        _spots.Add(light);
        _spotCookies.Add(cookie);
    }

    public void CollectPoint(QueryCursor cursor)
    {
        foreach (QueryRow row in cursor.Rows)
        {
            _points.Add(row.Read<RenderPointLight>());
            _pointCookies.Add(row.TryRead<RenderLightCookie>(out RenderLightCookie cookie)
                ? cookie
                : null);
        }
    }

    public void CollectSpot(QueryCursor cursor)
    {
        foreach (QueryRow row in cursor.Rows)
        {
            _spots.Add(row.Read<RenderSpotLight>());
            _spotCookies.Add(row.TryRead<RenderLightCookie>(out RenderLightCookie cookie)
                ? cookie
                : null);
        }
    }
}
