using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SomeEngine.Runtime;

namespace SomeEngine.Render.Cluster.Tests;

internal sealed record ShadowAccuracyRegion(int ComparedPixels, int MismatchedPixels,
    int MissedShadowPixels, int FalseShadowPixels, float MeanVisibilityError)
{
    internal static ShadowAccuracyRegion From(ShadowReferenceComparison value) => new(
        value.ComparedPixels, value.MismatchedPixels, value.MissedShadowPixels,
        value.FalseShadowPixels, value.MeanVisibilityError);
}

internal sealed record ShadowAccuracyObservation(string Capture, int ComparedPixels, int MismatchedPixels,
    int MissedShadowPixels, int FalseShadowPixels, int MissingRenderedReceiverPixels, float MeanVisibilityError,
    ShadowAccuracyRegion? Interior = null, ShadowAccuracyRegion? Boundary = null)
{
    internal static ShadowAccuracyObservation From(RayShadowComparison value) => new("current",
        value.AllReceivers.ComparedPixels, value.AllReceivers.MismatchedPixels,
        value.AllReceivers.MissedShadowPixels, value.AllReceivers.FalseShadowPixels,
        value.MissingRenderedReceiverPixels, value.AllReceivers.MeanVisibilityError,
        ShadowAccuracyRegion.From(value.Interior), ShadowAccuracyRegion.From(value.Boundary));
}

// A source-geometry quality baseline is not an exact shadow-map implementation oracle.
// No threshold is learned from the frame under test and no receivers are excluded.
internal sealed class BistroShadowBaseline
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public required string SourceGeometrySha256 { get; init; }
    public required JsonObject FrameContract { get; init; }
    public required ShadowAccuracyObservation[] Observations { get; init; }

    internal static BistroShadowBaseline Read(string file) =>
        JsonSerializer.Deserialize<BistroShadowBaseline>(File.ReadAllText(file), Json)!;

    internal bool MatchesSource(string geometry) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(geometry))).Equals(SourceGeometrySha256, StringComparison.OrdinalIgnoreCase);

    internal bool MatchesFrame(SceneFrame frame) => JsonNode.DeepEquals(FrameContract, DescribeFrame(frame));

    internal static JsonObject DescribeFrame(SceneFrame frame) => (JsonObject)JsonSerializer.SerializeToNode(new
        {
            frame.Frame, frame.SubmittedFrame, frame.SceneSeconds, frame.Width, frame.Height,
            frame.CameraPosition, frame.WorldOrigin, frame.View, frame.Projection,
            frame.UnjitteredProjection, frame.JitterPixels, frame.NearPlane, frame.FarPlane,
            MeshCount = frame.Meshes.Length, MeshStateSha256 = HashMeshState(frame.Meshes), frame.Lights, frame.ShadowSettings, frame.TemporalSettings,
            frame.RasterEdgePixels, frame.ClusterWorkCapacity, frame.Adapter, frame.DriverVersion,
            RenderingOptions = new
            {
                frame.Options.GraphicsBackend, frame.Options.DynamicScene, frame.Options.AsyncCompute,
                frame.Options.ForceHardwareRaster, frame.Options.DisableMeshShaders,
                frame.Options.TemporalAntialiasing, frame.Options.ShadowRayCount,
                frame.Options.VirtualShadows, frame.Options.FixedFrameSeconds,
            },
        }, Json)!;

    // Asset IDs plus row-major IEEE float32 transform bits, in captured order.
    // Output filenames and transient residency statistics do not affect identity.
    private static string HashMeshState(SceneMeshTransform[] meshes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> matrixBytes = stackalloc byte[64];
        foreach (SceneMeshTransform mesh in meshes)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(mesh.MeshAsset.ToString("D")));
            var m = mesh.Transform;
            float[] values = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
            for (int index = 0; index < values.Length; index++)
                BinaryPrimitives.WriteSingleLittleEndian(matrixBytes.Slice(index * 4, 4), values[index]);
            hash.AppendData(matrixBytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // Preserve the original accuracy contract for configurations without a
    // separately reviewed baseline. The Bistro profile cannot relax other scenes.
    internal static bool AcceptsUnprofiled(ShadowAccuracyObservation value) =>
        value.ComparedPixels > 0 && value.MeanVisibilityError <= .02f &&
        (float)value.MismatchedPixels / value.ComparedPixels <= .02f;

    internal bool Accepts(ShadowAccuracyObservation value) => Observations.Length >= 2 &&
        Observations.All(sample => sample.ComparedPixels == value.ComparedPixels) &&
        float.IsFinite(value.MeanVisibilityError) &&
        value.MismatchedPixels <= Observations.Max(sample => sample.MismatchedPixels) &&
        value.MissedShadowPixels <= Observations.Max(sample => sample.MissedShadowPixels) &&
        value.FalseShadowPixels <= Observations.Max(sample => sample.FalseShadowPixels) &&
        value.MissingRenderedReceiverPixels <= Observations.Max(sample => sample.MissingRenderedReceiverPixels) &&
        value.MeanVisibilityError <= Observations.Max(sample => sample.MeanVisibilityError) &&
        AcceptsRegion(value.Interior, Observations.Select(sample => sample.Interior).ToArray()) &&
        AcceptsRegion(value.Boundary, Observations.Select(sample => sample.Boundary).ToArray());

    // A reviewed edge allowance must not hide an equally sized interior regression.
    // Regions use the existing reference-only boundary definition; no pixel is removed.
    private static bool AcceptsRegion(ShadowAccuracyRegion? value, ShadowAccuracyRegion?[] samples) =>
        value is not null && samples.All(sample => sample is not null &&
            sample.ComparedPixels == value.ComparedPixels) &&
        float.IsFinite(value.MeanVisibilityError) &&
        value.MismatchedPixels <= samples.Max(sample => sample!.MismatchedPixels) &&
        value.MissedShadowPixels <= samples.Max(sample => sample!.MissedShadowPixels) &&
        value.FalseShadowPixels <= samples.Max(sample => sample!.FalseShadowPixels) &&
        value.MeanVisibilityError <= samples.Max(sample => sample!.MeanVisibilityError);
}
