using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Components;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Components;

/// <summary>One ordered material binding authored on a main-world mesh entity.</summary>
public readonly record struct MeshMaterialBinding(Material Material) : IBufferElement;

/// <summary>
/// One ordered set of extracted material values on a render-world mesh entity.
/// </summary>
public readonly record struct RenderMaterialBinding(MaterialData Material) : IBufferElement;
