using System.Buffers.Binary;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Runtime;

/// <summary>Reads the standard presentation target without changing the runtime render path.</summary>
internal sealed class FrameOutputVerifier : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly int _width;
    private readonly int _height;
    private readonly TextureCopyFootprint _footprint;
    private readonly Buffer _readback;
    private readonly byte[] _bytes;
    private bool _disposed;

    internal FrameOutputVerifier(
        IGraphicsBackend backend,
        Device device,
        int width,
        int height,
        Format format)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (format != Format.R8G8B8A8UNorm)
            throw new NotSupportedException($"Runtime frame verification does not support {format}.");

        _width = width;
        _height = height;
        TextureDesc description = new(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            format,
            TextureUsages.ColorAttachment | TextureUsages.CopySource,
            label: "Runtime frame verification source");
        BufferTextureCopy footprintRequest = new(
            null!,
            0,
            0,
            0,
            null!,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            checked((uint)width),
            checked((uint)height),
            1);
        _footprint = backend.GetTextureCopyFootprint(device, description, footprintRequest);
        _readback = backend.CreateBuffer(
            device,
            new BufferDesc(
                _footprint.TotalSize,
                BufferUsages.CopyDestination,
                "Runtime frame verification readback"),
            MemoryType.Readback);
        _bytes = new byte[checked((int)_footprint.TotalSize)];
    }

    internal void Record(
        ref RenderGraphFrame graph,
        GraphTextureId source,
        Queue presentationQueue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(presentationQueue);
        GraphBufferId destination = graph.Import(
            _readback,
            [new BufferBoundaryState(
                new BufferRange(0, _readback.Info.Size),
                _readback.InitialSync,
                _readback.InitialAccess,
                ResourceContentState.Undefined)]);
        ReadbackPassData passData = new(
            source,
            destination,
            _footprint.Offset,
            _footprint.RowPitch,
            _footprint.RowCount,
            checked((uint)_width),
            checked((uint)_height));
        _ = graph.AddCopyPass(
            "Verify runtime frame output",
            PassQueueSelection.Exact(presentationQueue),
            passData,
            default,
            static (ref PassDefinition access, ref ReadbackPassData data) =>
            {
                _ = access.Read(
                    data.Source,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    PipelineSync.Copy,
                    ResourceAccess.CopySource,
                    TextureLayout.CopySource);
                _ = access.Write(
                    data.Destination,
                    new BufferRange(0, data.TotalSize),
                    PipelineSync.Copy,
                    ResourceAccess.CopyDestination,
                    WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in ReadbackPassData data) =>
            {
                Buffer destinationBuffer = commands.GetBuffer(data.Destination);
                Texture sourceTexture = commands.GetTexture(data.Source);
                commands.CopyTextureToBuffer(new BufferTextureCopy(
                    destinationBuffer,
                    data.Offset,
                    data.RowPitch,
                    data.RowCount,
                    sourceTexture,
                    0,
                    0,
                    TextureAspects.Color,
                    0,
                    0,
                    0,
                    data.Width,
                    data.Height,
                    1));
            });
    }

    internal FrameOutputMetrics Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        BufferRange range = new(0, checked((ulong)_bytes.Length));
        using MappedBuffer mapping = _backend.Map(_readback, MapType.Read, range);
        mapping.Invalidate(range);
        mapping.Bytes.CopyTo(_bytes);
        return Analyze();
    }

    private FrameOutputMetrics Analyze()
    {
        byte minRed = byte.MaxValue;
        byte minGreen = byte.MaxValue;
        byte minBlue = byte.MaxValue;
        byte maxRed = byte.MinValue;
        byte maxGreen = byte.MinValue;
        byte maxBlue = byte.MinValue;
        int differentFromFirst = 0;
        int minDifferentX = _width;
        int minDifferentY = _height;
        int maxDifferentX = -1;
        int maxDifferentY = -1;
        uint firstRgb = 0;
        bool hasFirst = false;
        var colors = new HashSet<uint>();
        const int reportedColorLimit = 256;
        ulong hash = 14695981039346656037ul;

        for (int row = 0; row < _height; row++)
        {
            int rowOffset = checked(
                (int)(_footprint.Offset + (ulong)row * _footprint.RowPitch));
            ReadOnlySpan<byte> pixels = _bytes.AsSpan(rowOffset, checked(_width * 4));
            for (int column = 0; column < _width; column++)
            {
                ReadOnlySpan<byte> pixel = pixels.Slice(column * 4, 4);
                byte red = pixel[0];
                byte green = pixel[1];
                byte blue = pixel[2];
                minRed = Math.Min(minRed, red);
                minGreen = Math.Min(minGreen, green);
                minBlue = Math.Min(minBlue, blue);
                maxRed = Math.Max(maxRed, red);
                maxGreen = Math.Max(maxGreen, green);
                maxBlue = Math.Max(maxBlue, blue);

                uint rgba = BinaryPrimitives.ReadUInt32LittleEndian(pixel);
                uint rgb = rgba & 0x00FFFFFFu;
                if (!hasFirst)
                {
                    firstRgb = rgb;
                    hasFirst = true;
                }
                else if (rgb != firstRgb)
                {
                    differentFromFirst++;
                    minDifferentX = Math.Min(minDifferentX, column);
                    minDifferentY = Math.Min(minDifferentY, row);
                    maxDifferentX = Math.Max(maxDifferentX, column);
                    maxDifferentY = Math.Max(maxDifferentY, row);
                }
                if (colors.Count < reportedColorLimit)
                    colors.Add(rgb);
                for (int channel = 0; channel < 4; channel++)
                {
                    hash ^= pixel[channel];
                    hash *= 1099511628211ul;
                }
            }
        }

        return new FrameOutputMetrics(
            _width,
            _height,
            minRed,
            maxRed,
            minGreen,
            maxGreen,
            minBlue,
            maxBlue,
            colors.Count,
            differentFromFirst,
            minDifferentX,
            minDifferentY,
            maxDifferentX,
            maxDifferentY,
            hash);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _readback.Dispose();
        _disposed = true;
    }

    private readonly record struct ReadbackPassData(
        GraphTextureId Source,
        GraphBufferId Destination,
        ulong Offset,
        uint RowPitch,
        uint RowCount,
        uint Width,
        uint Height)
    {
        internal ulong TotalSize => checked((ulong)RowPitch * RowCount);
    }
}

internal readonly record struct FrameOutputMetrics(
    int Width,
    int Height,
    byte MinRed,
    byte MaxRed,
    byte MinGreen,
    byte MaxGreen,
    byte MinBlue,
    byte MaxBlue,
    int ReportedDistinctColors,
    int PixelsDifferentFromFirst,
    int MinDifferentX,
    int MinDifferentY,
    int MaxDifferentX,
    int MaxDifferentY,
    ulong Hash)
{
    internal int DifferentWidth => MaxDifferentX >= MinDifferentX
        ? MaxDifferentX - MinDifferentX + 1
        : 0;

    internal int DifferentHeight => MaxDifferentY >= MinDifferentY
        ? MaxDifferentY - MinDifferentY + 1
        : 0;

    internal int MaximumRgbRange => Math.Max(
        MaxRed - MinRed,
        Math.Max(MaxGreen - MinGreen, MaxBlue - MinBlue));

    internal bool HasVisibleVariation =>
        ReportedDistinctColors > 1 &&
        PixelsDifferentFromFirst > 0 &&
        MaximumRgbRange > 1;

    internal bool HasSubstantialCoverage =>
        HasVisibleVariation &&
        PixelsDifferentFromFirst >= checked(Width * Height / 100) &&
        DifferentWidth >= Math.Max(1, Width / 4) &&
        DifferentHeight >= Math.Max(1, Height / 4);
}
