namespace SomeEngine.ECS.Components;

/// <summary>
/// Bundle initialization payload for SharedComponent values.
/// </summary>
public readonly struct SharedComponentValue<T>
    where T : struct, ISharedComponent
{
    private readonly T _value;

    public SharedComponentValue(in T value)
    {
        _value = value;
    }

    public T Value => _value;

    public static implicit operator SharedComponentValue<T>(T value) => new(value);
}

/// <summary>
/// Source-generator payload used to route shared bundle allocation before row writes.
/// </summary>
public readonly struct SharedValueSlot
{
    public SharedValueSlot(int componentId, int sharedIndex)
    {
        ComponentId = componentId;
        SharedIndex = sharedIndex;
    }

    public int ComponentId { get; }

    public int SharedIndex { get; }
}

