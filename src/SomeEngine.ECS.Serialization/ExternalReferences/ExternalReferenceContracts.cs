namespace SomeEngine.ECS.Serialization;

public readonly record struct ExternalReferenceKey(Guid Value);

public interface IExternalResolver
{
    bool TryResolve(ExternalReferenceKey id, out object value);
}

