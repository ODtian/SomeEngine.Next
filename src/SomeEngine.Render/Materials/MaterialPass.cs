using SomeEngine.Assets;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Materials;

public readonly record struct MaterialPass(
    string Target,
    AssetHandle<Shader> Shader,
    string EntryPoint,
    MaterialState State);

