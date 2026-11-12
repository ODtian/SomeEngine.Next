using SomeEngine.ECS;
using SomeEngine.Resources;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;

namespace SomeEngine.Render.Systems;

/// <summary>Extracts all light variants and their optional cookie semantics.</summary>
public sealed partial class LightRenderExtractor : IRenderExtractionSystem
{
    private readonly List<DirectionalSnapshot> _directional = [];
    private readonly List<PointSnapshot> _points = [];
    private readonly List<SpotSnapshot> _spots = [];
    private readonly HashSet<Entity> _directionalSources = [];
    private readonly HashSet<Entity> _pointSources = [];
    private readonly HashSet<Entity> _spotSources = [];
    private readonly HashSet<Entity> _cookieSources = [];

    public bool CanApplyChangesDirect => true;

    public void DeclareReads(RenderExtractionQuery query)
    {
        query.Write<RenderDirectionalLight>();
        query.Write<RenderPointLight>();
        query.Write<RenderSpotLight>();
        query.Write<RenderLightCookie>();
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

    public void Collect(QueryChunk chunk)
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
            bool directionalRow = hasDirectional && chunk.Has<DirectionalLight>(row);
            bool pointRow = hasPoints && chunk.Has<PointLight>(row);
            bool spotRow = hasSpots && chunk.Has<SpotLight>(row);
            bool cookieRow = hasCookies && chunk.Has<LightCookie>(row);
            RenderLightCookie cookie = cookieRow && (directionalRow || pointRow || spotRow)
                ? ExtractCookie(cookies[row]) : default;
            if (directionalRow)
            {
                _directional.Add(new DirectionalSnapshot(
                    source,
                    ExtractDirectional(directional[row]),
                    cookieRow,
                    cookie,
                    LightChanged: true,
                    CookieChanged: true));
                _directionalSources.Add(source);
            }
            if (pointRow)
            {
                _points.Add(new PointSnapshot(
                    source,
                    ExtractPoint(points[row]),
                    cookieRow,
                    cookie,
                    LightChanged: true,
                    CookieChanged: true));
                _pointSources.Add(source);
            }
            if (spotRow)
            {
                _spots.Add(new SpotSnapshot(
                    source,
                    ExtractSpot(spots[row]),
                    cookieRow,
                    cookie,
                    LightChanged: true,
                    CookieChanged: true));
                _spotSources.Add(source);
            }
            if (cookieRow && (directionalRow || pointRow || spotRow))
                _cookieSources.Add(source);
        }
    }

    public bool CollectChanges(QueryChunk chunk, ulong since, RenderExtractionContext context) =>
        CollectChanges(chunk, since);

    private bool CollectChanges(QueryChunk chunk, ulong lastSystemVersion)
    {
        bool hasDirectional = chunk.TryRead<DirectionalLight>(
            out ReadOnlySpan<DirectionalLight> directional);
        bool hasPoints = chunk.TryRead<PointLight>(out ReadOnlySpan<PointLight> points);
        bool hasSpots = chunk.TryRead<SpotLight>(out ReadOnlySpan<SpotLight> spots);
        bool hasCookies = chunk.TryRead<LightCookie>(out ReadOnlySpan<LightCookie> cookies);
        bool directionalChunkChanged =
            hasDirectional &&
            chunk.WasWritten<DirectionalLight>(lastSystemVersion);
        bool pointChunkChanged =
            hasPoints &&
            chunk.WasWritten<PointLight>(lastSystemVersion);
        bool spotChunkChanged =
            hasSpots &&
            chunk.WasWritten<SpotLight>(lastSystemVersion);
        bool cookieChunkChanged =
            hasCookies &&
            chunk.WasWritten<LightCookie>(lastSystemVersion);
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
            bool directionalRow = hasDirectional && chunk.Has<DirectionalLight>(row);
            bool pointRow = hasPoints && chunk.Has<PointLight>(row);
            bool spotRow = hasSpots && chunk.Has<SpotLight>(row);
            bool directionalChanged =
                directionalChunkChanged &&
                chunk.WasWritten<DirectionalLight>(row, lastSystemVersion);
            bool pointChanged =
                pointChunkChanged &&
                chunk.WasWritten<PointLight>(row, lastSystemVersion);
            bool spotChanged =
                spotChunkChanged &&
                chunk.WasWritten<SpotLight>(row, lastSystemVersion);
            bool cookieChanged =
                cookieChunkChanged &&
                chunk.WasWritten<LightCookie>(row, lastSystemVersion);

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
            if (!cookieChanged || (!directionalRow && !pointRow && !spotRow))
                continue;

            RenderLightCookie cookie = ExtractCookie(cookies[row]);
            if (directionalRow)
            {
                _directional.Add(new DirectionalSnapshot(
                    source,
                    default,
                    HasCookie: true,
                    cookie,
                    LightChanged: false,
                    CookieChanged: true));
            }
            else if (pointRow)
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
            if (snapshot.LightChanged) context.Upsert(entity, snapshot.Light);
            if (snapshot.HasCookie)
                context.Upsert(entity, snapshot.Cookie);
        }
        for (int index = 0; index < _points.Count; index++)
        {
            PointSnapshot snapshot = _points[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            if (snapshot.LightChanged) context.Upsert(entity, snapshot.Light);
            if (snapshot.HasCookie)
                context.Upsert(entity, snapshot.Cookie);
        }
        for (int index = 0; index < _spots.Count; index++)
        {
            SpotSnapshot snapshot = _spots[index];
            Entity entity = context.RetainMirror(snapshot.Source);
            if (snapshot.LightChanged) context.Upsert(entity, snapshot.Light);
            if (snapshot.HasCookie)
                context.Upsert(entity, snapshot.Cookie);
        }

        ReadOnlySpan<RenderMirror> mirrors = context.Mirrors;
        for (int index = 0; index < mirrors.Length; index++)
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

    public void ApplyChanges(RenderExtractionContext context)
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

    public void ValidateChanges(RenderExtractionContext context)
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
        => new(source.Direction, source.Color, source.Intensity, source.LayerMask, source.ShadowSettings);

    private static RenderPointLight ExtractPoint(in PointLight source)
        => new(source.Position, source.Range, source.Color, source.Intensity, source.LayerMask, source.ShadowSettings);

    private static RenderSpotLight ExtractSpot(in SpotLight source)
        => new(
            source.Position,
            source.Range,
            source.Direction,
            source.InnerConeCos,
            source.OuterConeCos,
            source.Color,
            source.Intensity,
            source.LayerMask,
            source.ShadowSettings);

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
