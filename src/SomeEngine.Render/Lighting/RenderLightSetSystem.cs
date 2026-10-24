using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Entities;
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
    private EntityTableChanges? _tableChanges;
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
            _tableChanges = context.World.ObserveEntityTables();
            _created = true;
        }
        catch
        {
            if (_spot.IsValid) context.World.ReleaseQuery(_spot);
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
        EntityTableChangeBatch membership = _tableChanges!.Read();
        var update = new LightChanges(_lights);
        foreach (Entity entity in membership.Entities)
            update.Changed |= _lights.UpdateMembership(context.World, entity);
        context.World.ExecuteQuery(_directional, context.LastSystemVersion, ref update,
            static (scoped QueryCursor rows, ref LightChanges value) => value.Changed |= value.Lights.UpdateDirectional(rows));
        context.World.ExecuteQuery(_point, context.LastSystemVersion, ref update,
            static (scoped QueryCursor rows, ref LightChanges value) => value.Changed |= value.Lights.UpdatePoints(rows));
        context.World.ExecuteQuery(_spot, context.LastSystemVersion, ref update,
            static (scoped QueryCursor rows, ref LightChanges value) => value.Changed |= value.Lights.UpdateSpots(rows));
        if (update.Changed) _lights.CommitChanges();
        _tableChanges.Accept(membership);
    }

    private struct LightChanges(RenderLightSet lights)
    {
        internal readonly RenderLightSet Lights = lights;
        internal bool Changed;
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
        _tableChanges!.Dispose();
        _tableChanges = null;
        _created = false;
    }
}
