using System.Buffers;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

public record struct SaveCounter(int Value, int Ordinal) : IAddedComponent, IRemovedComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
public readonly record struct SaveStep(int Value) : IEnableableComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
public readonly record struct SaveShared(int Value) : ISharedComponent, IGroupComponent
{ public int GetGroup() => Value; }
public readonly record struct SaveLink(Entity Entity, long Value) : IAddedComponent, IRemovedComponent;
internal readonly record struct SaveSparseRow(SaveLink Value, ulong Added, ulong Written);
public readonly record struct SaveNativeFlags(bool Disabled) : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
public record struct SaveNativeVersions(ulong Added, ulong Written) : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
public readonly record struct SaveNativeRemoved(SaveCounter Value, ulong Version) : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
public struct SaveNativeBuffer : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
{ public SaveLink[] Items; public ulong Added, Written; }
public struct SaveNativeOldBuffer : EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
{ public SaveLink[] Items; public ulong Version; }
public readonly struct SaveTree : IHierarchyDomain;
[RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair)]
public readonly record struct SaveEdge(int Ordinal, long Value) : IRelationPayload<SaveEdge>
{ public static SaveEdge Create(int ordinal) => new(ordinal, ordinal * 3L + 7); }
internal sealed class SaveResource : ICloneable, IDisposable
{
    internal long[] Items = new long[64];
    internal bool Released;
    public object Clone() => new SaveResource { Items = (long[])Items.Clone() };
    public void Dispose() { Released = true; Items = []; }
}
internal sealed class SaveSystemVersions : ISystemVersions
{
    internal ulong[] Values = [7, 9, 11, 13];
    private ISystemVersions? _binding;
    internal void Bind(ISystemVersions versions)
    { _binding = versions; Values = new ulong[versions.VersionCount * 2]; }
    public int VersionCount => Values.Length / 2;
    public void Complete() { _binding?.Complete(); _binding?.CopyVersions(Values); }
    public void CopyVersions(Span<ulong> destination)
    { _binding?.CopyVersions(Values); Values.CopyTo(destination); }
    public void RestoreVersions(ReadOnlySpan<ulong> source)
    { source.CopyTo(Values); _binding?.RestoreVersions(source); }
}

internal static class SaveSchema
{
    internal static SerializationTypeKey Key(int id, string name) => new(new Guid(id, 0x12ab, 0x45cd, 0x81, 1, 2, 3, 4, 5, 6, 7), name, 1);
    internal readonly struct EntityCodec : IValueCodec<Entity>
    {
        public void Write(ref DataWriter writer, in Entity value) => writer.WriteEntity(value);
        public void Read(ref DataReader reader, out Entity value) => value = reader.ReadEntity();
    }
    internal readonly struct CounterCodec : IComponentCodec<SaveCounter>
    {
        public void Write(ref DataWriter w, in SaveCounter v) { w.WriteInt32(v.Value); w.WriteInt32(v.Ordinal); }
        public void Read(ref DataReader r, out SaveCounter v) => v = new(r.ReadInt32(), r.ReadInt32());
    }
    internal readonly struct PositionCodec : IComponentCodec<Position>
    {
        public void Write(ref DataWriter w, in Position v) { w.WriteSingle(v.X); w.WriteSingle(v.Y); }
        public void Read(ref DataReader r, out Position v) => v = new() { X = r.ReadSingle(), Y = r.ReadSingle() };
    }
    internal readonly struct VelocityCodec : IComponentCodec<Velocity>
    {
        public void Write(ref DataWriter w, in Velocity v) { w.WriteSingle(v.X); w.WriteSingle(v.Y); }
        public void Read(ref DataReader r, out Velocity v) => v = new() { X = r.ReadSingle(), Y = r.ReadSingle() };
    }
    internal readonly struct HealthCodec : IComponentCodec<Health>
    {
        public void Write(ref DataWriter w, in Health v) { w.WriteInt32(v.Value); w.WriteInt32(v.Max); }
        public void Read(ref DataReader r, out Health v) => v = new() { Value = r.ReadInt32(), Max = r.ReadInt32() };
    }
    internal readonly struct StepCodec : IComponentCodec<SaveStep>
    {
        public void Write(ref DataWriter w, in SaveStep v) => w.WriteInt32(v.Value);
        public void Read(ref DataReader r, out SaveStep v) => v = new(r.ReadInt32());
    }
    internal readonly struct SharedCodec : IComponentCodec<SaveShared>
    {
        public void Write(ref DataWriter w, in SaveShared v) => w.WriteInt32(v.Value);
        public void Read(ref DataReader r, out SaveShared v) => v = new(r.ReadInt32());
    }
    internal readonly struct IndexCodec : IComponentCodec<IndexedNumber>
    {
        public void Write(ref DataWriter w, in IndexedNumber v) { w.WriteInt32(v.Value); w.WriteInt32(v.Ordinal); }
        public void Read(ref DataReader r, out IndexedNumber v) => v = new(r.ReadInt32(), r.ReadInt32());
    }
    internal readonly struct LinkCodec : IComponentCodec<SaveLink>
    {
        public void Write(ref DataWriter w, in SaveLink v) { w.WriteEntity(v.Entity); w.WriteInt64(v.Value); }
        public void Read(ref DataReader r, out SaveLink v) => v = new(r.ReadEntity(), r.ReadInt64());
    }
    internal readonly struct ResourceCodec : IValueCodec<SaveResource>
    {
        public void Write(ref DataWriter w, in SaveResource v) { w.WriteInt32(v.Items.Length); foreach (long item in v.Items) w.WriteInt64(item); }
        public void Read(ref DataReader r, out SaveResource v)
        { v = new() { Items = new long[r.ReadInt32()] }; for (int i = 0; i < v.Items.Length; i++) v.Items[i] = r.ReadInt64(); }
    }
    internal readonly struct EdgeCodec : IComponentCodec<SaveEdge>
    {
        public void Write(ref DataWriter w, in SaveEdge v) { w.WriteInt32(v.Ordinal); w.WriteInt64(v.Value); }
        public void Read(ref DataReader r, out SaveEdge v) => v = new(r.ReadInt32(), r.ReadInt64());
    }
    internal readonly struct FlagsCodec : IValueCodec<SaveNativeFlags>
    {
        public void Write(ref DataWriter w, in SaveNativeFlags v) => w.WriteBoolean(v.Disabled);
        public void Read(ref DataReader r, out SaveNativeFlags v) => v = new(r.ReadBoolean());
    }
    internal readonly struct VersionsCodec : IValueCodec<SaveNativeVersions>
    {
        public void Write(ref DataWriter w, in SaveNativeVersions v) { w.WriteUInt64(v.Added); w.WriteUInt64(v.Written); }
        public void Read(ref DataReader r, out SaveNativeVersions v) => v = new(r.ReadUInt64(), r.ReadUInt64());
    }
    internal readonly struct RemovedCodec : IValueCodec<SaveNativeRemoved>
    {
        public void Write(ref DataWriter w, in SaveNativeRemoved v) { new CounterCodec().Write(ref w, v.Value); w.WriteUInt64(v.Version); }
        public void Read(ref DataReader r, out SaveNativeRemoved v) { new CounterCodec().Read(ref r, out SaveCounter counter); v = new(counter, r.ReadUInt64()); }
    }
    internal readonly struct SharedIdCodec : IValueCodec<SharedNumberId>
    {
        public void Write(ref DataWriter w, in SharedNumberId v) => w.WriteInt32(v.Value);
        public void Read(ref DataReader r, out SharedNumberId v) => v = new(r.ReadInt32());
    }
    internal readonly struct ParentCodec : IValueCodec<ReferenceParent<Entity>>
    {
        public void Write(ref DataWriter w, in ReferenceParent<Entity> v) { w.WriteEntity(v.Value); w.WriteUInt64(v.Added); w.WriteUInt64(v.Written); }
        public void Read(ref DataReader r, out ReferenceParent<Entity> v) => v = new() { Value = r.ReadEntity(), Added = r.ReadUInt64(), Written = r.ReadUInt64() };
    }
    internal readonly struct OldParentCodec : IValueCodec<ReferenceOldParent<Entity>>
    {
        public void Write(ref DataWriter w, in ReferenceOldParent<Entity> v) { w.WriteEntity(v.Value); w.WriteUInt64(v.Version); }
        public void Read(ref DataReader r, out ReferenceOldParent<Entity> v) => v = new() { Value = r.ReadEntity(), Version = r.ReadUInt64() };
    }
    internal readonly struct EndpointsCodec : IValueCodec<ReferenceRelationEndpoints<Entity>>
    {
        public void Write(ref DataWriter w, in ReferenceRelationEndpoints<Entity> v)
        { w.WriteEntity(v.Source); w.WriteEntity(v.Target); w.WriteUInt64(v.Added); w.WriteUInt64(v.Written); }
        public void Read(ref DataReader r, out ReferenceRelationEndpoints<Entity> v)
            => v = new() { Source = r.ReadEntity(), Target = r.ReadEntity(), Added = r.ReadUInt64(), Written = r.ReadUInt64() };
    }
    internal readonly struct OldEndpointsCodec : IValueCodec<ReferenceOldRelationEndpoints<Entity>>
    {
        public void Write(ref DataWriter w, in ReferenceOldRelationEndpoints<Entity> v)
        { w.WriteEntity(v.Source); w.WriteEntity(v.Target); w.WriteUInt64(v.Version); }
        public void Read(ref DataReader r, out ReferenceOldRelationEndpoints<Entity> v)
            => v = new() { Source = r.ReadEntity(), Target = r.ReadEntity(), Version = r.ReadUInt64() };
    }
    internal readonly struct BufferCodec : IValueCodec<SaveNativeBuffer>
    {
        public void Write(ref DataWriter w, in SaveNativeBuffer v)
        { w.WriteInt32(v.Items.Length); foreach (SaveLink item in v.Items) new LinkCodec().Write(ref w, item); w.WriteUInt64(v.Added); w.WriteUInt64(v.Written); }
        public void Read(ref DataReader r, out SaveNativeBuffer v)
        {
            v = new() { Items = new SaveLink[r.ReadInt32()] }; for (int i = 0; i < v.Items.Length; i++) new LinkCodec().Read(ref r, out v.Items[i]);
            v.Added = r.ReadUInt64(); v.Written = r.ReadUInt64();
        }
    }
    internal readonly struct OldBufferCodec : IValueCodec<SaveNativeOldBuffer>
    {
        public void Write(ref DataWriter w, in SaveNativeOldBuffer v)
        { w.WriteInt32(v.Items.Length); foreach (SaveLink item in v.Items) new LinkCodec().Write(ref w, item); w.WriteUInt64(v.Version); }
        public void Read(ref DataReader r, out SaveNativeOldBuffer v)
        {
            v = new() { Items = new SaveLink[r.ReadInt32()] }; for (int i = 0; i < v.Items.Length; i++) new LinkCodec().Read(ref r, out v.Items[i]);
            v.Version = r.ReadUInt64();
        }
    }
    internal static SaveNativeBuffer Clone(SaveNativeBuffer value)
    { value.Items = (SaveLink[])value.Items.Clone(); return value; }
    internal readonly struct ChildrenCodec : IValueCodec<ReferenceChildren<Entity>>
    {
        public void Write(ref DataWriter w, in ReferenceChildren<Entity> v)
        {
            w.WriteInt32(v.Count); for (int i = 0; i < v.Count; i++) w.WriteEntity(v.Items![i]);
            w.WriteUInt64(v.Added); w.WriteUInt64(v.Written);
        }
        public void Read(ref DataReader r, out ReferenceChildren<Entity> v)
        {
            int count = r.ReadInt32(); v = new() { Count = count, Items = ArrayPool<Entity>.Shared.Rent(Math.Max(1, count)) };
            for (int i = 0; i < count; i++) v.Items[i] = r.ReadEntity(); v.Added = r.ReadUInt64(); v.Written = r.ReadUInt64();
        }
    }
    internal readonly struct OldChildrenCodec : IValueCodec<ReferenceOldChildren<Entity>>
    {
        public void Write(ref DataWriter w, in ReferenceOldChildren<Entity> v)
        { w.WriteInt32(v.Count); for (int i = 0; i < v.Count; i++) w.WriteEntity(v.Items![i]); w.WriteUInt64(v.Version); }
        public void Read(ref DataReader r, out ReferenceOldChildren<Entity> v)
        {
            int count = r.ReadInt32(); v = new() { Count = count, Items = ArrayPool<Entity>.Shared.Rent(Math.Max(1, count)) };
            for (int i = 0; i < count; i++) v.Items[i] = r.ReadEntity(); v.Version = r.ReadUInt64();
        }
    }
    internal static ReferenceChildren<Entity> Clone(ReferenceChildren<Entity> source)
    {
        var copied = source; copied.Items = ArrayPool<Entity>.Shared.Rent(Math.Max(1, source.Count));
        source.Items.AsSpan(0, source.Count).CopyTo(copied.Items); return copied;
    }
    internal static ReferenceOldChildren<Entity> Clone(ReferenceOldChildren<Entity> source)
    {
        var copied = source; copied.Items = ArrayPool<Entity>.Shared.Rent(Math.Max(1, source.Count));
        source.Items.AsSpan(0, source.Count).CopyTo(copied.Items); return copied;
    }
}
