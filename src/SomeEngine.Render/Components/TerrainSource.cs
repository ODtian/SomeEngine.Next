using SomeEngine.ECS;

namespace SomeEngine.Render.Components;

/// <summary>Selects terrain data requests for the existing game-to-render extraction.</summary>
public readonly record struct TerrainSource(Resources.Terrain Terrain) : IComponent;
