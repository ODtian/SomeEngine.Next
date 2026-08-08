using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public PipelineCache CreatePipelineCache(Device device, in PipelineCacheDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        D3D12PipelineCache result = new(nativeDevice, desc.Data, desc.Label);
        nativeDevice.RegisterChild(result);
        return result;
    }

    public bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount)
    {
        byte[] data = NativeCast.PipelineCache(cache).Serialize();
        requiredByteCount = data.Length;
        if (destination.Length < data.Length)
            return false;
        data.CopyTo(destination);
        return true;
    }

    public void MergePipelineCaches(
        PipelineCache destination,
        ReadOnlySpan<PipelineCache> sources)
    {
        D3D12PipelineCache nativeDestination = NativeCast.PipelineCache(destination);
        foreach (PipelineCache source in sources)
            nativeDestination.Merge(NativeCast.PipelineCache(source));
    }

    private sealed class D3D12PipelineCache : PipelineCache
    {
        private static readonly byte[] Magic = "SERHIC01"u8.ToArray();
        private const uint Version = 1;

        private readonly D3D12Device _device;
        private readonly object _gate = new();
        private readonly SortedDictionary<CacheEntryKey, byte[]> _entries = [];
        private int _released;

        internal D3D12PipelineCache(
            D3D12Device device,
            ReadOnlySpan<byte> data,
            string? label)
            : base(device, label)
        {
            _device = device;
            Compatibility = ComputeCompatibility(device);
            if (!data.IsEmpty)
                Parse(data);
        }

        internal byte[] Compatibility { get; }

        internal bool TryGet(byte family, ReadOnlySpan<byte> key, out byte[] data)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_entries.TryGetValue(
                    new CacheEntryKey(family, Convert.ToHexString(key), Convert.ToHexString(Compatibility)),
                    out byte[]? result))
                {
                    data = result;
                    return true;
                }
                data = [];
                return false;
            }
        }

        internal void Store(byte family, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                CacheEntryKey entry = new(
                    family,
                    Convert.ToHexString(key),
                    Convert.ToHexString(Compatibility));
                byte[] candidate = data.ToArray();
                if (_entries.TryGetValue(entry, out byte[]? existing))
                {
                    if (CompareBytes(candidate, existing) < 0)
                        _entries[entry] = candidate;
                }
                else
                {
                    _entries.Add(entry, candidate);
                }
            }
        }

        internal void Merge(D3D12PipelineCache source)
        {
            KeyValuePair<CacheEntryKey, byte[]>[] snapshot = source.Snapshot();
            lock (_gate)
            {
                ThrowIfDisposed();
                foreach ((CacheEntryKey key, byte[] value) in snapshot)
                {
                    if (_entries.TryGetValue(key, out byte[]? existing))
                    {
                        if (CompareBytes(value, existing) < 0)
                            _entries[key] = value;
                    }
                    else
                    {
                        _entries.Add(key, value);
                    }
                }
            }
        }

        internal byte[] Serialize()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                using MemoryStream stream = new();
                using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(Magic);
                    writer.Write(Version);
                    writer.Write(_entries.Count);
                    foreach ((CacheEntryKey key, byte[] payload) in _entries)
                    {
                        writer.Write(key.Family);
                        writer.Write(Convert.FromHexString(key.Key));
                        writer.Write(Convert.FromHexString(key.Compatibility));
                        writer.Write(payload.Length);
                        writer.Write(payload);
                        writer.Write(SHA256.HashData(payload));
                    }
                }
                byte[] body = stream.ToArray();
                byte[] result = new byte[checked(body.Length + 32)];
                body.CopyTo(result, 0);
                SHA256.HashData(body).CopyTo(result, body.Length);
                return result;
            }
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            lock (_gate)
                _entries.Clear();
            _device.UnregisterChild(this);
        }

        private void Parse(ReadOnlySpan<byte> data)
        {
            try
            {
                if (data.Length < Magic.Length + 4 + 4 + 32)
                    throw new InvalidDataException("The pipeline-cache envelope is truncated.");
                ReadOnlySpan<byte> body = data[..^32];
                if (!SHA256.HashData(body).AsSpan().SequenceEqual(data[^32..]))
                    throw new InvalidDataException("The pipeline-cache envelope checksum is invalid.");
                int offset = 0;
                if (!body[..Magic.Length].SequenceEqual(Magic))
                    throw new InvalidDataException("The pipeline-cache magic is invalid.");
                offset += Magic.Length;
                uint version = ReadUInt32(body, ref offset);
                if (version != Version)
                    throw new InvalidDataException("The pipeline-cache schema is unsupported.");
                uint count = ReadUInt32(body, ref offset);
                if (count > 1_000_000)
                    throw new InvalidDataException("The pipeline-cache entry count is invalid.");
                for (uint index = 0; index < count; index++)
                {
                    byte family = ReadByte(body, ref offset);
                    byte[] key = ReadBytes(body, ref offset, 32);
                    byte[] compatibility = ReadBytes(body, ref offset, 32);
                    uint length = ReadUInt32(body, ref offset);
                    byte[] payload = ReadBytes(body, ref offset, checked((int)length));
                    byte[] checksum = ReadBytes(body, ref offset, 32);
                    if (!SHA256.HashData(payload).AsSpan().SequenceEqual(checksum))
                        throw new InvalidDataException("A pipeline-cache section checksum is invalid.");
                    CacheEntryKey entry = new(
                        family,
                        Convert.ToHexString(key),
                        Convert.ToHexString(compatibility));
                    if (!_entries.TryAdd(entry, payload))
                        throw new InvalidDataException("The pipeline-cache envelope contains a duplicate section.");
                }
                if (offset != body.Length)
                    throw new InvalidDataException("The pipeline-cache envelope has trailing bytes.");
            }
            catch (Exception exception) when (exception is not GraphicsException)
            {
                _entries.Clear();
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "The pipeline-cache envelope is corrupt.",
                    innerException: exception);
            }
        }

        private KeyValuePair<CacheEntryKey, byte[]>[] Snapshot()
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                return _entries
                    .Select(static pair =>
                        new KeyValuePair<CacheEntryKey, byte[]>(pair.Key, (byte[])pair.Value.Clone()))
                    .ToArray();
            }
        }

        private static byte[] ComputeCompatibility(D3D12Device device)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(device.Adapter.Id.Low);
            Append(device.Adapter.Id.High);
            AppendUInt32(device.Adapter.VendorId);
            AppendUInt32(device.Adapter.DeviceId);
            AppendUInt32(device.EnabledNodeMask);
            Append((ulong)device.Capabilities.Features);
            AppendUInt32(619u);
            hash.AppendData(Encoding.UTF8.GetBytes(device.Adapter.DriverVersion));
            return hash.GetHashAndReset();

            void Append(ulong value)
            {
                Span<byte> bytes = stackalloc byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
                hash.AppendData(bytes);
            }

            void AppendUInt32(uint value)
            {
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
                hash.AppendData(bytes);
            }
        }

        private static int CompareBytes(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
            left.SequenceCompareTo(right);

        private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
        {
            if (source.Length - offset < 4)
                throw new EndOfStreamException();
            uint result = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
            offset += 4;
            return result;
        }

        private static byte ReadByte(ReadOnlySpan<byte> source, ref int offset)
        {
            if ((uint)offset >= (uint)source.Length)
                throw new EndOfStreamException();
            return source[offset++];
        }

        private static byte[] ReadBytes(ReadOnlySpan<byte> source, ref int offset, int length)
        {
            if (length < 0 || source.Length - offset < length)
                throw new EndOfStreamException();
            byte[] result = source.Slice(offset, length).ToArray();
            offset += length;
            return result;
        }

        private readonly record struct CacheEntryKey(
            byte Family,
            string Key,
            string Compatibility) : IComparable<CacheEntryKey>
        {
            public int CompareTo(CacheEntryKey other)
            {
                int result = Family.CompareTo(other.Family);
                if (result != 0)
                    return result;
                result = StringComparer.Ordinal.Compare(Key, other.Key);
                return result != 0
                    ? result
                    : StringComparer.Ordinal.Compare(Compatibility, other.Compatibility);
            }
        }
    }

    private static partial class NativeCast
    {
        internal static D3D12PipelineCache PipelineCache(PipelineCache value)
        {
#if DEBUG
            return (D3D12PipelineCache)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<PipelineCache, D3D12PipelineCache>(ref value);
#endif
        }
    }
}
