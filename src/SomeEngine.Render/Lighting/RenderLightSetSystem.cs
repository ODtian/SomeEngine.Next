using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Lighting;

/// <summary>
/// Owns light-component queries and one immutable-for-rendering light set. Geometry,
/// light assignment, shadow and shading pipelines consume this system without querying ECS.
/// </summary>
public sealed class RenderLightSetSystem : ISystem<RenderFrameSystemContext>
{
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace = new("render", "LightSet.Update", detail: true);

    private readonly RenderLightSet _lights = new();
    private QueryHandle _directional;
    private QueryHandle _point;
    private QueryHandle _spot;
    private long _topologyRevision = -1;
    private bool _created;

    public RenderLightSet RequireCurrent() => _created
        ? _lights
        : throw new InvalidOperationException("The render-light set system is not active.");

    public void OnCreate(ref RenderFrameSystemContext context)
    {
        if (_created)
            throw new InvalidOperationException("The render-light set system is already created.");
        QueryHandle directional = default;
        QueryHandle point = default;
        try
        {
            directional = context.World.Query(
                new QueryDefinitionBuilder()
                    .Read<RenderDirectionalLight>()
                    .Optional<RenderLightCookie>(QueryAccess.Read));
            point = context.World.Query(
                new QueryDefinitionBuilder()
                    .Read<RenderPointLight>()
                    .Optional<RenderLightCookie>(QueryAccess.Read));
            _spot = context.World.Query(
                new QueryDefinitionBuilder()
                    .Read<RenderSpotLight>()
                    .Optional<RenderLightCookie>(QueryAccess.Read));
            _directional = directional;
            _point = point;
            _created = true;
        }
        catch
        {
            if (point.IsValid) context.World.ReleaseQuery(point);
            if (directional.IsValid) context.World.ReleaseQuery(directional);
            throw;
        }
    }

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        if (!_created)
            throw new InvalidOperationException("The render-light set system was not created.");
        long topologyRevision = context.World.PublishedTopologyRevision;
        if (_topologyRevision == topologyRevision &&
            !AnyLightChanged(ref context))
        {
            return;
        }
        RenderLightSet lights = _lights;
        lights.Clear();
        context.World.ExecuteQuery(
            _directional,
            ref lights,
            static (QueryCursor cursor, ref RenderLightSet lights) =>
                lights.CollectDirectional(cursor));
        context.World.ExecuteQuery(
            _point,
            ref lights,
            static (QueryCursor cursor, ref RenderLightSet lights) => lights.CollectPoint(cursor));
        context.World.ExecuteQuery(
            _spot,
            ref lights,
            static (QueryCursor cursor, ref RenderLightSet lights) => lights.CollectSpot(cursor));
        lights.CommitChanges();
        _topologyRevision = topologyRevision;
    }

    private bool AnyLightChanged(ref RenderFrameSystemContext context)
    {
        bool changed = false;
        context.World.ExecuteQuery(
            _directional,
            context.LastSystemVersion,
            ref changed,
            static (QueryCursor cursor, ref bool changed) =>
            {
                foreach (QueryChunkView chunk in cursor.Chunks)
                {
                    if (!chunk.HasChangedSinceLastSystemVersion<RenderDirectionalLight>())
                    {
                        if (!chunk.Has<RenderLightCookie>() ||
                            !chunk.HasChangedSinceLastSystemVersion<RenderLightCookie>())
                        {
                            continue;
                        }
                    }
                    changed = true;
                    return;
                }
            });
        if (changed)
            return true;
        context.World.ExecuteQuery(
            _point,
            context.LastSystemVersion,
            ref changed,
            static (QueryCursor cursor, ref bool changed) =>
            {
                foreach (QueryChunkView chunk in cursor.Chunks)
                {
                    if (!chunk.HasChangedSinceLastSystemVersion<RenderPointLight>())
                    {
                        if (!chunk.Has<RenderLightCookie>() ||
                            !chunk.HasChangedSinceLastSystemVersion<RenderLightCookie>())
                        {
                            continue;
                        }
                    }
                    changed = true;
                    return;
                }
            });
        if (changed)
            return true;
        context.World.ExecuteQuery(
            _spot,
            context.LastSystemVersion,
            ref changed,
            static (QueryCursor cursor, ref bool changed) =>
            {
                foreach (QueryChunkView chunk in cursor.Chunks)
                {
                    if (!chunk.HasChangedSinceLastSystemVersion<RenderSpotLight>())
                    {
                        if (!chunk.Has<RenderLightCookie>() ||
                            !chunk.HasChangedSinceLastSystemVersion<RenderLightCookie>())
                        {
                            continue;
                        }
                    }
                    changed = true;
                    return;
                }
            });
        return changed;
    }

    public void OnDestroy(ref RenderFrameSystemContext context)
    {
        if (!_created)
            return;
        context.World.ReleaseQuery(_spot);
        context.World.ReleaseQuery(_point);
        context.World.ReleaseQuery(_directional);
        _spot = default;
        _point = default;
        _directional = default;
        _lights.Clear();
        _topologyRevision = -1;
        _created = false;
    }
}
