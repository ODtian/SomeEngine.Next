using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Runtime;

/// <summary>Inputs of the submitted frame, retained with its unfiltered material color.</summary>
public sealed record SceneFrame
{
    public int Frame { get; init; }
    public uint SubmittedFrame { get; init; }
    public float SceneSeconds { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public Vector3 CameraPosition { get; init; }
    public Matrix4x4 View { get; init; }
    public Matrix4x4 Projection { get; init; }
    public Matrix4x4 UnjitteredProjection { get; init; }
    public Vector2 JitterPixels { get; init; }
    public float NearPlane { get; init; }
    public float FarPlane { get; init; }
    public SceneMeshTransform[] Meshes { get; init; } = [];
    public SceneLight[] Lights { get; init; } = [];
    public RuntimeStartupOptions Options { get; init; } = RuntimeStartupOptions.Parse([]);
    public TemporalResolveSettings TemporalSettings { get; init; } = TemporalResolveSettings.Default;
    public float RasterEdgePixels { get; init; } = 4;
    public uint ClusterWorkCapacity { get; init; } = 1_048_576;
    public string Adapter { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    /// <summary>Configured quality, not a measurement of adaptive resolution or resident pages.</summary>
    public VirtualShadowMapSettings ShadowSettings { get; init; } = new();
}

public sealed record SceneMeshTransform(Guid MeshAsset, Matrix4x4 Transform);
public enum SceneLightKind : byte { Directional, Point, Spot }
public sealed record SceneLight(SceneLightKind Kind, Vector3 Position, Vector3 Direction,
    float Range, float InnerConeCos, float OuterConeCos, Vector3 Color, float Intensity, uint LayerMask);

/// <summary>Packed, top-down linear RGBA16F material output before TAA and tone mapping.</summary>
public sealed record SceneLinearImage(SceneFrame Frame, ReadOnlyMemory<Half> Pixels)
{
    /// <summary>Writes little-endian RGBA16F and a companion &lt;path&gt;.json frame description.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Frame.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Frame.Height);
        if (Pixels.Length != checked(Frame.Width * Frame.Height * 4))
            throw new ArgumentException("Linear color requires four channels per pixel.", nameof(Pixels));
        byte[] bytes = new byte[checked(Pixels.Length * 2)];
        for (int channel = 0; channel < Pixels.Length; channel++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(channel * 2),
                BitConverter.HalfToUInt16Bits(Pixels.Span[channel]));
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.Write(bytes);
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(Frame,
            new JsonSerializerOptions { IncludeFields = true, WriteIndented = true }));
    }

    /// <summary>Loads retained color and frame inputs without a graphics device.</summary>
    public static SceneLinearImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SceneFrame frame = JsonSerializer.Deserialize<SceneFrame>(File.ReadAllText(path + ".json"),
            new JsonSerializerOptions { IncludeFields = true }) ?? throw new InvalidDataException("Missing frame inputs.");
        byte[] bytes = File.ReadAllBytes(path);
        if (frame.Width <= 0 || frame.Height <= 0 || bytes.Length != checked(frame.Width * frame.Height * 8))
            throw new InvalidDataException("Linear color dimensions do not match its frame inputs.");
        var pixels = new Half[bytes.Length / 2];
        for (int channel = 0; channel < pixels.Length; channel++)
            pixels[channel] = BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(channel * 2)));
        return new(frame, pixels);
    }
}
