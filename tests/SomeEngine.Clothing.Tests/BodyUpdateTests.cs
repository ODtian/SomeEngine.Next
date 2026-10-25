using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Clothing.Demo;
using Xunit;

namespace SomeEngine.Clothing.Tests;

public sealed class BodyUpdateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MotionUpdatesTheDisplayFrameAndInterpolatedContactSurface(bool dualQuaternion)
    {
        var scene = new ClothingScene();
        BodyMesh body = scene.BodyGeometry;
        Vector3[] previous = body.Positions.ToArray();
        scene.Advance(new DemoControls { Running = .65f, Spread = .07f, DualQuaternion = dualQuaternion });
        ReadOnlySpan<float> frame = MemoryMarshal.Cast<byte, float>(scene.Bytes);
        int values = body.Positions.Length * 3;
        Assert.Equal(body.Positions, MemoryMarshal.Cast<float, Vector3>(frame.Slice(ClothingScene.HeaderLength, values)).ToArray());
        Assert.Equal(body.Normals, MemoryMarshal.Cast<float, Vector3>(frame.Slice(ClothingScene.HeaderLength + values, values)).ToArray());

        const float blend = .35f;
        body.UpdateContactSurface(blend);
        var coordinates = new Vector3(1f / 3);
        SurfaceContact contact = body.FindContact(0, coordinates).Body;
        Vector3 position = Vector3.Zero, normal = Vector3.Zero, velocity = Vector3.Zero;
        for (int corner = 0; corner < 3; corner++)
        {
            int vertex = body.Triangles[corner];
            position += Vector3.Lerp(previous[vertex], body.Positions[vertex], blend) * coordinates[corner];
            normal += body.Normals[vertex] * coordinates[corner];
            velocity += (body.Positions[vertex] - previous[vertex]) * (coordinates[corner] / ClothingScene.StepSeconds);
        }
        Assert.Equal(position, contact.Position);
        Assert.Equal(Vector3.Normalize(normal), contact.Normal);
        Assert.Equal(velocity, contact.Velocity);
        Assert.InRange(body.Surface.Nearest(position).DistanceSquared, 0, 1e-10f);
    }
}
