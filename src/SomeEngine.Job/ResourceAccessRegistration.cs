using System.Buffers;
using System.Runtime.CompilerServices;

namespace SomeEngine.Job;

internal readonly struct ResourceAccessRegistration
{
    internal static readonly ResourceAccessRegistration Empty =
        new(null);

    internal readonly ResourceAccessRegistrationData? Data;

    internal ResourceAccessRegistration(ResourceAccessRegistrationData? data)
    {
        Data = data;
    }

    internal int AccessCount => Data?.Accesses.Count ?? 0;

    internal int DependencyCount => Data?.Dependencies.Count ?? 0;

    internal ResourceManager.ActiveResourceAccess GetAccess(int index)
    {
        if ((uint)index >= (uint)AccessCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return Data!.Accesses.Get(index);
    }

    internal ResourceDependency GetDependency(int index)
    {
        if ((uint)index >= (uint)DependencyCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return Data!.Dependencies.Get(index);
    }
}

internal sealed class ResourceAccessRegistrationData
{
    internal AccessBuilder<ResourceManager.ActiveResourceAccess> Accesses { get; private set; }

    internal AccessBuilder<ResourceDependency> Dependencies { get; private set; }

    internal void Reset(
        AccessBuilder<ResourceManager.ActiveResourceAccess> accesses,
        AccessBuilder<ResourceDependency> dependencies)
    {
        Accesses = accesses;
        Dependencies = dependencies;
    }

    internal void Clear()
    {
        Accesses.Clear();
        Dependencies.Clear();
    }
}

internal struct AccessBuilder<T>
{
    internal const int InlineCapacity = 4;
    private const int InlineItem0 = 0;
    private const int InlineItem1 = 1;
    private const int InlineItem2 = 2;
    private const int InlineItem3 = 3;
    private const int OverflowGrowthFactor = 2;
    private const string MissingOverflowStorageMessage = "Overflow storage is missing.";
    private static readonly bool ContainsReferences = RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    private T _item0;
    private T _item1;
    private T _item2;
    private T _item3;
    private T[]? _many;

    internal int Count { get; private set; }

    internal void Clear()
    {
        _item0 = default!;
        _item1 = default!;
        _item2 = default!;
        _item3 = default!;
        if (_many is not null)
        {
            ArrayPool<T>.Shared.Return(_many, clearArray: ContainsReferences);
            _many = null;
        }

        Count = 0;
    }

    internal void Add(T item)
    {
        switch (Count)
        {
            case InlineItem0:
                _item0 = item;
                break;
            case InlineItem1:
                _item1 = item;
                break;
            case InlineItem2:
                _item2 = item;
                break;
            case InlineItem3:
                _item3 = item;
                break;
            case InlineCapacity:
                if (_many is null)
                {
                    _many = ArrayPool<T>.Shared.Rent(InlineCapacity * OverflowGrowthFactor);
                }

                _many[InlineItem0] = _item0;
                _many[InlineItem1] = _item1;
                _many[InlineItem2] = _item2;
                _many[InlineItem3] = _item3;
                _many[InlineCapacity] = item;
                break;
            default:
                if (Count == _many!.Length)
                {
                    GrowMany();
                }

                _many[Count] = item;
                break;
        }

        Count++;
    }

    private void GrowMany()
    {
        T[] current = _many ?? throw new InvalidOperationException(MissingOverflowStorageMessage);
        T[] grown = ArrayPool<T>.Shared.Rent(current.Length * OverflowGrowthFactor);
        Array.Copy(current, grown, Count);
        ArrayPool<T>.Shared.Return(current, clearArray: ContainsReferences);
        _many = grown;
    }

    internal readonly T Get(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (Count > InlineCapacity)
        {
            return _many![index];
        }

        return index switch
        {
            InlineItem0 => _item0,
            InlineItem1 => _item1,
            InlineItem2 => _item2,
            InlineItem3 => _item3,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}

internal readonly struct ResourceDependency
{
    internal ResourceDependency(JobHandle handle, bool waitForWorkOnly)
    {
        Handle = handle;
        WaitForWorkOnly = waitForWorkOnly;
    }

    internal JobHandle Handle { get; }

    internal bool WaitForWorkOnly { get; }
}

internal enum ResourceResolveOperation
{
    Access,
    Release
}

internal readonly struct ScopeOwnedResource
{
    internal ScopeOwnedResource(int id, int version, long generation, ResourceKind kind)
    {
        Id = id;
        Version = version;
        Generation = generation;
        Kind = kind;
    }

    internal int Id { get; }

    internal int Version { get; }

    internal long Generation { get; }

    internal ResourceKind Kind { get; }
}



