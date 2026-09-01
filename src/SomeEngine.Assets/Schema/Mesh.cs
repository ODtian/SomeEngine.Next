namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".mesh.asset")]
public partial class Mesh
{
    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        if (MaterialGuids is not { Count: > 0 })
            return [];

        var result = new List<AssetGuid>(MaterialGuids.Count);
        for (int index = 0; index < MaterialGuids.Count; index++)
        {
            string? value = MaterialGuids[index];
            if (!global::SomeEngine.Assets.AssetGuid.TryParse(
                    value,
                    out global::SomeEngine.Assets.AssetGuid material) || material.IsEmpty)
            {
                throw new InvalidDataException(
                    $"Mesh asset '{path}' MaterialGuids[{index}] is invalid.");
            }
            if (!result.Contains(material))
                result.Add(material);
        }
        result.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        return result;
    }
}
