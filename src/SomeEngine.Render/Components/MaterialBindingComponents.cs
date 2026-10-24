using SomeEngine.Resources;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

/// <summary>One ordered material binding authored on a main-world mesh entity.</summary>
public readonly record struct MeshMaterialBinding(Material Material) : IBufferElement;

/// <summary>
/// One ordered reference to a shared material in the rendering world.
/// </summary>
public readonly record struct RenderMaterialBinding(long MaterialId) : IBufferElement;

/// <summary>One shared material reference registered for all meshes using its identifier.</summary>
public readonly record struct RenderMaterialReference(Material Material) : SomeEngine.ECS.IComponent;
