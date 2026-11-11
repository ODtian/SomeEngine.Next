using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

internal interface IArchiveResources
{
    void Initialize<T>(int ordinal) where T : struct;
    void Capture<T>(Span<long> values) where T : struct;
    void Restore<T>(ReadOnlySpan<long> values) where T : struct;
    void Release<T>() where T : struct;
}

internal readonly struct SourceArchiveResources(World world, ResourceCounter counter) : IArchiveResources
{
    public void Initialize<T>(int ordinal) where T : struct => world.SetResource(ArchiveResources.Value<T>(counter, ordinal));
    public void Capture<T>(Span<long> values) where T : struct
    { using var read = world.OpenResourceRead<ResourceData<T>>(); ArchiveResources.Copy(read.Value, values); }
    public void Restore<T>(ReadOnlySpan<long> values) where T : struct => world.SetResource(ArchiveResources.Value<T>(counter, values));
    public void Release<T>() where T : struct => world.RemoveResource<ResourceData<T>>();
}
internal readonly struct NativeArchiveResources(ReferenceResources resources, ResourceCounter counter) : IArchiveResources
{
    public void Initialize<T>(int ordinal) where T : struct => resources.Set(ArchiveResources.Value<T>(counter, ordinal));
    public void Capture<T>(Span<long> values) where T : struct => ArchiveResources.Copy(resources.Read<ResourceData<T>>(), values);
    public void Restore<T>(ReadOnlySpan<long> values) where T : struct => resources.Set(ArchiveResources.Value<T>(counter, values));
    public void Release<T>() where T : struct => resources.Remove<ResourceData<T>>();
}
internal readonly struct ArchiveResourceCodec<T>(ResourceCounter counter) : IValueCodec<ResourceData<T>> where T : struct
{
    public void Write(ref DataWriter writer, in ResourceData<T> value)
    { writer.WriteInt64(value.Value); writer.WriteInt64(value.Second); writer.WriteInt64(value.Third); }
    public void Read(ref DataReader reader, out ResourceData<T> value)
        => value = new(counter, reader.ReadInt64()) { Second = reader.ReadInt64(), Third = reader.ReadInt64() };
}
internal static class ArchiveResources
{
    internal static ResourceData<T> Value<T>(ResourceCounter counter, int ordinal) where T : struct
        => new(counter, ordinal * 3L + 1) { Second = ordinal * 3L + 2, Third = ordinal * 3L + 3 };
    internal static ResourceData<T> Value<T>(ResourceCounter counter, ReadOnlySpan<long> values) where T : struct
        => new(counter, values[0]) { Second = values[1], Third = values[2] };
    internal static void Copy<T>(in ResourceData<T> value, Span<long> values) where T : struct
    { values[0] = value.Value; values[1] = value.Second; values[2] = value.Third; }
    internal struct Kind0 { }
    internal struct Kind1 { }
    internal struct Kind2 { }
    internal struct Kind3 { }
    internal struct Kind4 { }
    internal struct Kind5 { }
    internal struct Kind6 { }
    internal struct Kind7 { }
    internal struct Kind8 { }
    internal struct Kind9 { }
    internal struct Kind10 { }
    internal struct Kind11 { }
    internal struct Kind12 { }
    internal struct Kind13 { }
    internal struct Kind14 { }
    internal struct Kind15 { }
    internal static void Initialize<TAccess>(TAccess values) where TAccess : struct, IArchiveResources
    {
        values.Initialize<Kind0>(0);
        values.Initialize<Kind1>(1);
        values.Initialize<Kind2>(2);
        values.Initialize<Kind3>(3);
        values.Initialize<Kind4>(4);
        values.Initialize<Kind5>(5);
        values.Initialize<Kind6>(6);
        values.Initialize<Kind7>(7);
        values.Initialize<Kind8>(8);
        values.Initialize<Kind9>(9);
        values.Initialize<Kind10>(10);
        values.Initialize<Kind11>(11);
        values.Initialize<Kind12>(12);
        values.Initialize<Kind13>(13);
        values.Initialize<Kind14>(14);
        values.Initialize<Kind15>(15);
    }
    internal static long[] Capture<TAccess>(TAccess values) where TAccess : struct, IArchiveResources
    {
        long[] data = new long[48];
        values.Capture<Kind0>(data.AsSpan(0, 3));
        values.Capture<Kind1>(data.AsSpan(3, 3));
        values.Capture<Kind2>(data.AsSpan(6, 3));
        values.Capture<Kind3>(data.AsSpan(9, 3));
        values.Capture<Kind4>(data.AsSpan(12, 3));
        values.Capture<Kind5>(data.AsSpan(15, 3));
        values.Capture<Kind6>(data.AsSpan(18, 3));
        values.Capture<Kind7>(data.AsSpan(21, 3));
        values.Capture<Kind8>(data.AsSpan(24, 3));
        values.Capture<Kind9>(data.AsSpan(27, 3));
        values.Capture<Kind10>(data.AsSpan(30, 3));
        values.Capture<Kind11>(data.AsSpan(33, 3));
        values.Capture<Kind12>(data.AsSpan(36, 3));
        values.Capture<Kind13>(data.AsSpan(39, 3));
        values.Capture<Kind14>(data.AsSpan(42, 3));
        values.Capture<Kind15>(data.AsSpan(45, 3));
        return data;
    }
    internal static void Restore<TAccess>(TAccess values, ReadOnlySpan<long> data) where TAccess : struct, IArchiveResources
    {
        values.Restore<Kind0>(data.Slice(0, 3));
        values.Restore<Kind1>(data.Slice(3, 3));
        values.Restore<Kind2>(data.Slice(6, 3));
        values.Restore<Kind3>(data.Slice(9, 3));
        values.Restore<Kind4>(data.Slice(12, 3));
        values.Restore<Kind5>(data.Slice(15, 3));
        values.Restore<Kind6>(data.Slice(18, 3));
        values.Restore<Kind7>(data.Slice(21, 3));
        values.Restore<Kind8>(data.Slice(24, 3));
        values.Restore<Kind9>(data.Slice(27, 3));
        values.Restore<Kind10>(data.Slice(30, 3));
        values.Restore<Kind11>(data.Slice(33, 3));
        values.Restore<Kind12>(data.Slice(36, 3));
        values.Restore<Kind13>(data.Slice(39, 3));
        values.Restore<Kind14>(data.Slice(42, 3));
        values.Restore<Kind15>(data.Slice(45, 3));
    }
    internal static void Release<TAccess>(TAccess values) where TAccess : struct, IArchiveResources
    {
        values.Release<Kind0>();
        values.Release<Kind1>();
        values.Release<Kind2>();
        values.Release<Kind3>();
        values.Release<Kind4>();
        values.Release<Kind5>();
        values.Release<Kind6>();
        values.Release<Kind7>();
        values.Release<Kind8>();
        values.Release<Kind9>();
        values.Release<Kind10>();
        values.Release<Kind11>();
        values.Release<Kind12>();
        values.Release<Kind13>();
        values.Release<Kind14>();
        values.Release<Kind15>();
    }
    internal static void Register(SerializationRegistry registry, ResourceCounter counter)
    {
        registry.RegisterResource<ResourceData<Kind0>, ArchiveResourceCodec<Kind0>>(SaveSchema.Key(100, "Resource.Kind0"), new(counter));
        registry.RegisterResource<ResourceData<Kind1>, ArchiveResourceCodec<Kind1>>(SaveSchema.Key(101, "Resource.Kind1"), new(counter));
        registry.RegisterResource<ResourceData<Kind2>, ArchiveResourceCodec<Kind2>>(SaveSchema.Key(102, "Resource.Kind2"), new(counter));
        registry.RegisterResource<ResourceData<Kind3>, ArchiveResourceCodec<Kind3>>(SaveSchema.Key(103, "Resource.Kind3"), new(counter));
        registry.RegisterResource<ResourceData<Kind4>, ArchiveResourceCodec<Kind4>>(SaveSchema.Key(104, "Resource.Kind4"), new(counter));
        registry.RegisterResource<ResourceData<Kind5>, ArchiveResourceCodec<Kind5>>(SaveSchema.Key(105, "Resource.Kind5"), new(counter));
        registry.RegisterResource<ResourceData<Kind6>, ArchiveResourceCodec<Kind6>>(SaveSchema.Key(106, "Resource.Kind6"), new(counter));
        registry.RegisterResource<ResourceData<Kind7>, ArchiveResourceCodec<Kind7>>(SaveSchema.Key(107, "Resource.Kind7"), new(counter));
        registry.RegisterResource<ResourceData<Kind8>, ArchiveResourceCodec<Kind8>>(SaveSchema.Key(108, "Resource.Kind8"), new(counter));
        registry.RegisterResource<ResourceData<Kind9>, ArchiveResourceCodec<Kind9>>(SaveSchema.Key(109, "Resource.Kind9"), new(counter));
        registry.RegisterResource<ResourceData<Kind10>, ArchiveResourceCodec<Kind10>>(SaveSchema.Key(110, "Resource.Kind10"), new(counter));
        registry.RegisterResource<ResourceData<Kind11>, ArchiveResourceCodec<Kind11>>(SaveSchema.Key(111, "Resource.Kind11"), new(counter));
        registry.RegisterResource<ResourceData<Kind12>, ArchiveResourceCodec<Kind12>>(SaveSchema.Key(112, "Resource.Kind12"), new(counter));
        registry.RegisterResource<ResourceData<Kind13>, ArchiveResourceCodec<Kind13>>(SaveSchema.Key(113, "Resource.Kind13"), new(counter));
        registry.RegisterResource<ResourceData<Kind14>, ArchiveResourceCodec<Kind14>>(SaveSchema.Key(114, "Resource.Kind14"), new(counter));
        registry.RegisterResource<ResourceData<Kind15>, ArchiveResourceCodec<Kind15>>(SaveSchema.Key(115, "Resource.Kind15"), new(counter));
    }
    internal static void Copy<TSource, TTarget>(TSource source, TTarget target)
        where TSource : struct, IArchiveResources where TTarget : struct, IArchiveResources
    {
        Span<long> value = stackalloc long[3];
        source.Capture<Kind0>(value); target.Restore<Kind0>(value);
        source.Capture<Kind1>(value); target.Restore<Kind1>(value);
        source.Capture<Kind2>(value); target.Restore<Kind2>(value);
        source.Capture<Kind3>(value); target.Restore<Kind3>(value);
        source.Capture<Kind4>(value); target.Restore<Kind4>(value);
        source.Capture<Kind5>(value); target.Restore<Kind5>(value);
        source.Capture<Kind6>(value); target.Restore<Kind6>(value);
        source.Capture<Kind7>(value); target.Restore<Kind7>(value);
        source.Capture<Kind8>(value); target.Restore<Kind8>(value);
        source.Capture<Kind9>(value); target.Restore<Kind9>(value);
        source.Capture<Kind10>(value); target.Restore<Kind10>(value);
        source.Capture<Kind11>(value); target.Restore<Kind11>(value);
        source.Capture<Kind12>(value); target.Restore<Kind12>(value);
        source.Capture<Kind13>(value); target.Restore<Kind13>(value);
        source.Capture<Kind14>(value); target.Restore<Kind14>(value);
        source.Capture<Kind15>(value); target.Restore<Kind15>(value);
    }
    internal static void Write<TAccess>(TAccess values, ref DataWriter writer) where TAccess : struct, IArchiveResources
    {
        Span<long> value = stackalloc long[3];
        values.Capture<Kind0>(value); Write(ref writer, value);
        values.Capture<Kind1>(value); Write(ref writer, value);
        values.Capture<Kind2>(value); Write(ref writer, value);
        values.Capture<Kind3>(value); Write(ref writer, value);
        values.Capture<Kind4>(value); Write(ref writer, value);
        values.Capture<Kind5>(value); Write(ref writer, value);
        values.Capture<Kind6>(value); Write(ref writer, value);
        values.Capture<Kind7>(value); Write(ref writer, value);
        values.Capture<Kind8>(value); Write(ref writer, value);
        values.Capture<Kind9>(value); Write(ref writer, value);
        values.Capture<Kind10>(value); Write(ref writer, value);
        values.Capture<Kind11>(value); Write(ref writer, value);
        values.Capture<Kind12>(value); Write(ref writer, value);
        values.Capture<Kind13>(value); Write(ref writer, value);
        values.Capture<Kind14>(value); Write(ref writer, value);
        values.Capture<Kind15>(value); Write(ref writer, value);
    }
    internal static void Read<TAccess>(TAccess values, ref DataReader reader) where TAccess : struct, IArchiveResources
    {
        Span<long> value = stackalloc long[3];
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind0>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind1>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind2>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind3>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind4>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind5>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind6>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind7>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind8>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind9>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind10>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind11>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind12>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind13>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind14>(value);
        for (int i = 0; i < 3; i++) value[i] = reader.ReadInt64(); values.Restore<Kind15>(value);
    }
    internal static NativeArchiveType[] Metadata()
        => [
            new NativeArchiveMetadataType(SaveSchema.Key(100, "Resource.Kind0"), typeof(ResourceData<Kind0>), typeof(ArchiveResourceCodec<Kind0>)),
            new NativeArchiveMetadataType(SaveSchema.Key(101, "Resource.Kind1"), typeof(ResourceData<Kind1>), typeof(ArchiveResourceCodec<Kind1>)),
            new NativeArchiveMetadataType(SaveSchema.Key(102, "Resource.Kind2"), typeof(ResourceData<Kind2>), typeof(ArchiveResourceCodec<Kind2>)),
            new NativeArchiveMetadataType(SaveSchema.Key(103, "Resource.Kind3"), typeof(ResourceData<Kind3>), typeof(ArchiveResourceCodec<Kind3>)),
            new NativeArchiveMetadataType(SaveSchema.Key(104, "Resource.Kind4"), typeof(ResourceData<Kind4>), typeof(ArchiveResourceCodec<Kind4>)),
            new NativeArchiveMetadataType(SaveSchema.Key(105, "Resource.Kind5"), typeof(ResourceData<Kind5>), typeof(ArchiveResourceCodec<Kind5>)),
            new NativeArchiveMetadataType(SaveSchema.Key(106, "Resource.Kind6"), typeof(ResourceData<Kind6>), typeof(ArchiveResourceCodec<Kind6>)),
            new NativeArchiveMetadataType(SaveSchema.Key(107, "Resource.Kind7"), typeof(ResourceData<Kind7>), typeof(ArchiveResourceCodec<Kind7>)),
            new NativeArchiveMetadataType(SaveSchema.Key(108, "Resource.Kind8"), typeof(ResourceData<Kind8>), typeof(ArchiveResourceCodec<Kind8>)),
            new NativeArchiveMetadataType(SaveSchema.Key(109, "Resource.Kind9"), typeof(ResourceData<Kind9>), typeof(ArchiveResourceCodec<Kind9>)),
            new NativeArchiveMetadataType(SaveSchema.Key(110, "Resource.Kind10"), typeof(ResourceData<Kind10>), typeof(ArchiveResourceCodec<Kind10>)),
            new NativeArchiveMetadataType(SaveSchema.Key(111, "Resource.Kind11"), typeof(ResourceData<Kind11>), typeof(ArchiveResourceCodec<Kind11>)),
            new NativeArchiveMetadataType(SaveSchema.Key(112, "Resource.Kind12"), typeof(ResourceData<Kind12>), typeof(ArchiveResourceCodec<Kind12>)),
            new NativeArchiveMetadataType(SaveSchema.Key(113, "Resource.Kind13"), typeof(ResourceData<Kind13>), typeof(ArchiveResourceCodec<Kind13>)),
            new NativeArchiveMetadataType(SaveSchema.Key(114, "Resource.Kind14"), typeof(ResourceData<Kind14>), typeof(ArchiveResourceCodec<Kind14>)),
            new NativeArchiveMetadataType(SaveSchema.Key(115, "Resource.Kind15"), typeof(ResourceData<Kind15>), typeof(ArchiveResourceCodec<Kind15>))
        ];
    internal static void Write(ref DataWriter writer, scoped ReadOnlySpan<long> values)
    { foreach (long value in values) writer.WriteInt64(value); }
    internal static long[] Read(ref DataReader reader)
    { long[] data = new long[48]; for (int i = 0; i < data.Length; i++) data[i] = reader.ReadInt64(); return data; }
    internal static void Check(ReadOnlySpan<long> data)
    { for (int i = 0; i < data.Length; i++) if (data[i] != i + 1) throw new InvalidOperationException("Archive typed resource values differ."); }
}
