namespace SomeEngine.Resources;

/// <summary>Constructs the concrete material chosen by its caller.</summary>
public interface IMaterial<TMaterial> where TMaterial : Material
{
    static abstract TMaterial Create(string? name, Shader? shader,
        IReadOnlyList<(MaterialValue Value, Texture? Texture)> values);
    static abstract void Update(TMaterial material, TMaterial replacement);
}
