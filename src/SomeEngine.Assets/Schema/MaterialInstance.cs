namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".materialinstance.asset")]
public partial class MaterialInstance
{
    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        var result = new List<AssetGuid>();
        AddRequired(result, ParentGuid, path, nameof(ParentGuid));
        if (Overrides is not null)
        {
            for (int index = 0; index < Overrides.Count; index++)
                AddRequired(result, Overrides[index]?.TextureGuid, path, $"Overrides[{index}].TextureGuid");
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
            throw new InvalidDataException($"Material instance asset '{path}' field '{field}' has an invalid asset GUID '{value}'.");
        if (!result.Contains(guid))
            result.Add(guid);
    }
}
