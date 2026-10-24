using SomeEngine.ECS;
using SomeEngine.Resources;

namespace SomeEngine.Render.Components;

/// <summary>A material layer projected inside the entity transform's unit box, in ascending order.</summary>
public readonly record struct MaterialDecal(Material Material, int Order = 0) : IComponent;

/// <summary>A material layer and its order extracted for the rendering world.</summary>
public readonly record struct RenderMaterialDecal(Material Material, int Order) : IComponent;
