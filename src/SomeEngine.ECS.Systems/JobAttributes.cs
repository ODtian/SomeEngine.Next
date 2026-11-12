namespace SomeEngine.ECS.Systems;

public enum JobStorage { Table, Sparse, Resource }

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class JobReadAttribute(Type type, JobStorage storage = JobStorage.Table) : Attribute
{
    public Type Type { get; } = type;
    public JobStorage Storage { get; } = storage;
}

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
public sealed class JobWriteAttribute(Type type, JobStorage storage = JobStorage.Table) : Attribute
{
    public Type Type { get; } = type;
    public JobStorage Storage { get; } = storage;
}
