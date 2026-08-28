namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".material.asset")]
public partial class Material
{
    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        var result = new List<AssetGuid>();
        if (Passes is not null)
        {
            for (int index = 0; index < Passes.Count; index++)
            {
                AssetGuid shader = ShaderRef.Require(
                    Passes[index]?.Shader,
                    $"Material asset '{path}'",
                    $"Passes[{index}].Shader");
                if (!result.Contains(shader))
                    result.Add(shader);
            }
        }
        if (Textures is not null)
        {
            for (int index = 0; index < Textures.Count; index++)
                AddRequired(result, Textures[index]?.TextureGuid, path, $"Textures[{index}].TextureGuid");
        }
        result.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        return result;
    }

    private static void AddRequired(
        List<AssetGuid> result,
        string? value,
        string path,
        string field)
    {
        if (!global::SomeEngine.Assets.AssetGuid.TryParse(value, out AssetGuid guid) || guid.IsEmpty)
            throw new InvalidDataException($"Material asset '{path}' field '{field}' has an invalid asset GUID '{value}'.");
        if (!result.Contains(guid))
            result.Add(guid);
    }
}
