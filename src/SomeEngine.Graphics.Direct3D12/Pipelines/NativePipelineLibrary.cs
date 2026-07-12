using System.Text;
using Vortice.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

/// <summary>
/// Owns a real D3D12 pipeline library and an optional versioned persistent container. The
/// container keeps the native driver blob together with its otherwise non-enumerable names so
/// a cache miss does not deliberately issue an invalid native Load* call into the InfoQueue.
/// </summary>
internal sealed class NativePipelineLibrary : IDisposable
{
    private const uint ContainerMagic = 0x424C_5044; // "DPLB"
    private const uint ContainerVersion = 2;

    private readonly ID3D12Device1 _device;
    private readonly string? _path;
    private readonly Action<string> _warning;
    private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
    private ID3D12PipelineLibrary _library;
    private ulong _generation;
    private bool _disposed;

    public NativePipelineLibrary(ID3D12Device device, string? path, Action<string> warning)
    {
        _device = device.QueryInterface<ID3D12Device1>();
        _warning = warning;
        _path = ResolvePath(path);
        try
        {
            _library = CreateFromPersistentContainer();
        }
        catch
        {
            _device.Dispose();
            throw;
        }
    }

    public int EntryCount => _entries.Count;

    public bool Contains(PipelineCacheKey key) =>
        _entries.ContainsKey(LogicalName(key, PipelineType.Raster)) ||
        _entries.ContainsKey(LogicalName(key, PipelineType.Compute));

    public bool Invalidate(PipelineCacheKey key)
    {
        bool removed = _entries.Remove(LogicalName(key, PipelineType.Raster));
        removed |= _entries.Remove(LogicalName(key, PipelineType.Compute));
        if (removed) SaveSafely();
        return removed;
    }

    public bool TryLoadGraphics(
        PipelineCacheKey key,
        in GraphicsPipelineStateDescription description,
        out ID3D12PipelineState pipeline)
    {
        string logicalName = LogicalName(key, PipelineType.Raster);
        if (!_entries.TryGetValue(logicalName, out string? physicalName))
        {
            pipeline = null!;
            return false;
        }
        try
        {
            pipeline = _library.LoadGraphicsPipeline(physicalName, description);
            return true;
        }
        catch (Exception exception)
        {
            _warning($"D3D12 pipeline-library graphics entry '{logicalName}' was rejected and will be rebuilt: {exception.Message}");
            _entries.Remove(logicalName);
            pipeline = null!;
            return false;
        }
    }

    public bool TryLoadCompute(
        PipelineCacheKey key,
        in ComputePipelineStateDescription description,
        out ID3D12PipelineState pipeline)
    {
        string logicalName = LogicalName(key, PipelineType.Compute);
        if (!_entries.TryGetValue(logicalName, out string? physicalName))
        {
            pipeline = null!;
            return false;
        }
        try
        {
            pipeline = _library.LoadComputePipeline(physicalName, description);
            return true;
        }
        catch (Exception exception)
        {
            _warning($"D3D12 pipeline-library compute entry '{logicalName}' was rejected and will be rebuilt: {exception.Message}");
            _entries.Remove(logicalName);
            pipeline = null!;
            return false;
        }
    }

    public void Store(PipelineCacheKey key, PipelineType type, ID3D12PipelineState pipeline)
    {
        string logicalName = LogicalName(key, type);
        if (_entries.ContainsKey(logicalName)) return;
        string physicalName = PhysicalName(logicalName, checked(++_generation));
        try
        {
            _library.StorePipeline(physicalName, pipeline);
            _entries.Add(logicalName, physicalName);
        }
        catch (Exception exception)
        {
            _warning($"D3D12 pipeline-library entry '{logicalName}' could not be stored and will remain memory-only: {exception.Message}");
        }
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            ID3D12PipelineLibrary replacement = _device.CreatePipelineLibrary(Span<byte>.Empty);
            ID3D12PipelineLibrary previous = _library;
            _library = replacement;
            _entries.Clear();
            _generation = 0;
            previous.Dispose();
            SaveSafely();
        }
        catch (Exception exception)
        {
            // Native pipeline-library contents cannot be enumerated or selectively removed. If
            // replacement fails, clearing the portable name index still makes every old entry
            // unreachable while preserving a usable in-memory library for new pipelines.
            _entries.Clear();
            _generation = 0;
            _warning($"D3D12 pipeline-library reset failed; old entries were made unreachable: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        SaveSafely();
        _library.Dispose();
        _device.Dispose();
        _disposed = true;
    }

    private ID3D12PipelineLibrary CreateFromPersistentContainer()
    {
        if (_path is null || !File.Exists(_path))
            return _device.CreatePipelineLibrary(Span<byte>.Empty);

        try
        {
            byte[] container = File.ReadAllBytes(_path);
            using MemoryStream stream = new(container, writable: false);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != ContainerMagic)
                throw new InvalidDataException("Pipeline cache magic is invalid.");
            uint version = reader.ReadUInt32();
            if (version is not 1 and not ContainerVersion)
                throw new InvalidDataException("Pipeline cache version is unsupported.");
            int nameCount = reader.ReadInt32();
            if (nameCount < 0 || nameCount > 1_000_000)
                throw new InvalidDataException("Pipeline cache name count is invalid.");
            ReadEntryIndex(reader, version, nameCount);
            int blobLength = reader.ReadInt32();
            if (blobLength < 0 || blobLength > stream.Length - stream.Position)
                throw new InvalidDataException("Pipeline cache native blob length is invalid.");
            byte[] blob = reader.ReadBytes(blobLength);
            if (stream.Position != stream.Length)
                throw new InvalidDataException("Pipeline cache contains trailing data.");
            return _device.CreatePipelineLibrary(blob.AsSpan());
        }
        catch (Exception exception)
        {
            _entries.Clear();
            _generation = 0;
            _warning($"D3D12 persistent pipeline cache '{_path}' was rejected and reset: {exception.Message}");
            return _device.CreatePipelineLibrary(Span<byte>.Empty);
        }
    }

    private void ReadEntryIndex(BinaryReader reader, uint version, int nameCount)
    {
        for (int index = 0; index < nameCount; index++)
        {
            string logicalName = reader.ReadString();
            string physicalName = version == 1 ? logicalName : reader.ReadString();
            _entries.Add(logicalName, physicalName);
        }
        if (version != 1) _generation = reader.ReadUInt64();
    }

    private void SaveSafely()
    {
        try
        {
            SaveCore();
        }
        catch (Exception exception)
        {
            _warning($"D3D12 persistent pipeline cache '{_path}' could not be saved; execution continues without persistence: {exception.Message}");
        }
    }

    private unsafe void SaveCore()
    {
        if (_path is null) return;
        ulong size = _library.SerializedSize.Value.ToUInt64();
        if (size > int.MaxValue)
            throw new InvalidOperationException("The D3D12 pipeline-library blob exceeds the supported managed artifact size.");
        byte[] blob = new byte[checked((int)size)];
        fixed (byte* destination = blob)
        {
            _library.Serialize((nint)destination, _library.SerializedSize);
        }

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ContainerMagic);
            writer.Write(ContainerVersion);
            KeyValuePair<string, string>[] entries = _entries
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
            writer.Write(entries.Length);
            foreach (KeyValuePair<string, string> entry in entries)
            {
                writer.Write(entry.Key);
                writer.Write(entry.Value);
            }
            writer.Write(_generation);
            writer.Write(blob.Length);
            writer.Write(blob);
        }

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = $"{_path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, stream.ToArray());
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Persistence is optional. A failed best-effort cleanup must not replace the
                // original save failure or make device shutdown fail.
            }
        }
    }

    private string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception)
        {
            _warning($"D3D12 persistent pipeline cache path was rejected; execution continues without persistence: {exception.Message}");
            return null;
        }
    }

    private static string LogicalName(PipelineCacheKey key, PipelineType type) =>
        $"{key.StableId:N}-{key.Version:X16}-{type}";

    private static string PhysicalName(string logicalName, ulong generation) =>
        $"{logicalName}-g{generation:X16}";
}
