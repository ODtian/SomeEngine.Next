using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class RayShadowMaterialSidesTests
{
    [Fact]
    public async Task SourceMaterialSidesControlCameraAndLightVisibility()
    {
        using var evidence = new ShadowEvidence("ray-material-sides");
        Directory.CreateDirectory(evidence.Content);
        Vector3[] receiver = [new(-4, -4, 0), new(4, -4, 0), new(4, 4, 0), new(-4, 4, 0)];
        Vector3[] blocker = [new(1, -1, 1), new(3, -1, 1), new(3, 1, 1), new(1, 1, 1)];
        ushort[] front = [0, 1, 2, 0, 2, 3];
        ushort[] back = [0, 2, 1, 0, 3, 2];
        var materials = new[] { new SourceMaterial(false), new SourceMaterial(true) };

        string singleReceiver = await WriteScene("single-receiver", [new(receiver, front, 0)]);
        string twoSidedReceiver = await WriteScene("two-sided-receiver", [new(receiver, front, 1)]);
        var cameraFront = Inputs(new(0, 0, 2), Vector3.UnitZ);
        var cameraBack = Inputs(new(0, 0, -2), -Vector3.UnitZ);
        Assert.Equal(1f, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(singleReceiver), cameraFront, false))[0].Visibility);
        Assert.Equal(uint.MaxValue, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(singleReceiver), cameraBack, false))[0].Flags);
        Assert.Equal(1f, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(twoSidedReceiver), cameraBack, false))[0].Visibility);

        Matrix4x4 mirror = Matrix4x4.CreateScale(-1, 1, 1);
        Assert.Equal(uint.MaxValue, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(singleReceiver, [mirror]), cameraFront, false))[0].Flags);
        Assert.Equal(1f, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(singleReceiver, [mirror]), cameraBack, false))[0].Visibility);
        Assert.Equal(1f, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(twoSidedReceiver, [mirror]), cameraFront, false))[0].Visibility);

        string frontCaster = await WriteScene("single-front-caster", [new(receiver, front, 0), new(blocker, front, 0)]);
        string backCaster = await WriteScene("single-back-caster", [new(receiver, front, 0), new(blocker, back, 0)]);
        string twoSidedCaster = await WriteScene("two-sided-back-caster", [new(receiver, front, 0), new(blocker, back, 1)]);
        var lighting = Inputs(new(0, 0, 2), Vector3.Normalize(new(2, 0, 1)));
        Assert.Equal(0u, (await RayShadowImage.TraceAsync(FindEngine(), new(frontCaster), lighting, false))[0]);
        Assert.Equal(1u, (await RayShadowImage.TraceAsync(FindEngine(), new(backCaster), lighting, false))[0]);
        Assert.Equal(0u, (await RayShadowImage.TraceAsync(FindEngine(), new(twoSidedCaster), lighting, false))[0]);
        Assert.Equal(1f, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(backCaster), lighting, false))[0].Visibility);
        Assert.Equal(0f, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(twoSidedCaster), lighting, false))[0].Visibility);

        string mirroredFrontCaster = await WriteScene("mirrored-front-caster", [new(receiver, back, 0), new(blocker, back, 0)]);
        string mirroredBackCaster = await WriteScene("mirrored-back-caster", [new(receiver, back, 0), new(blocker, front, 0)]);
        string mirroredTwoSidedCaster = await WriteScene("mirrored-two-sided-caster", [new(receiver, back, 0), new(blocker, front, 1)]);
        var mirroredLighting = Inputs(new(0, 0, 2), Vector3.Normalize(new(-2, 0, 1)));
        Assert.Equal(0u, (await RayShadowImage.TraceAsync(FindEngine(), new(mirroredFrontCaster, [mirror]), mirroredLighting, false))[0]);
        Assert.Equal(1u, (await RayShadowImage.TraceAsync(FindEngine(), new(mirroredBackCaster, [mirror]), mirroredLighting, false))[0]);
        Assert.Equal(0u, (await RayShadowImage.TraceAsync(FindEngine(), new(mirroredTwoSidedCaster, [mirror]), mirroredLighting, false))[0]);
        Assert.Equal(1f, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(mirroredBackCaster, [mirror]), mirroredLighting, false))[0].Visibility);
        Assert.Equal(0f, (await RayShadowImage.TraceSoftAsync(FindEngine(), new(mirroredTwoSidedCaster, [mirror]), mirroredLighting, false))[0].Visibility);
        evidence.Complete();

        RayShadowImage.Inputs Inputs(Vector3 camera, Vector3 toLight)
        {
            Assert.True(Matrix4x4.Invert(Matrix4x4.CreateLookAt(camera, Vector3.Zero, Vector3.UnitY) *
                Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), out Matrix4x4 clipToWorld));
            return new() { ClipToWorld = clipToWorld, CameraPosition = camera, Width = 1, Height = 1,
                LightDirection = -toLight, NearPlane = .1f, FarPlane = 100, SampleCount = 1 };
        }

        async Task<string> WriteScene(string name, SourcePrimitive[] primitives)
        {
            var bytes = new List<byte>();
            var views = new List<object>();
            var accessors = new List<object>();
            var geometry = new List<object>();
            foreach (SourcePrimitive primitive in primitives)
            {
                int positionView = views.Count;
                views.Add(new { buffer = 0, byteOffset = bytes.Count, byteLength = primitive.Positions.Length * 12 });
                bytes.AddRange(MemoryMarshal.AsBytes(primitive.Positions.AsSpan()).ToArray());
                int positionAccessor = accessors.Count;
                Vector3 min = primitive.Positions.Aggregate(Vector3.Min), max = primitive.Positions.Aggregate(Vector3.Max);
                accessors.Add(new { bufferView = positionView, componentType = 5126, count = primitive.Positions.Length,
                    type = "VEC3", min = new[] { min.X, min.Y, min.Z }, max = new[] { max.X, max.Y, max.Z } });
                Vector3[] normals = Enumerable.Repeat(Vector3.UnitZ, primitive.Positions.Length).ToArray();
                int normalView = views.Count;
                views.Add(new { buffer = 0, byteOffset = bytes.Count, byteLength = normals.Length * 12 });
                bytes.AddRange(MemoryMarshal.AsBytes(normals.AsSpan()).ToArray());
                int normalAccessor = accessors.Count;
                accessors.Add(new { bufferView = normalView, componentType = 5126, count = normals.Length, type = "VEC3" });
                int indexView = views.Count;
                views.Add(new { buffer = 0, byteOffset = bytes.Count, byteLength = primitive.Indices.Length * 2 });
                bytes.AddRange(MemoryMarshal.AsBytes(primitive.Indices.AsSpan()).ToArray());
                int indexAccessor = accessors.Count;
                accessors.Add(new { bufferView = indexView, componentType = 5123, count = primitive.Indices.Length, type = "SCALAR" });
                geometry.Add(new { attributes = new { POSITION = positionAccessor, NORMAL = normalAccessor },
                    indices = indexAccessor, material = primitive.Material });
            }
            string file = Path.Combine(evidence.Content, name + ".gltf");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
            {
                asset = new { version = "2.0" },
                buffers = new[] { new { byteLength = bytes.Count, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes.ToArray()) } },
                bufferViews = views, accessors, materials = materials.Select(value => new { doubleSided = value.DoubleSided }),
                meshes = new[] { new { primitives = geometry } }, nodes = new[] { new { mesh = 0 } },
                scenes = new[] { new { nodes = new[] { 0 } } }, scene = 0,
            }));
            return file;
        }
    }

    private sealed record SourcePrimitive(Vector3[] Positions, ushort[] Indices, int Material);
    private sealed record SourceMaterial(bool DoubleSided);

    private static string FindEngine()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Select the engine checkout for the source-material ray reference.");
    }
}
