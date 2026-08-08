using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

/// <summary>Extracts all light variants and their optional cookie semantics.</summary>
internal sealed class LightRenderExtractor : IRenderExtractionSystem
{
    private readonly List<DirectionalSnapshot> _directional = [];
    private readonly List<PointSnapshot> _points = [];
    private readonly List<SpotSnapshot> _spots = [];
    private readonly HashSet<Entity> _directionalSources = [];
    private readonly HashSet<Entity> _pointSources = [];
    private readonly HashSet<Entity> _spotSources = [];
    private readonly HashSet<Entity> _cookieSources = [];

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.ReadOptional<DirectionalLight>();
        query.ReadOptional<PointLight>();
        query.ReadOptional<SpotLight>();
        query.ReadOptional<LightCookie>();
    }

    public void Reset()
    {
        _directional.Clear();
        _points.Clear();
        _spots.Clear();
        _directionalSources.Clear();
        _pointSources.Clear();
        _spotSources.Clear();
        _cookieSources.Clear();
    }

    public void Collect(QueryChunkView chunk)
    {
        ReadOnlySpan<Entity> entities = chunk.Entities;
        bool hasDirectional = chunk.TryRead<DirectionalLight>(
            out ReadOnlySpan<DirectionalLight> directional);
        bool hasPoints = chunk.TryRead<PointLight>(out ReadOnlySpan<PointLight> points);
        bool hasSpots = chunk.TryRead<SpotLight>(out ReadOnlySpan<SpotLight> spots);
        bool hasCookies = chunk.TryRead<LightCookie>(out ReadOnlySpan<LightCookie> cookies);

        for (int row = 0; row < entities.Length; row++)
        {
            Entity source = entities[row];
            RenderLightCookie cookie = hasCookies ? ExtractCookie(cookies[row]) : default;
            if (hasDirectional)
            {
                _directional.Add(new DirectionalSnapshot(
                    source,
                    ExtractDirectional(directional[row]),
                    hasCookies,
                    cookie,
                    LightChanged: true,
                    CookieChanged: true));
                _directionalSources.Add(source);
            }
            if (hasPoints)
            {
                _points.Add(new PointSnapshot(
                    source,
                    ExtractPoint(points[row]),
                    hasCookies,
                    cookie,
                    LightChanged: true,
                    CookieChanged: true));
                _pointSources.Add(source);
            }
            if (hasSpots)
            {
                _spots.Add(new SpotSnapshot(
                    source,
                    ExtractSpot(spots[row]),
                    hasCookies,
                    cookie,
                    LightChanged: true,
                    CookieChanged: true));
                _spotSources.Add(source);
            }
            if (hasCookies && (hasDirectional || hasPoints || hasSpots))
                _cookieSources.Add(source);
        }
    }

    internal bool CollectChanges(QueryChunkView chunk)
    {
        bool hasDirectional = chunk.TryRead<DirectionalLight>(
            out ReadOnlySpan<DirectionalLight> directional);
        bool hasPoints = chunk.TryRead<PointLight>(out ReadOnlySpan<PointLight> points);
        bool hasSpots = chunk.TryRead<SpotLight>(out ReadOnlySpan<SpotLight> spots);
        bool hasCookies = chunk.TryRead<LightCookie>(out ReadOnlySpan<LightCookie> cookies);
        bool directionalChunkChanged =
            hasDirectional &&
            chunk.HasChangedSinceLastSystemVersion<DirectionalLight>();
        bool pointChunkChanged =
            hasPoints &&
            chunk.HasChangedSinceLastSystemVersion<PointLight>();
        bool spotChunkChanged =
            hasSpots &&
            chunk.HasChangedSinceLastSystemVersion<SpotLight>();
        bool cookieChunkChanged =
            hasCookies &&
            chunk.HasChangedSinceLastSystemVersion<LightCookie>();
        if (!directionalChunkChanged &&
            !pointChunkChanged &&
            !spotChunkChanged &&
            !cookieChunkChanged)
        {
            return false;
        }

        int firstChangeCount = _directional.Count + _points.Count + _spots.Count;
        ReadOnlySpan<Entity> entities = chunk.Entities;
        for (int row = 0; row < entities.Length; row++)
        {
            Entity source = entities[row];
            bool directionalChanged =
                directionalChunkChanged &&
                chunk.RowChangedSinceLastSystemVersion<DirectionalLight>(row);
            bool pointChanged =
                pointChunkChanged &&
                chunk.RowChangedSinceLastSystemVersion<PointLight>(row);
            bool spotChanged =
                spotChunkChanged &&
                chunk.RowChangedSinceLastSystemVersion<SpotLight>(row);
            bool cookieChanged =
                cookieChunkChanged &&
                chunk.RowChangedSinceLastSystemVersion<LightCookie>(row);

            if (directionalChanged)
            {
                _directional.Add(new DirectionalSnapshot(
                    source,
                    ExtractDirectional(directional[row]),
                    HasCookie: false,
                    Cookie: default,
                    LightChanged: true,
                    CookieChanged: false));
            }
            if (pointChanged)
            {
                _points.Add(new PointSnapshot(
                    source,
                    ExtractPoint(points[row]),
                    HasCookie: false,
                    Cookie: default,
                    LightChanged: true,
                    CookieChanged: false));
            }
            if (spotChanged)
            {
                _spots.Add(new SpotSnapshot(
                    source,
                    ExtractSpot(spots[row]),
                    HasCookie: false,
                    Cookie: default,
                    LightChanged: true,
                    CookieChanged: false));
            }
            if (!cookieChanged || (!hasDirectional && !hasPoints && !hasSpots))
                continue;

            RenderLightCookie cookie = ExtractCookie(cookies[row]);
            if (hasDirectional)
            {
                _directional.Add(new DirectionalSnapshot(
                    source,
                    default,
                    HasCookie: true,
                    cookie,
                    LightChanged: false,
                    CookieChanged: true));
            }
            else if (hasPoints)
            {
                _points.Add(new PointSnapshot(
                    source,
                    default,
                    HasCookie: true,
                    cookie,
                    LightChanged: false,
                    CookieChanged: true));
            }
            else
            {
                _spots.Add(new SpotSnapshot(
                    source,
                    default,
                    HasCookie: true,
                    cookie,
                    LightChanged: false,
                    CookieChanged: true));
            }
        }

        return _directional.Count + _points.Count + _spots.Count != firstChangeCount;
    }

    public void Apply(RenderExtractionContext context)
    {
        for (int index = 0; index < _directional.Count; index++)
        {
            DirectionalSnapshot snapshot = _directional[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            context.Upsert(entity, snapshot.Light);
            if (snapshot.HasCookie)
                context.Upsert(entity, snapshot.Cookie);
        }
        for (int index = 0; index < _points.Count; index++)
        {
            PointSnapshot snapshot = _points[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            context.Upsert(entity, snapshot.Light);
            if (snapshot.HasCookie)
                context.Upsert(entity, snapshot.Cookie);
        }
        for (int index = 0; index < _spots.Count; index++)
        {
            SpotSnapshot snapshot = _spots[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            context.Upsert(entity, snapshot.Light);
            if (snapshot.HasCookie)
                context.Upsert(entity, snapshot.Cookie);
        }

        IReadOnlyList<RenderMirror> mirrors = context.Mirrors;
        for (int index = 0; index < mirrors.Count; index++)
        {
            RenderMirror mirror = mirrors[index];
            if (!_directionalSources.Contains(mirror.Source))
                context.RemoveIfExists<RenderDirectionalLight>(mirror.RenderEntity);
            if (!_pointSources.Contains(mirror.Source))
                context.RemoveIfExists<RenderPointLight>(mirror.RenderEntity);
            if (!_spotSources.Contains(mirror.Source))
                context.RemoveIfExists<RenderSpotLight>(mirror.RenderEntity);
            if (!_cookieSources.Contains(mirror.Source))
                context.RemoveIfExists<RenderLightCookie>(mirror.RenderEntity);
        }
    }

    internal void ApplyChanges(RenderExtractionContext context)
    {
        for (int index = 0; index < _directional.Count; index++)
        {
            DirectionalSnapshot snapshot = _directional[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            if (snapshot.LightChanged)
                context.UpdateExisting(entity, snapshot.Light);
            if (snapshot.CookieChanged)
                context.UpdateExisting(entity, snapshot.Cookie);
        }
        for (int index = 0; index < _points.Count; index++)
        {
            PointSnapshot snapshot = _points[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            if (snapshot.LightChanged)
                context.UpdateExisting(entity, snapshot.Light);
            if (snapshot.CookieChanged)
                context.UpdateExisting(entity, snapshot.Cookie);
        }
        for (int index = 0; index < _spots.Count; index++)
        {
            SpotSnapshot snapshot = _spots[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            if (snapshot.LightChanged)
                context.UpdateExisting(entity, snapshot.Light);
            if (snapshot.CookieChanged)
                context.UpdateExisting(entity, snapshot.Cookie);
        }
    }

    internal void ValidateChanges(RenderExtractionContext context)
    {
        for (int index = 0; index < _directional.Count; index++)
        {
            DirectionalSnapshot snapshot = _directional[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            if (snapshot.LightChanged)
                context.RequireExisting<RenderDirectionalLight>(entity);
            if (snapshot.CookieChanged)
                context.RequireExisting<RenderLightCookie>(entity);
        }
        for (int index = 0; index < _points.Count; index++)
        {
            PointSnapshot snapshot = _points[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            if (snapshot.LightChanged)
                context.RequireExisting<RenderPointLight>(entity);
            if (snapshot.CookieChanged)
                context.RequireExisting<RenderLightCookie>(entity);
        }
        for (int index = 0; index < _spots.Count; index++)
        {
            SpotSnapshot snapshot = _spots[index];
            Entity entity = context.RequireMirror(snapshot.Source);
            if (snapshot.LightChanged)
                context.RequireExisting<RenderSpotLight>(entity);
            if (snapshot.CookieChanged)
                context.RequireExisting<RenderLightCookie>(entity);
        }
    }

    private static RenderDirectionalLight ExtractDirectional(in DirectionalLight source)
        => new(source.Direction, source.Color, source.Intensity, source.LayerMask);

    private static RenderPointLight ExtractPoint(in PointLight source)
        => new(source.Position, source.Range, source.Color, source.Intensity, source.LayerMask);

    private static RenderSpotLight ExtractSpot(in SpotLight source)
        => new(
            source.Position,
            source.Range,
            source.Direction,
            source.InnerConeCos,
            source.OuterConeCos,
            source.Color,
            source.Intensity,
            source.LayerMask);

    private static RenderLightCookie ExtractCookie(in LightCookie source)
        => new(source.Texture, source.Strength, source.ScaleOffset, source.WorldToCookie);

    private readonly record struct DirectionalSnapshot(
        Entity Source,
        RenderDirectionalLight Light,
        bool HasCookie,
        RenderLightCookie Cookie,
        bool LightChanged,
        bool CookieChanged);

    private readonly record struct PointSnapshot(
        Entity Source,
        RenderPointLight Light,
        bool HasCookie,
        RenderLightCookie Cookie,
        bool LightChanged,
        bool CookieChanged);

    private readonly record struct SpotSnapshot(
        Entity Source,
        RenderSpotLight Light,
        bool HasCookie,
        RenderLightCookie Cookie,
        bool LightChanged,
        bool CookieChanged);
}
