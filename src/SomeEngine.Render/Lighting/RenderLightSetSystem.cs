using SomeEngine.ECS;
using SomeEngine.Job;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Lighting;

/// <summary>
/// Owns light-component queries and one immutable-for-rendering light set. Geometry,
/// light assignment, shadow and shading pipelines consume this system without querying ECS.
/// </summary>
public sealed class RenderLightSetSystem(uint lightActiveFrameCount = 10) : ISystem<RenderPrepareSystemContext>
{
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace = new("render", "LightSet.Update", detail: true);

    private readonly RenderLightSet _lights = new();
    private Query _directional;
    private Query _point;
    private Query _spot;
    private EventReader<MembershipChange>[] _members = [];
    private bool _created;
    private ulong _renderedFrame;
    private readonly uint _lightActiveFrameCount = Math.Max(1u, lightActiveFrameCount);

    public RenderLightSet RequireCurrent() => _created
        ? _lights
        : throw new InvalidOperationException("The render-light set system is not active.");

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        if (_created)
            throw new InvalidOperationException("The render-light set system is already created.");
        World world = context.World.World;
        _directional = world.Query(new QueryDescription().With<RenderDirectionalLight>().Optional<RenderLightCookie>());
        _point = world.Query(new QueryDescription().With<RenderPointLight>().Optional<RenderLightCookie>());
        _spot = world.Query(new QueryDescription().With<RenderSpotLight>().Optional<RenderLightCookie>());
        _members = [world.WatchMembership(_directional), world.WatchMembership(_point), world.WatchMembership(_spot),
            world.WatchMembership(world.Query(new QueryDescription().WithStored<RenderLightCookie>()))];
        _created = true;
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        if (!_created)
            throw new InvalidOperationException("The render-light set system was not created.");
        World world = context.World.World;
        bool changed = false;
        foreach (EventReader<MembershipChange> reader in _members)
        {
            EventReadRange range = reader.Capture();
            using EventRead<MembershipChange> read = reader.Open(range);
            foreach (MembershipChange value in read) changed |= _lights.UpdateMembership(world, value.Entity);
            reader.Advance(range.Through);
        }
        using (QueryScope rows = _directional.Since(context.LastSystemVersion).OpenRead()) changed |= _lights.UpdateDirectional(rows);
        using (QueryScope rows = _point.Since(context.LastSystemVersion).OpenRead()) changed |= _lights.UpdatePoints(rows);
        using (QueryScope rows = _spot.Since(context.LastSystemVersion).OpenRead()) changed |= _lights.UpdateSpots(rows);
        _lights.AdvanceLightMovement(checked(++_renderedFrame), _lightActiveFrameCount);
        if (changed) _lights.CommitChanges();
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        if (!_created)
            return;
        _spot = default;
        _point = default;
        _directional = default;
        _lights.Clear();
        _renderedFrame = 0;
        foreach (EventReader<MembershipChange> reader in _members) reader.Dispose();
        _members = [];
        _created = false;
    }
}
