namespace SomeEngine.ECS.Systems;

/// <summary>Whether a declared access names a component, whose type decides its storage, or a World resource.</summary>
public enum JobStorage { Component, Resource }

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class JobReadAttribute(Type type, JobStorage storage = JobStorage.Component) : Attribute
{
    public Type Type { get; } = type;
    public JobStorage Storage { get; } = storage;
}

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class JobWriteAttribute(Type type, JobStorage storage = JobStorage.Component) : Attribute
{
    public Type Type { get; } = type;
    public JobStorage Storage { get; } = storage;
}
