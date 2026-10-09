using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class RayShadowImageSamplingTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task HalfOfAnEmitterOccludedByAPlaneHasHalfVisibility(uint lightKind)
    {
        using var evidence = new ShadowEvidence("soft-ray-half-emitter");
        Directory.CreateDirectory(evidence.Content);
        string geometry = Path.Combine(evidence.Content, "half-emitter.gltf");
        Vector3[] positions =
        [
            new(-20, 0, -20), new(-20, 0, 20), new(20, 0, 20), new(20, 0, -20),
            new(0, 1, -20), new(0, 1, 20), new(20, 1, 20), new(20, 1, -20),
        ];
        ushort[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7];
        byte[] bytes = new byte[positions.Length * 12 + indices.Length * 2];
        MemoryMarshal.AsBytes(positions.AsSpan()).CopyTo(bytes);
        MemoryMarshal.AsBytes(indices.AsSpan()).CopyTo(bytes.AsSpan(positions.Length * 12));
        await File.WriteAllTextAsync(geometry, JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" },
            buffers = new[] { new { byteLength = bytes.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes) } },
            bufferViews = new[]
            {
                new { buffer = 0, byteOffset = 0, byteLength = positions.Length * 12 },
                new { buffer = 0, byteOffset = positions.Length * 12, byteLength = indices.Length * 2 },
            },
            accessors = new object[]
            {
                new { bufferView = 0, componentType = 5126, count = positions.Length, type = "VEC3", min = new[] { -20, 0, -20 }, max = new[] { 20, 1, 20 } },
                new { bufferView = 1, componentType = 5123, count = indices.Length, type = "SCALAR" },
            },
            meshes = new[] { new { primitives = new[] { new { attributes = new { POSITION = 0 }, indices = 1 } } } },
            nodes = new[] { new { mesh = 0 } }, scenes = new[] { new { nodes = new[] { 0 } } }, scene = 0,
        }));
        Vector3 camera = new(-2, 2, 0);
        Assert.True(Matrix4x4.Invert(Matrix4x4.CreateLookAt(camera, Vector3.Zero, Vector3.UnitY) *
            Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), out Matrix4x4 clipToWorld));
        var reference = await RayShadowImage.TraceSoftAsync(FindEngine(), new RayShadowScene(geometry), new()
        {
            ClipToWorld = clipToWorld, CameraPosition = camera, Width = 1, Height = 1,
            LightDirection = -Vector3.UnitY, LightPosition = new(0, 3, 0), LightKind = lightKind,
            LightRange = 10, OuterConeCos = .5f, NearPlane = .1f, FarPlane = 100,
            SourceAngleRadians = MathF.PI / 18, SourceRadius = .5f,
            SampleCount = 2048,
        }, vulkan: false);
        Assert.InRange(reference[0].Visibility, .499f, .501f);
        Assert.Equal(0u, reference[0].Flags & 8u);
        evidence.Complete();
    }

    private static string FindEngine()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Select the engine checkout for the area-light ray reference.");
    }
}
