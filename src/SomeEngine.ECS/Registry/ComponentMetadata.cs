using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Owners;
using ICleanupComponent = global::SomeEngine.ECS.ICleanupComponent;
using IComponent = global::SomeEngine.ECS.IComponent;
using IEnableableComponent = global::SomeEngine.ECS.IEnableableComponent;

namespace SomeEngine.ECS.Registry;

public static unsafe class ComponentMetadata<T> where T : struct
{
    public static readonly int Id;
    public static readonly int Size;
    public static readonly StoragePath Storage;
    public static readonly bool ContainsReferences;

    public static readonly bool IsCleanup;
    public static readonly bool IsRemovedFact;
    public static readonly bool IsEnableable;
    public static readonly bool IsIndexed;
    public static readonly bool IsRelationshipSource;
    public static readonly bool IsRelationshipTarget;
    internal static readonly bool IsBufferStorage;
    internal static readonly IHierarchyComponentRegistration? HierarchyRegistration;
    public static readonly bool AllowsPublicStructuralMutation;
    public static readonly bool AllowsPublicValueMutation;

    internal static readonly ComponentOperations Operations;

    static ComponentMetadata()
    {
        Id = ComponentTypeCounter.Next();
        Storage = DetectStorage();
        ContainsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();
        IsRelationshipSource = default(T) is IRelationshipSource;
        IsRelationshipTarget = default(T) is IRelationshipTarget;
        IsBufferStorage = default(T) is IBufferStorageComponent;
        HierarchyRegistration = default(T) is IHierarchyComponentRegistration registration
            ? registration
            : null;

        ValidateRelationshipRole();

        AllowsPublicStructuralMutation = !IsRelationshipSource && !IsRelationshipTarget;
        AllowsPublicValueMutation = !IsRelationshipSource && !IsRelationshipTarget;

        if (Storage == StoragePath.Table)
        {
            IsCleanup = default(T) is ICleanupComponent;
            IsRemovedFact = typeof(T).IsGenericType &&
                            typeof(T).GetGenericTypeDefinition() == typeof(Removed<>);
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
            IsRemovedFact = IsRemovedFact,
            IsEnableable = IsEnableable,
            IsIndexed = IsIndexed,
            IsRelationshipSource = IsRelationshipSource,
            IsRelationshipTarget = IsRelationshipTarget,
            IsBufferStorage = IsBufferStorage,
            HierarchyRegistration = HierarchyRegistration,
            AllowsPublicStructuralMutation = AllowsPublicStructuralMutation,
            AllowsPublicValueMutation = AllowsPublicValueMutation,
            Operations = Operations,
        });
    }

    private static void ValidateRelationshipRole()
    {
        if (IsRelationshipSource && IsRelationshipTarget)
        {
            throw new InvalidOperationException(
                $"Component type {typeof(T).Name} cannot be both {nameof(IRelationshipSource)} " +
                $"and {nameof(IRelationshipTarget)}.");
        }

        if ((IsRelationshipSource || IsRelationshipTarget) && Storage != StoragePath.Table)
        {
            throw new InvalidOperationException(
                $"Relationship component type {typeof(T).Name} must use {StoragePath.Table} storage, " +
                $"but its detected storage path is {Storage}.");
        }

        if ((IsRelationshipSource || IsRelationshipTarget) && default(T) is IEnableableComponent)
        {
            throw new InvalidOperationException(
                $"Relationship component type {typeof(T).Name} cannot be enableable. " +
                "Relationship presence is represented by canonical component presence, not an enable bit.");
        }

        if ((IsRelationshipSource || IsRelationshipTarget) && default(T) is ICleanupComponent)
        {
            throw new InvalidOperationException(
                $"Relationship component type {typeof(T).Name} cannot be a cleanup component. " +
                "Relationship teardown is owned by its typed lifecycle kernel.");
        }
    }

    private static ComponentOperations CreateOperations()
    {
        return new ComponentOperations
        {
            GetReference = &DoGetReference,
            CopyValue = &DoCopy,
            SwapRemove = &DoSwapRemove,
            CreateArray = &DoCreateArray,
        };
    }

    internal static ref byte DoGetReference(object column, int row)
    {
        return ref Unsafe.As<T, byte>(ref Unsafe.As<T[]>(column)[row]);
    }

    internal static void DoCopy(ref byte source, ref byte destination)
    {
        Unsafe.As<byte, T>(ref destination) = Unsafe.As<byte, T>(ref source);
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
                "Use World.AddBuffer<T>(), World.ExecuteBufferRead/Write<T>(), or buffer-specific query APIs.");
        }

        if (default(T) is IComponent)
            return StoragePath.Table;

        throw new InvalidOperationException(
            $"Type {typeof(T).Name} does not implement any known component interface " +
            "(IComponent, ITag, ISparseComponent, ISharedComponent, IBufferElement).");
    }
}

