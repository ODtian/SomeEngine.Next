using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Clothing.Demo;
using Xunit;

namespace SomeEngine.Clothing.Tests;

public sealed class ClothingTargetTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TargetsCountsStretchAndDisplayFollowTheCurrentMotion(bool dualQuaternion)
    {
        var scene = new ClothingScene();
        var controls = new DemoControls { Briefs = .4f, Tights = .4f, Running = .65f, Spread = .07f, DualQuaternion = dualQuaternion };
        for (int frame = 0; frame < 8; frame++) scene.Advance(controls);
        ClothingSimulation[] garments = [scene.Briefs, scene.Tights];
        foreach (ClothingSimulation garment in garments)
        {
            float travel = garment.Progress * scene.Trajectory.TravelDistance;
            for (int vertex = 0; vertex < garment.Targets.Length; vertex++)
                Assert.Equal(scene.Trajectory.Sample(garment.Mesh.Bindings[vertex], travel, dualQuaternion).Position, garment.Targets[vertex]);
            Assert.Equal(garment.SimulationWeights.Count(weight => weight > 0), garment.SimulatedVertices);
            Assert.Equal(garment.Mesh.Bindings.Count(binding => binding.Distance - travel < TrajectoryField.Bottom), garment.ReleasedVertices);
            float maximum = 1;
            foreach ((int a, int b) in garment.Mesh.Edges)
            {
                float ratio = Vector3.Distance(garment.Positions[a], garment.Positions[b])
                    / Vector3.Distance(garment.Mesh.Vertices[a], garment.Mesh.Vertices[b]);
                maximum = MathF.Max(maximum, ratio);
                Assert.True(garment.Stretch[a] >= ratio);
                Assert.True(garment.Stretch[b] >= ratio);
            }
            Assert.InRange(MathF.Abs(maximum - garment.MaximumStretch), 0, 1e-6f);
        }
        ReadOnlySpan<float> displayed = MemoryMarshal.Cast<byte, float>(scene.Bytes);
        int offset = ClothingScene.HeaderLength + scene.BodyGeometry.Positions.Length * 6;
        foreach (ClothingSimulation garment in garments)
        {
            int count = garment.Positions.Length * 3;
            Assert.Equal(garment.Positions, MemoryMarshal.Cast<float, Vector3>(displayed.Slice(offset, count)).ToArray());
            offset += count;
        }
        foreach (ClothingSimulation garment in garments)
        {
            Assert.Equal(garment.SimulationWeights, displayed.Slice(offset, garment.SimulationWeights.Length).ToArray());
            offset += garment.SimulationWeights.Length;
        }
        foreach (ClothingSimulation garment in garments)
        {
            Assert.Equal(garment.Stretch, displayed.Slice(offset, garment.Stretch.Length).ToArray());
            offset += garment.Stretch.Length;
        }
    }
}
