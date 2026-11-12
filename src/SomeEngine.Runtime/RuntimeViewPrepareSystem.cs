using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;

namespace SomeEngine.Runtime;

/// <summary>
/// Carries main-thread camera output across the render update boundary. Frame recording observes
/// only the value published by prepare and never races a direct RenderWorld replacement.
/// </summary>
internal sealed class RuntimeViewPrepareSystem : ISystem<RenderPrepareSystemContext>
{
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateTrace =
        new("render", "Prepare.View");
    private readonly Entity _viewEntity;
    private readonly object _gate = new();
    private RenderView _view;
    private bool _hasView;

    internal RuntimeViewPrepareSystem(Entity viewEntity)
        => _viewEntity = viewEntity;

    internal void Stage(in RenderView view)
    {
        lock (_gate)
        {
            if (_hasView)
                throw new InvalidOperationException("The previous runtime view update was not prepared.");
            _view = view;
            _hasView = true;
        }
    }

    public void OnCreate(ref RenderPrepareSystemContext context)
    {
        if (!context.World.World.IsAlive(_viewEntity) || !context.World.World.Has<RenderView>(_viewEntity))
            throw new InvalidOperationException("The runtime view entity is not present in RenderWorld.");
    }

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        using var trace = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateTrace);
        RenderView next;
        lock (_gate)
        {
            if (!_hasView)
                return;
            next = _view;
            _view = default;
            _hasView = false;
        }

        if (context.World.World.ReadComponentData<RenderView>(_viewEntity) != next)
            context.World.World.Set(_viewEntity, next);
    }
}
