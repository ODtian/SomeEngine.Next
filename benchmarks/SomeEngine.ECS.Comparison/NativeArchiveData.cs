using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

internal interface INativeArchiveProgress
{
    ulong Version { get; set; }
    void Complete();
    void CompleteSystems();
    void Write(ref DataWriter writer, bool preserve);
    void Read(ref DataReader reader, bool preserve);
    void ReleaseValues();
    CapturedArchiveProgress CaptureProgress(bool preserve);
}

internal abstract class CapturedArchiveProgress : IDisposable
{
    internal abstract void Restore(INativeArchiveProgress state, bool preserve);
    internal abstract void Write(ref DataWriter writer);
    public abstract void Dispose();
}

internal abstract class NativeArchiveColumn : IDisposable
{
    internal abstract void Restore(NativeArchiveStore store);
    internal abstract void Write(IBufferWriter<byte> output);
    public abstract void Dispose();
}

internal abstract class NativeArchiveType(SerializationTypeKey key, Type valueType, Type codecType)
{
    internal readonly SerializationTypeKey Key = key;
    internal readonly SerializationTypeKey CheckpointKey = key with
    { StableName = key.StableName + "|" + Build(valueType) + "|" + Build(codecType) };
    private static string Build(Type type) => type.AssemblyQualifiedName + "#" + type.Module.ModuleVersionId
        + (type.IsGenericType ? "[" + string.Join(';', type.GetGenericArguments().Select(Build)) + "]" : "");
    internal SerializationTypeKey KeyFor(bool raw) => raw ? CheckpointKey : Key;
    internal abstract NativeArchiveColumnGroups Bind(NativeArchiveStore store, NativeArchiveLayout layout);
    internal virtual void Declare(EasyEcs.Prefab prefab) { }
    internal virtual void Declare(ref Friflo.Engine.ECS.ComponentTypes types) { }
    internal virtual void Declare(List<Arch.Core.ComponentType> types) { }
    internal abstract NativeArchiveColumn Capture(NativeArchiveStore store, NativeArchiveColumnGroups plan);
    internal abstract void Write(NativeArchiveStore store, IBufferWriter<byte> output, NativeArchiveColumnGroups plan);
    internal abstract void Read(NativeArchiveStore store, ref DataReader reader, NativeArchiveGroup[] groups);
}

internal sealed class NativeArchiveMetadataType(SerializationTypeKey key, Type valueType, Type codecType)
    : NativeArchiveType(key, valueType, codecType)
{
    private sealed class EmptyColumn : NativeArchiveColumn
    {
        internal override void Restore(NativeArchiveStore store) { }
        internal override void Write(IBufferWriter<byte> output) { }
        public override void Dispose() { }
    }
    private static readonly NativeArchiveColumn Empty = new EmptyColumn();
    internal override NativeArchiveColumnGroups Bind(NativeArchiveStore store, NativeArchiveLayout layout) => new([]);
    internal override NativeArchiveColumn Capture(NativeArchiveStore store, NativeArchiveColumnGroups plan) => Empty;
    internal override void Write(NativeArchiveStore store, IBufferWriter<byte> output, NativeArchiveColumnGroups plan) { }
    internal override void Read(NativeArchiveStore store, ref DataReader reader, NativeArchiveGroup[] groups) { }
}

internal sealed class NativeArchiveType<T, TCodec>(SerializationTypeKey key, TCodec codec, bool raw = false,
    Func<T, T>? clone = null, Action<T>? release = null) : NativeArchiveType(key, typeof(T), typeof(TCodec))
    where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where TCodec : struct, IValueCodec<T>
{
    private readonly record struct Block(NativeArchiveGroup Group, T[] Values);
    private sealed class Column(NativeArchiveType<T, TCodec> type, Block[] blocks) : NativeArchiveColumn
    {
        internal override void Restore(NativeArchiveStore store)
        {
            foreach (Block block in blocks)
                store.WriteColumn<T>(block.Group, block.Values.AsSpan(), type.CloneValue);
        }
        internal override void Write(IBufferWriter<byte> output)
        {
            foreach (Block block in blocks) type.WriteBlock(output, block.Group.Id, block.Values);
            End(output);
        }
        public override void Dispose()
        {
            if (type.ReleasesValues)
                foreach (Block block in blocks) foreach (T value in block.Values) type.Release(value);
            blocks = [];
        }
    }
    private T Clone(T value) => clone is null ? value : clone(value);
    internal Func<T, T>? CloneValue => clone;
    private bool ReleasesValues => release is not null;
    private void Release(T value) => release?.Invoke(value);
    internal override void Declare(EasyEcs.Prefab prefab) => prefab.Set(default(T));
    internal override void Declare(ref Friflo.Engine.ECS.ComponentTypes types) => types.Add<T>();
    internal override void Declare(List<Arch.Core.ComponentType> types) => types.Add(Arch.Core.Component<T>.ComponentType);
    private struct BindVisitor(NativeArchiveLayout layout) : INativeArchiveVisitor<T>
    {
        internal readonly List<NativeArchiveGroup> Groups = new();
        public void Visit(ReadOnlySpan<ArchiveIdentity> identities, Span<T> values) => Groups.Add(layout.Bind(identities));
    }
    internal override NativeArchiveColumnGroups Bind(NativeArchiveStore store, NativeArchiveLayout layout)
    { var visitor = new BindVisitor(layout); store.Visit<T, BindVisitor>(ref visitor); return new(visitor.Groups.ToArray()); }
    private struct CaptureVisitor(NativeArchiveType<T, TCodec> type, NativeArchiveColumnGroups plan) : INativeArchiveVisitor<T>
    {
        internal readonly List<Block> Blocks = new();
        private int _block;
        public void Visit(ReadOnlySpan<ArchiveIdentity> identities, Span<T> values)
        {
            T[] copied = values.ToArray();
            if (type.CloneValue is not null)
                for (int i = 0; i < copied.Length; i++) copied[i] = type.Clone(copied[i]);
            Blocks.Add(new(plan.Groups[_block++], copied));
        }
    }
    internal override NativeArchiveColumn Capture(NativeArchiveStore store, NativeArchiveColumnGroups plan)
    {
        var visitor = new CaptureVisitor(this, plan); store.Visit<T, CaptureVisitor>(ref visitor);
        return new Column(this, visitor.Blocks.ToArray());
    }
    private struct WriteVisitor(NativeArchiveType<T, TCodec> type, IBufferWriter<byte> output, NativeArchiveColumnGroups plan) : INativeArchiveVisitor<T>
    {
        private int _block;
        public void Visit(ReadOnlySpan<ArchiveIdentity> identities, Span<T> values) => type.WriteBlock(output, plan.Groups[_block++].Id, values);
    }
    private void WriteBlock(IBufferWriter<byte> output, int group, ReadOnlySpan<T> values)
    {
        var writer = new DataWriter(output); writer.WriteInt32(values.Length); writer.WriteInt32(group);
        if (raw) writer.WriteRawBytes(MemoryMarshal.AsBytes(values));
        else
        {
            long start = writer.Position;
            foreach (ref readonly T value in values) { TCodec item = codec; item.Write(ref writer, value); }
            writer.WriteInt32(checked((int)(writer.Position - start)));
        }
        writer.Flush();
    }
    private static void End(IBufferWriter<byte> output)
    { var writer = new DataWriter(output); writer.WriteInt32(0); writer.Flush(); }
    internal override void Write(NativeArchiveStore store, IBufferWriter<byte> output, NativeArchiveColumnGroups plan)
    { var visitor = new WriteVisitor(this, output, plan); store.Visit<T, WriteVisitor>(ref visitor); End(output); }
    internal override void Read(NativeArchiveStore store, ref DataReader reader, NativeArchiveGroup[] groups)
    {
        for (int count = reader.ReadInt32(); count != 0; count = reader.ReadInt32())
        {
            int readerGroup = reader.ReadInt32();
            store.ReadColumn<T, TCodec>(groups[readerGroup], count, ref reader, codec, raw);
        }
    }
}

internal sealed class CapturedNativeArchive(int[] generations, bool[] alive, int used, int[] free, ulong version,
    bool preserve, NativeArchiveLayout layout, NativeArchiveType[] types, NativeArchiveColumn[] columns, CapturedArchiveProgress progress) : IDisposable
{
    internal void Restore(NativeArchiveStore store, INativeArchiveProgress state)
    {
        state.CompleteSystems();
        state.ReleaseValues(); store.RestoreIdentities(generations, alive, used, free, layout.Groups, layout.DisjointGroups, types);
        foreach (NativeArchiveColumn column in columns) column.Restore(store);
        state.Version = preserve ? version : Math.Max(state.Version, version) + 1;
        progress.Restore(state, preserve);
    }
    internal void Write(NativeArchiveSerializer serializer, IBufferWriter<byte> output)
    {
        serializer.Header(output, generations, alive, used, free, version, preserve, layout);
        foreach (NativeArchiveColumn column in columns) column.Write(output);
        var writer = new DataWriter(output); progress.Write(ref writer); writer.Flush();
    }
    public void Dispose()
    {
        foreach (NativeArchiveColumn column in columns) column.Dispose(); progress.Dispose();
        generations = []; alive = []; free = []; columns = []; layout = null!; types = []; progress = null!;
    }
}

internal sealed class NativeArchiveSerializer(NativeArchiveType[] types, bool raw)
{
    private NativeArchiveStore? _boundStore;
    private ulong _boundRevision;
    private NativeArchiveLayout? _layout;
    private NativeArchiveLayout Bind(NativeArchiveStore store)
    {
        if (_layout is not null && ReferenceEquals(_boundStore, store) && _boundRevision == store.Revision) return _layout;
        var layout = new NativeArchiveLayout { Columns = new NativeArchiveColumnGroups[types.Length], DisjointGroups = store.SupportsDirectGroups };
        for (int i = 0; i < types.Length; i++)
        {
            layout.Columns[i] = types[i].Bind(store, layout);
            foreach (NativeArchiveGroup group in layout.Columns[i].Groups) group.Types |= 1UL << i;
        }
        _boundStore = store; _boundRevision = store.Revision; return _layout = layout;
    }
    internal CapturedNativeArchive Capture(NativeArchiveStore store, INativeArchiveProgress state, bool preserve)
    {
        state.CompleteSystems();
        if (!preserve) state.Complete();
        NativeArchiveLayout layout = Bind(store);
        var columns = new NativeArchiveColumn[types.Length];
        for (int i = 0; i < types.Length; i++) columns[i] = types[i].Capture(store, layout.Columns[i]);
        return new(store.Generations.AsSpan(0, store.Used + 1).ToArray(), store.AliveIds.AsSpan(0, store.Used + 1).ToArray(),
            store.Used, store.FreeIds.ToArray(), state.Version, preserve, layout, types, columns, state.CaptureProgress(preserve));
    }
    internal void Header(IBufferWriter<byte> output, int[] generations, bool[] alive, int used, ReadOnlySpan<int> free,
        ulong version, bool preserve, NativeArchiveLayout layout)
    {
        var writer = new DataWriter(output);
        writer.WriteUInt32(0x41524348); writer.WriteUInt16(3); writer.WriteBoolean(raw);
        if (raw)
        {
            writer.WriteByte((byte)(BitConverter.IsLittleEndian ? 1 : 0)); writer.WriteInt32(IntPtr.Size);
            writer.WriteString(RuntimeInformation.FrameworkDescription); writer.WriteString(RuntimeInformation.ProcessArchitecture.ToString());
        }
        writer.WriteBoolean(preserve); writer.WriteUInt64(version); writer.WriteInt32(used);
        writer.WriteRawBytes(MemoryMarshal.AsBytes(generations.AsSpan(0, used + 1)));
        writer.WriteRawBytes(MemoryMarshal.AsBytes(alive.AsSpan(0, used + 1)));
        writer.WriteInt32(free.Length); writer.WriteRawBytes(MemoryMarshal.AsBytes(free));
        writer.WriteInt32(types.Length);
        foreach (NativeArchiveType type in types)
        { SerializationTypeKey key = type.KeyFor(raw); writer.WriteGuid(key.StableId); writer.WriteString(key.StableName); writer.WriteUInt64(key.SchemaFingerprint); }
        writer.WriteBoolean(layout.DisjointGroups); writer.WriteInt32(layout.Groups.Count);
        foreach (NativeArchiveGroup group in layout.Groups)
        { writer.WriteUInt64(group.Types); writer.WriteInt32(group.Identities.Length); writer.WriteRawBytes(MemoryMarshal.AsBytes(group.Identities.AsSpan())); }
        writer.Flush();
    }
    internal void Save(NativeArchiveStore store, INativeArchiveProgress state, IBufferWriter<byte> output, bool preserve)
    {
        state.CompleteSystems();
        if (!preserve) state.Complete();
        NativeArchiveLayout layout = Bind(store);
        Header(output, store.Generations, store.AliveIds, store.Used, CollectionsMarshal.AsSpan(store.FreeIds), state.Version, preserve, layout);
        for (int i = 0; i < types.Length; i++) types[i].Write(store, output, layout.Columns[i]);
        var writer = new DataWriter(output); state.Write(ref writer, preserve); writer.Flush();
    }
    internal void Save(NativeArchiveStore store, INativeArchiveProgress state, Stream output, bool preserve)
        => Save(store, state, new StreamDataOutput(output), preserve);
    internal void Load(NativeArchiveStore store, INativeArchiveProgress state, ReadOnlySpan<byte> input)
    { var reader = new DataReader(input); Load(store, state, ref reader); }
    internal void Load(NativeArchiveStore store, INativeArchiveProgress state, Stream input)
    { var reader = new DataReader(input); try { Load(store, state, ref reader); } finally { reader.Dispose(); } }
    private void Load(NativeArchiveStore store, INativeArchiveProgress state, ref DataReader reader)
    {
        state.CompleteSystems();
        if (reader.ReadUInt32() != 0x41524348 || reader.ReadUInt16() != 3) throw new InvalidDataException("Native archive format differs.");
        if (reader.ReadBoolean() != raw) throw new InvalidDataException("Native archive encoding differs.");
        if (raw)
        {
            byte endian = reader.ReadByte(); int pointer = reader.ReadInt32(); string? runtime = reader.ReadString(), architecture = reader.ReadString();
            if (endian != (BitConverter.IsLittleEndian ? 1 : 0) || pointer != IntPtr.Size || runtime != RuntimeInformation.FrameworkDescription
                || architecture != RuntimeInformation.ProcessArchitecture.ToString()) throw new InvalidDataException("Native checkpoint build identity differs.");
        }
        bool preserve = reader.ReadBoolean(); ulong version = reader.ReadUInt64(); int used = reader.ReadInt32();
        int[] generations = new int[used + 1]; bool[] alive = new bool[used + 1];
        reader.ReadRawBytes(MemoryMarshal.AsBytes(generations.AsSpan())); reader.ReadRawBytes(MemoryMarshal.AsBytes(alive.AsSpan()));
        int[] free = new int[reader.ReadInt32()]; reader.ReadRawBytes(MemoryMarshal.AsBytes(free.AsSpan()));
        if (reader.ReadInt32() != types.Length) throw new InvalidDataException("Native archive manifest count differs.");
        foreach (NativeArchiveType type in types)
        {
            SerializationTypeKey key = type.KeyFor(raw);
            if (reader.ReadGuid() != key.StableId || reader.ReadString() != key.StableName || reader.ReadUInt64() != key.SchemaFingerprint)
                throw new InvalidDataException("Native archive schema differs.");
        }
        bool disjointGroups = reader.ReadBoolean(); var groups = new NativeArchiveGroup[reader.ReadInt32()];
        for (int i = 0; i < groups.Length; i++)
        {
            ulong mask = reader.ReadUInt64();
            var identities = new ArchiveIdentity[reader.ReadInt32()]; reader.ReadRawBytes(MemoryMarshal.AsBytes(identities.AsSpan()));
            groups[i] = new(i, identities) { Types = mask };
        }
        state.ReleaseValues(); store.RestoreIdentities(generations, alive, used, free, groups, disjointGroups, types);
        foreach (NativeArchiveType type in types) type.Read(store, ref reader, groups);
        state.Version = preserve ? version : Math.Max(state.Version, version) + 1;
        state.Read(ref reader, preserve);
    }
    internal Task SaveAsync(NativeArchiveStore store, INativeArchiveProgress state, Stream output, bool preserve)
    {
        CapturedNativeArchive snapshot = Capture(store, state, preserve);
        return Task.Run(() => { using (snapshot) snapshot.Write(this, new StreamDataOutput(output)); });
    }
    internal void SaveFile(NativeArchiveStore store, INativeArchiveProgress state, string path, bool preserve)
        => AtomicSaveFile.Write(path, new SaveArchive(this, store, state, preserve));
    internal Task SaveFileAsync(NativeArchiveStore store, INativeArchiveProgress state, string path, bool preserve)
    {
        CapturedNativeArchive snapshot = Capture(store, state, preserve);
        return Task.Run(() => { using (snapshot) AtomicSaveFile.Write(path, new SaveCapturedArchive(this, snapshot)); });
    }
    private readonly struct SaveArchive(NativeArchiveSerializer serializer, NativeArchiveStore store,
        INativeArchiveProgress state, bool preserve) : ISaveDataWriter
    {
        public void Write(Stream output) => serializer.Save(store, state, output, preserve);
    }
    private readonly struct SaveCapturedArchive(NativeArchiveSerializer serializer, CapturedNativeArchive captured) : ISaveDataWriter
    {
        public void Write(Stream output) => captured.Write(serializer, new StreamDataOutput(output));
    }

}
