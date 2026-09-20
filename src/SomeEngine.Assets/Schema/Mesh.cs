namespace SomeEngine.Assets.Schema;

internal partial class MeshData
{
    internal void ValidateMaterialReferences(string path)
    {
        if (MaterialGuids is not { Count: > 0 })
            return;

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
        }
    }
}
