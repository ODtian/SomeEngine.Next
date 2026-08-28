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

            IList<ShaderRef> shaders = operation.Shaders
                ?? throw Invalid(path, $"{field}.Shaders is missing.");
            bool isCompute =
                shaders.Count == 1 &&
                shaders[0] is not null &&
                shaders[0].Stage == ShaderStage.Compute;
            bool isRaster =
                shaders.Count == 2 &&
                shaders.Count(shader => shader is not null && shader.Stage == ShaderStage.Vertex) == 1 &&
                shaders.Count(shader => shader is not null && shader.Stage == ShaderStage.Pixel) == 1;
            if (!isCompute && !isRaster)
            {
                throw Invalid(
                    path,
                    $"{field}.Shaders must contain either one compute entry or one vertex/pixel pair.");
            }

            AssetGuid? operationShader = null;
            for (int shaderIndex = 0; shaderIndex < shaders.Count; shaderIndex++)
            {
                AssetGuid shader = ShaderRef.Require(
                    shaders[shaderIndex],
                    $"Cluster render asset '{path}'",
                    $"{field}.Shaders[{shaderIndex}]");
                if (operationShader.HasValue && operationShader.Value != shader)
                {
                    throw Invalid(
                        path,
                        $"{field}.Shaders must reference entry points from one shader asset.");
                }
                operationShader = shader;
                if (!result.Contains(shader))
                    result.Add(shader);
            }
        }

        foreach (ClusterShaderOperationRole role in Enum.GetValues<ClusterShaderOperationRole>())
        {
            if (role != ClusterShaderOperationRole.None && !roles.Contains(role))
                throw Invalid(path, $"Operations has no '{role}' role.");
        }

        result.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        return result;
    }

    private static InvalidDataException Invalid(string path, string message)
        => new($"Cluster render asset '{path}' {message}");
}
