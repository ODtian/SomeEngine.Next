using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Lighting;

/// <summary>
/// Owns the reusable upload buffers and exact GPU conversion for independently admitted render
/// generations. Geometry pipelines borrow the uploaded buffer and light counts.
/// </summary>
public sealed class GpuLightBufferPool : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Buffer?[] _buffers;
    private readonly int[] _capacities;
    private readonly ulong[] _revisions;
    private readonly ulong[] _cookieRevisions;
    private readonly uint[] _directionalCounts;
    private readonly uint[] _pointCounts;
    private readonly uint[] _spotCounts;
    private readonly List<GpuLight> _values = [];
    private bool _disposed;

    public GpuLightBufferPool(
        IGraphicsBackend backend,
        Device device,
        int generationCount)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationCount);
        _buffers = new Buffer?[generationCount];
        _capacities = new int[generationCount];
        _revisions = new ulong[generationCount];
        _cookieRevisions = new ulong[generationCount];
        _directionalCounts = new uint[generationCount];
        _pointCounts = new uint[generationCount];
        _spotCounts = new uint[generationCount];
    }

    public GpuLightUpload Upload(int generation, RenderLightSet source)
        => Upload(
            generation,
            source,
            ReadOnlySpan<GpuLightCookieAssignment>.Empty,
            ReadOnlySpan<GpuLightCookieAssignment>.Empty,
            ReadOnlySpan<GpuLightCookieAssignment>.Empty,
            0ul);

    public GpuLightUpload Upload(
        int generation,
        RenderLightSet source,
        ReadOnlySpan<GpuLightCookieAssignment> directionalCookies,
        ReadOnlySpan<GpuLightCookieAssignment> pointCookies,
        ReadOnlySpan<GpuLightCookieAssignment> spotCookies,
        ulong cookieRevision)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)generation >= (uint)_buffers.Length)
            throw new ArgumentOutOfRangeException(nameof(generation));
        ValidateCookieAssignments(source, directionalCookies, pointCookies, spotCookies);
        if (_buffers[generation] is { } cached &&
            source.Revision != 0uL &&
            _revisions[generation] == source.Revision &&
            _cookieRevisions[generation] == cookieRevision)
        {
            return new GpuLightUpload(
                cached,
                _directionalCounts[generation],
                _pointCounts[generation],
                _spotCounts[generation]);
        }

        _values.Clear();
        _values.EnsureCapacity(
            source.Directional.Count + source.Points.Count + source.Spots.Count);
        for (int index = 0; index < source.Directional.Count; index++)
        {
            RenderDirectionalLight light = source.Directional[index];
            GpuLight gpu = new()
            {
                Direction = NormalizeOrZero(light.Direction),
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = new Vector4(1, 1, 0, 0),
            };
            ApplyCookie(
                ref gpu,
                source.DirectionalCookies[index],
                CookieAssignment(directionalCookies, index));
            _values.Add(gpu);
        }
        for (int index = 0; index < source.Points.Count; index++)
        {
            RenderPointLight light = source.Points[index];
            GpuLight gpu = new()
            {
                Position = light.Position,
                Range = light.Range,
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = new Vector4(1, 1, 0, 0),
            };
            ApplyCookie(
                ref gpu,
                source.PointCookies[index],
                CookieAssignment(pointCookies, index));
            _values.Add(gpu);
        }
        for (int index = 0; index < source.Spots.Count; index++)
        {
            RenderSpotLight light = source.Spots[index];
            GpuLight gpu = new()
            {
                Position = light.Position,
                Range = light.Range,
                Direction = NormalizeOrZero(light.Direction),
                InnerConeCos = light.InnerConeCos,
                OuterConeCos = light.OuterConeCos,
                Color = light.Color,
                Intensity = light.Intensity,
                LayerMask = light.LayerMask,
                CookieIndex = -1,
                CookieStrength = 1.0f,
                WorldToLightCookie = Matrix4x4.Identity,
                CookieScaleOffset = new Vector4(1, 1, 0, 0),
            };
            ApplyCookie(
                ref gpu,
                source.SpotCookies[index],
                CookieAssignment(spotCookies, index));
            _values.Add(gpu);
        }
        if (_values.Count == 0)
            _values.Add(default);

        int byteCount = checked(_values.Count * GpuLight.SizeInBytes);
        EnsureCapacity(generation, byteCount);
        Buffer buffer = _buffers[generation]
            ?? throw new InvalidOperationException("The GPU light buffer was not created.");
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(_values));
        BufferRange range = new(0, checked((ulong)bytes.Length));
        using MappedBuffer mapping = _backend.Map(buffer, MapType.Write, range);
        bytes.CopyTo(mapping.Bytes);
        mapping.Flush(range);
        uint directionalCount = checked((uint)source.Directional.Count);
        uint pointCount = checked((uint)source.Points.Count);
        uint spotCount = checked((uint)source.Spots.Count);
        _revisions[generation] = source.Revision;
        _cookieRevisions[generation] = cookieRevision;
        _directionalCounts[generation] = directionalCount;
        _pointCounts[generation] = pointCount;
        _spotCounts[generation] = spotCount;
        return new GpuLightUpload(
            buffer,
            directionalCount,
            pointCount,
            spotCount);
    }

    private void EnsureCapacity(int generation, int byteCount)
    {
        if (_buffers[generation] is not null && _capacities[generation] >= byteCount)
            return;
        Buffer replacement = _backend.CreateBuffer(
            _device,
            new BufferDesc(
                checked((ulong)byteCount),
                BufferUsages.ShaderRead,
                $"Render lights {generation}"),
            MemoryType.Upload);
        Buffer? previous = _buffers[generation];
        _buffers[generation] = replacement;
        _capacities[generation] = byteCount;
        _revisions[generation] = 0uL;
        _cookieRevisions[generation] = 0uL;
        previous?.Dispose();
    }

    private static void ValidateCookieAssignments(
        RenderLightSet source,
        ReadOnlySpan<GpuLightCookieAssignment> directional,
        ReadOnlySpan<GpuLightCookieAssignment> points,
        ReadOnlySpan<GpuLightCookieAssignment> spots)
    {
        if ((!directional.IsEmpty && directional.Length != source.Directional.Count) ||
            (!points.IsEmpty && points.Length != source.Points.Count) ||
            (!spots.IsEmpty && spots.Length != source.Spots.Count))
        {
            throw new ArgumentException(
                "GPU light-cookie assignments must be empty or match their light list.");
        }
    }

    private static GpuLightCookieAssignment CookieAssignment(
        ReadOnlySpan<GpuLightCookieAssignment> assignments,
        int index) => assignments.IsEmpty
            ? GpuLightCookieAssignment.None
            : assignments[index];

    private static void ApplyCookie(
        ref GpuLight destination,
        RenderLightCookie? cookie,
        in GpuLightCookieAssignment assignment)
    {
        if (cookie is not { } value || assignment.Index < 0)
            return;
        destination.CookieIndex = assignment.Index;
        destination.CookieStrength = value.Strength;
        destination.WorldToLightCookie = value.WorldToCookie;
        destination.CookieScaleOffset = new Vector4(
            value.ScaleOffset.X * assignment.AtlasScale.X,
            value.ScaleOffset.Y * assignment.AtlasScale.Y,
            value.ScaleOffset.Z * assignment.AtlasScale.X,
            value.ScaleOffset.W * assignment.AtlasScale.Y);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        for (int index = 0; index < _buffers.Length; index++)
        {
            try { _buffers[index]?.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            _buffers[index] = null;
            _capacities[index] = 0;
            _revisions[index] = 0uL;
            _cookieRevisions[index] = 0uL;
            _directionalCounts[index] = 0u;
            _pointCounts[index] = 0u;
            _spotCounts[index] = 0u;
        }
        _values.Clear();
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
        => value.LengthSquared() > 1e-12f ? Vector3.Normalize(value) : Vector3.Zero;
}

public readonly record struct GpuLightUpload(
    Buffer Buffer,
    uint DirectionalCount,
    uint PointCount,
    uint SpotCount);

public readonly record struct GpuLightCookieAssignment(
    int Index,
    Vector2 AtlasScale)
{
    public static GpuLightCookieAssignment None => new(-1, Vector2.One);
}
