using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Render.Materials;

public readonly record struct MaterialPass(
    string Target,
    Shader Shader,
    string EntryPoint,
    MaterialState State);

