namespace SomeEngine.Assets.Schema;

public partial class ShaderData
{
    internal const uint Version = 8;
    internal IReadOnlyList<Guid> GetDependencies(string path) => [];
}
