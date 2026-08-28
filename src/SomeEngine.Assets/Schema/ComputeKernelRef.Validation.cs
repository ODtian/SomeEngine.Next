namespace SomeEngine.Assets.Schema;

internal static class ComputeKernelRefValidation
{
    internal static AssetGuid Require(
        ComputeKernelRef? kernel,
        string assetPath,
        string field)
    {
        if (kernel is null || string.IsNullOrWhiteSpace(kernel.EntryPoint))
        {
            throw new InvalidDataException(
                $"Compute algorithm asset '{assetPath}' has no {field} entry point.");
        }
        if (!AssetGuid.TryParse(kernel.Shader?.ShaderGuid, out AssetGuid shader) || shader.IsEmpty)
        {
            throw new InvalidDataException(
                $"Compute algorithm asset '{assetPath}' has an invalid {field} shader GUID.");
        }
        return shader;
    }
}
