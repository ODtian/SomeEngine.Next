using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Registry;

public static unsafe class ComponentMetadata<T> where T : struct
{
    public static readonly int Id;
    public static readonly int Size;
    public static readonly StoragePath Storage;
    public static readonly bool ContainsReferences;

    public static readonly bool IsCleanup;
    public static readonly bool IsEnableable;
    public static readonly bool IsIndexed;

    internal static readonly ComponentOperations Operations;

    static ComponentMetadata()
    {
        Id = ComponentTypeCounter.Next();
        Storage = DetectStorage();
        ContainsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        if (Storage == StoragePath.Table)
        {
            IsCleanup = default(T) is ICleanupComponent;
            IsEnableable = default(T) is IEnableableComponent;
            IsIndexed = default(T) is IIndexedComponent;
        }

        Size = Unsafe.SizeOf<T>();
        Operations = CreateOperations();

        ComponentRegistry.Register(Id, new ComponentInfo
        {
            Id = Id,
            Type = typeof(T),
            Size = Size,
            Storage = Storage,
            ContainsReferences = ContainsReferences,
            IsCleanup = IsCleanup,
            IsEnableable = IsEnableable,
            IsIndexed = IsIndexed,
            IsRelationTag = default(T) is IRelationTag,
            Operations = Operations,
        });
    }

    private static ComponentOperations CreateOperations()
    {
        return new ComponentOperations
        {
            CopyElement = &DoCopy,
            SwapRemove = &DoSwapRemove,
            CreateArray = &DoCreateArray,
        };
    }

    internal static void DoCopy(object source, int sourceIndex, object destination, int destinationIndex)
    {
        var sourceArray = Unsafe.As<T[]>(source);
        var destinationArray = Unsafe.As<T[]>(destination);
        destinationArray[destinationIndex] = sourceArray[sourceIndex];
    }

    internal static void DoSwapRemove(object column, int removeIndex, int lastIndex)
    {
        var array = Unsafe.As<T[]>(column);
        if (removeIndex != lastIndex)
            array[removeIndex] = array[lastIndex];

        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            array[lastIndex] = default;
    }

    internal static object DoCreateArray(int capacity)
    {
        return new T[capacity];
    }

    private static StoragePath DetectStorage()
    {
        if (default(T) is IExclusiveRelation)
            return StoragePath.ExclusiveRelation;

        if (default(T) is IRelation)
            return StoragePath.Relation;

        if (default(T) is ISparseComponent)
            return StoragePath.Sparse;

        if (default(T) is ITag)
            return StoragePath.Tag;

        if (default(T) is ISharedComponent)
            return StoragePath.Shared;

        if (default(T) is IBufferElement)
        {
            throw new InvalidOperationException(
                $"Buffer element type {typeof(T).Name} is not a standalone archetype component. " +
                "Use World.AddBuffer<T>() / World.GetBuffer<T>() or buffer-specific query APIs.");
        }

        if (default(T) is IComponent)
            return StoragePath.Table;

        throw new InvalidOperationException(
            $"Type {typeof(T).Name} does not implement any known component interface " +
            "(IComponent, ITag, ISparseComponent, IRelation, IExclusiveRelation, ISharedComponent, IBufferElement).");
    }
}

