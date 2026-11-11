using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
namespace SomeEngine.ECS.Comparison;
internal sealed class ResourceCounter { internal long Count; }
internal struct ResourceData<T>(ResourceCounter counter, long value) : EasyEcs.ISingletonComponent, IDisposable where T : struct
{
    private ResourceCounter _counter = counter;
    internal long Value = value;
    internal long Second = 0, Third = 0;
    public void Dispose() => _counter.Count++;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long ReplaceValue(long value)
    {
        long previous = Value;
        Dispose();
        Value = value;
        Second = Third = 0;
        return previous;
    }
}
internal delegate void DisposeResource<T>(ref T value);
internal static class ResourceDisposal
{
    private static int _next;
    internal static int Next() => Interlocked.Increment(ref _next) - 1;
    internal static void Release<T>(ref T value) where T : IDisposable { value.Dispose(); value = default!; }
}
internal static class ResourceDataType<T>
{
    internal static readonly int Id = ResourceDisposal.Next();
    internal static readonly DisposeResource<T> Dispose = typeof(ResourceDisposal)
        .GetMethod(nameof(ResourceDisposal.Release), BindingFlags.Static | BindingFlags.NonPublic)!
        .MakeGenericMethod(typeof(T)).CreateDelegate<DisposeResource<T>>();
}
internal abstract class ReferenceResourceBox { internal abstract void Release(); }
internal static class ReferenceResourceTypes
{
    private static int _next;
    internal static int Next() => Interlocked.Increment(ref _next) - 1;
}
internal static class ReferenceResourceType<T>
{
    internal static readonly int Id = ReferenceResourceTypes.Next();
}
internal sealed class ReferenceResourceBox<T>(T value) : ReferenceResourceBox
{
    internal T Value = value;
    internal override void Release()
    {
        if (Value is IDisposable disposable) disposable.Dispose();
        Value = default!;
    }
}
internal sealed class ReferenceResources
{
    private ReferenceResourceBox?[] _values = [];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Read<T>()
    {
        ReferenceResourceBox?[] values = _values;
        int id = ReferenceResourceType<T>.Id;
        ReferenceResourceBox? value = (uint)id < (uint)values.Length ? values[id] : null;
        if (value is null) Missing();
        return ref Unsafe.As<ReferenceResourceBox<T>>(value).Value;
    }
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Missing() => throw new ArgumentException("Resource is absent.");
    internal void Set<T>(in T value)
    {
        int id = ReferenceResourceType<T>.Id;
        if ((uint)id < (uint)_values.Length && _values[id] is { } existing)
        {
            ReferenceResourceBox<T> stored = Unsafe.As<ReferenceResourceBox<T>>(existing);
            stored.Release();
            stored.Value = value;
            return;
        }
        if (id >= _values.Length) Array.Resize(ref _values, Math.Max(id + 1, Math.Max(16, _values.Length * 2)));
        _values[id] = new ReferenceResourceBox<T>(value);
    }
    internal void Remove<T>()
    {
        int id = ReferenceResourceType<T>.Id;
        if ((uint)id >= (uint)_values.Length || _values[id] is not { } value) return;
        _values[id] = null;
        value.Release();
    }
}
internal interface IResourceActions
{
    ref ResourceData<T> Get<T>() where T : struct;
    void Initialize<T>() where T : struct;
    long ReadSet<T>() where T : struct;
    void Rebuild<T>() where T : struct;
    void Release<T>() where T : struct;
}
internal static class ResourceSequence
{
    private struct GrowthKind<T> where T : struct { }
    internal static long Grow<TAccess>(ref TAccess access) where TAccess : struct, IResourceActions
    {
        access.Initialize<GrowthKind<Kind0>>();
        access.Initialize<GrowthKind<Kind1>>();
        access.Initialize<GrowthKind<Kind2>>();
        access.Initialize<GrowthKind<Kind3>>();
        access.Initialize<GrowthKind<Kind4>>();
        access.Initialize<GrowthKind<Kind5>>();
        access.Initialize<GrowthKind<Kind6>>();
        access.Initialize<GrowthKind<Kind7>>();
        access.Initialize<GrowthKind<Kind8>>();
        access.Initialize<GrowthKind<Kind9>>();
        access.Initialize<GrowthKind<Kind10>>();
        access.Initialize<GrowthKind<Kind11>>();
        access.Initialize<GrowthKind<Kind12>>();
        access.Initialize<GrowthKind<Kind13>>();
        access.Initialize<GrowthKind<Kind14>>();
        access.Initialize<GrowthKind<Kind15>>();
        long total = 0;
        total += access.ReadSet<GrowthKind<Kind0>>();
        total += access.ReadSet<GrowthKind<Kind1>>();
        total += access.ReadSet<GrowthKind<Kind2>>();
        total += access.ReadSet<GrowthKind<Kind3>>();
        total += access.ReadSet<GrowthKind<Kind4>>();
        total += access.ReadSet<GrowthKind<Kind5>>();
        total += access.ReadSet<GrowthKind<Kind6>>();
        total += access.ReadSet<GrowthKind<Kind7>>();
        total += access.ReadSet<GrowthKind<Kind8>>();
        total += access.ReadSet<GrowthKind<Kind9>>();
        total += access.ReadSet<GrowthKind<Kind10>>();
        total += access.ReadSet<GrowthKind<Kind11>>();
        total += access.ReadSet<GrowthKind<Kind12>>();
        total += access.ReadSet<GrowthKind<Kind13>>();
        total += access.ReadSet<GrowthKind<Kind14>>();
        total += access.ReadSet<GrowthKind<Kind15>>();
        access.Release<GrowthKind<Kind0>>();
        access.Release<GrowthKind<Kind1>>();
        access.Release<GrowthKind<Kind2>>();
        access.Release<GrowthKind<Kind3>>();
        access.Release<GrowthKind<Kind4>>();
        access.Release<GrowthKind<Kind5>>();
        access.Release<GrowthKind<Kind6>>();
        access.Release<GrowthKind<Kind7>>();
        access.Release<GrowthKind<Kind8>>();
        access.Release<GrowthKind<Kind9>>();
        access.Release<GrowthKind<Kind10>>();
        access.Release<GrowthKind<Kind11>>();
        access.Release<GrowthKind<Kind12>>();
        access.Release<GrowthKind<Kind13>>();
        access.Release<GrowthKind<Kind14>>();
        access.Release<GrowthKind<Kind15>>();
        return total;
    }
    private struct Kind0 { }
    private struct Kind1 { }
    private struct Kind2 { }
    private struct Kind3 { }
    private struct Kind4 { }
    private struct Kind5 { }
    private struct Kind6 { }
    private struct Kind7 { }
    private struct Kind8 { }
    private struct Kind9 { }
    private struct Kind10 { }
    private struct Kind11 { }
    private struct Kind12 { }
    private struct Kind13 { }
    private struct Kind14 { }
    private struct Kind15 { }

    private ref struct ResourceRefs
    {
        private readonly ref ResourceData<Kind0> _kind0;
        private readonly ref ResourceData<Kind1> _kind1;
        private readonly ref ResourceData<Kind2> _kind2;
        private readonly ref ResourceData<Kind3> _kind3;
        private readonly ref ResourceData<Kind4> _kind4;
        private readonly ref ResourceData<Kind5> _kind5;
        private readonly ref ResourceData<Kind6> _kind6;
        private readonly ref ResourceData<Kind7> _kind7;
        private readonly ref ResourceData<Kind8> _kind8;
        private readonly ref ResourceData<Kind9> _kind9;
        private readonly ref ResourceData<Kind10> _kind10;
        private readonly ref ResourceData<Kind11> _kind11;
        private readonly ref ResourceData<Kind12> _kind12;
        private readonly ref ResourceData<Kind13> _kind13;
        private readonly ref ResourceData<Kind14> _kind14;
        private readonly ref ResourceData<Kind15> _kind15;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ResourceRefs Bind<TAccess>(ref TAccess access) where TAccess : struct, IResourceActions
            => new(ref access.Get<Kind0>(),
                ref access.Get<Kind1>(),
                ref access.Get<Kind2>(),
                ref access.Get<Kind3>(),
                ref access.Get<Kind4>(),
                ref access.Get<Kind5>(),
                ref access.Get<Kind6>(),
                ref access.Get<Kind7>(),
                ref access.Get<Kind8>(),
                ref access.Get<Kind9>(),
                ref access.Get<Kind10>(),
                ref access.Get<Kind11>(),
                ref access.Get<Kind12>(),
                ref access.Get<Kind13>(),
                ref access.Get<Kind14>(),
                ref access.Get<Kind15>());

        private ResourceRefs(ref ResourceData<Kind0> kind0,
            ref ResourceData<Kind1> kind1,
            ref ResourceData<Kind2> kind2,
            ref ResourceData<Kind3> kind3,
            ref ResourceData<Kind4> kind4,
            ref ResourceData<Kind5> kind5,
            ref ResourceData<Kind6> kind6,
            ref ResourceData<Kind7> kind7,
            ref ResourceData<Kind8> kind8,
            ref ResourceData<Kind9> kind9,
            ref ResourceData<Kind10> kind10,
            ref ResourceData<Kind11> kind11,
            ref ResourceData<Kind12> kind12,
            ref ResourceData<Kind13> kind13,
            ref ResourceData<Kind14> kind14,
            ref ResourceData<Kind15> kind15)
        {
            _kind0 = ref kind0;
            _kind1 = ref kind1;
            _kind2 = ref kind2;
            _kind3 = ref kind3;
            _kind4 = ref kind4;
            _kind5 = ref kind5;
            _kind6 = ref kind6;
            _kind7 = ref kind7;
            _kind8 = ref kind8;
            _kind9 = ref kind9;
            _kind10 = ref kind10;
            _kind11 = ref kind11;
            _kind12 = ref kind12;
            _kind13 = ref kind13;
            _kind14 = ref kind14;
            _kind15 = ref kind15;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal long ReadSet<T>() where T : struct
        {
            if (typeof(T) == typeof(Kind0)) return ReadSet(ref _kind0);
            if (typeof(T) == typeof(Kind1)) return ReadSet(ref _kind1);
            if (typeof(T) == typeof(Kind2)) return ReadSet(ref _kind2);
            if (typeof(T) == typeof(Kind3)) return ReadSet(ref _kind3);
            if (typeof(T) == typeof(Kind4)) return ReadSet(ref _kind4);
            if (typeof(T) == typeof(Kind5)) return ReadSet(ref _kind5);
            if (typeof(T) == typeof(Kind6)) return ReadSet(ref _kind6);
            if (typeof(T) == typeof(Kind7)) return ReadSet(ref _kind7);
            if (typeof(T) == typeof(Kind8)) return ReadSet(ref _kind8);
            if (typeof(T) == typeof(Kind9)) return ReadSet(ref _kind9);
            if (typeof(T) == typeof(Kind10)) return ReadSet(ref _kind10);
            if (typeof(T) == typeof(Kind11)) return ReadSet(ref _kind11);
            if (typeof(T) == typeof(Kind12)) return ReadSet(ref _kind12);
            if (typeof(T) == typeof(Kind13)) return ReadSet(ref _kind13);
            if (typeof(T) == typeof(Kind14)) return ReadSet(ref _kind14);
            return ReadSet(ref _kind15);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long ReadSet<T>(ref ResourceData<T> stored) where T : struct
            => stored.ReplaceValue(42);
    }
    internal static void Initialize<TAccess>(ref TAccess access) where TAccess : struct, IResourceActions
    {
        InitializeType<Kind0, TAccess>(ref access);
        InitializeType<Kind1, TAccess>(ref access);
        InitializeType<Kind2, TAccess>(ref access);
        InitializeType<Kind3, TAccess>(ref access);
        InitializeType<Kind4, TAccess>(ref access);
        InitializeType<Kind5, TAccess>(ref access);
        InitializeType<Kind6, TAccess>(ref access);
        InitializeType<Kind7, TAccess>(ref access);
        InitializeType<Kind8, TAccess>(ref access);
        InitializeType<Kind9, TAccess>(ref access);
        InitializeType<Kind10, TAccess>(ref access);
        InitializeType<Kind11, TAccess>(ref access);
        InitializeType<Kind12, TAccess>(ref access);
        InitializeType<Kind13, TAccess>(ref access);
        InitializeType<Kind14, TAccess>(ref access);
        InitializeType<Kind15, TAccess>(ref access);
    }
    private static void InitializeType<T, TAccess>(ref TAccess access)
        where T : struct where TAccess : struct, IResourceActions
    {
        access.Initialize<T>();
    }
    internal static void Release<TAccess>(ref TAccess access) where TAccess : struct, IResourceActions
    {
        access.Release<Kind0>();
        access.Release<Kind1>();
        access.Release<Kind2>();
        access.Release<Kind3>();
        access.Release<Kind4>();
        access.Release<Kind5>();
        access.Release<Kind6>();
        access.Release<Kind7>();
        access.Release<Kind8>();
        access.Release<Kind9>();
        access.Release<Kind10>();
        access.Release<Kind11>();
        access.Release<Kind12>();
        access.Release<Kind13>();
        access.Release<Kind14>();
        access.Release<Kind15>();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long ReadSetResources(ref ResourceRefs values)
    {
        long total = 0;
        for (int i = 0; i < ComparisonWorld.Lookups; i++)
            total += ((i * 7_919) & 15) switch
            {
                0 => values.ReadSet<Kind0>(),
                1 => values.ReadSet<Kind1>(),
                2 => values.ReadSet<Kind2>(),
                3 => values.ReadSet<Kind3>(),
                4 => values.ReadSet<Kind4>(),
                5 => values.ReadSet<Kind5>(),
                6 => values.ReadSet<Kind6>(),
                7 => values.ReadSet<Kind7>(),
                8 => values.ReadSet<Kind8>(),
                9 => values.ReadSet<Kind9>(),
                10 => values.ReadSet<Kind10>(),
                11 => values.ReadSet<Kind11>(),
                12 => values.ReadSet<Kind12>(),
                13 => values.ReadSet<Kind13>(),
                14 => values.ReadSet<Kind14>(),
                _ => values.ReadSet<Kind15>(),
            };
        return total;
    }

    internal static long Run<TAccess>(ref TAccess access) where TAccess : struct, IResourceActions
    {
        long total;
        {
            var values = ResourceRefs.Bind(ref access);
            total = ReadSetResources(ref values);
        }
        access.Rebuild<Kind0>();
        access.Rebuild<Kind1>();
        access.Rebuild<Kind2>();
        access.Rebuild<Kind3>();
        access.Rebuild<Kind4>();
        access.Rebuild<Kind5>();
        access.Rebuild<Kind6>();
        access.Rebuild<Kind7>();
        access.Rebuild<Kind8>();
        access.Rebuild<Kind9>();
        access.Rebuild<Kind10>();
        access.Rebuild<Kind11>();
        access.Rebuild<Kind12>();
        access.Rebuild<Kind13>();
        access.Rebuild<Kind14>();
        access.Rebuild<Kind15>();
        return total;
    }
}
internal readonly struct SomeResourceActions(World world, ResourceCounter counter) : IResourceActions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ResourceData<T> Get<T>() where T : struct => ref world.GetResource<ResourceData<T>>();
    public void Initialize<T>() where T : struct => world.SetResource(new ResourceData<T>(counter, 1));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadSet<T>() where T : struct
    {
        ref ResourceData<T> stored = ref Get<T>();
        long value = stored.Value;
        stored.Dispose();
        stored = new ResourceData<T>(counter, 42);
        return value;
    }
    public void Rebuild<T>() where T : struct { Release<T>(); Initialize<T>(); }
    public void Release<T>() where T : struct
    {
        world.RemoveResource<ResourceData<T>>();
    }
}
internal readonly struct EasyResourceActions(EasyEcs.World world, ResourceCounter counter) : IResourceActions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ResourceData<T> Get<T>() where T : struct => ref world.GetSingleton<ResourceData<T>>();
    public void Initialize<T>() where T : struct => world.AddSingleton<ResourceData<T>>() = new ResourceData<T>(counter, 1);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadSet<T>() where T : struct
    {
        ref ResourceData<T> stored = ref Get<T>();
        long value = stored.Value;
        stored.Dispose();
        stored = new ResourceData<T>(counter, 42);
        return value;
    }
    public void Rebuild<T>() where T : struct { Release<T>(); Initialize<T>(); }
    public void Release<T>() where T : struct
    {
        world.GetSingleton<ResourceData<T>>().Dispose();
        world.RemoveSingleton<ResourceData<T>>();
    }
}
internal readonly struct DefaultResourceActions(DefaultEcs.World world, ResourceCounter counter) : IResourceActions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ResourceData<T> Get<T>() where T : struct => ref world.Get<ResourceData<T>>();
    public void Initialize<T>() where T : struct => world.Set(new ResourceData<T>(counter, 1));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadSet<T>() where T : struct
    {
        ref ResourceData<T> stored = ref Get<T>();
        long value = stored.Value;
        stored.Dispose();
        stored = new ResourceData<T>(counter, 42);
        return value;
    }
    public void Rebuild<T>() where T : struct { Release<T>(); Initialize<T>(); }
    public void Release<T>() where T : struct
    {
        world.Get<ResourceData<T>>().Dispose();
        world.Remove<ResourceData<T>>();
    }
}
internal readonly struct ReferenceResourceActions(ReferenceResources world, ResourceCounter counter) : IResourceActions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref ResourceData<T> Get<T>() where T : struct => ref world.Read<ResourceData<T>>();
    public void Initialize<T>() where T : struct => world.Set(new ResourceData<T>(counter, 1));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadSet<T>() where T : struct
    {
        ref ResourceData<T> stored = ref Get<T>();
        long value = stored.Value;
        stored.Dispose();
        stored = new ResourceData<T>(counter, 42);
        return value;
    }
    public void Rebuild<T>() where T : struct { Release<T>(); Initialize<T>(); }
    public void Release<T>() where T : struct
    {
        world.Remove<ResourceData<T>>();
    }
}
internal sealed partial class SomeEngineWorld
{
    private ResourceCounter? _resourceCounter;
    private void PrepareResources()
    {
        if (_resourceCounter is not null) return;
        _resourceCounter = new();
        var access = new SomeResourceActions(_world, _resourceCounter);
        ResourceSequence.Initialize(ref access);
    }
    internal override void ResourceCycle()
    {
        long before = _resourceCounter!.Count;
        var access = new SomeResourceActions(_world, _resourceCounter);
        ReadTotal = ResourceSequence.Run(ref access);
        ResourceReleaseCount = _resourceCounter.Count - before;
    }
}
internal sealed partial class EasyEcsWorld
{
    private ResourceCounter? _resourceCounter;
    private void PrepareResources()
    {
        if (_resourceCounter is not null) return;
        _resourceCounter = new();
        var access = new EasyResourceActions(_world, _resourceCounter);
        ResourceSequence.Initialize(ref access);
    }
    internal override void ResourceCycle()
    {
        long before = _resourceCounter!.Count;
        var access = new EasyResourceActions(_world, _resourceCounter);
        ReadTotal = ResourceSequence.Run(ref access);
        ResourceReleaseCount = _resourceCounter.Count - before;
    }
    private void ReleaseComparisonResources()
    {
        if (_resourceCounter is null) return;
        var access = new EasyResourceActions(_world, _resourceCounter);
        ResourceSequence.Release(ref access);
        _resourceCounter = null;
    }
}
internal sealed partial class DefaultEcsWorld
{
    private ResourceCounter? _resourceCounter;
    private void PrepareResources()
    {
        if (_resourceCounter is not null) return;
        _resourceCounter = new();
        var access = new DefaultResourceActions(_world, _resourceCounter);
        ResourceSequence.Initialize(ref access);
    }
    internal override void ResourceCycle()
    {
        long before = _resourceCounter!.Count;
        var access = new DefaultResourceActions(_world, _resourceCounter);
        ReadTotal = ResourceSequence.Run(ref access);
        ResourceReleaseCount = _resourceCounter.Count - before;
    }
    private void ReleaseComparisonResources()
    {
        if (_resourceCounter is null) return;
        var access = new DefaultResourceActions(_world, _resourceCounter);
        ResourceSequence.Release(ref access);
        _resourceCounter = null;
    }
}
internal sealed partial class FrifloWorld
{
    private ResourceCounter? _resourceCounter;
    private ReferenceResources? _resourceValues;
    private void PrepareResources()
    {
        if (_resourceCounter is not null) return;
        _resourceCounter = new();
        _resourceValues = new();
        var access = new ReferenceResourceActions(_resourceValues!, _resourceCounter);
        ResourceSequence.Initialize(ref access);
    }
    internal override void ResourceCycle()
    {
        long before = _resourceCounter!.Count;
        var access = new ReferenceResourceActions(_resourceValues!, _resourceCounter);
        ReadTotal = ResourceSequence.Run(ref access);
        ResourceReleaseCount = _resourceCounter.Count - before;
    }
    private void ReleaseComparisonResources()
    {
        if (_resourceCounter is null) return;
        var access = new ReferenceResourceActions(_resourceValues!, _resourceCounter);
        ResourceSequence.Release(ref access);
        _resourceCounter = null;
    }
}
internal sealed partial class ArchWorld
{
    private ResourceCounter? _resourceCounter;
    private ReferenceResources? _resourceValues;
    private void PrepareResources()
    {
        if (_resourceCounter is not null) return;
        _resourceCounter = new();
        _resourceValues = new();
        var access = new ReferenceResourceActions(_resourceValues!, _resourceCounter);
        ResourceSequence.Initialize(ref access);
    }
    internal override void ResourceCycle()
    {
        long before = _resourceCounter!.Count;
        var access = new ReferenceResourceActions(_resourceValues!, _resourceCounter);
        ReadTotal = ResourceSequence.Run(ref access);
        ResourceReleaseCount = _resourceCounter.Count - before;
    }
    private void ReleaseComparisonResources()
    {
        if (_resourceCounter is null) return;
        var access = new ReferenceResourceActions(_resourceValues!, _resourceCounter);
        ResourceSequence.Release(ref access);
        _resourceCounter = null;
    }
}
