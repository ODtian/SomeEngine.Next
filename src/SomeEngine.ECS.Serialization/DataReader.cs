using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Unicode;
using SomeEngine.ECS.Entities;
using SomeEngine.Serialization;

namespace SomeEngine.ECS.Serialization;

public ref struct DataReader
{
    private readonly Stream? _stream;
    private readonly byte[]? _buffer;
    private ReadOnlySpan<byte> _bytes;
    private int _offset;
    private long _committed;
    internal readonly SerializationReadBudget Budget;
    internal World? ValuesWorld;
    internal ReadIdentityMap? Identities;
    private ReadReferenceRemapper? _entityReferences;
    internal Dictionary<int, int[]>? SharedMappings;
    internal bool NormalizeShared;
    private LoadedEntityMapper _currentMapper, _progressMapper;

    public DataReader(ReadOnlySpan<byte> bytes, SerializationReadLimits? limits = null)
    {
        _stream = null; _buffer = null; _bytes = bytes; _offset = 0; _committed = 0;
        Budget = new(limits);
        ValuesWorld = null;
        Identities = null; SharedMappings = null; NormalizeShared = false;
        _entityReferences = null;
        _currentMapper = _progressMapper = default;
    }
    public DataReader(Stream stream, SerializationReadLimits? limits = null)
    {
        _stream = stream; _buffer = ArrayPool<byte>.Shared.Rent(4096); _bytes = default; _offset = 0; _committed = 0;
        Budget = new(limits);
        ValuesWorld = null;
        Identities = null; SharedMappings = null; NormalizeShared = false;
        _entityReferences = null;
        _currentMapper = _progressMapper = default;
    }
    public long Position => _committed + _offset;
    internal DataReader(ReadOnlySpan<byte> bytes, SerializationReadBudget budget)
        : this(bytes) => Budget = budget;

    internal bool PatchesValues => Identities is not null || NormalizeShared || _entityReferences is not null;

    internal void BindEntityReferences(ReadReferenceRemapper references)
    {
        _entityReferences = references;
        _currentMapper = _progressMapper = new(references);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > _bytes.Length - _offset) Next(count);
        ReadOnlySpan<byte> bytes = _bytes.Slice(_offset, count);
        _offset += count;
        return bytes;
    }
    private void Next(int count)
    {
        if (_stream is null) throw new InvalidDataException("Truncated serialized value.");
        int remaining = _bytes.Length - _offset;
        _bytes[_offset..].CopyTo(_buffer);
        _committed += _offset;
        _offset = 0;
        int length = _stream.CanSeek ? _buffer!.Length - remaining : count - remaining;
        int read = _stream.ReadAtLeast(_buffer.AsSpan(remaining, length), count - remaining);
        _bytes = _buffer.AsSpan(0, remaining + read);
    }
    public bool ReadBoolean() => BinaryPrimitiveEncoding.ReadBoolean(Take(1));
    public byte ReadByte() => BinaryPrimitiveEncoding.ReadByte(Take(1));
    public sbyte ReadSByte() => BinaryPrimitiveEncoding.ReadSByte(Take(1));
    public short ReadInt16() => BinaryPrimitiveEncoding.ReadInt16(Take(2));
    public ushort ReadUInt16() => BinaryPrimitiveEncoding.ReadUInt16(Take(2));
    public int ReadInt32() => BinaryPrimitiveEncoding.ReadInt32(Take(4));
    public uint ReadUInt32() => BinaryPrimitiveEncoding.ReadUInt32(Take(4));
    public long ReadInt64() => BinaryPrimitiveEncoding.ReadInt64(Take(8));
    public ulong ReadUInt64() => BinaryPrimitiveEncoding.ReadUInt64(Take(8));
    public float ReadSingle() => BinaryPrimitiveEncoding.ReadSingle(Take(4));
    public double ReadDouble() => BinaryPrimitiveEncoding.ReadDouble(Take(8));
    public char ReadChar() => BinaryPrimitiveEncoding.ReadChar(Take(2));
    public Guid ReadGuid() => BinaryPrimitiveEncoding.ReadGuid(Take(16));
    public ExternalReferenceKey ReadExternalReference() => new(ReadGuid());
    public Entity ReadEntity() => new(ReadInt32(), ReadInt32());
    internal Entity MapEntity(Entity entity, bool progress = false)
    {
        if (_entityReferences is { } references)
        { references.TryMap(entity, out Entity groupEntity); return groupEntity; }
        if (Identities is not { } map) return entity;
        (progress ? map.Progress : map.Current).TryMap(entity, out Entity mapped);
        return mapped;
    }
    internal int MapId(int id) => Identities?.TargetId(id) ?? id;
    internal void BindReferenceViews()
    {
        ReadReferenceRemapper current = Identities is { } map ? map.Current : PreserveReferenceRemapper.Instance;
        ReadReferenceRemapper progress = Identities is { } history ? history.Progress : PreserveReferenceRemapper.Instance;
        _currentMapper = new(current);
        _progressMapper = new(progress);
    }
    internal void Patch<T>(ref T value, IReferencePatcher<T>? patcher, bool progress = false)
    {
        if (patcher is not null && PatchesValues)
        {
            if (patcher is IGeneratedReferencePatcher<T> generated)
            {
                ref LoadedEntityMapper mapper = ref (progress ? ref _progressMapper : ref _currentMapper);
                EntityMap entities = EntityMap.Create(ref mapper);
                generated.Remap(ref value, entities);
                return;
            }
            ReadReferenceRemapper remapper = _entityReferences ?? (Identities is { } map
                ? progress ? map.Progress : map.Current : PreserveReferenceRemapper.Instance);
            patcher.Remap(ref value, remapper);
        }
    }
    public string? ReadString() => ReadBoolean() ? ReadText() : null;

    internal string ReadText(bool stableName = false)
    {
        int count = Budget.StringCharacterCount(ReadInt32(), stableName);
        if (count == 0) return string.Empty;
        if (_offset == _bytes.Length) Next(1);
        long start = Position;
        StringBytes result = default;
        string value = string.Create(count, new StringInput(_bytes[_offset..], _stream, _buffer, ref result),
            static (destination, input) => input.Read(destination));
        Budget.StringBytesConsumed(result.Consumed, stableName);
        if (result.Refilled)
        {
            _bytes = _buffer.AsSpan(0, result.Length);
            _offset = result.Offset;
            _committed = start + result.Consumed - result.Offset;
        }
        else _offset += result.Consumed;
        return value;
    }

    private struct StringBytes { internal int Consumed, Offset, Length; internal bool Refilled; }
    private readonly ref struct StringInput
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly Stream? _stream;
        private readonly byte[]? _buffer;
        private readonly ref StringBytes _result;
        internal StringInput(ReadOnlySpan<byte> bytes, Stream? stream, byte[]? buffer, ref StringBytes result)
        { _bytes = bytes; _stream = stream; _buffer = buffer; _result = ref result; }
        internal void Read(Span<char> destination)
        {
            ReadOnlySpan<byte> input = _bytes;
            int written = 0;
            while (written < destination.Length)
            {
                OperationStatus status = Utf8.ToUtf16(input, destination[written..], out int read, out int chars,
                    replaceInvalidSequences: false, isFinalBlock: _stream is null);
                _result.Consumed += read;
                written += chars;
                if (written == destination.Length) { _result.Offset += read; return; }
                if (_stream is null || status is OperationStatus.InvalidData or OperationStatus.DestinationTooSmall)
                    throw new InvalidDataException("Serialized string does not match its UTF-8 character count.");
                input = input[read..];
                int remaining = input.Length;
                input.CopyTo(_buffer);
                int length = _stream.CanSeek ? _buffer!.Length - remaining : 1;
                int added = _stream.ReadAtLeast(_buffer.AsSpan(remaining, length), 1);
                input = _buffer.AsSpan(0, remaining + added);
                _result.Refilled = true;
                _result.Length = input.Length;
                _result.Offset = 0;
            }
        }
    }

    internal void ReadRawBytes(scoped Span<byte> destination)
    {
        if (destination.Length <= _bytes.Length - _offset)
        {
            Take(destination.Length).CopyTo(destination);
            return;
        }
        if (_stream is null) throw new InvalidDataException("Truncated serialized column.");
        int remaining = _bytes.Length - _offset;
        _bytes[_offset..].CopyTo(destination);
        _committed += _offset + destination.Length;
        _offset = 0;
        _bytes = default;
        _stream.ReadExactly(destination[remaining..]);
    }
    internal void RequireEndOfInput()
    {
        if (_offset != _bytes.Length || _stream is not null && _stream.ReadByte() != -1)
            throw new InvalidDataException("The serialized World has trailing input.");
    }

    public void Dispose()
    {
        if (_stream?.CanSeek == true && _offset != _bytes.Length)
            _stream.Seek(_offset - _bytes.Length, SeekOrigin.Current);
        if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
    }
}
