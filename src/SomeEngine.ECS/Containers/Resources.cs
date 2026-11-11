using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.ECS;

internal static class ResourceTypes
{
    private static int _next;
    internal static int Next() => Interlocked.Increment(ref _next) - 1;
}

internal static class ResourceType<T>
{
    internal static readonly int Id = ResourceTypes.Next();
}

internal abstract class ResourceValue
{
    internal bool HasValue = true;
    internal abstract void Release();
    internal abstract ResourceValue Copy(ResourceValue? target = null);
}

internal sealed class ResourceReference(IDisposable value)
{
    private static readonly ConditionalWeakTable<object, ResourceReference> References = new();
    private readonly IDisposable _value = value;
    private int _count;

    internal static ResourceReference Retain(IDisposable value)
    {
        ResourceReference reference = References.GetValue(value, static item => new((IDisposable)item));
        Interlocked.Increment(ref reference._count);
        return reference;
    }

    internal bool Holds(object? value) => ReferenceEquals(_value, value);

    internal void Release()
    {
        if (Interlocked.Decrement(ref _count) != 0) return;
        References.Remove(_value);
        _value.Dispose();
    }
}

internal sealed class ResourceValue<T>(T value) : ResourceValue
{
    internal T Value = value;
    private ResourceReference? _reference = RetainReference(value);

    private static ResourceReference? RetainReference(in T value)
        => !typeof(T).IsValueType && value is IDisposable disposable ? ResourceReference.Retain(disposable) : null;

    private void SynchronizeReference()
    {
        // GetResource can replace Value through ref; reconcile it at the next transfer or release.
        if (typeof(T).IsValueType || _reference?.Holds(Value) == true) return;
        ResourceReference? previous = _reference;
        _reference = RetainReference(Value);
        previous?.Release();
    }

    internal override ResourceValue Copy(ResourceValue? target = null)
    {
        SynchronizeReference();
        T value = Value is ICloneable clone ? (T)clone.Clone() : Value;
        if (target is ResourceValue<T> reused) { reused.Replace(value); return reused; }
        return new ResourceValue<T>(value);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal override void Release()
    {
        SynchronizeReference();
        if (_reference is { } reference) { _reference = null; reference.Release(); }
        else if (typeof(T).IsValueType && Value is IDisposable disposable) disposable.Dispose();
        Value = default!;
        HasValue = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Replace(in T value)
    {
        T next = value;
        ResourceReference? reference = RetainReference(next);
        if (HasValue) Release();
        Value = next;
        _reference = reference;
        HasValue = true;
    }
}

[InlineArray(32)]
internal struct ResourceReferences
{
    private ResourceValue? _first;
}

// The first thirty-two type entries occupy the directory itself; higher type IDs use its tail.
internal struct ResourceEntries
{
    private ResourceReferences _first;
    private ResourceValue?[]? _tail;
    internal readonly int Capacity => 32 + (_tail?.Length ?? 0);

    internal readonly ResourceEntries Copy(in ResourceEntries target = default)
    {
        ResourceEntries copy = target;
        for (int type = 0; type < Capacity; type++)
            if (Get(type) is { HasValue: true } value) copy.Set(type, value.Copy(copy.Get(type)));
            else if (type < copy.Capacity) copy.Set(type, null);
        for (int type = Capacity; type < copy.Capacity; type++) copy.Set(type, null);
        return copy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly ResourceValue? Get(int type)
        => (uint)type < 32 ? _first[type] : GetTail(type - 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly ResourceValue? GetTail(int index)
    {
        ResourceValue?[]? tail = _tail;
        return tail is not null && (uint)index < (uint)tail.Length ? tail[index] : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Set(int type, ResourceValue? value)
    {
        if ((uint)type < 32) _first[type] = value;
        else SetTail(type - 32, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetTail(int index, ResourceValue? value)
    {
        ResourceValue?[]? tail = _tail;
        if (tail is null || index >= tail.Length) tail = GrowTail(index);
        tail[index] = value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ResourceValue?[] GrowTail(int index)
    {
        int capacity = _tail?.Length ?? 0;
        Array.Resize(ref _tail, Math.Max(index + 1, Math.Max(16, capacity * 2)));
        return _tail!;
    }

    internal void Clear(bool keepEntries = false)
    {
        for (int i = 0; i < 32; i++)
        {
            if (_first[i] is { HasValue: true } value) { value.Release(); value.HasValue = false; }
            if (!keepEntries) _first[i] = null;
        }
        if (_tail is { } tail)
        {
            foreach (ResourceValue? value in tail)
                if (value is { HasValue: true }) { value.Release(); value.HasValue = false; }
            if (!keepEntries) Array.Clear(tail);
        }
    }
}

public sealed partial class World
{
    private ResourceEntries _resources;

    public bool HasResource<T>()
    {
        using WorldAccess access = OpenStructure(false);
        return _resources.Get(ResourceType<T>.Id) is { HasValue: true };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T ReadResource<T>()
    {
        if (Execution is { } execution) RequireResourceValue<T>(execution, false);
        return ref Resource<T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetResource<T>()
    {
        if (Execution is { } execution) RequireResourceValue<T>(execution, true);
        return ref Resource<T>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RequireResourceValue<T>(WorldExecution execution, bool write)
        => execution.RequireValue(new(ResourceType<T>.Id, WorldValueKind.Resource, write, false));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Resource<T>()
    {
        ResourceValue? stored = _resources.Get(ResourceType<T>.Id);
        if (stored is null || !stored.HasValue) MissingResource();
        return ref Unsafe.As<ResourceValue<T>>(stored).Value;
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MissingResource() => throw new ArgumentException("World does not have this resource.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetResource<T>(in T value)
    {
        if (Execution is not null) { SetResourceWithExecution(value); return; }
        SetResourceCore(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetResourceCore<T>(in T value)
    {
        int type = ResourceType<T>.Id;
        ref ResourceEntries resources = ref _resources;
        if (resources.Get(type) is { } existing)
        {
            if (existing.HasValue) Unsafe.As<ResourceValue<T>>(existing).Replace(value);
            else
            {
                RequireStructure();
                Unsafe.As<ResourceValue<T>>(existing).Replace(value);
            }
            return;
        }
        RequireStructure();
        resources.Set(type, new ResourceValue<T>(value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool RemoveResource<T>()
    {
        if (Execution is not null) return RemoveResourceWithExecution<T>();
        return RemoveResourceCore<T>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool RemoveResourceWithExecution<T>()
    {
        using WorldAccess access = OpenStructure();
        return RemoveResourceCore<T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RemoveResourceCore<T>()
    {
        RequireStructure();
        int type = ResourceType<T>.Id;
        ref ResourceEntries resources = ref _resources;
        if (resources.Get(type) is not { HasValue: true } value) return false;
        value.HasValue = false;
        Unsafe.As<ResourceValue<T>>(value).Release();
        return true;
    }

    private void ReleaseResources() => _resources.Clear();
}
