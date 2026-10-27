using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SomeEngine.Geometry.Cluster;
using Xunit;
using Xunit.Abstractions;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;
using StandardClusterVertexEncoder = SomeEngine.Geometry.Cluster.StandardClusterVertexEncoder;
using StandardClusterPositionEncoder = SomeEngine.Geometry.Cluster.StandardClusterPositionEncoder;

namespace SomeEngine.Assets.Tests;

public sealed class ClusterEncodingMeasurementTests(ITestOutputHelper output)
{
    private const int WarmupOperations = 5000;
    private const int Operations = 2000;
    private const int Batches = 5;
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    [Fact]
    [Trait("Category", "Performance")]
    public void MeasurePublicEncoders()
    {
        Fixture[] fixtures = [CreateFixture(64, false), CreateFixture(255, true)];
        string[] paths = ["typed", "raw-standard", "positions"];
        var hashes = new string[fixtures.Length, paths.Length];
        for (int fixture = 0; fixture < fixtures.Length; fixture++)
        {
            Fixture input = fixtures[fixture];
            EncodedClusterVertices typed = StandardClusterVertexEncoder.Instance.Encode(input.Positions, input.Attributes, input.Vertices);
            EncodedClusterVertices raw = StandardClusterVertexEncoder.Instance.Encode(input.Positions, input.RawAttributes, input.Vertices);
            EncodedClusterPositions positions = StandardClusterPositionEncoder.Encode(input.Positions, input.Vertices);
            Assert.Equal(raw.Blob.ToArray(), typed.Blob.ToArray());
            Assert.Equal(positions.Data.ToArray(), typed.Blob.Span[..typed.PositionBytes].ToArray());
            hashes[fixture, 0] = Convert.ToHexString(SHA256.HashData(typed.Blob.Span));
            hashes[fixture, 1] = Convert.ToHexString(SHA256.HashData(raw.Blob.Span));
            hashes[fixture, 2] = Convert.ToHexString(SHA256.HashData(positions.Data.AsSpan()));
        }
        for (int warmup = 0; warmup < Batches; warmup++)
            for (int fixture = 0; fixture < fixtures.Length; fixture++)
                for (int path = 0; path < paths.Length; path++)
                    _ = RunBatch(fixtures[fixture], path, WarmupOperations / Batches);
        _ = GC.GetAllocatedBytesForCurrentThread();
        _ = Stopwatch.GetTimestamp();

        var samples = new Sample[Batches * fixtures.Length * paths.Length];
        int next = 0;
        for (int batch = 0; batch < Batches; batch++)
            for (int fixture = 0; fixture < fixtures.Length; fixture++)
                for (int path = 0; path < paths.Length; path++)
                {
                    int thread = Environment.CurrentManagedThreadId;
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    uint checksum = RunBatch(fixtures[fixture], path, Operations);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.Equal(thread, Environment.CurrentManagedThreadId);
                    samples[next++] = new(fixture, path, batch, Operations, allocated, elapsed, checksum);
                }

        output.WriteLine("ENCODING_ENV " + JsonSerializer.Serialize(new
        {
            Runtime = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Configuration,
            ServerGC = GCSettings.IsServerGC,
            Stopwatch.Frequency,
            WarmupOperations,
            Operations,
            Batches,
        }));
        foreach (Sample sample in samples)
            output.WriteLine("ENCODING_SAMPLE " + JsonSerializer.Serialize(new
            {
                Fixture = fixtures[sample.Fixture].Name,
                Path = paths[sample.Path],
                sample.Batch,
                sample.Operations,
                sample.AllocatedBytes,
                sample.ElapsedTicks,
                sample.Checksum,
            }));
        for (int fixture = 0; fixture < fixtures.Length; fixture++)
            for (int path = 0; path < paths.Length; path++)
            {
                Sample[] selected = samples.Where(sample => sample.Fixture == fixture && sample.Path == path).ToArray();
                Assert.All(selected, sample => Assert.Equal(selected[0].Checksum, sample.Checksum));
                double[] allocations = selected.Select(sample => (double)sample.AllocatedBytes / sample.Operations).Order().ToArray();
                double[] timings = selected.Select(sample => (double)sample.ElapsedTicks * 1_000_000_000 / Stopwatch.Frequency / sample.Operations).Order().ToArray();
                output.WriteLine("ENCODING_SUMMARY " + JsonSerializer.Serialize(new
                {
                    Fixture = fixtures[fixture].Name,
                    Path = paths[path],
                    OutputSha256 = hashes[fixture, path],
                    BytesPerOperation = allocations[Batches / 2],
                    BytesPerOperationMin = allocations[0],
                    BytesPerOperationMax = allocations[^1],
                    NanosecondsPerOperation = timings[Batches / 2],
                    NanosecondsPerOperationMin = timings[0],
                    NanosecondsPerOperationMax = timings[^1],
                }));
            }
    }

    private static uint RunBatch(Fixture input, int path, int operations)
    {
        uint checksum = 2166136261;
        switch (path)
        {
            case 0:
                for (int index = 0; index < operations; index++)
                {
                    EncodedClusterVertices result = StandardClusterVertexEncoder.Instance.Encode(input.Positions, input.Attributes, input.Vertices);
                    checksum = Consume(checksum, result.Blob.Span);
                }
                break;
            case 1:
                for (int index = 0; index < operations; index++)
                {
                    EncodedClusterVertices result = StandardClusterVertexEncoder.Instance.Encode(input.Positions, input.RawAttributes, input.Vertices);
                    checksum = Consume(checksum, result.Blob.Span);
                }
                break;
            case 2:
                for (int index = 0; index < operations; index++)
                {
                    EncodedClusterPositions result = StandardClusterPositionEncoder.Encode(input.Positions, input.Vertices);
                    checksum = Consume(checksum, result.Data.AsSpan());
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }
        return checksum;
    }

    private static uint Consume(uint checksum, ReadOnlySpan<byte> bytes)
        => BitOperations.RotateLeft(checksum, 5) ^ (uint)bytes.Length ^ bytes[0] ^ ((uint)bytes[^1] << 8);

    private static Fixture CreateFixture(int count, bool remapped)
    {
        const int sourceCount = 1024;
        var positions = new Vector3[sourceCount];
        var attributes = new StandardClusterAttributes[sourceCount];
        var normals = new float[sourceCount * 3];
        var tangents = new float[sourceCount * 4];
        var texCoords = new float[sourceCount * 2];
        for (int index = 0; index < sourceCount; index++)
        {
            positions[index] = new(index % 31 * 0.125f, index % 17 * 0.25f, index % 13 * 0.0625f);
            Vector3 normal = Vector3.Normalize(new(index % 7 - 3, index % 11 + 1, index % 5 - 2));
            Vector4 tangent = new(normal, index % 2 == 0 ? 1 : -1);
            Vector2 uv = new(index % 19 * 0.0625f - 0.5f, index % 23 * 0.03125f);
            attributes[index] = new(normal, tangent, uv);
            normals[index * 3] = normal.X;
            normals[index * 3 + 1] = normal.Y;
            normals[index * 3 + 2] = normal.Z;
            tangents[index * 4] = tangent.X;
            tangents[index * 4 + 1] = tangent.Y;
            tangents[index * 4 + 2] = tangent.Z;
            tangents[index * 4 + 3] = tangent.W;
            texCoords[index * 2] = uv.X;
            texCoords[index * 2 + 1] = uv.Y;
        }
        var vertices = new uint[count];
        for (int index = 0; index < count; index++)
            vertices[index] = remapped
                ? index > 0 && index % 13 == 0 ? vertices[index - 1] : (uint)((index * 17 + 3) % sourceCount)
                : (uint)index;
        RawAttribute[] raw =
        [
            new("NORMAL", normals, 3, GeometryValueType.Float32, 3, false),
            new("TANGENT", tangents, 4, GeometryValueType.Float32, 4, false),
            new("TEXCOORD_0", texCoords, 2, GeometryValueType.Float32, 2, false),
        ];
        return new(count + (remapped ? "-remapped-duplicates" : "-contiguous"), positions, attributes, raw, vertices);
    }

    private sealed record Fixture(string Name, Vector3[] Positions, StandardClusterAttributes[] Attributes,
        RawAttribute[] RawAttributes, uint[] Vertices);
    private readonly record struct Sample(int Fixture, int Path, int Batch, int Operations,
        long AllocatedBytes, long ElapsedTicks, uint Checksum);
}
