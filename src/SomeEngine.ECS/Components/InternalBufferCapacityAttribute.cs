namespace SomeEngine.ECS.Components;

/// <summary>
/// 指定 DynamicBuffer 的 inline 容量。未标注时默认为 8 个元素。
/// </summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class BufferCapacityAttribute : Attribute
{
    public int Capacity { get; }

    public BufferCapacityAttribute(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be >= 1.");
        Capacity = capacity;
    }
}

