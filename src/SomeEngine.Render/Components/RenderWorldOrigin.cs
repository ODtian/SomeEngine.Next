using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;

namespace SomeEngine.Render.Components;

/// <summary>World origin delivered at the same extraction boundary as mesh and light data.</summary>
public readonly record struct RenderWorldOrigin(WorldOrigin Value) : IComponent, ITrackedComponent;
