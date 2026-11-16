namespace SomeEngine.ECS.Serialization;

public readonly record struct AssetReference(Guid Value);

public interface IExternalResolver
{
    bool TryResolve(AssetReference id, out object value);
}
