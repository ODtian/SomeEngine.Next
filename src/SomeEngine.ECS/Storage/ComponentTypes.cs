using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS
{
    public interface IComponent { }
    public interface ITag : IComponent { }
    public interface IEnableableComponent : IComponent { }
    public interface ITrackedComponent : IComponent { }
    public interface IAddedComponent : ITrackedComponent { }
    public interface IRemovedComponent : IComponent { }
    public interface ICleanupComponent : IComponent { }
    public interface ISharedComponent : IComponent { }
}

namespace SomeEngine.ECS.Storage
{
    public abstract class ComponentType
    {
        public readonly int Id;
        internal object? SerializationDescription;
        public static ComponentType Of<T>() where T : struct => ComponentValues.Bind<T>();
        internal readonly int Size;
        internal readonly bool ContainsReferences;
        internal readonly bool IsTag;
        internal readonly bool TracksWrites;
        internal readonly bool TracksAdded;
        internal readonly bool KeepsRemoved;
        internal readonly bool IsCleanup;
        internal readonly bool HasValueOperations;
        internal readonly bool IsShared;
        internal abstract bool IsSparse { get; }
        internal readonly bool IsIndexed;
        internal readonly bool IsGrouped;
        internal readonly bool IsComputed;
        internal readonly bool InitializesEntity;
        internal readonly bool HasEntityReferences;

        protected ComponentType(int size, bool containsReferences, bool isTag, bool tracksWrites,
            bool tracksAdded, bool keepsRemoved, bool isCleanup, bool hasValueOperations, bool isShared, bool isIndexed, bool isGrouped, bool isComputed, bool initializesEntity, bool hasEntityReferences)
        {
            Id = ComponentTypes.Register(this);
            Size = size;
            ContainsReferences = containsReferences;
            IsTag = isTag;
            TracksWrites = tracksWrites;
            TracksAdded = tracksAdded;
            KeepsRemoved = keepsRemoved;
            IsCleanup = isCleanup;
            HasValueOperations = hasValueOperations;
            IsShared = isShared;
            IsIndexed = isIndexed;
            IsGrouped = isGrouped;
            IsComputed = isComputed;
            InitializesEntity = initializesEntity;
            HasEntityReferences = hasEntityReferences;
        }

        internal abstract ColumnEntry Allocate(int capacity, int columnPlusOne);
        internal abstract bool UsesStructureWrites { get; }
        internal abstract bool IsJobStorageAliasFree { get; }
        internal abstract void Grow(ref ColumnEntry column, int count, int capacity);
        internal abstract void Copy(ref ColumnEntry source, int sourceRow, ref ColumnEntry target, int targetRow);
        internal abstract void Clear(ref ColumnEntry column, int row);
        internal abstract void Remove(World world, Entities.Entity entity, ref Entities.EntityRecord record);
        internal abstract void Release(World world, ref ColumnEntry column, int row);
        internal abstract bool HasColumnRelease { get; }
        internal abstract void ReleaseRows(World world, ref ColumnEntry column, int start, int count);
        internal abstract void Clone(World world, ref ColumnEntry source, int sourceRow, ref ColumnEntry target, int targetRow);
        internal abstract void InitializeEntity(World world, Entities.Entity entity);
        internal abstract void Remap(ref ColumnEntry column, int row, in EntityMap entities);
        internal abstract int InternShared(World source, World target, in ColumnEntry column, int row, in EntityMap entities);
        internal IRemovedValues? RemovedValues;
        internal ComponentType RemovedType => RemovedValues!.Type;
    }

    internal static class ComponentTypes
    {
        private static readonly List<ComponentType> Types = [];
        internal static int Count => Types.Count;
        internal static int Register(ComponentType type)
        {
            lock (Types)
            {
                int id = Types.Count;
                Types.Add(type);
                return id;
            }
        }
        internal static ComponentType Get(int id) => Types[id];
    }

    internal sealed unsafe class ComponentType<T> : ComponentType where T : struct
    {
        internal override bool IsSparse => IsSparseType;
        internal static readonly bool IsSparseType = typeof(Components.ISparseComponent).IsAssignableFrom(typeof(T));
        internal override bool UsesStructureWrites => IsControlledRemovalType || IsSharedType || IsIndexedType || IsGroupedType;
        internal override bool IsJobStorageAliasFree => IsControlledRemovalType || (Operations is IBufferJobElements buffer
            ? buffer.IsAliasFree : Registry.JobStorageTypeMetadata<T>.IsAliasFree);
        private static readonly IComponentOperations<T>? Operations = ComponentOperations.Create<T>();
        private static readonly IComponentInitialization<T>? Initialization = ComponentInitializers.Create<T>();
        internal static readonly ComponentType<T> Value = new();
        internal static readonly int TypeId = Value.Id;
        internal static readonly bool IsTagType = Value.IsTag;
        internal static readonly bool IsEnableableType = typeof(IEnableableComponent).IsAssignableFrom(typeof(T));
        internal static readonly bool TracksWritesType = Value.TracksWrites;
        internal static readonly bool TracksAddedType = Value.TracksAdded;
        internal static readonly bool KeepsRemovedType = Value.KeepsRemoved;
        internal static readonly bool IsCleanupType = Value.IsCleanup;
        internal static readonly bool HasValueOperationsType = Operations is not null;
        internal static readonly bool IsSharedType = Value.IsShared;
        internal static readonly bool IsIndexedType = Value.IsIndexed;
        internal static readonly bool IsGroupedType = Value.IsGrouped;
        internal static readonly bool HasInitializerType = Initialization is not null;
        internal static readonly bool IsControlledRemovalType = typeof(IControlledComponent).IsAssignableFrom(typeof(T));
        internal static readonly bool IsControlledType = ComponentOperations.IsBuffer(typeof(T)) || IsSharedType || IsControlledRemovalType;

        private ComponentType() : base(Unsafe.SizeOf<T>(),
            RuntimeHelpers.IsReferenceOrContainsReferences<T>(), typeof(ITag).IsAssignableFrom(typeof(T)),
            ComponentOperations.Declares<ITrackedComponent>(typeof(T)), ComponentOperations.Declares<IAddedComponent>(typeof(T)),
            ComponentOperations.Declares<IRemovedComponent>(typeof(T)), ComponentOperations.Declares<ICleanupComponent>(typeof(T)),
            Operations is not null, typeof(ISharedComponent).IsAssignableFrom(typeof(T)),
            typeof(T).GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIndexedComponent<>)),
            typeof(IGroupComponent).IsAssignableFrom(typeof(T)), typeof(IComputedComponent).IsAssignableFrom(typeof(T)), Initialization is not null,
            EntityFields<T>.Operations is not null) { }

        internal override ColumnEntry Allocate(int capacity, int columnPlusOne)
        {
            ColumnEntry column = new() { ColumnPlusOne = columnPlusOne };
            if (IsTag) return column;
            if (IsShared)
            {
                int[] ids = GC.AllocateArray<int>(capacity, pinned: true);
                column.Values = ids;
                column.Rows = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(ids));
                return column;
            }
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                column.Values = new T[capacity];
            else
            {
                byte[] bytes = GC.AllocateArray<byte>(checked(capacity * Size + 63), pinned: true);
                column.Values = bytes;
                nuint address = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(bytes));
                column.Rows = (nint)((address + 63) & ~(nuint)63);
            }
            return column;
        }

        internal override void Grow(ref ColumnEntry column, int count, int capacity)
        {
            ColumnEntry replacement = Allocate(capacity, column.ColumnPlusOne);
            if (IsShared)
                Unsafe.As<int[]>(column.Values!).AsSpan(0, count).CopyTo(Unsafe.As<int[]>(replacement.Values!));
            else if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Unsafe.As<T[]>(column.Values!).AsSpan(0, count).CopyTo(Unsafe.As<T[]>(replacement.Values!));
            else
                new ReadOnlySpan<byte>((void*)column.Rows, count * Size)
                    .CopyTo(new Span<byte>((void*)replacement.Rows, count * Size));
            replacement.Presence = column.Presence;
            replacement.GroupPlusOne = column.GroupPlusOne;
            column = replacement;
        }

        internal override void Copy(ref ColumnEntry source, int sourceRow, ref ColumnEntry target, int targetRow)
            => ColumnEntry.At<T>(ref target, targetRow) = ColumnEntry.At<T>(ref source, sourceRow);

        internal override void Clear(ref ColumnEntry column, int row)
            => ColumnEntry.At<T>(ref column, row) = default;

        internal override void Remove(World world, Entities.Entity entity, ref Entities.EntityRecord record)
            => world.RemoveValueKnown<T>(entity, ref record);

        internal static void ValidateCreate(World world, in T value) => Initialization?.ValidateCreate(world, value);
        internal static void Initialize(World world, Entities.Entity entity) => Initialization?.Initialize(world, entity);
        internal override void InitializeEntity(World world, Entities.Entity entity) => Initialize(world, entity);
        internal override void Remap(ref ColumnEntry column, int row, in EntityMap entities)
            => EntityFields<T>.Operations!.Remap(ref ColumnEntry.At<T>(ref column, row), entities);
        internal override int InternShared(World source, World target, in ColumnEntry column, int row, in EntityMap entities)
        {
            T value = source.ReadSharedColumn<T>(column, row);
            EntityFields<T>.Operations?.Remap(ref value, entities);
            return target.InternSharedValue(value);
        }

        internal static T CloneValue(World world, in T value) => Operations is { } operations ? operations.Clone(world, value) : value;
        internal static void ReleaseValue(World world, ref T value) => Operations?.Release(world, ref value);

        internal override void Release(World world, ref ColumnEntry column, int row)
            => ReleaseValue(world, ref ColumnEntry.At<T>(ref column, row));

        internal override bool HasColumnRelease => Operations is IComponentColumnOperations<T>;

        internal override void ReleaseRows(World world, ref ColumnEntry column, int start, int count)
        {
            Span<T> values = ColumnEntry.Span<T>(ref column, start + count).Slice(start, count);
            IComponentOperations<T> operations = Operations!;
            if (column.Presence is null)
            {
                if (operations is IComponentColumnOperations<T> columns) columns.Release(world, values);
                else foreach (ref T value in values) operations.Release(world, ref value);
            }
            else
                for (int row = 0; row < count; row++)
                    if (column.Has(start + row)) operations.Release(world, ref values[row]);
        }

        internal override void Clone(World world, ref ColumnEntry source, int sourceRow, ref ColumnEntry target, int targetRow)
        {
            if (IsShared) Unsafe.As<int[]>(target.Values!)[targetRow] = Unsafe.As<int[]>(source.Values!)[sourceRow];
            else if (!IsTag) ColumnEntry.At<T>(ref target, targetRow) = CloneValue(world, ColumnEntry.At<T>(ref source, sourceRow));
        }
    }

    internal static class TypeSetIds
    {
        private static int _next;
        internal static int New() => Interlocked.Increment(ref _next);
    }

    internal sealed class TypeSet : IEquatable<TypeSet>
    {
        internal readonly int Id;
        internal readonly int[] Types;
        internal readonly ComponentBits Bits;
        private readonly int _hash;
        private ComponentColumns? _columns;
        internal ComponentColumns Columns => _columns ?? BindColumns();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ComponentColumns BindColumns()
        {
            var columns = new ComponentColumns(Types);
            return Interlocked.CompareExchange(ref _columns, columns, null) ?? columns;
        }

        internal TypeSet(params int[] types) : this(true, types) { }

        internal TypeSet(bool cacheById, params int[] types)
        {
            Id = cacheById ? TypeSetIds.New() : 0;
            Types = types;
            Array.Sort(Types);
            var hash = new HashCode();
            foreach (int type in Types)
            {
                Bits.Add(type);
                hash.Add(type);
            }
            _hash = hash.ToHashCode();
        }

        public bool Equals(TypeSet? other)
            => other is not null && Types.AsSpan().SequenceEqual(other.Types);
        public override bool Equals(object? other) => other is TypeSet set && Equals(set);
        public override int GetHashCode() => _hash;
    }

    internal static class TypeSet<T> where T : struct
    {
        internal static readonly TypeSet Value = new(ComponentValues.Bind<T>().Id);
    }

    internal static class TypeSet<T1, T2, T3>
        where T1 : struct where T2 : struct where T3 : struct
    {
        internal static readonly TypeSet Value = new(
            ComponentValues.Bind<T1>().Id, ComponentValues.Bind<T2>().Id, ComponentValues.Bind<T3>().Id);
    }
}
