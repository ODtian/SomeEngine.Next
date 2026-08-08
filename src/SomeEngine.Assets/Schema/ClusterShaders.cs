namespace SomeEngine.Assets.Schema;

[global::SomeEngine.Assets.Asset(".clusterrender.asset")]
public partial class ClusterShaders
{
    internal IReadOnlyList<AssetGuid> GetDependencies(string path)
    {
        IList<ClusterShaderOperation> operations = Operations
            ?? throw Invalid(path, "Operations is missing.");
        if (operations.Count == 0)
            throw Invalid(path, "Operations is empty.");

        var result = new List<AssetGuid>();
        var roles = new HashSet<ClusterShaderOperationRole>();
        for (int index = 0; index < operations.Count; index++)
        {
            ClusterShaderOperation operation = operations[index]
                ?? throw Invalid(path, $"Operations[{index}] is null.");
            string field = $"Operations[{index}]";
            if (operation.Role == ClusterShaderOperationRole.None
                || !Enum.IsDefined(operation.Role))
            {
                throw Invalid(path, $"{field}.Role '{operation.Role}' is invalid.");
            }
            if (!roles.Add(operation.Role))
                throw Invalid(path, $"{field}.Role '{operation.Role}' is duplicated.");

            bool hasCompute = !string.IsNullOrWhiteSpace(operation.ComputeEntryPoint);
            bool hasVertex = !string.IsNullOrWhiteSpace(operation.VertexEntryPoint);
            bool hasPixel = !string.IsNullOrWhiteSpace(operation.PixelEntryPoint);
            bool isCompute = hasCompute && !hasVertex && !hasPixel;
            bool isRaster = !hasCompute && hasVertex && hasPixel;
            if (!isCompute && !isRaster)
            {
                throw Invalid(
                    path,
                    $"{field} must declare either one compute entry point or one vertex/pixel pair.");
            }

            AddRequired(result, operation.Shader?.ShaderGuid, path, $"{field}.Shader");
        }

        foreach (ClusterShaderOperationRole role in Enum.GetValues<ClusterShaderOperationRole>())
        {
            if (role != ClusterShaderOperationRole.None && !roles.Contains(role))
                throw Invalid(path, $"Operations has no '{role}' role.");
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
        {
            throw Invalid(path, $"{field} has invalid Shader GUID '{value}'.");
        }
        if (!result.Contains(guid))
            result.Add(guid);
    }

    private static InvalidDataException Invalid(string path, string message)
        => new($"Cluster render asset '{path}' {message}");
}
