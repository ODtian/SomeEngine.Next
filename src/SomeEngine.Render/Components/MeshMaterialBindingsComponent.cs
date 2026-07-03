using System;
using SomeEngine.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

public struct MeshMaterialBindings : IComponent
{
    public ReadOnlyMemory<Handle<Material>> Materials;
}

