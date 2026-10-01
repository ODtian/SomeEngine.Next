namespace SomeEngine.Assets.Schema;

public sealed partial class ClusterShadersBlock
{
    internal void Validate(string path)
    {
        IList<ClusterShaderOperation> operations = Operations
            ?? throw Invalid(path, "Operations is missing.");
        if (operations.Count == 0)
            throw Invalid(path, "Operations is empty.");

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
            if (shaders.Count is not (1 or 2) || shaders.Any(static shader => shader is null))
            {
                throw Invalid(
                    path,
                    $"{field}.Shaders must contain one or two entry references.");
            }

            Guid? operationShader = null;
            for (int shaderIndex = 0; shaderIndex < shaders.Count; shaderIndex++)
            {
                Guid shader = ShaderRef.Require(
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
            }
        }

        foreach (ClusterShaderOperationRole role in Enum.GetValues<ClusterShaderOperationRole>())
        {
            if (role != ClusterShaderOperationRole.None && !roles.Contains(role))
                throw Invalid(path, $"Operations has no '{role}' role.");
        }

    }


    private static InvalidDataException Invalid(string path, string message)
        => new($"Cluster render asset '{path}' {message}");

}
