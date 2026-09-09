namespace SomeEngine.Assets.Schema;

public partial class MeshData
{
    internal IReadOnlyList<Guid> GetDependencies(string path)
    {
        if (MaterialGuids is not { Count: > 0 })
            return [];

        var result = new List<Guid>(MaterialGuids.Count);
        for (int index = 0; index < MaterialGuids.Count; index++)
        {
            string? value = MaterialGuids[index];
            if (!global::System.Guid.TryParse(
                    value,
                    out global::System.Guid material) || (material == System.Guid.Empty))
            {
                throw new InvalidDataException(
                    $"Mesh asset '{path}' MaterialGuids[{index}] is invalid.");
            }
            if (!result.Contains(material))
                result.Add(material);
        }
        result.Sort(static (left, right) => left.CompareTo(right));
        return result;
    }
}
