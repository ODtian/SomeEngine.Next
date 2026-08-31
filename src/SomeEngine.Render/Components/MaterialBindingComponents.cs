using SomeEngine.Assets.Schema;
using SomeEngine.ECS.Components;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Components;

/// <summary>One ordered material binding authored on a main-world mesh entity.</summary>
public readonly record struct MeshMaterialBinding(Material Material) : IBufferElement;

/// <summary>
/// One ordered material snapshot on a render-world mesh entity. Keeping this distinct from the
/// authoring element lets render systems evolve their ECS composition independently.
/// </summary>
public readonly record struct RenderMaterialBinding(Material Material) : IBufferElement;
